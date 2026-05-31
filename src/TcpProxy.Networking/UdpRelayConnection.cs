using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;

namespace TcpProxy.Networking
{
    /// <summary>
    /// UDP relay connection. Two usage modes:
    ///   - Client (ownsSocket=true):  creates its own UdpClient, receives in a loop.
    ///   - Virtual session (ownsSocket=false): shares the server socket; data delivered
    ///     via DeliverReceived(); SendAsync sends to the specific remote endpoint.
    /// </summary>
    public sealed class UdpRelayConnection : IRelayConnection
    {
        private readonly UdpClient         _udp;
        private readonly IPEndPoint        _remoteEp;
        private readonly bool              _ownsSocket;
        private readonly IMetricsCollector _metrics;
        private readonly ILogger           _logger;
        private readonly string            _channel;

        private bool             _disposed;
        private DateTime         _lastActivity = DateTime.UtcNow;
        private const int        SessionTimeoutSeconds = 30;

        public string Name { get; }

        public bool IsConnected
        {
            get
            {
                if (_disposed) return false;
                if (!_ownsSocket)
                    return (DateTime.UtcNow - _lastActivity).TotalSeconds < SessionTimeoutSeconds;
                return true;
            }
        }

        public event Action<string, byte[], int, int>? DataReceived;
        public event Action?                           Disconnected;

        // ── UDP Client ctor (proxy connects to a fixed remote) ────────────────
        public UdpRelayConnection(
            string name, string channel,
            string remoteHost, int remotePort,
            IMetricsCollector metrics, ILogger logger)
        {
            Name        = name;
            _channel    = channel;
            _metrics    = metrics;
            _logger     = logger;
            _ownsSocket = true;
            _remoteEp   = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
            _udp        = new UdpClient();
            _udp.Connect(_remoteEp); // restricts ReceiveAsync to this endpoint
            // Suppress Windows ICMP port-unreachable (WSAECONNRESET=10054) on receive.
            // Without this, sending to a closed UDP port causes ReceiveAsync to throw
            // SocketException and tears down the connection before the target is ready.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
            catch (SocketException) { } // non-Windows: ignore
        }

        // ── Virtual session ctor (server-mode, shared socket) ─────────────────
        public UdpRelayConnection(
            string name, string channel,
            UdpClient sharedSocket, IPEndPoint remoteEp,
            IMetricsCollector metrics, ILogger logger)
        {
            Name        = name;
            _channel    = channel;
            _metrics    = metrics;
            _logger     = logger;
            _ownsSocket = false;
            _udp        = sharedSocket;
            _remoteEp   = remoteEp;
        }

        // ── IRelayConnection ──────────────────────────────────────────────────

        public void StartReceiving(CancellationToken ct)
        {
            if (!_ownsSocket) return; // server-mode: UdpServerEndpoint drives delivery

            ct.Register(() =>
            {
                if (!_disposed) Dispose();
            });
            Task.Run(() => ReceiveLoopAsync(ct));
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    var result = await _udp.ReceiveAsync().ConfigureAwait(false);
                    _metrics.RecordBytesReceived(_channel, Name, result.Buffer.Length);
                    DataReceived?.Invoke(Name, result.Buffer, 0, result.Buffer.Length);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException ex)
            {
                _logger.LogWarning("[{Channel}][{Name}] UDP receive socket error: {Msg}", _channel, Name, ex.Message);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[{Channel}][{Name}] UDP receive error", _channel, Name);
            }
            finally
            {
                // If the loop exited unexpectedly (e.g. ICMP port-unreachable SocketException),
                // mark disposed so IsConnected returns false and the reconnect loop can exit.
                if (!_disposed)
                {
                    _disposed = true;
                    if (_ownsSocket) _udp.Close();
                }
                Disconnected?.Invoke();
                _logger.LogInformation("[{Channel}][{Name}] UDP receive loop ended (ownsSocket={Own})", _channel, Name, _ownsSocket);
            }
        }

        // Called by UdpServerEndpoint when a datagram arrives for this session
        public void DeliverReceived(byte[] data)
        {
            _lastActivity = DateTime.UtcNow;
            _metrics.RecordBytesReceived(_channel, Name, data.Length);
            DataReceived?.Invoke(Name, data, 0, data.Length);
        }

        public Task SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_disposed) return Task.CompletedTask;
            var data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            try
            {
                if (_ownsSocket)
                    _udp.Send(data, count);                // connected socket — sends to _remoteEp
                else
                    _udp.Send(data, count, _remoteEp);    // shared socket — explicit endpoint

                _metrics.RecordBytesSent(_channel, Name, count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Channel}][{Name}] UDP send error", _channel, Name);
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ownsSocket)
            {
                _udp.Close(); // causes ReceiveAsync to throw -> ReceiveLoopAsync fires Disconnected
            }
            else
            {
                Disconnected?.Invoke(); // virtual session: fire immediately
            }
        }
    }
}
