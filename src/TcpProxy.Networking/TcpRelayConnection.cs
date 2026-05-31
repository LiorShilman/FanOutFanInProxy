using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;

namespace TcpProxy.Networking
{
    public sealed class TcpRelayConnection : IRelayConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly QueueConfig _queueConfig;
        private readonly BufferConfig _bufferConfig;
        private readonly IMetricsCollector _metrics;
        private readonly ILogger<TcpRelayConnection> _logger;
        private readonly string _channel;
        private readonly ConcurrentQueue<(byte[] data, int offset, int count)> _sendQueue = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public string Name { get; }
        public bool IsConnected => _client.Connected && !_disposed;

        public event Action<string, byte[], int, int>? DataReceived;
        public event Action? Disconnected;

        public TcpRelayConnection(
            string name,
            string channel,
            TcpClient client,
            QueueConfig queueConfig,
            BufferConfig bufferConfig,
            IMetricsCollector metrics,
            ILogger<TcpRelayConnection> logger)
        {
            Name          = name;
            _channel      = channel;
            _client       = client;
            _stream       = client.GetStream();
            _queueConfig  = queueConfig;
            _bufferConfig = bufferConfig;
            _metrics      = metrics;
            _logger       = logger;
        }

        public void StartReceiving(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
            Task.Run(() => SendLoopAsync(_cts.Token), _cts.Token);
        }

        public Task SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!IsConnected) return Task.CompletedTask;

            if (_sendQueue.Count >= _queueConfig.MaxSizePerConnection)
            {
                if (_queueConfig.OverflowPolicy == OverflowPolicy.DropOldest)
                {
                    _sendQueue.TryDequeue(out _);
                    _metrics.RecordQueueDrop(_channel, Name);
                    _logger.LogWarning("[{Channel}][{Name}] Queue full, dropping oldest packet", _channel, Name);
                }
                else
                {
                    _logger.LogWarning("[{Channel}][{Name}] Queue full, disconnecting client", _channel, Name);
                    Dispose();
                    return Task.CompletedTask;
                }
            }

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _sendQueue.Enqueue((copy, 0, count));
            _sendSignal.Release();
            return Task.CompletedTask;
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[_bufferConfig.ReadBufferSize];
            try
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    int read = await _stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (read == 0) break;

                    _metrics.RecordBytesReceived(_channel, Name, read);
                    DataReceived?.Invoke(Name, buf, 0, read);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[{Channel}][{Name}] Receive error", _channel, Name);
            }
            finally
            {
                _metrics.RecordConnectionClosed(_channel, Name);
                _logger.LogInformation("[{Channel}][{Name}] Disconnected", _channel, Name);
                Disconnected?.Invoke();
                Dispose();
            }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(ct).ConfigureAwait(false);

                    while (_sendQueue.TryDequeue(out var item))
                    {
                        if (!IsConnected) return;
                        await _stream.WriteAsync(item.data, item.offset, item.count, ct).ConfigureAwait(false);
                        _metrics.RecordBytesSent(_channel, Name, item.count);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[{Channel}][{Name}] Send error", _channel, Name);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _stream.Close();
            _client.Close();
        }
    }
}
