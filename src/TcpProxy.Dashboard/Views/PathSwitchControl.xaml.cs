using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard.Views
{
    public partial class PathSwitchControl : UserControl
    {
        // ── Colours / effects (frozen) ────────────────────────────────────────
        private static readonly SolidColorBrush BrGreen      = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
        private static readonly SolidColorBrush BrGreenDim   = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0xAF, 0x50)));
        private static readonly SolidColorBrush BrBlue       = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)));
        private static readonly SolidColorBrush BrBlueBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x4F, 0xC3, 0xF7)));
        private static readonly SolidColorBrush BrBlueDim    = Freeze(new SolidColorBrush(Color.FromArgb(0x0A, 0x4F, 0xC3, 0xF7)));
        private static readonly SolidColorBrush BrRed        = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)));
        private static readonly SolidColorBrush BrRedBorder  = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xF4, 0x43, 0x36)));
        private static readonly SolidColorBrush BrRedDim     = Freeze(new SolidColorBrush(Color.FromArgb(0x0A, 0xF4, 0x43, 0x36)));
        private static readonly SolidColorBrush BrMuted      = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x88)));

        private static readonly DropShadowEffect GlowGreen = FreezeEffect(new DropShadowEffect
            { Color = Color.FromRgb(0x4C, 0xAF, 0x50), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 });

        private static T Freeze<T>(T obj) where T : Freezable { obj.Freeze(); return obj; }
        private static DropShadowEffect FreezeEffect(DropShadowEffect e) { e.Freeze(); return e; }

        // ── Switch mode ───────────────────────────────────────────────────────

        /// <summary>
        /// Bidirectional: 2 channels each with full upstream+downstream (FOFI AIR sim).
        /// Switch = activate both directions of A, block both directions of B.
        /// </summary>
        private sealed class BidirectionalMode
        {
            public string ChannelA { get; }
            public string ChannelB { get; }
            public BidirectionalMode(string a, string b) { ChannelA = a; ChannelB = b; }
        }

        /// <summary>
        /// Fan-out: hub channel routes to one of two output channels (FOFI AIR operational).
        /// Switch = change only the hub's outbound target.
        /// </summary>
        private sealed class FanOutMode
        {
            public string PivotSource { get; }   // e.g. "ACCC_IN:Upstream"
            public string ChannelA    { get; }   // e.g. "LOS_UPPER"
            public string ChannelB    { get; }   // e.g. "SUPERNOVA_AIR"
            public FanOutMode(string pivot, string a, string b)
            { PivotSource = pivot; ChannelA = a; ChannelB = b; }
        }

        // ── State ─────────────────────────────────────────────────────────────
        private int    _commandPort = 19002;
        private bool   _aIsActive;
        private bool   _bIsActive;
        private object? _mode;   // BidirectionalMode | FanOutMode | null

        public PathSwitchControl() => InitializeComponent();

        // ── Public update entry point ─────────────────────────────────────────
        public void UpdateRouting(
            List<RoutingRuleSnapshot> rules,
            List<string>              allEndpoints,
            int                       commandPort,
            List<ChannelStatus>       channels)
        {
            _commandPort = commandPort;

            var channelNames = allEndpoints
                .Select(ep => { var i = ep.IndexOf(':'); return i > 0 ? ep.Substring(0, i) : ep; })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

            // Detect switch topology
            _mode = DetectMode(rules, channelNames);

            bool visible = _mode != null;
            PanelTwoPath.Visibility       = visible ? Visibility.Visible   : Visibility.Collapsed;
            PanelNotApplicable.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            if (!visible) return;

            string channelA, channelB;
            bool allBlocked;
            bool aHasRoutes, bHasRoutes;

            if (_mode is BidirectionalMode bi)
            {
                channelA    = bi.ChannelA;
                channelB    = bi.ChannelB;
                allBlocked  = rules.Count > 0 && rules.TrueForAll(r => r.To.Count == 0);
                aHasRoutes  = !allBlocked && rules.Any(r =>
                    r.From.StartsWith(bi.ChannelA + ":", StringComparison.OrdinalIgnoreCase) && r.To.Count > 0);
                bHasRoutes  = !allBlocked && rules.Any(r =>
                    r.From.StartsWith(bi.ChannelB + ":", StringComparison.OrdinalIgnoreCase) && r.To.Count > 0);
            }
            else
            {
                var fo = (FanOutMode)_mode!;
                channelA   = fo.ChannelA;
                channelB   = fo.ChannelB;
                var pivotRule = rules.FirstOrDefault(r =>
                    r.From.Equals(fo.PivotSource, StringComparison.OrdinalIgnoreCase));
                allBlocked  = pivotRule != null && pivotRule.To.Count == 0;
                aHasRoutes  = pivotRule != null && pivotRule.To.Any(t =>
                    ChannelOf(t).Equals(fo.ChannelA, StringComparison.OrdinalIgnoreCase));
                bHasRoutes  = pivotRule != null && pivotRule.To.Any(t =>
                    ChannelOf(t).Equals(fo.ChannelB, StringComparison.OrdinalIgnoreCase));
            }

            _aIsActive = aHasRoutes;
            _bIsActive = bHasRoutes;

            NameA.Text = channelA;
            NameB.Text = channelB;

            bool aConn = channels.Any(c =>
                c.Name.Equals(channelA, StringComparison.OrdinalIgnoreCase) && c.UpstreamConnected);
            bool bConn = channels.Any(c =>
                c.Name.Equals(channelB, StringComparison.OrdinalIgnoreCase) && c.UpstreamConnected);

            if (allBlocked)
            {
                ApplyState(CardA, DotA, BadgeTextA, BadgeA, StatusA, CardState.Blocked, aConn, false);
                ApplyState(CardB, DotB, BadgeTextB, BadgeB, StatusB, CardState.Blocked, bConn, false);
            }
            else
            {
                ApplyState(CardA, DotA, BadgeTextA, BadgeA, StatusA,
                    aHasRoutes ? CardState.Active : CardState.Standby, aConn, !aHasRoutes);
                ApplyState(CardB, DotB, BadgeTextB, BadgeB, StatusB,
                    bHasRoutes ? CardState.Active : CardState.Standby, bConn, !bHasRoutes);
            }
        }

        // ── Topology detection ────────────────────────────────────────────────

        private static object? DetectMode(List<RoutingRuleSnapshot> rules, List<string> channelNames)
        {
            if (channelNames.Count == 2)
                return new BidirectionalMode(channelNames[0], channelNames[1]);

            // Active rules: find pivot → target.
            // Alternate = channel Z where rule "Z:Upstream → sourceCh:Downstream" exists,
            // meaning Z and target are symmetric alternatives for the same pivot.
            foreach (var rule in rules.Where(r => r.To.Count > 0))
            {
                var targetCh = ChannelOf(rule.To[0]);
                var sourceCh = ChannelOf(rule.From);
                if (sourceCh.Equals(targetCh, StringComparison.OrdinalIgnoreCase)) continue;
                if (!RoleOf(rule.To[0]).Equals("Downstream", StringComparison.OrdinalIgnoreCase)) continue;

                var alternate = rules
                    .Where(r => r.To.Count > 0 && r.To.Any(t =>
                        ChannelOf(t).Equals(sourceCh, StringComparison.OrdinalIgnoreCase) &&
                        RoleOf(t).Equals("Downstream", StringComparison.OrdinalIgnoreCase)))
                    .Select(r => ChannelOf(r.From))
                    .FirstOrDefault(ch =>
                        !ch.Equals(sourceCh, StringComparison.OrdinalIgnoreCase) &&
                        !ch.Equals(targetCh, StringComparison.OrdinalIgnoreCase));

                if (alternate != null)
                    return new FanOutMode(rule.From, targetCh, alternate);
            }

            // Blocked pivot: active route is empty but we can still infer fan-out topology
            foreach (var rule in rules.Where(r => r.To.Count == 0))
            {
                var sourceCh = ChannelOf(rule.From);
                var outputs  = channelNames
                    .Where(ch => !ch.Equals(sourceCh, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (outputs.Count == 2)
                    return new FanOutMode(rule.From, outputs[0], outputs[1]);
            }

            return null;
        }

        // ── Card visual state ─────────────────────────────────────────────────
        private enum CardState { Active, Standby, Blocked }

        private static void ApplyState(
            Border card, TextBlock dot, TextBlock badgeText, Border badge,
            TextBlock status, CardState state, bool upstreamConn, bool clickable)
        {
            card.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;

            switch (state)
            {
                case CardState.Active:
                    card.BorderBrush     = BrGreen;
                    card.Background      = BrGreenDim;
                    card.Opacity         = 1.0;
                    card.Effect          = GlowGreen;
                    dot.Text             = "◉";
                    dot.Foreground       = BrGreen;
                    badgeText.Text       = "ACTIVE";
                    badgeText.Foreground = BrGreen;
                    badge.Background     = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0xAF, 0x50));
                    break;
                case CardState.Standby:
                    card.BorderBrush     = BrBlueBorder;
                    card.Background      = BrBlueDim;
                    card.Opacity         = 0.75;
                    card.Effect          = null;
                    dot.Text             = "○";
                    dot.Foreground       = BrBlue;
                    badgeText.Text       = "STANDBY";
                    badgeText.Foreground = BrBlue;
                    badge.Background     = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xC3, 0xF7));
                    break;
                case CardState.Blocked:
                    card.BorderBrush     = BrRedBorder;
                    card.Background      = BrRedDim;
                    card.Opacity         = 0.55;
                    card.Effect          = null;
                    dot.Text             = "✕";
                    dot.Foreground       = BrRed;
                    badgeText.Text       = "BLOCKED";
                    badgeText.Foreground = BrRed;
                    badge.Background     = new SolidColorBrush(Color.FromArgb(0x18, 0xF4, 0x43, 0x36));
                    break;
            }

            status.Text       = upstreamConn ? "●  Upstream connected" : "○  Waiting for upstream";
            status.Foreground = upstreamConn ? BrGreen : BrMuted;
        }

        // ── Click handlers ────────────────────────────────────────────────────
        private void CardA_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_aIsActive) SwitchTo(isCardA: true);
        }

        private void CardB_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_bIsActive) SwitchTo(isCardA: false);
        }

        private void SwitchTo(bool isCardA)
        {
            if (_mode is BidirectionalMode bi)
            {
                string activate   = isCardA ? bi.ChannelA : bi.ChannelB;
                string deactivate = isCardA ? bi.ChannelB : bi.ChannelA;
                _ = PostAsync(BuildJson(activate   + ":Downstream", new[] { activate   + ":Upstream"   }));
                _ = PostAsync(BuildJson(activate   + ":Upstream",   new[] { activate   + ":Downstream" }));
                _ = PostAsync(BuildJson(deactivate + ":Downstream", Array.Empty<string>()));
                _ = PostAsync(BuildJson(deactivate + ":Upstream",   Array.Empty<string>()));
            }
            else if (_mode is FanOutMode fo)
            {
                // Only the pivot source needs to change its target
                string targetChannel = isCardA ? fo.ChannelA : fo.ChannelB;
                _ = PostAsync(BuildJson(fo.PivotSource, new[] { targetChannel + ":Downstream" }));
            }
        }

        // ── Hover highlights ──────────────────────────────────────────────────
        private void CardA_MouseEnter(object sender, MouseEventArgs e)
        { if (!_aIsActive) CardA.BorderBrush = BrBlue; }
        private void CardA_MouseLeave(object sender, MouseEventArgs e)
        { if (!_aIsActive) CardA.BorderBrush = BrBlueBorder; }
        private void CardB_MouseEnter(object sender, MouseEventArgs e)
        { if (!_bIsActive) CardB.BorderBrush = BrBlue; }
        private void CardB_MouseLeave(object sender, MouseEventArgs e)
        { if (!_bIsActive) CardB.BorderBrush = BrBlueBorder; }

        // ── BLOCK ALL ─────────────────────────────────────────────────────────
        private void BtnBlockAll_Click(object sender, RoutedEventArgs e)
        {
            if (_mode is BidirectionalMode bi)
            {
                _ = PostAsync(BuildJson(bi.ChannelA + ":Downstream", Array.Empty<string>()));
                _ = PostAsync(BuildJson(bi.ChannelA + ":Upstream",   Array.Empty<string>()));
                _ = PostAsync(BuildJson(bi.ChannelB + ":Downstream", Array.Empty<string>()));
                _ = PostAsync(BuildJson(bi.ChannelB + ":Upstream",   Array.Empty<string>()));
            }
            else if (_mode is FanOutMode fo)
            {
                // Block only the outbound pivot — data stops flowing out
                _ = PostAsync(BuildJson(fo.PivotSource, Array.Empty<string>()));
            }
        }

        // ── TCP command send ──────────────────────────────────────────────────
        private async Task PostAsync(string json)
        {
            bool ok = false;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var connectTask = client.ConnectAsync("127.0.0.1", _commandPort);
                if (await Task.WhenAny(connectTask, Task.Delay(2000)) != connectTask)
                { SetCmdStatus(false, "timeout"); return; }
                await connectTask;

                var stream = client.GetStream();
                var bytes  = Encoding.UTF8.GetBytes(json + "\n");
                await stream.WriteAsync(bytes, 0, bytes.Length);
                client.Client.Shutdown(SocketShutdown.Send);

                using var reader = new StreamReader(stream, Encoding.UTF8, false, 256, leaveOpen: true);
                var response = await reader.ReadLineAsync();
                ok = response?.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                SetCmdStatus(false, msg.Length > 20 ? msg.Substring(0, 20) : msg);
                return;
            }
            SetCmdStatus(ok, ok ? "OK" : "err");
        }

        private void SetCmdStatus(bool ok, string label)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CmdStatusDot.Fill  = new SolidColorBrush(
                    ok ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xF4, 0x43, 0x36));
                CmdStatusText.Text = label;
            }));
        }

        private static string BuildJson(string from, IEnumerable<string> to)
        {
            var sb = new StringBuilder();
            sb.Append("{\"from\":\"").Append(from).Append("\",\"to\":[");
            bool first = true;
            foreach (var t in to) { if (!first) sb.Append(','); sb.Append('"').Append(t).Append('"'); first = false; }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string ChannelOf(string endpoint)
        {
            var i = endpoint.IndexOf(':');
            return i > 0 ? endpoint.Substring(0, i) : endpoint;
        }

        private static string RoleOf(string endpoint)
        {
            var i = endpoint.IndexOf(':');
            return i > 0 ? endpoint.Substring(i + 1) : string.Empty;
        }
    }
}
