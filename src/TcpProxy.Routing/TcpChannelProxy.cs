using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;
using TcpProxy.Networking;

namespace TcpProxy.Routing
{
    public sealed class ChannelProxy : ITcpChannelProxy, IChannelStatusProvider
    {
        // ── Config ────────────────────────────────────────────────────────────
        private readonly ChannelConfig               _config;
        private readonly IReadOnlyList<DownstreamSlotConfig> _downstreamSlots;
        private readonly QueueConfig                 _queueConfig;
        private readonly BufferConfig                _bufferConfig;
        private readonly SocketConfig                _socketConfig;
        private readonly ReconnectConfig             _reconnectConfig;

        // ── Routing ───────────────────────────────────────────────────────────
        private readonly RoutingEngine _routingEngine;

        // ── Infrastructure ────────────────────────────────────────────────────
        private readonly IMetricsCollector     _metrics;
        private readonly ILoggerFactory        _loggerFactory;
        private readonly ILogger<ChannelProxy> _logger;

        // ── Upstream state ────────────────────────────────────────────────────
        private volatile bool               _upstreamConnected;
        private volatile IRelayConnection? _upstream;
        private TcpListener?       _tcpUpstreamListener;
        private UdpServerEndpoint? _udpUpstreamServer;

        // ── Downstream state ──────────────────────────────────────────────────
        private readonly List<IRelayConnection>             _downstreams         = new();
        private readonly object                             _dsLock              = new();
        private readonly ConcurrentDictionary<string, bool> _downstreamConnected = new();
        private readonly ConcurrentQueue<string>            _slotPool;
        private int                                         _anonSlot;
        private TcpListener?                                _tcpDownstreamListener;
        private UdpServerEndpoint?                          _udpDownstreamServer;

        private CancellationTokenSource? _cts;
        private int _noDownstreamWarned;
        private int _noUpstreamWarned;

        public string ChannelName => _config.Name;

        public ChannelProxy(
            ChannelConfig config,
            IReadOnlyList<DownstreamSlotConfig> downstreamSlots,
            QueueConfig queueConfig,
            BufferConfig bufferConfig,
            SocketConfig socketConfig,
            ReconnectConfig reconnectConfig,
            RoutingEngine routingEngine,
            IMetricsCollector metrics,
            ILoggerFactory loggerFactory)
        {
            _config          = config;
            _downstreamSlots = downstreamSlots;
            _queueConfig     = queueConfig;
            _bufferConfig    = bufferConfig;
            _socketConfig    = socketConfig;
            _reconnectConfig = reconnectConfig;
            _routingEngine   = routingEngine;
            _metrics         = metrics;
            _loggerFactory   = loggerFactory;
            _logger          = loggerFactory.CreateLogger<ChannelProxy>();
            _slotPool        = new ConcurrentQueue<string>(downstreamSlots.Select(d => d.Name));
        }

        // ── IChannelStatusProvider ────────────────────────────────────────────

        public ChannelStatus GetStatus()
        {
            var snap        = _metrics.GetSnapshot(ChannelName);
            var downstreams = new List<DownstreamStatus>();

            foreach (var slot in _downstreamSlots)
                downstreams.Add(new DownstreamStatus
                {
                    Name         = slot.Name,
                    Connected    = _downstreamConnected.TryGetValue(slot.Name, out var c) && c,
                    RxBytesTotal = _metrics.GetDsBytesReceived(ChannelName, slot.Name),
                    TxBytesTotal = _metrics.GetDsBytesSent(ChannelName, slot.Name)
                });

            // Count anonymous (non-named-slot) active connections and sum their RX bytes
            int namedConnected = downstreams.Count(d => d.Connected);
            int totalConnected = _downstreamConnected.Count(kv => kv.Value);
            int anonConnected  = Math.Max(0, totalConnected - namedConnected);

            var namedNames = new HashSet<string>(_downstreamSlots.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            long anonRxBytes = 0;
            foreach (var kv in _downstreamConnected)
                if (!namedNames.Contains(kv.Key))
                    anonRxBytes += _metrics.GetDsBytesReceived(ChannelName, kv.Key);

            return new ChannelStatus
            {
                Name                     = ChannelName,
                DownstreamLabel          = _config.DownstreamLabel,
                AnonymousDownstreamCount = anonConnected,
                AnonymousRxBytesTotal    = anonRxBytes,
                UpstreamConnected        = _upstreamConnected,
                Downstreams              = downstreams,
                RxBytesTotal             = snap.TotalBytesReceived,
                TxBytesTotal             = snap.TotalBytesSent,
                ReconnectCount           = snap.ReconnectCount,
                DroppedPackets           = snap.DroppedPackets
            };
        }

        // ── ITcpChannelProxy ──────────────────────────────────────────────────

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = _cts.Token;
            await Task.WhenAll(StartUpstreamAsync(ct), StartDownstreamAsync(ct)).ConfigureAwait(false);
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            _tcpUpstreamListener?.Stop();
            _tcpDownstreamListener?.Stop();
            _udpUpstreamServer?.Stop();
            _udpDownstreamServer?.Stop();
            return Task.CompletedTask;
        }

        // ── Public routing interface (called by RoutingEngine) ────────────────

        public async Task FanOutToDownstreamsAsync(byte[] buf, int offset, int count, CancellationToken ct)
        {
            IRelayConnection[] snapshot;
            lock (_dsLock) snapshot = _downstreams.ToArray();

            var tasks = new List<Task>(snapshot.Length);
            foreach (var ds in snapshot)
                if (ds.IsConnected) tasks.Add(ds.SendAsync(buf, offset, count, ct));

            if (tasks.Count == 0)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _noDownstreamWarned, 1, 0) == 0)
                    _logger.LogWarning("[{Ch}] FanOut: no connected downstream ({N} in list) — data dropping. Will not repeat.",
                        ChannelName, snapshot.Length);
            }
            else
            {
                System.Threading.Interlocked.Exchange(ref _noDownstreamWarned, 0);
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        public Task SendToUpstreamAsync(byte[] buf, int offset, int count, CancellationToken ct)
        {
            var up = _upstream;
            if (up == null || !up.IsConnected)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _noUpstreamWarned, 1, 0) == 0)
                    _logger.LogWarning("[{Ch}] SendToUpstream: upstream {State} — data dropping. Will not repeat.",
                        ChannelName, up == null ? "null" : "disconnected");
                return Task.CompletedTask;
            }
            System.Threading.Interlocked.Exchange(ref _noUpstreamWarned, 0);
            return up.SendAsync(buf, offset, count, ct);
        }

        // ── Upstream dispatch ─────────────────────────────────────────────────

        private Task StartUpstreamAsync(CancellationToken ct)
        {
            if (_config.Upstream.Port == 0) return Task.CompletedTask; // not defined in YAML

            var proto = _config.Upstream.Protocol;
            var mode  = _config.Upstream.Mode;

            if (proto == SocketProtocol.Tcp && mode == SocketMode.Client) return TcpClientUpstreamLoopAsync(ct);
            if (proto == SocketProtocol.Tcp && mode == SocketMode.Server) return TcpServerUpstreamLoopAsync(ct);
            if (proto == SocketProtocol.Udp && mode == SocketMode.Client) return UdpClientUpstreamLoopAsync(ct);
            if (proto == SocketProtocol.Udp && mode == SocketMode.Server) return UdpServerUpstreamLoopAsync(ct);
            return Task.CompletedTask;
        }

        // TCP Client: proxy connects to upstream server with retry/reconnect
        private async Task TcpClientUpstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Upstream;
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                IRelayConnection? conn = null;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            var client = MakeTcpClient();
                            await client.ConnectAsync(ep.Host, ep.Port).ConfigureAwait(false);
                            conn = new TcpRelayConnection(
                                "Upstream", ChannelName, client,
                                _queueConfig, _bufferConfig, _metrics,
                                _loggerFactory.CreateLogger<TcpRelayConnection>());
                            attempt = 0;
                            break;
                        }
                        catch (SocketException ex)
                        {
                            attempt++;
                            if (_reconnectConfig.MaxAttempts > 0 && attempt >= _reconnectConfig.MaxAttempts)
                                throw new InvalidOperationException("Max reconnect attempts reached", ex);
                            _logger.LogWarning("[{Ch}] Upstream connect failed ({A}), retry in {S}s: {M}",
                                ChannelName, attempt, _reconnectConfig.IntervalSeconds, ex.Message);
                            await Task.Delay(TimeSpan.FromSeconds(_reconnectConfig.IntervalSeconds), ct).ConfigureAwait(false);
                        }
                    }

                    if (conn == null) break;
                    AttachUpstream(conn);
                    conn.StartReceiving(ct);
                    _logger.LogInformation("[{Ch}] Upstream TCP connected to {H}:{P}", ChannelName, ep.Host, ep.Port);

                    while (conn.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "[{Ch}] Upstream TCP error", ChannelName); }
                finally { DetachUpstream(); conn?.Dispose(); }
            }
        }

        // TCP Server: proxy listens, upstream connects to it
        private async Task TcpServerUpstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Upstream;
            _tcpUpstreamListener = new TcpListener(IPAddress.Parse(ep.ListenIp), ep.Port);
            _tcpUpstreamListener.Start();
            _logger.LogInformation("[{Ch}] Upstream TCP Server: listening on {Ip}:{Port}", ChannelName, ep.ListenIp, ep.Port);

            while (!ct.IsCancellationRequested)
            {
                IRelayConnection? conn = null;
                try
                {
                    var tcpClient = await _tcpUpstreamListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _logger.LogInformation("[{Ch}] Upstream connected from {Ep}", ChannelName, tcpClient.Client.RemoteEndPoint);

                    conn = new TcpRelayConnection(
                        "Upstream", ChannelName, tcpClient,
                        _queueConfig, _bufferConfig, _metrics,
                        _loggerFactory.CreateLogger<TcpRelayConnection>());

                    AttachUpstream(conn);
                    conn.StartReceiving(ct);

                    while (conn.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "[{Ch}] Upstream TCP Server error", ChannelName);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                finally { DetachUpstream(); conn?.Dispose(); }
            }
        }

        // UDP Client: proxy sends/receives to/from a fixed remote endpoint
        private async Task UdpClientUpstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Upstream;
            while (!ct.IsCancellationRequested)
            {
                IRelayConnection? conn = null;
                try
                {
                    conn = new UdpRelayConnection(
                        "Upstream", ChannelName,
                        ep.Host, ep.Port,
                        _metrics, _loggerFactory.CreateLogger<UdpRelayConnection>());

                    AttachUpstream(conn);
                    conn.StartReceiving(ct);
                    _logger.LogInformation("[{Ch}] Upstream UDP client → {H}:{P}", ChannelName, ep.Host, ep.Port);

                    while (conn.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Ch}] Upstream UDP error", ChannelName);
                    await Task.Delay(TimeSpan.FromSeconds(_reconnectConfig.IntervalSeconds), ct).ConfigureAwait(false);
                }
                finally { DetachUpstream(); conn?.Dispose(); }
            }
        }

        // UDP Server: proxy binds to a port, upstream sends datagrams to it
        private async Task UdpServerUpstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Upstream;
            _udpUpstreamServer = new UdpServerEndpoint(
                ep.ListenIp, ep.Port, "Upstream", ChannelName, _metrics, _loggerFactory);

            _udpUpstreamServer.NewSession += session =>
            {
                var current = _upstream;
                if (current != null && current.IsConnected) return; // only one live upstream at a time
                AttachUpstream(session);
            };

            _logger.LogInformation("[{Ch}] Upstream UDP Server: listening on {Ip}:{Port}", ChannelName, ep.ListenIp, ep.Port);
            await _udpUpstreamServer.RunAsync(ct).ConfigureAwait(false);
        }

        // ── Upstream helpers ──────────────────────────────────────────────────

        private void AttachUpstream(IRelayConnection conn)
        {
            conn.DataReceived += OnUpstreamData;
            conn.Disconnected += () =>
            {
                _upstreamConnected = false;
                _upstream          = null;
                _metrics.RecordConnectionClosed(ChannelName, "Upstream");
                _logger.LogInformation("[{Ch}] Upstream disconnected ({Name})", ChannelName, conn.Name);
            };
            _upstream          = conn;
            _upstreamConnected = true;
            _metrics.RecordConnectionOpened(ChannelName, "Upstream");
            _logger.LogInformation("[{Ch}] Upstream attached ({Name})", ChannelName, conn.Name);
        }

        private void DetachUpstream()
        {
            _upstreamConnected = false;
            _upstream          = null;
        }

        // ── Downstream dispatch ───────────────────────────────────────────────

        private Task StartDownstreamAsync(CancellationToken ct)
        {
            if (_config.Downstream.Port == 0) return Task.CompletedTask; // not defined in YAML

            var proto = _config.Downstream.Protocol;
            var mode  = _config.Downstream.Mode;

            if (proto == SocketProtocol.Tcp && mode == SocketMode.Server) return TcpServerDownstreamLoopAsync(ct);
            if (proto == SocketProtocol.Tcp && mode == SocketMode.Client) return TcpClientDownstreamLoopAsync(ct);
            if (proto == SocketProtocol.Udp && mode == SocketMode.Server) return UdpServerDownstreamLoopAsync(ct);
            if (proto == SocketProtocol.Udp && mode == SocketMode.Client) return UdpClientDownstreamLoopAsync(ct);
            return Task.CompletedTask;
        }

        // TCP Server: proxy listens, DS clients connect (default behavior)
        private async Task TcpServerDownstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Downstream;
            _tcpDownstreamListener = new TcpListener(IPAddress.Parse(ep.ListenIp), ep.Port);
            _tcpDownstreamListener.Start();
            _logger.LogInformation("[{Ch}] Downstream TCP Server: listening on {Ip}:{Port}", ChannelName, ep.ListenIp, ep.Port);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _tcpDownstreamListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var remoteEp  = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    _ = HandleNewTcpDownstreamAsync(tcpClient, remoteEp, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "[{Ch}] Downstream accept error", ChannelName);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleNewTcpDownstreamAsync(TcpClient tcpClient, string ep, CancellationToken ct)
        {
            tcpClient.NoDelay = true;

            if (!_slotPool.TryDequeue(out var name))
                name = $"DS-{Interlocked.Increment(ref _anonSlot)}";

            _logger.LogInformation("[{Ch}] Downstream {Name} connected from {Ep}", ChannelName, name, ep);

            var conn = new TcpRelayConnection(
                name, ChannelName, tcpClient,
                _queueConfig, _bufferConfig, _metrics,
                _loggerFactory.CreateLogger<TcpRelayConnection>());

            AttachDownstream(conn, name);
            conn.StartReceiving(ct);

            while (conn.IsConnected && !ct.IsCancellationRequested)
                await Task.Delay(500, ct).ConfigureAwait(false);

            conn.Dispose();
        }

        // TCP Client: proxy connects to each DS slot (or directly when no slots configured)
        private Task TcpClientDownstreamLoopAsync(CancellationToken ct)
        {
            if (_downstreamSlots.Count > 0)
                return Task.WhenAll(_downstreamSlots.Select(s => TcpClientDsSlotLoopAsync(s, _config.Downstream, ct)));
            var direct = new DownstreamSlotConfig { Name = ChannelName };
            return TcpClientDsSlotLoopAsync(direct, _config.Downstream, ct);
        }

        private async Task TcpClientDsSlotLoopAsync(DownstreamSlotConfig slot, EndpointConfig ep, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                IRelayConnection? conn = null;
                try
                {
                    var client = MakeTcpClient();
                    await client.ConnectAsync(ep.Host, ep.Port).ConfigureAwait(false);

                    conn = new TcpRelayConnection(
                        slot.Name, ChannelName, client,
                        _queueConfig, _bufferConfig, _metrics,
                        _loggerFactory.CreateLogger<TcpRelayConnection>());

                    AttachDownstream(conn, slot.Name);
                    conn.StartReceiving(ct);
                    _logger.LogInformation("[{Ch}][{S}] DS TCP connected to {H}:{P}", ChannelName, slot.Name, ep.Host, ep.Port);

                    while (conn.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Ch}][{S}] DS TCP connect failed, retry in {Sec}s", ChannelName, slot.Name, _reconnectConfig.IntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_reconnectConfig.IntervalSeconds), ct).ConfigureAwait(false);
                }
                finally { conn?.Dispose(); }
            }
        }

        // UDP Server: proxy binds, DS clients send datagrams
        private async Task UdpServerDownstreamLoopAsync(CancellationToken ct)
        {
            var ep = _config.Downstream;
            _udpDownstreamServer = new UdpServerEndpoint(
                ep.ListenIp, ep.Port, "Downstream", ChannelName, _metrics, _loggerFactory);

            _udpDownstreamServer.NewSession += session =>
            {
                if (!_slotPool.TryDequeue(out var name))
                    name = $"DS-{Interlocked.Increment(ref _anonSlot)}";
                AttachDownstream(session, name);
            };

            _logger.LogInformation("[{Ch}] Downstream UDP Server: listening on {Ip}:{Port}", ChannelName, ep.ListenIp, ep.Port);
            await _udpDownstreamServer.RunAsync(ct).ConfigureAwait(false);
        }

        // UDP Client: proxy connects to each DS slot via UDP (or directly when no slots configured)
        private Task UdpClientDownstreamLoopAsync(CancellationToken ct)
        {
            if (_downstreamSlots.Count > 0)
                return Task.WhenAll(_downstreamSlots.Select(s => UdpClientDsSlotLoopAsync(s, _config.Downstream, ct)));
            var direct = new DownstreamSlotConfig { Name = ChannelName };
            return UdpClientDsSlotLoopAsync(direct, _config.Downstream, ct);
        }

        private async Task UdpClientDsSlotLoopAsync(DownstreamSlotConfig slot, EndpointConfig ep, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                IRelayConnection? conn = null;
                try
                {
                    conn = new UdpRelayConnection(
                        slot.Name, ChannelName,
                        ep.Host, ep.Port,
                        _metrics, _loggerFactory.CreateLogger<UdpRelayConnection>());

                    AttachDownstream(conn, slot.Name);
                    conn.StartReceiving(ct);
                    _logger.LogInformation("[{Ch}][{S}] DS UDP client → {H}:{P}", ChannelName, slot.Name, ep.Host, ep.Port);

                    while (conn.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Ch}][{S}] DS UDP error, retry in {Sec}s", ChannelName, slot.Name, _reconnectConfig.IntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_reconnectConfig.IntervalSeconds), ct).ConfigureAwait(false);
                }
                finally { conn?.Dispose(); }
            }
        }

        // ── Downstream helpers ────────────────────────────────────────────────

        private void AttachDownstream(IRelayConnection conn, string name)
        {
            conn.DataReceived += OnDownstreamData;
            conn.Disconnected += () =>
            {
                _downstreamConnected.TryRemove(name, out _);
                _slotPool.Enqueue(name);
                lock (_dsLock) _downstreams.Remove(conn);
                _metrics.RecordConnectionClosed(ChannelName, name);
                _logger.LogInformation("[{Ch}] Downstream '{Name}' disconnected — list now {N}",
                    ChannelName, name, _downstreams.Count);
            };

            lock (_dsLock) _downstreams.Add(conn);
            _downstreamConnected[name] = true;
            _metrics.RecordConnectionOpened(ChannelName, name);
            _logger.LogInformation("[{Ch}] Downstream '{Name}' attached — list now {N}",
                ChannelName, name, _downstreams.Count);
        }

        // ── Data events → RoutingEngine ───────────────────────────────────────

        private void OnUpstreamData(string source, byte[] buf, int offset, int count)
            => _ = _routingEngine.RouteAsync(ChannelName, "Upstream", buf, offset, count,
                                             _cts?.Token ?? CancellationToken.None);

        private void OnDownstreamData(string source, byte[] buf, int offset, int count)
            => _ = _routingEngine.RouteAsync(ChannelName, "Downstream", buf, offset, count,
                                             _cts?.Token ?? CancellationToken.None);

        // ── Helpers ───────────────────────────────────────────────────────────

        private TcpClient MakeTcpClient()
        {
            var c = new TcpClient
            {
                NoDelay           = _socketConfig.NoDelay,
                ReceiveBufferSize = _socketConfig.ReceiveBufferSize,
                SendBufferSize    = _socketConfig.SendBufferSize
            };
            if (_socketConfig.KeepAlive)
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return c;
        }
    }
}
