﻿/*
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using DotPulsar.Exceptions;
using DotPulsar.Internal.Abstractions;
using DotPulsar.Internal.Extensions;
using DotPulsar.Internal.PulsarApi;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotPulsar.Internal
{
    public sealed class ConnectionPool : IConnectionPool
    {
        private readonly AsyncLock _lock;
        private readonly CommandConnect _commandConnect;
        private readonly Uri _serviceUrl;
        private readonly Connector _connector;
        private readonly EncryptionPolicy _encryptionPolicy;
        private readonly ConcurrentDictionary<Uri, Connection> _connections;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _closeInactiveConnections;

        public ConnectionPool(CommandConnect commandConnect, Uri serviceUrl, Connector connector, EncryptionPolicy encryptionPolicy, TimeSpan closeInactiveConnectionsInterval)
        {
            _lock = new AsyncLock();
            _commandConnect = commandConnect;
            _serviceUrl = serviceUrl;
            _connector = connector;
            _encryptionPolicy = encryptionPolicy;
            _connections = new ConcurrentDictionary<Uri, Connection>();
            _cancellationTokenSource = new CancellationTokenSource();
            _closeInactiveConnections = CloseInactiveConnections(closeInactiveConnectionsInterval, _cancellationTokenSource.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource.Cancel();
            await _closeInactiveConnections.ConfigureAwait(false);

            await _lock.DisposeAsync().ConfigureAwait(false);

            foreach (var serviceUrl in _connections.Keys.ToArray())
            {
                await DisposeConnection(serviceUrl).ConfigureAwait(false);
            }
        }

        public async ValueTask<IConnection> FindConnectionForTopic(string topic, CancellationToken cancellationToken)
        {
            var lookup = new CommandLookupTopic
            {
                Topic = topic,
                Authoritative = false
            };

            var serviceUrl = _serviceUrl;

            while (true)
            {
                var connection = await GetConnection(serviceUrl, cancellationToken).ConfigureAwait(false);
                var response = await connection.Send(lookup, cancellationToken).ConfigureAwait(false);

                response.Expect(BaseCommand.Type.LookupResponse);

                if (response.LookupTopicResponse.Response == CommandLookupTopicResponse.LookupType.Failed)
                    response.LookupTopicResponse.Throw();

                lookup.Authoritative = response.LookupTopicResponse.Authoritative;

                serviceUrl = new Uri(GetBrokerServiceUrl(response.LookupTopicResponse));

                if (response.LookupTopicResponse.Response == CommandLookupTopicResponse.LookupType.Redirect || !response.LookupTopicResponse.Authoritative)
                    continue;

                // LookupType is 'Connect', ServiceUrl is local and response is authoritative. Assume the Pulsar server is a standalone docker.
                return _serviceUrl.IsLoopback ? connection : await GetConnection(serviceUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        private string GetBrokerServiceUrl(CommandLookupTopicResponse response)
        {
            var hasBrokerServiceUrl = !string.IsNullOrEmpty(response.BrokerServiceUrl);
            var hasBrokerServiceUrlTls = !string.IsNullOrEmpty(response.BrokerServiceUrlTls);

            switch (_encryptionPolicy)
            {
                case EncryptionPolicy.EnforceEncrypted:
                    if (!hasBrokerServiceUrlTls)
                        throw new ConnectionSecurityException("Cannot enforce encrypted connections. The lookup topic response from broker gave no secure alternative.");
                    return response.BrokerServiceUrlTls;
                case EncryptionPolicy.EnforceUnencrypted:
                    if (!hasBrokerServiceUrl)
                        throw new ConnectionSecurityException("Cannot enforce unencrypted connections. The lookup topic response from broker gave no unsecure alternative.");
                    return response.BrokerServiceUrl;
                case EncryptionPolicy.PreferEncrypted:
                    return hasBrokerServiceUrlTls ? response.BrokerServiceUrlTls : response.BrokerServiceUrl;
                default:
                    return hasBrokerServiceUrl ? response.BrokerServiceUrl : response.BrokerServiceUrlTls;
            }
        }

        private async ValueTask<Connection> GetConnection(Uri serviceUrl, CancellationToken cancellationToken)
        {
            using (await _lock.Lock(cancellationToken).ConfigureAwait(false))
            {
                if (_connections.TryGetValue(serviceUrl, out Connection connection))
                    return connection;

                return await EstablishNewConnection(serviceUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Connection> EstablishNewConnection(Uri serviceUrl, CancellationToken cancellationToken)
        {
            var stream = await _connector.Connect(serviceUrl).ConfigureAwait(false);
            var connection = new Connection(new PulsarStream(stream));
            DotPulsarEventSource.Log.ConnectionCreated();
            _connections[serviceUrl] = connection;
            _ = connection.ProcessIncommingFrames(cancellationToken).ContinueWith(t => DisposeConnection(serviceUrl));
            var response = await connection.Send(_commandConnect, cancellationToken).ConfigureAwait(false);
            response.Expect(BaseCommand.Type.Connected);
            return connection;
        }

        private async ValueTask DisposeConnection(Uri serviceUrl)
        {
            if (_connections.TryRemove(serviceUrl, out Connection connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                DotPulsarEventSource.Log.ConnectionDisposed();
            }
        }

        private async Task CloseInactiveConnections(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                    using (await _lock.Lock(cancellationToken).ConfigureAwait(false))
                    {
                        var serviceUrls = _connections.Keys;
                        foreach (var serviceUrl in serviceUrls)
                        {
                            var connection = _connections[serviceUrl];
                            if (connection is null)
                                continue;

                            if (!await connection.HasChannels(cancellationToken).ConfigureAwait(false))
                                await DisposeConnection(serviceUrl).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}
