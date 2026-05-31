using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;

namespace TcpProxy.Networking
{
    public sealed class ReconnectWorker : IConnectionManager
    {
        private readonly ReconnectConfig _reconnectConfig;
        private readonly QueueConfig _queueConfig;
        private readonly BufferConfig _bufferConfig;
        private readonly SocketConfig _socketConfig;
        private readonly IMetricsCollector _metrics;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ReconnectWorker> _logger;

        public ReconnectWorker(
            ReconnectConfig reconnectConfig,
            QueueConfig queueConfig,
            BufferConfig bufferConfig,
            SocketConfig socketConfig,
            IMetricsCollector metrics,
            ILoggerFactory loggerFactory)
        {
            _reconnectConfig = reconnectConfig;
            _queueConfig     = queueConfig;
            _bufferConfig    = bufferConfig;
            _socketConfig    = socketConfig;
            _metrics         = metrics;
            _loggerFactory   = loggerFactory;
            _logger          = loggerFactory.CreateLogger<ReconnectWorker>();
        }

        public async Task<IRelayConnection> ConnectWithRetryAsync(string host, int port, string name, string channel, CancellationToken ct)
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = new TcpClient();
                    client.NoDelay           = _socketConfig.NoDelay;
                    client.ReceiveBufferSize = _socketConfig.ReceiveBufferSize;
                    client.SendBufferSize    = _socketConfig.SendBufferSize;

                    await client.ConnectAsync(host, port).ConfigureAwait(false);
                    _logger.LogInformation("[{Channel}][{Name}] Connected to {Host}:{Port}", channel, name, host, port);

                    return new TcpRelayConnection(
                        name, channel, client,
                        _queueConfig, _bufferConfig,
                        _metrics,
                        _loggerFactory.CreateLogger<TcpRelayConnection>());
                }
                catch (SocketException ex)
                {
                    attempt++;
                    if (_reconnectConfig.MaxAttempts > 0 && attempt >= _reconnectConfig.MaxAttempts)
                        throw new InvalidOperationException($"Max reconnect attempts reached for {name}", ex);

                    _logger.LogWarning("[{Name}] Connect failed ({Attempt}), retrying in {Sec}s: {Msg}",
                        name, attempt, _reconnectConfig.IntervalSeconds, ex.Message);

                    await Task.Delay(TimeSpan.FromSeconds(_reconnectConfig.IntervalSeconds), ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }
    }
}
