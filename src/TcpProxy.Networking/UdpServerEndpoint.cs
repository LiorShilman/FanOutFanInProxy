using System;
using System.Collections.Concurrent;
using System.Linq;
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
    /// UDP server endpoint: binds to a local port and tracks each unique remote
    /// sender as a <see cref="UdpRelayConnection"/> virtual session.
    /// Fires <see cref="NewSession"/> when the first datagram from a new sender arrives.
    /// </summary>
    public sealed class UdpServerEndpoint
    {
        private readonly UdpClient         _socket;
        private readonly string            _role;       // "Upstream" | "Downstream"
        private readonly string            _channel;
        private readonly IMetricsCollector _metrics;
        private readonly ILoggerFactory    _loggerFactory;
        private readonly ILogger           _logger;

        private readonly ConcurrentDictionary<string, UdpRelayConnection> _sessions = new();

        public event Action<UdpRelayConnection>? NewSession;

        public UdpServerEndpoint(
            string listenIp, int port,
            string role, string channel,
            IMetricsCollector metrics, ILoggerFactory loggerFactory)
        {
            _role          = role;
            _channel       = channel;
            _metrics       = metrics;
            _loggerFactory = loggerFactory;
            _logger        = loggerFactory.CreateLogger<UdpServerEndpoint>();
            _socket        = new UdpClient(new IPEndPoint(IPAddress.Parse(listenIp), port));
        }

        public async Task RunAsync(CancellationToken ct)
        {
            ct.Register(() => _socket.Close());

            _ = CleanupLoopAsync(ct);

            _logger.LogInformation("[{Channel}][{Role}] UDP server receiving", _channel, _role);

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)         { if (ct.IsCancellationRequested) break; continue; }

                var key = result.RemoteEndPoint.ToString();

                if (!_sessions.TryGetValue(key, out var session) || !session.IsConnected)
                {
                    var sessionName = $"{_role}-{result.RemoteEndPoint.Address}:{result.RemoteEndPoint.Port}";
                    session = new UdpRelayConnection(
                        sessionName, _channel,
                        _socket, result.RemoteEndPoint,
                        _metrics,
                        _loggerFactory.CreateLogger<UdpRelayConnection>());

                    session.Disconnected += () => _sessions.TryRemove(key, out _);
                    _sessions[key] = session;

                    _logger.LogInformation("[{Channel}][{Role}] New UDP session from {Ep}", _channel, _role, key);
                    NewSession?.Invoke(session);
                }

                session.DeliverReceived(result.Buffer);
            }
        }

        public void Stop() => _socket.Close();

        private async Task CleanupLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(5000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                foreach (var key in _sessions.Keys.ToArray())
                {
                    if (_sessions.TryGetValue(key, out var s) && !s.IsConnected)
                    {
                        _sessions.TryRemove(key, out _);
                        s.Dispose(); // fires Disconnected
                        _logger.LogInformation("[{Channel}][{Role}] UDP session timed out: {Key}", _channel, _role, key);
                    }
                }
            }
        }
    }
}
