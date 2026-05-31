using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;
using TcpProxy.Logging;
using TcpProxy.Routing;

namespace TcpProxy.Service
{
    public sealed class StatusBroadcaster
    {
        private readonly DashboardConfig                  _config;
        private readonly IReadOnlyList<IChannelStatusProvider> _providers;
        private readonly RoutingEngine                    _routingEngine;
        private readonly ILogger<StatusBroadcaster>       _logger;
        private readonly object                           _clientsLock = new object();
        private readonly List<NetworkStream>              _clients     = new();

        public StatusBroadcaster(
            DashboardConfig config,
            IReadOnlyList<IChannelStatusProvider> providers,
            RoutingEngine routingEngine,
            ILogger<StatusBroadcaster> logger)
        {
            _config        = config;
            _providers     = providers;
            _routingEngine = routingEngine;
            _logger        = logger;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            if (!_config.Enabled) return;

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Parse(_config.StatusHost), _config.StatusPort);
                listener.Start();
                _logger.LogInformation("[Dashboard] Status server listening on {Host}:{Port}",
                    _config.StatusHost, _config.StatusPort);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Dashboard] Cannot bind status port {Port} — dashboard push disabled (port in use?)",
                    _config.StatusPort);
                await PushLoopAsync(ct).ConfigureAwait(false);   // still run push loop (no-op if no clients)
                return;
            }

            _ = AcceptLoopAsync(listener, ct);
            await PushLoopAsync(ct).ConfigureAwait(false);
            listener.Stop();
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    lock (_clientsLock) _clients.Add(client.GetStream());
                    _logger.LogInformation("[Dashboard] Client connected from {Ep}",
                        client.Client.RemoteEndPoint);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task PushLoopAsync(CancellationToken ct)
        {
            var interval = TimeSpan.FromMilliseconds(_config.PushIntervalMs);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                var snap  = BuildSnapshot();
                var json  = SimpleJson(snap) + "\n";
                var bytes = Encoding.UTF8.GetBytes(json);

                List<NetworkStream> snapshot;
                lock (_clientsLock) snapshot = new List<NetworkStream>(_clients);

                var dead = new List<NetworkStream>();
                foreach (var stream in snapshot)
                {
                    try { await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false); }
                    catch { dead.Add(stream); }
                }

                if (dead.Count > 0)
                    lock (_clientsLock)
                    {
                        foreach (var d in dead) { _clients.Remove(d); try { d.Close(); } catch { } }
                    }
            }
        }

        private StatusSnapshot BuildSnapshot()
        {
            var channels = new List<ChannelStatus>();
            foreach (var p in _providers)
                channels.Add(p.GetStatus());

            return new StatusSnapshot
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Channels      = channels,
                RecentLogs    = RecentLogBuffer.GetSnapshot(),
                RoutingRules  = _routingEngine.GetCurrentRules(),
                AllEndpoints  = new List<string>(_routingEngine.GetAllEndpoints()),
                CommandPort   = _config.CommandPort,
                UpstreamLabel = _config.UpstreamLabel
            };
        }

        // ── Minimal JSON serializer (no external deps in Service project) ─────

        private static string SimpleJson(StatusSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("{\"timestamp\":\"").Append(s.Timestamp).Append('"');

            // channels
            sb.Append(",\"channels\":[");
            for (int i = 0; i < s.Channels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var ch = s.Channels[i];
                sb.Append("{\"name\":\"").Append(ch.Name).Append('"');
                sb.Append(",\"upstreamConnected\":").Append(ch.UpstreamConnected ? "true" : "false");
                sb.Append(",\"rxBytesTotal\":").Append(ch.RxBytesTotal);
                sb.Append(",\"txBytesTotal\":").Append(ch.TxBytesTotal);
                sb.Append(",\"reconnectCount\":").Append(ch.ReconnectCount);
                sb.Append(",\"droppedPackets\":").Append(ch.DroppedPackets);
                sb.Append(",\"downstreamLabel\":\"").Append(ch.DownstreamLabel).Append('"');
                sb.Append(",\"anonymousDownstreamCount\":").Append(ch.AnonymousDownstreamCount);
                sb.Append(",\"anonymousRxBytesTotal\":").Append(ch.AnonymousRxBytesTotal);
                sb.Append(",\"downstreams\":[");
                for (int j = 0; j < ch.Downstreams.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    var ds = ch.Downstreams[j];
                    sb.Append("{\"name\":\"").Append(ds.Name).Append('"');
                    sb.Append(",\"connected\":").Append(ds.Connected ? "true" : "false");
                    sb.Append(",\"rxBytesTotal\":").Append(ds.RxBytesTotal);
                    sb.Append(",\"txBytesTotal\":").Append(ds.TxBytesTotal).Append('}');
                }
                sb.Append("]}");
            }
            sb.Append(']');

            // routing rules
            sb.Append(",\"routingRules\":[");
            for (int i = 0; i < s.RoutingRules.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = s.RoutingRules[i];
                sb.Append("{\"from\":\"").Append(r.From).Append("\",\"to\":[");
                for (int j = 0; j < r.To.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append('"').Append(r.To[j]).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append(']');

            // all endpoints
            sb.Append(",\"allEndpoints\":[");
            for (int i = 0; i < s.AllEndpoints.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(s.AllEndpoints[i]).Append('"');
            }
            sb.Append(']');

            // command port
            sb.Append(",\"commandPort\":").Append(s.CommandPort);

            // upstream label (optional — empty string when not configured)
            sb.Append(",\"upstreamLabel\":\"").Append(s.UpstreamLabel).Append('"');

            // logs
            sb.Append(",\"recentLogs\":[");
            for (int i = 0; i < s.RecentLogs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(s.RecentLogs[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            sb.Append("]}");

            return sb.ToString();
        }
    }
}
