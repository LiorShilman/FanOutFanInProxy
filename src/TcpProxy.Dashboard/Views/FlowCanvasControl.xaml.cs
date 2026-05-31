using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard.Views
{
    public partial class FlowCanvasControl : UserControl
    {
        // ── Canvas layout constants ───────────────────────────────────────────
        private const double CanvasW  = 520;
        private const double DsBoxW   = 120;
        private const double DsBoxH   = 44;
        private const double DsY      = 215;      // top of DS box row
        private const double PrxCenX  = 242;      // proxy center X
        private const double PrxBotY  = 162;      // proxy bottom Y

        // Upstream ↔ Proxy particle endpoints (fixed)
        private static readonly Point PtUpDown       = new(252, 56);
        private static readonly Point PtProxyUp      = new(252, 116);
        private static readonly Point PtProxyUpFanIn = new(236, 116);
        private static readonly Point PtUpFanIn      = new(236, 56);

        // ── Brushes / effects — frozen so WPF never clones them per-element ─────
        private static readonly SolidColorBrush BrFanOut  = MakeBrush(0x4F, 0xC3, 0xF7);
        private static readonly SolidColorBrush BrFanIn   = MakeBrush(0x4C, 0xAF, 0x50);
        private static readonly DropShadowEffect GlowBlue  = MakeGlow(0x4F, 0xC3, 0xF7);
        private static readonly DropShadowEffect GlowGreen = MakeGlow(0x4C, 0xAF, 0x50);

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
        private static DropShadowEffect MakeGlow(byte r, byte g, byte b)
        {
            var fx = new DropShadowEffect
                { Color = Color.FromRgb(r, g, b), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
            fx.Freeze();
            return fx;
        }

        // ── Particle ──────────────────────────────────────────────────────────
        private sealed class Particle
        {
            public Point   Start, End;
            public double  Progress, Speed;
            public Ellipse Visual = null!;
            public bool    IsBlue;          // for pool routing
        }

        // Object pools — eliminates per-frame allocation pressure
        private readonly Queue<Particle> _particlePool = new();
        private readonly Queue<Ellipse>  _bluePool     = new();
        private readonly Queue<Ellipse>  _greenPool    = new();

        private readonly List<Particle> _particles   = new();
        private readonly List<Particle> _toRemove    = new();
        private readonly Random         _particleRng = new(Guid.NewGuid().GetHashCode());

        // Max simultaneous particles — keeps the canvas readable
        private const int MaxParticles = 20;

        // ── Per-DS state ──────────────────────────────────────────────────────
        private sealed class DsState
        {
            public string Name = "";
            public bool   Active;

            // Particle endpoints
            public Point PtFanOutStart;
            public Point PtFanOutEnd;
            public Point PtFanInStart;
            public Point PtFanInEnd;

            // Visuals created in code
            public Border?           NodeBorder;
            public SolidColorBrush?  BorderColor;
            public TextBlock?        StatusText;
            public Line?             FanOutLine;
            public Line?             FanInLine;
        }

        private readonly List<DsState> _dsStates  = new();
        private string _layoutKey = "";

        // ── Routing-aware animation state ─────────────────────────────────────
        private long     _prevTxTotal;
        private long     _prevAnonRxTotal;
        private bool     _routingActive;
        private DateTime _lastTxChangeTime = DateTime.MinValue;

        private bool _firstFrame = true;

        public FlowCanvasControl()
        {
            InitializeComponent();
            Loaded   += (_, _2) => CompositionTarget.Rendering += OnFrame;
            Unloaded += (_, _2) => CompositionTarget.Rendering -= OnFrame;
        }

        // ── Public entry point ────────────────────────────────────────────────
        public void UpdateFromSnapshot(StatusSnapshot snap)
        {
            // Aggregate DS status across all channels
            // A DS is considered connected if connected in ANY channel
            var dsConnected = new Dictionary<string, bool>(StringComparer.Ordinal);

            long rxTotal    = 0;
            long txTotal    = 0;
            bool upConn     = false;

            foreach (var ch in snap.Channels)
            {
                rxTotal += ch.RxBytesTotal;
                txTotal += ch.TxBytesTotal;
                if (ch.UpstreamConnected) upConn = true;

                // Named downstream slots
                foreach (var ds in ch.Downstreams)
                {
                    if (!dsConnected.ContainsKey(ds.Name))
                        dsConnected[ds.Name] = ds.Connected;
                    else if (ds.Connected)
                        dsConnected[ds.Name] = true;
                }

                // Synthetic entry for labeled anonymous connections (e.g. "FOFI AIR" in FOFI GROUND)
                if (!string.IsNullOrEmpty(ch.DownstreamLabel))
                {
                    var lbl = ch.DownstreamLabel;
                    bool active = ch.AnonymousDownstreamCount > 0;
                    if (!dsConnected.ContainsKey(lbl))
                        dsConnected[lbl] = active;
                    else if (active)
                        dsConnected[lbl] = true;
                }
            }

            // Determine animation activity.
            // If this proxy has anonymous downstream connections (e.g. FOFI GROUND receiving from FOFI AIR),
            // use *their* RX as the signal — so BLOCK ALL in AIR stops the animation even if TX stays
            // non-zero due to traffic in the reverse direction (GCCC→FOFI AIR).
            // Otherwise (e.g. FOFI AIR with named ACCC slot), use TX as the signal.
            long anonRxTotal = 0;
            bool hasDownstreamLabel = false;
            foreach (var ch in snap.Channels)
            {
                if (!string.IsNullOrEmpty(ch.DownstreamLabel))
                {
                    hasDownstreamLabel = true;
                    anonRxTotal += ch.AnonymousRxBytesTotal;
                }
            }

            // TX delta — proxy forwarding data (fan-out signal, valid for both proxy types)
            long txDelta = Math.Max(0, txTotal - _prevTxTotal);
            _prevTxTotal = txTotal;

            // Fan-in delta — bytes arriving FROM the data source:
            //   FOFI GROUND: bytes from FOFI AIR (anonymous DS RX)
            //   FOFI AIR:    TX == forwarded ACCC data (no separate source RX available)
            long fanInDelta;
            if (hasDownstreamLabel)
            {
                fanInDelta = Math.Max(0, anonRxTotal - _prevAnonRxTotal);
                _prevAnonRxTotal = anonRxTotal;
                if (fanInDelta > 0) _lastTxChangeTime = DateTime.UtcNow;
            }
            else
            {
                fanInDelta = txDelta;
                if (txDelta > 0) _lastTxChangeTime = DateTime.UtcNow;
            }
            // allBlocked: every routing rule for THIS proxy has an empty target list.
            // After Fix 1 in RoutingEngine, blocked sources are returned as empty-To rules,
            // so Count > 0 && TrueForAll is a reliable BLOCK ALL signal.
            bool allBlocked = snap.RoutingRules.Count > 0 &&
                              snap.RoutingRules.TrueForAll(r => r.To.Count == 0);

            if (allBlocked)
            {
                _routingActive = false;
                _lastTxChangeTime = DateTime.MinValue;   // re-arms cleanly on unblock
            }
            else
            {
                _routingActive = (DateTime.UtcNow - _lastTxChangeTime).TotalMilliseconds < 2500;
            }

            // Rebuild layout only when DS names change
            var newKey = string.Join(",", new SortedDictionary<string, bool>(dsConnected).Keys);
            if (newKey != _layoutKey)
            {
                RebuildDsLayout(new List<string>(new SortedDictionary<string, bool>(dsConnected).Keys));
                _layoutKey = newKey;
            }

            // Update per-DS connected state and visuals
            foreach (var state in _dsStates)
            {
                bool conn = dsConnected.TryGetValue(state.Name, out var c) && c;
                state.Active = conn;

                var color = conn
                    ? Color.FromRgb(0x4C, 0xAF, 0x50)
                    : Color.FromRgb(0xF4, 0x43, 0x36);
                if (state.BorderColor != null)
                    state.BorderColor.Color = color;

                if (state.StatusText != null)
                {
                    state.StatusText.Text       = conn ? "● Connected" : "○ Disconnected";
                    state.StatusText.Foreground = conn ? BrFanIn : new SolidColorBrush(Colors.OrangeRed);
                }
            }

            // Upstream label: use configured label (e.g. "GCCC"), else connected channel names
            string upLabel;
            if (!string.IsNullOrEmpty(snap.UpstreamLabel))
            {
                upLabel = snap.UpstreamLabel;
            }
            else
            {
                var connNames = new System.Text.StringBuilder();
                foreach (var ch in snap.Channels)
                    if (ch.UpstreamConnected)
                    {
                        if (connNames.Length > 0) connNames.Append(" / ");
                        connNames.Append(ch.Name);
                    }
                upLabel = connNames.Length > 0 ? connNames.ToString() : "UPSTREAM";
            }
            TbUpstreamLabel.Text = upLabel;

            // Upstream status label
            TbUpstreamStatus.Text       = upConn ? "●  CONNECTED" : "○  WAITING";
            TbUpstreamStatus.Foreground = upConn ? BrFanIn : new SolidColorBrush(Colors.OrangeRed);

            long traffic = txTotal > 0 ? txTotal : rxTotal;
            TbRxProxy.Text = FmtBytes(rxTotal) + " ↓";
            TbTxProxy.Text = (allBlocked || !_routingActive) ? "BLOCKED" : FmtBytes(traffic) + " →";

            // ── Event-driven particle spawning — only when real bytes moved ─────
            // Gate on !allBlocked (local BLOCK ALL) rather than _routingActive so that
            // green and blue are independent:
            //   BLOCK in remote proxy  → fanInDelta drops to 0 naturally → green stops,
            //                            txDelta may still be > 0 → blue continues.
            //   BLOCK ALL in this proxy → allBlocked=true → both stop immediately.
            if (!allBlocked)
            {
                // Fan-in (green): data source → Proxy → Upstream
                if (fanInDelta > 0)
                {
                    foreach (var s in _dsStates)
                        if (s.Active) SpawnParticle(s.PtFanInStart, s.PtFanInEnd, BrFanIn, GlowGreen);
                    SpawnParticle(PtProxyUpFanIn, PtUpFanIn, BrFanIn, GlowGreen);
                }

                // Fan-out (blue): Upstream → Proxy → data destination
                if (txDelta > 0)
                {
                    SpawnParticle(PtUpDown, PtProxyUp, BrFanOut, GlowBlue);
                    foreach (var s in _dsStates)
                        if (s.Active) SpawnParticle(s.PtFanOutStart, s.PtFanOutEnd, BrFanOut, GlowBlue);
                }
            }
        }

        // ── OnFrame: move existing particles only — spawning is event-driven ──
        private void OnFrame(object? sender, EventArgs e)
        {
            if (_firstFrame) { _firstFrame = false; return; }

            _toRemove.Clear();
            foreach (var p in _particles)
            {
                p.Progress += p.Speed;
                if (p.Progress >= 1.0)
                    _toRemove.Add(p);
                else
                {
                    Canvas.SetLeft(p.Visual, p.Start.X + (p.End.X - p.Start.X) * p.Progress - 5);
                    Canvas.SetTop (p.Visual, p.Start.Y + (p.End.Y - p.Start.Y) * p.Progress - 5);
                }
            }
            foreach (var p in _toRemove)
            {
                ReturnParticleToPool(p);
                _particles.Remove(p);
            }
        }

        // ── Layout builder ────────────────────────────────────────────────────
        private void RebuildDsLayout(List<string> names)
        {
            // Remove old DS visuals from canvas
            foreach (var s in _dsStates)
            {
                if (s.FanOutLine  != null) FlowCanvas.Children.Remove(s.FanOutLine);
                if (s.FanInLine   != null) FlowCanvas.Children.Remove(s.FanInLine);
                if (s.NodeBorder  != null) FlowCanvas.Children.Remove(s.NodeBorder);
            }
            _dsStates.Clear();

            if (names.Count == 0) return;

            double[] cxs = CalcCenterXs(names.Count);

            for (int i = 0; i < names.Count; i++)
            {
                double cx = cxs[i];

                var state = new DsState
                {
                    Name          = names[i],
                    PtFanOutStart = new Point(PrxCenX + 4, PrxBotY),
                    PtFanOutEnd   = new Point(cx, DsY),
                    PtFanInStart  = new Point(cx, DsY),
                    PtFanInEnd    = new Point(PrxCenX - 4, PrxBotY),
                };

                // Fan-out line (proxy → DS)
                state.FanOutLine = new Line
                {
                    X1 = PrxCenX + 4, Y1 = PrxBotY,
                    X2 = cx,          Y2 = DsY,
                    Stroke = BrFanOut, StrokeThickness = 2, Opacity = 0.35
                };

                // Fan-in line (DS → proxy)
                state.FanInLine = new Line
                {
                    X1 = cx,          Y1 = DsY,
                    X2 = PrxCenX - 4, Y2 = PrxBotY,
                    Stroke = BrFanIn, StrokeThickness = 2, Opacity = 0.35
                };

                // DS node border
                var borderColor = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
                state.BorderColor = borderColor;

                var statusTb = new TextBlock
                {
                    Text      = "○ Disconnected",
                    Foreground = new SolidColorBrush(Colors.OrangeRed),
                    FontSize   = 9,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                state.StatusText = statusTb;

                var nameTb = new TextBlock
                {
                    Text       = names[i],
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var sp = new StackPanel
                {
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                sp.Children.Add(nameTb);
                sp.Children.Add(statusTb);

                state.NodeBorder = new Border
                {
                    Width           = DsBoxW,
                    Height          = DsBoxH,
                    CornerRadius    = new CornerRadius(8),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush     = borderColor,
                    Background      = new LinearGradientBrush(
                        Color.FromRgb(0x0A, 0x25, 0x10),
                        Color.FromRgb(0x05, 0x12, 0x08),
                        new Point(0, 0), new Point(0, 1)),
                    Child = sp
                };

                double boxLeft = cx - DsBoxW / 2;
                Canvas.SetLeft(state.NodeBorder, boxLeft);
                Canvas.SetTop (state.NodeBorder, DsY);

                // Lines go at the bottom of z-order (index 0), nodes after static elements
                FlowCanvas.Children.Insert(0, state.FanInLine);
                FlowCanvas.Children.Insert(0, state.FanOutLine);
                FlowCanvas.Children.Add(state.NodeBorder);

                _dsStates.Add(state);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static double[] CalcCenterXs(int n)
        {
            const double leftEdge  = 80;   // center X of leftmost box
            const double rightEdge = 440;  // center X of rightmost box
            var xs = new double[n];
            if (n == 1)
            {
                xs[0] = CanvasW / 2;
                return xs;
            }
            for (int i = 0; i < n; i++)
                xs[i] = leftEdge + i * (rightEdge - leftEdge) / (n - 1);
            return xs;
        }

        private void SpawnParticle(Point start, Point end, SolidColorBrush brush, DropShadowEffect glow)
        {
            if (_particles.Count >= MaxParticles) return;

            bool isBlue = ReferenceEquals(brush, BrFanOut);
            var pool    = isBlue ? _bluePool : _greenPool;
            Ellipse dot;
            if (pool.Count > 0)
            {
                dot = pool.Dequeue();
            }
            else
            {
                dot = new Ellipse { Width = 10, Height = 10, Fill = brush, Effect = glow };
            }
            Canvas.SetLeft(dot, start.X - 5);
            Canvas.SetTop (dot, start.Y - 5);
            FlowCanvas.Children.Add(dot);

            Particle p = _particlePool.Count > 0 ? _particlePool.Dequeue() : new Particle();
            p.Start    = start;
            p.End      = end;
            p.Progress = 0;
            p.Speed    = 0.020 + _particleRng.NextDouble() * 0.012;
            p.Visual   = dot;
            p.IsBlue   = isBlue;
            _particles.Add(p);
        }

        private void ReturnParticleToPool(Particle p)
        {
            FlowCanvas.Children.Remove(p.Visual);
            var pool = p.IsBlue ? _bluePool : _greenPool;
            if (pool.Count < MaxParticles) pool.Enqueue(p.Visual);
            p.Visual = null!;
            if (_particlePool.Count < MaxParticles) _particlePool.Enqueue(p);
        }

        private static string FmtBytes(long b)
        {
            if (b >= 1048576) return $"{b / 1048576.0:F1}MB";
            if (b >= 1024)    return $"{b / 1024.0:F1}KB";
            return $"{b}B";
        }
    }
}
