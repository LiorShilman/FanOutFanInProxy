using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Configuration;
using TcpProxy.Core.Interfaces;
using TcpProxy.Core.Models;
using TcpProxy.Metrics;
using TcpProxy.Routing;

namespace TcpProxy.Service
{
    public sealed class ProxyHost
    {
        private readonly AppSettings        _settings;
        private readonly ILoggerFactory     _loggerFactory;
        private readonly ILogger<ProxyHost> _logger;
        private readonly List<ChannelProxy> _channels = new();
        private readonly IMetricsCollector  _metrics;
        private CancellationTokenSource     _cts = new();

        public ProxyHost(AppSettings settings, ILoggerFactory loggerFactory)
        {
            _settings      = settings;
            _loggerFactory = loggerFactory;
            _logger        = loggerFactory.CreateLogger<ProxyHost>();
            _metrics       = new MetricsCollector();
        }

        public async Task StartAsync(CancellationToken externalCt = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var ct    = _cts.Token;
            var proxy = _settings.Proxy;

            // Global routing engine — resolves rules from config
            var routingEngine = new RoutingEngine(
                proxy.Routing,
                _loggerFactory.CreateLogger<RoutingEngine>());

            foreach (var channelConfig in proxy.Channels)
            {
                var ch = new ChannelProxy(
                    channelConfig,
                    proxy.Downstreams,
                    proxy.Queue,
                    proxy.Buffer,
                    proxy.Socket,
                    proxy.Reconnect,
                    routingEngine,
                    _metrics,
                    _loggerFactory);

                _channels.Add(ch);
                routingEngine.RegisterChannel(ch);
            }

            var channelNames = proxy.Channels.Select(c => c.Name);
            var reporter = new MetricsReporter(
                _metrics, _settings.Metrics, channelNames,
                _loggerFactory.CreateLogger<MetricsReporter>());

            var statusProviders = _channels.Cast<IChannelStatusProvider>().ToList();
            var broadcaster = new StatusBroadcaster(
                _settings.Dashboard, statusProviders, routingEngine,
                _loggerFactory.CreateLogger<StatusBroadcaster>());

            var routingController = new RoutingController(
                _settings.Dashboard.CommandPort, routingEngine,
                _loggerFactory.CreateLogger<RoutingController>());

            LogConfiguration(proxy);
            _logger.LogInformation("Starting {Count} channel(s)...", _channels.Count);

            var tasks = new List<Task>();
            foreach (var ch in _channels)
                tasks.Add(ch.StartAsync(ct));
            tasks.Add(reporter.RunAsync(ct));
            tasks.Add(broadcaster.RunAsync(ct));
            tasks.Add(routingController.RunAsync(ct));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private void LogConfiguration(ProxyConfig proxy)
        {
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _logger.LogInformation("  TCP Proxy Configuration");
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            foreach (var ch in proxy.Channels)
            {
                _logger.LogInformation("  Channel : {Name}", ch.Name);

                var up = ch.Upstream;
                if (up.Mode == SocketMode.Client)
                    _logger.LogInformation("    Upstream   : {Proto}/{Mode}  connect → {Host}:{Port}",
                        up.Protocol, up.Mode, up.Host, up.Port);
                else
                    _logger.LogInformation("    Upstream   : {Proto}/{Mode}  listen  {Ip}:{Port}",
                        up.Protocol, up.Mode, up.ListenIp, up.Port);

                var dn = ch.Downstream;
                if (dn.Mode == SocketMode.Server)
                    _logger.LogInformation("    Downstream : {Proto}/{Mode}  listen  {Ip}:{Port}",
                        dn.Protocol, dn.Mode, dn.ListenIp, dn.Port);
                else
                    _logger.LogInformation("    Downstream : {Proto}/{Mode}  connect → {Host}:{Port}",
                        dn.Protocol, dn.Mode, dn.Host, dn.Port);
            }

            _logger.LogInformation("  DS Slots : {Slots}",
                string.Join(", ", proxy.Downstreams.ConvertAll(d => d.Name)));

            if (proxy.Routing.Rules.Count > 0)
            {
                _logger.LogInformation("  Routing rules:");
                foreach (var rule in proxy.Routing.Rules)
                    _logger.LogInformation("    {From}  →  {To}",
                        rule.From, string.Join(", ", rule.To));
            }
            else
            {
                _logger.LogInformation("  Routing  : default (same-channel fan-out / fan-in)");
            }

            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping proxy...");
            _cts.Cancel();
            foreach (var ch in _channels)
                await ch.StopAsync().ConfigureAwait(false);
        }
    }
}
