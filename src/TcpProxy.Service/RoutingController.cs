using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Routing;

namespace TcpProxy.Service
{
    /// <summary>
    /// Raw-TCP command channel for runtime routing changes.
    /// Client sends one JSON line, server replies with one JSON line and closes.
    ///   {"from":"MC:Upstream","to":["MC:Downstream","DATA:Downstream"]}
    ///   → {"ok":true}
    /// No HttpListener / HTTP.SYS / URL ACL required.
    /// </summary>
    public sealed class RoutingController
    {
        private readonly RoutingEngine              _engine;
        private readonly int                        _port;
        private readonly ILogger<RoutingController> _logger;

        public RoutingController(int port, RoutingEngine engine, ILogger<RoutingController> logger)
        {
            _port   = port;
            _engine = engine;
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            try { listener.Start(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RoutingController] Cannot bind port {Port} — dashboard routing changes disabled", _port);
                return;
            }

            ct.Register(() => { try { listener.Stop(); } catch { } });
            _logger.LogInformation("[RoutingController] Command listener on 127.0.0.1:{Port}", _port);

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { break; }
                _ = HandleAsync(client, ct);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();

                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) return;

                    byte[] reply;
                    if (TryParseUpdate(line, out var from, out var to))
                    {
                        _engine.UpdateTargets(from, to);
                        reply = Encoding.UTF8.GetBytes("{\"ok\":true}\n");
                    }
                    else
                    {
                        reply = Encoding.UTF8.GetBytes("{\"error\":\"bad request\"}\n");
                    }

                    await stream.WriteAsync(reply, 0, reply.Length, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "[RoutingController] Command error");
                }
            }
        }

        // Parses: {"from":"MC:Upstream","to":["MC:Downstream","DATA:Downstream"]}
        private static bool TryParseUpdate(string json, out string from, out List<string> to)
        {
            from = string.Empty;
            to   = new List<string>();

            var fromIdx = json.IndexOf("\"from\"", StringComparison.Ordinal);
            if (fromIdx < 0) return false;
            var colon = json.IndexOf(':', fromIdx);
            if (colon < 0) return false;
            var q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return false;
            var q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return false;
            from = json.Substring(q1 + 1, q2 - q1 - 1);

            var toIdx    = json.IndexOf("\"to\"", StringComparison.Ordinal);
            if (toIdx < 0) return false;
            var arrStart = json.IndexOf('[', toIdx);
            var arrEnd   = json.IndexOf(']', arrStart > 0 ? arrStart : toIdx);
            if (arrStart < 0 || arrEnd < 0) return false;

            var arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            var pos = 0;
            while (pos < arrContent.Length)
            {
                var s = arrContent.IndexOf('"', pos);
                if (s < 0) break;
                var e = arrContent.IndexOf('"', s + 1);
                if (e < 0) break;
                to.Add(arrContent.Substring(s + 1, e - s - 1));
                pos = e + 1;
            }

            return from.Length > 0;
        }
    }
}
