using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcpProxy.Core.Models;

namespace TcpProxy.Routing
{
    /// <summary>
    /// Resolves routing rules and dispatches data across channels.
    /// The routing table is mutable at runtime via <see cref="UpdateTargets"/>.
    ///
    /// Default (no rules configured): same-channel routing
    ///   Upstream   → all Downstreams of same channel
    ///   Downstream → Upstream of same channel
    /// </summary>
    public sealed class RoutingEngine
    {
        // "MC:Upstream" -> [("MC","Downstream"), ("DATA","Downstream")]
        private Dictionary<string, List<(string Channel, string Role)>> _table;

        // Sources that have been explicitly set to [] (blocked).
        // Sources absent from both _table and _blocked use default same-channel routing.
        private HashSet<string> _blocked;

        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private readonly Dictionary<string, ChannelProxy> _channels
            = new Dictionary<string, ChannelProxy>(StringComparer.OrdinalIgnoreCase);

        // Ordered list of all known endpoints (populated as channels register)
        private readonly List<string> _allEndpoints = new List<string>();

        private readonly ILogger<RoutingEngine> _logger;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _blockedWarned = new();

        public RoutingEngine(RoutingConfig config, ILogger<RoutingEngine> logger)
        {
            _logger  = logger;
            _table   = BuildTable(config.Rules, out _blocked);

            if (_table.Count > 0 || _blocked.Count > 0)
                _logger.LogInformation(
                    "RoutingEngine: {RuleCount} rule(s) loaded, {BlockCount} source(s) blocked at startup",
                    _table.Count, _blocked.Count);
            else
                _logger.LogInformation("RoutingEngine: no rules — using default same-channel routing");
        }

        public void RegisterChannel(ChannelProxy channel)
        {
            _channels[channel.ChannelName] = channel;
            _allEndpoints.Add($"{channel.ChannelName}:Upstream");
            _allEndpoints.Add($"{channel.ChannelName}:Downstream");
            _logger.LogDebug("RoutingEngine: registered channel '{Channel}'", channel.ChannelName);
        }

        // ── Runtime mutation ──────────────────────────────────────────────────

        /// <summary>
        /// Replaces the target list for <paramref name="from"/> at runtime.
        /// <paramref name="toList"/> contains strings like "MC:Downstream".
        /// Passing an empty list removes the rule (falls back to default routing).
        /// </summary>
        public void UpdateTargets(string from, IReadOnlyList<string> toList)
        {
            var targets = ParseTargetList(toList);
            _lock.EnterWriteLock();
            try
            {
                var tableCopy   = new Dictionary<string, List<(string Channel, string Role)>>(_table, StringComparer.OrdinalIgnoreCase);
                var blockedCopy = new HashSet<string>(_blocked, StringComparer.OrdinalIgnoreCase);

                if (targets.Count > 0)
                {
                    // Explicit targets: add to table, remove from blocked set
                    tableCopy[from] = targets;
                    blockedCopy.Remove(from);
                    _blockedWarned.TryRemove(from, out _);
                }
                else
                {
                    // Empty target list: explicitly block this source
                    tableCopy.Remove(from);
                    blockedCopy.Add(from);
                }

                _table   = tableCopy;
                _blocked = blockedCopy;
            }
            finally { _lock.ExitWriteLock(); }

            _logger.LogInformation("RoutingEngine: updated '{From}' → [{To}]",
                from, string.Join(", ", toList));
        }

        // ── State queries ─────────────────────────────────────────────────────

        public IReadOnlyList<string> GetAllEndpoints() => _allEndpoints;

        public List<RoutingRuleSnapshot> GetCurrentRules()
        {
            Dictionary<string, List<(string Channel, string Role)>> table;
            HashSet<string> blocked;
            _lock.EnterReadLock();
            try { table = _table; blocked = _blocked; }
            finally { _lock.ExitReadLock(); }

            var result = new List<RoutingRuleSnapshot>();

            // Explicit rules
            foreach (var kv in table)
            {
                result.Add(new RoutingRuleSnapshot
                {
                    From = kv.Key,
                    To   = kv.Value.Select(t => $"{t.Channel}:{t.Role}").ToList()
                });
            }

            // Default rules for endpoints not explicitly configured and not blocked
            // (so the dashboard can display their current active targets)
            foreach (var ep in _allEndpoints)
            {
                if (table.ContainsKey(ep) || blocked.Contains(ep)) continue;

                var idx = ep.IndexOf(':');
                if (idx <= 0) continue;
                var channel = ep.Substring(0, idx);
                var role    = ep.Substring(idx + 1);

                // Default: Upstream → same-channel Downstream, Downstream → same-channel Upstream
                var defaultTarget = role.Equals("Upstream", StringComparison.OrdinalIgnoreCase)
                    ? $"{channel}:Downstream"
                    : $"{channel}:Upstream";

                result.Add(new RoutingRuleSnapshot
                {
                    From = ep,
                    To   = new List<string> { defaultTarget }
                });
            }

            // Blocked endpoints as empty-To rules — lets the dashboard detect BLOCK ALL
            // (when every entry has To.Count == 0 the dashboard knows all routes are cut)
            foreach (var ep in _allEndpoints)
            {
                if (!table.ContainsKey(ep) && blocked.Contains(ep))
                    result.Add(new RoutingRuleSnapshot { From = ep, To = new List<string>() });
            }

            return result;
        }

        // ── Routing ───────────────────────────────────────────────────────────

        public Task RouteAsync(
            string fromChannel, string fromRole,
            byte[] buf, int offset, int count,
            CancellationToken ct)
        {
            var key = $"{fromChannel}:{fromRole}";

            Dictionary<string, List<(string, string)>> table;
            HashSet<string> blocked;
            _lock.EnterReadLock();
            try { table = _table; blocked = _blocked; }
            finally { _lock.ExitReadLock(); }

            // 1. Explicit routing rule for this source?
            if (table.TryGetValue(key, out var targets))
                return DispatchAsync(targets, buf, offset, count, ct);

            // 2. Source was explicitly blocked (sent [] via dashboard)?
            if (blocked.Contains(key))
            {
                if (_blockedWarned.TryAdd(key, true))
                    _logger.LogWarning("RoutingEngine: '{Key}' is BLOCKED — all data dropped", key);
                return Task.CompletedTask;
            }

            // 3. No explicit rule and not blocked → default same-channel routing
            return DefaultRouteAsync(fromChannel, fromRole, buf, offset, count, ct);
        }

        // ── Default routing (no rules) ────────────────────────────────────────

        private Task DefaultRouteAsync(
            string fromChannel, string fromRole,
            byte[] buf, int offset, int count,
            CancellationToken ct)
        {
            if (!_channels.TryGetValue(fromChannel, out var ch)) return Task.CompletedTask;

            return fromRole == "Upstream"
                ? ch.FanOutToDownstreamsAsync(buf, offset, count, ct)
                : ch.SendToUpstreamAsync(buf, offset, count, ct);
        }

        // ── Dispatching ───────────────────────────────────────────────────────

        private async Task DispatchAsync(
            List<(string Channel, string Role)> targets,
            byte[] buf, int offset, int count,
            CancellationToken ct)
        {
            var tasks = new List<Task>(targets.Count);
            foreach (var (targetChannel, targetRole) in targets)
            {
                if (!_channels.TryGetValue(targetChannel, out var ch))
                {
                    _logger.LogWarning("RoutingEngine: unknown target channel '{Channel}'", targetChannel);
                    continue;
                }

                if (targetRole.Equals("Downstream", StringComparison.OrdinalIgnoreCase))
                    tasks.Add(ch.FanOutToDownstreamsAsync(buf, offset, count, ct));
                else if (targetRole.Equals("Upstream", StringComparison.OrdinalIgnoreCase))
                    tasks.Add(ch.SendToUpstreamAsync(buf, offset, count, ct));
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Dictionary<string, List<(string, string)>> BuildTable(
            IEnumerable<RoutingRule> rules,
            out HashSet<string> blocked)
        {
            var table = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                var targets = ParseTargetList(rule.To);
                if (targets.Count > 0)
                    table[rule.From] = targets;
                else
                    blocked.Add(rule.From); // to: [] in config = blocked at startup
            }
            return table;
        }

        private static List<(string, string)> ParseTargetList(IEnumerable<string> targets)
        {
            var result = new List<(string, string)>();
            foreach (var target in targets)
            {
                var idx = target.IndexOf(':');
                if (idx > 0 && idx < target.Length - 1)
                    result.Add((target.Substring(0, idx), target.Substring(idx + 1)));
            }
            return result;
        }
    }
}
