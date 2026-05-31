using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;

namespace TcpProxy.Metrics
{
    public sealed class MetricsReporter
    {
        private readonly IMetricsCollector _metrics;
        private readonly MetricsConfig _config;
        private readonly ILogger<MetricsReporter> _logger;
        private readonly List<string> _channels;

        public MetricsReporter(
            IMetricsCollector metrics,
            MetricsConfig config,
            IEnumerable<string> channels,
            ILogger<MetricsReporter> logger)
        {
            _metrics  = metrics;
            _config   = config;
            _logger   = logger;
            _channels = new List<string>(channels);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            if (!_config.Enabled) return;

            var interval = TimeSpan.FromSeconds(_config.ReportIntervalSeconds);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                foreach (var ch in _channels)
                {
                    var snap = _metrics.GetSnapshot(ch);
                    _logger.LogInformation(
                        "[Metrics][{Channel}] RX={Rx}B TX={Tx}B Active={Active} Reconnects={R} Dropped={D}",
                        ch, snap.TotalBytesReceived, snap.TotalBytesSent,
                        snap.ActiveConnections, snap.ReconnectCount, snap.DroppedPackets);
                }
            }
        }
    }
}
