using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Interfaces;

namespace TcpProxy.Routing
{
    public sealed class TrafficRouter : ITrafficRouter
    {
        private IRelayConnection? _upstream;
        private readonly List<IRelayConnection> _downstreams = new();
        private readonly ILogger<TrafficRouter> _logger;
        private readonly string _channel;

        public TrafficRouter(string channel, ILogger<TrafficRouter> logger)
        {
            _channel = channel;
            _logger  = logger;
        }

        public void SetUpstream(IRelayConnection connection) => _upstream = connection;

        public void AddDownstream(IRelayConnection connection) => _downstreams.Add(connection);

        public void RemoveDownstream(IRelayConnection connection) => _downstreams.Remove(connection);

        public async Task RouteUpstreamToDownstreamsAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var tasks = new Task[_downstreams.Count];
            for (int i = 0; i < _downstreams.Count; i++)
            {
                var ds = _downstreams[i];
                tasks[i] = ds.IsConnected
                    ? ds.SendAsync(buffer, offset, count, ct)
                    : Task.CompletedTask;
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async Task RouteDownstreamToUpstreamAsync(string sourceName, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_upstream is null || !_upstream.IsConnected) return;
            await _upstream.SendAsync(buffer, offset, count, ct).ConfigureAwait(false);
        }
    }
}
