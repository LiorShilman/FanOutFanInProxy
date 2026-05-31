using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace TcpProxy.Dashboard.Views
{
    public partial class ConfigCanvasControl : UserControl
    {
        // ── Visual constants ──────────────────────────────────────────────────
        private const double NodeW  = 216;
        private const double NodeH  = 112;
        private const double PortR  = 8;     // port circle radius
        private const double PortD  = 16;    // port circle diameter

        private static readonly SolidColorBrush BrNodeBg     = Br("#FF0F0F1E");
        private static readonly SolidColorBrush BrNodeBorder = Br("#FF2A2A4A");
        private static readonly SolidColorBrush BrNodeSel    = Br("#FF4FC3F7");
        private static readonly SolidColorBrush BrPortUS     = Br("#FF4FC3F7"); // left  (upstream role)
        private static readonly SolidColorBrush BrPortDS     = Br("#FF4CAF50"); // right (downstream role)
        private static readonly SolidColorBrush BrPortHover  = Br("#FFFFB74D");
        private static readonly SolidColorBrush BrArrowBlue  = Br("#FF4FC3F7");
        private static readonly SolidColorBrush BrArrowGreen = Br("#FF4CAF50");
        private static readonly SolidColorBrush BrArrowOrange= Br("#FFFFB74D");
        private static readonly SolidColorBrush BrText       = Br("#FFCCCCDD");
        private static readonly SolidColorBrush BrMuted      = Br("#FF666688");
        private static readonly SolidColorBrush BrHeader     = Br("#FF111120");
        private static readonly SolidColorBrush BrSep        = Br("#FF1E1E3A");

        // ── Data models ───────────────────────────────────────────────────────

        private sealed class ChannelNode
        {
            public string Name        = "CH";
            public string UpHost      = "127.0.0.1";
            public int    UpPort      = 28000;
            public string UpProtocol  = "Tcp";
            public string UpMode      = "Client";
            public string DsListenIp  = "127.0.0.1";
            public int    DsPort      = 28100;
            public string DsProtocol  = "Tcp";
            public string DsMode      = "Server";

            public double X, Y;

            // Visual elements owned by this node
            public Border  Body     = null!;
            public Ellipse LeftPort = null!;   // Upstream role
            public Ellipse RightPort= null!;   // Downstream role
            public TextBlock NameTb = null!;
            public TextBlock UpTb   = null!;
            public TextBlock DsTb   = null!;
        }

        private sealed class DsSlot
        {
            public string Name   = "DS";
            public int    MinMs  = 100;
            public int    MaxMs  = 700;
        }

        private sealed class RoutingArrow
        {
            public ChannelNode From          = null!;
            public bool        FromIsUpstream;  // true = left port
            public ChannelNode To            = null!;
            public bool        ToIsUpstream;
            public WpfPath     Line          = null!;
            public Polygon     Head          = null!;
        }

        // ── State ─────────────────────────────────────────────────────────────

        private readonly List<ChannelNode>  _nodes  = new();
        private readonly List<DsSlot>       _slots  = new();
        private readonly List<RoutingArrow> _arrows = new();

        // Lookup maps for hit testing
        private readonly Dictionary<UIElement, ChannelNode>                       _bodyMap = new();
        private readonly Dictionary<UIElement, (ChannelNode n, bool isUpstream)> _portMap = new();
        private readonly Dictionary<UIElement, RoutingArrow>                      _arrowMap= new();

        // Drag state
        private enum DragMode { None, MoveNode, DrawArrow }
        private DragMode    _drag       = DragMode.None;
        private ChannelNode? _dragNode;
        private Point        _dragOffset;

        // Arrow-draw state
        private ChannelNode? _arrowSrcNode;
        private bool         _arrowSrcIsUp;
        private Point        _arrowSrcPt;
        private WpfPath?     _tempArrowLine;

        // Selection
        private object? _selected; // ChannelNode | RoutingArrow | null

        // Port hover tracking (to restore color on leave)
        private Ellipse? _hoveredPort;

        // Next default port numbers
        private int _nextUpPort = 28000;
        private int _nextDsPort = 28100;

        public ConfigCanvasControl()
        {
            InitializeComponent();
            StyleToolbarButtons();
            Loaded += (_, _2) => { Focus(); ShowGlobalPanel(); };
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void StyleToolbarButtons()
        {
            foreach (var btn in new[] { BtnAddChannel, BtnAddDs, BtnLoad })
                ApplyBtnStyle(btn, "#FF4FC3F7");
            ApplyBtnStyle(BtnDelete, "#FFF44336");
            ApplyBtnStyle(BtnSave,   "#FF4CAF50");
        }

        private static void ApplyBtnStyle(Button btn, string hexColor)
        {
            var c   = (Color)ColorConverter.ConvertFromString(hexColor);
            var br  = new SolidColorBrush(c);
            btn.Foreground      = br;
            btn.Background      = Brushes.Transparent;
            btn.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
            btn.BorderThickness = new Thickness(1);
            btn.Padding         = new Thickness(12, 4, 12, 4);
            btn.FontSize        = 11;
            btn.FontWeight      = FontWeights.SemiBold;
            btn.Cursor          = Cursors.Hand;
            btn.Template        = BuildBtnTemplate(c);
        }

        private static ControlTemplate BuildBtnTemplate(Color c)
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty,      Brushes.Transparent);
            bd.SetValue(Border.BorderBrushProperty,     new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.PaddingProperty,         new Thickness(12, 4, 12, 4));
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty,   VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B))));
            t.Triggers.Add(hoverTrigger);
            return t;
        }

        // ── Add nodes ─────────────────────────────────────────────────────────

        private void AddChannel_Click(object sender, RoutedEventArgs e)
        {
            double x = 80 + _nodes.Count * 40;
            double y = 80 + _nodes.Count * 30;
            CreateChannelNode(x, y);
        }

        private void AddDs_Click(object sender, RoutedEventArgs e)
        {
            var slot = new DsSlot
            {
                Name  = $"DS{(char)('A' + _slots.Count)}",
                MinMs = 100,
                MaxMs = 700
            };
            _slots.Add(slot);
            if (_selected == null) ShowGlobalPanel();
        }

        private ChannelNode CreateChannelNode(double x, double y)
        {
            var n = new ChannelNode
            {
                Name   = $"CH{_nodes.Count + 1}",
                UpPort = _nextUpPort,
                DsPort = _nextDsPort,
                X = x, Y = y
            };
            _nextUpPort += 1;
            _nextDsPort += 1;

            // ── Body ──────────────────────────────────────────────────────────
            var nameTb = new TextBlock
            {
                Text       = n.Name,
                Foreground = BrArrowBlue,
                FontSize   = 13,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 6)
            };

            var upTb = new TextBlock { FontSize = 10, Foreground = BrText, Margin = new Thickness(0, 2, 0, 0) };
            var dsTb = new TextBlock { FontSize = 10, Foreground = BrText, Margin = new Thickness(0, 2, 0, 0) };

            var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            sp.Children.Add(nameTb);
            sp.Children.Add(new Border { Height = 1, Background = BrSep, Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(upTb);
            sp.Children.Add(dsTb);

            var body = new Border
            {
                Width           = NodeW,
                Height          = NodeH,
                Background      = BrNodeBg,
                BorderBrush     = BrNodeBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius    = new CornerRadius(8),
                Child           = sp
            };
            body.MouseDown += (s, me) => { me.Handled = true; Body_MouseDown(n, me); };

            n.NameTb = nameTb;
            n.UpTb   = upTb;
            n.DsTb   = dsTb;
            n.Body   = body;

            // ── Ports ─────────────────────────────────────────────────────────
            n.LeftPort  = MakePort(n, true);
            n.RightPort = MakePort(n, false);

            // ── Place on canvas ───────────────────────────────────────────────
            _bodyMap[body]         = n;
            _portMap[n.LeftPort]   = (n, true);
            _portMap[n.RightPort]  = (n, false);
            _nodes.Add(n);

            Panel.SetZIndex(n.LeftPort,  2);
            Panel.SetZIndex(n.RightPort, 2);
            Panel.SetZIndex(body,        1);

            MainCanvas.Children.Add(n.LeftPort);
            MainCanvas.Children.Add(n.RightPort);
            MainCanvas.Children.Add(body);

            PositionNode(n);
            UpdateNodeLabels(n);
            return n;
        }

        private Ellipse MakePort(ChannelNode n, bool isUpstream)
        {
            var port = new Ellipse
            {
                Width  = PortD,
                Height = PortD,
                Fill   = isUpstream ? BrPortUS : BrPortDS,
                Stroke = Br("#FF0A0A14"),
                StrokeThickness = 1.5,
                Cursor = Cursors.Cross,
                Effect = new DropShadowEffect
                {
                    Color = isUpstream ? Color.FromRgb(0x4F, 0xC3, 0xF7) : Color.FromRgb(0x4C, 0xAF, 0x50),
                    BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7
                }
            };
            port.MouseDown  += (s, me) => { me.Handled = true; Port_MouseDown(n, isUpstream, me); };
            port.MouseEnter += (_, _2) => { _hoveredPort = port; port.Fill = BrPortHover; };
            port.MouseLeave += (_, _2) =>
            {
                if (_hoveredPort == port) _hoveredPort = null;
                port.Fill = isUpstream ? BrPortUS : BrPortDS;
            };
            return port;
        }

        private void PositionNode(ChannelNode n)
        {
            Canvas.SetLeft(n.Body,      n.X);
            Canvas.SetTop (n.Body,      n.Y);
            Canvas.SetLeft(n.LeftPort,  n.X - PortR);
            Canvas.SetTop (n.LeftPort,  n.Y + NodeH / 2 - PortR);
            Canvas.SetLeft(n.RightPort, n.X + NodeW - PortR);
            Canvas.SetTop (n.RightPort, n.Y + NodeH / 2 - PortR);
        }

        private void UpdateNodeLabels(ChannelNode n)
        {
            n.NameTb.Text = n.Name;
            n.UpTb.Text   = $"↑ {n.UpProtocol} {n.UpMode}  {n.UpHost}:{n.UpPort}";
            n.DsTb.Text   = $"↓ {n.DsProtocol} {n.DsMode}  {n.DsListenIp}:{n.DsPort}";
        }

        // ── Port / arrow coordinates ──────────────────────────────────────────

        private static Point PortCenter(ChannelNode n, bool isUpstream)
            => isUpstream
                ? new Point(n.X,        n.Y + NodeH / 2)
                : new Point(n.X + NodeW, n.Y + NodeH / 2);

        // ── Canvas mouse events ───────────────────────────────────────────────

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Click on canvas background → deselect
            Select(null);
            Focus();
        }

        private void Body_MouseDown(ChannelNode n, MouseButtonEventArgs e)
        {
            Select(n);
            _drag       = DragMode.MoveNode;
            _dragNode   = n;
            _dragOffset = e.GetPosition(MainCanvas) - new Vector(n.X, n.Y);
            MainCanvas.CaptureMouse();
        }

        private void Port_MouseDown(ChannelNode n, bool isUpstream, MouseButtonEventArgs e)
        {
            _drag          = DragMode.DrawArrow;
            _arrowSrcNode  = n;
            _arrowSrcIsUp  = isUpstream;
            _arrowSrcPt    = PortCenter(n, isUpstream);

            // Temp arrow line
            _tempArrowLine = new WpfPath
            {
                Stroke          = Br("#88FFFFFF"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            Panel.SetZIndex(_tempArrowLine, 0);
            MainCanvas.Children.Add(_tempArrowLine);
            MainCanvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(MainCanvas);

            if (_drag == DragMode.MoveNode && _dragNode != null)
            {
                _dragNode.X = Math.Max(PortR, pos.X - _dragOffset.X);
                _dragNode.Y = Math.Max(PortR, pos.Y - _dragOffset.Y);
                PositionNode(_dragNode);
                UpdateAllArrows();
            }
            else if (_drag == DragMode.DrawArrow && _tempArrowLine != null)
            {
                // Draw temp line from source port to current mouse
                _tempArrowLine.Data = BuildBezierGeometry(
                    _arrowSrcPt, !_arrowSrcIsUp,   // exit right if DS port
                    pos, true,                       // enter from left (arbitrary)
                    null, null);
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            MainCanvas.ReleaseMouseCapture();

            if (_drag == DragMode.DrawArrow)
            {
                // Remove temp arrow
                if (_tempArrowLine != null)
                {
                    MainCanvas.Children.Remove(_tempArrowLine);
                    _tempArrowLine = null;
                }

                // Check if we released over a port
                var pos = e.GetPosition(MainCanvas);
                var hit = MainCanvas.InputHitTest(pos) as UIElement;
                if (hit != null && _portMap.TryGetValue(hit, out var tgt) && tgt.n != _arrowSrcNode)
                {
                    // Avoid duplicate connections
                    bool dup = _arrows.Any(a =>
                        a.From == _arrowSrcNode && a.FromIsUpstream == _arrowSrcIsUp &&
                        a.To   == tgt.n         && a.ToIsUpstream   == tgt.isUpstream);
                    if (!dup)
                        CreateArrow(_arrowSrcNode!, _arrowSrcIsUp, tgt.n, tgt.isUpstream);
                }
            }

            _drag     = DragMode.None;
            _dragNode = null;
            _arrowSrcNode = null;
        }

        // ── Arrow management ──────────────────────────────────────────────────

        private void CreateArrow(ChannelNode from, bool fromIsUp, ChannelNode to, bool toIsUp)
        {
            // Choose color: same channel same-dir = blue/green; cross-channel = orange
            SolidColorBrush color;
            if (from == to)
                color = fromIsUp ? BrArrowBlue : BrArrowGreen;
            else if (fromIsUp && !toIsUp)
                color = BrArrowBlue;
            else if (!fromIsUp && toIsUp)
                color = BrArrowGreen;
            else
                color = BrArrowOrange;

            var line = new WpfPath
            {
                Stroke          = color,
                StrokeThickness = 2,
                StrokeEndLineCap= PenLineCap.Round
            };

            var head = new Polygon
            {
                Points = new PointCollection { new(0, 0), new(-11, 5), new(-11, -5) },
                Fill   = color
            };

            var arrow = new RoutingArrow
            {
                From          = from,
                FromIsUpstream= fromIsUp,
                To            = to,
                ToIsUpstream  = toIsUp,
                Line          = line,
                Head          = head
            };

            line.MouseDown += (_, me) => { me.Handled = true; Select(arrow); };
            head.MouseDown += (_, me) => { me.Handled = true; Select(arrow); };

            _arrowMap[line] = arrow;
            _arrowMap[head] = arrow;
            _arrows.Add(arrow);

            Panel.SetZIndex(line, 0);
            Panel.SetZIndex(head, 0);
            MainCanvas.Children.Insert(0, line);
            MainCanvas.Children.Insert(0, head);

            UpdateArrow(arrow);
        }

        private void UpdateArrow(RoutingArrow a)
        {
            Point from = PortCenter(a.From, a.FromIsUpstream);
            Point to   = PortCenter(a.To,   a.ToIsUpstream);

            // fromIsUpstream=true → LEFT port → exit LEFT; false → RIGHT port → exit RIGHT
            bool fromExitsRight = !a.FromIsUpstream;
            // toIsUpstream=true  → LEFT port → enter from LEFT; false → RIGHT → enter from RIGHT
            bool toEntersRight  = !a.ToIsUpstream;

            // For self-loops: from and to are on the same node
            Point? selfCtrl = (a.From == a.To)
                ? (fromExitsRight
                    ? new Point(a.From.X + NodeW / 2, a.From.Y - 80)   // arch above
                    : new Point(a.From.X + NodeW / 2, a.From.Y + NodeH + 60)) // arch below
                : (Point?)null;

            a.Line.Data = BuildBezierGeometry(from, fromExitsRight, to, toEntersRight, selfCtrl, selfCtrl);

            // Arrowhead angle: direction of last tangent (cy2 → to)
            double cx2, cy2;
            if (selfCtrl.HasValue)
            {
                cx2 = selfCtrl.Value.X;
                cy2 = selfCtrl.Value.Y;
            }
            else
            {
                double off = 90;
                cx2 = to.X + (toEntersRight ? off : -off);
                cy2 = to.Y;
            }

            double angle = Math.Atan2(to.Y - cy2, to.X - cx2) * 180.0 / Math.PI;
            a.Head.RenderTransform = new RotateTransform(angle);
            Canvas.SetLeft(a.Head, to.X);
            Canvas.SetTop (a.Head, to.Y);
        }

        private static Geometry BuildBezierGeometry(
            Point from, bool fromExitsRight,
            Point to,   bool toEntersRight,
            Point? ctrl1Override, Point? ctrl2Override)
        {
            double off = 90;
            double cx1 = ctrl1Override?.X ?? (from.X + (fromExitsRight ?  off : -off));
            double cy1 = ctrl1Override?.Y ?? from.Y;
            double cx2 = ctrl2Override?.X ?? (to.X   + (toEntersRight  ?  off : -off));
            double cy2 = ctrl2Override?.Y ?? to.Y;

            return Geometry.Parse(
                $"M {F(from.X)},{F(from.Y)} " +
                $"C {F(cx1)},{F(cy1)} {F(cx2)},{F(cy2)} {F(to.X)},{F(to.Y)}");
        }

        private void UpdateAllArrows()
        {
            foreach (var a in _arrows) UpdateArrow(a);
        }

        // ── Selection ─────────────────────────────────────────────────────────

        private void Select(object? target)
        {
            // Clear previous selection highlight
            if (_selected is ChannelNode old)
                old.Body.BorderBrush = BrNodeBorder;
            else if (_selected is RoutingArrow oa)
            {
                oa.Line.StrokeThickness = 2;
                oa.Head.Opacity = 1;
            }

            _selected = target;
            BtnDelete.IsEnabled = target != null;

            if (target is ChannelNode n)
            {
                n.Body.BorderBrush = BrNodeSel;
                ShowChannelPanel(n);
            }
            else if (target is RoutingArrow a)
            {
                a.Line.StrokeThickness = 3;
                ShowArrowPanel(a);
            }
            else
            {
                ShowGlobalPanel();
            }
        }

        // ── Delete ────────────────────────────────────────────────────────────

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void UserControl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Back) DeleteSelected();
        }

        private void DeleteSelected()
        {
            if (_selected is ChannelNode n)
            {
                // Remove all arrows touching this node
                var toRemove = _arrows.Where(a => a.From == n || a.To == n).ToList();
                foreach (var a in toRemove) RemoveArrow(a);

                MainCanvas.Children.Remove(n.Body);
                MainCanvas.Children.Remove(n.LeftPort);
                MainCanvas.Children.Remove(n.RightPort);
                _bodyMap.Remove(n.Body);
                _portMap.Remove(n.LeftPort);
                _portMap.Remove(n.RightPort);
                _nodes.Remove(n);
            }
            else if (_selected is RoutingArrow a)
            {
                RemoveArrow(a);
            }

            _selected = null;
            BtnDelete.IsEnabled = false;
            ShowGlobalPanel();
        }

        private void RemoveArrow(RoutingArrow a)
        {
            MainCanvas.Children.Remove(a.Line);
            MainCanvas.Children.Remove(a.Head);
            _arrowMap.Remove(a.Line);
            _arrowMap.Remove(a.Head);
            _arrows.Remove(a);
        }

        // ── Properties panel helpers ──────────────────────────────────────────

        private void ClearProps(string header)
        {
            PropsPanel.Children.Clear();
            TbPropsHeader.Text = header;
        }

        private void AddHeader(string text)
        {
            PropsPanel.Children.Add(new TextBlock
            {
                Text       = text,
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = BrMuted,
                Margin     = new Thickness(0, 14, 0, 4)
            });
        }

        private void AddSep()
        {
            PropsPanel.Children.Add(new Border
            {
                Height     = 1,
                Background = BrSep,
                Margin     = new Thickness(0, 10, 0, 4)
            });
        }

        private void AddField(string label, string value, Action<string> onChange, bool readOnly = false)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            sp.Children.Add(new TextBlock
            {
                Text       = label,
                FontSize   = 9,
                Foreground = BrMuted
            });
            var tb = new TextBox
            {
                Text            = value,
                FontSize        = 11,
                Foreground      = BrText,
                Background      = Br("#FF0C0C1A"),
                BorderBrush     = BrSep,
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 4, 6, 4),
                Margin          = new Thickness(0, 2, 0, 0),
                IsReadOnly      = readOnly
            };
            if (!readOnly)
                tb.TextChanged += (_, _2) => onChange(tb.Text);
            sp.Children.Add(tb);
            PropsPanel.Children.Add(sp);
        }

        private void AddCombo(string label, string[] options, string selected, Action<string> onChange)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            sp.Children.Add(new TextBlock
            {
                Text       = label,
                FontSize   = 9,
                Foreground = BrMuted
            });
            var cb = new ComboBox
            {
                FontSize        = 11,
                Foreground      = BrText,
                Background      = Br("#FF0C0C1A"),
                BorderBrush     = BrSep,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 2, 0, 0)
            };
            foreach (var o in options) cb.Items.Add(o);
            cb.SelectedItem = options.Contains(selected) ? selected : options[0];
            cb.SelectionChanged += (_, _2) =>
            {
                if (cb.SelectedItem is string s) onChange(s);
            };
            sp.Children.Add(cb);
            PropsPanel.Children.Add(sp);
        }

        private void AddButton(string text, Action onClick, string hexColor = "#FF4FC3F7")
        {
            var c   = (Color)ColorConverter.ConvertFromString(hexColor);
            var btn = new Button
            {
                Content         = text,
                FontSize        = 10,
                FontWeight      = FontWeights.SemiBold,
                Foreground      = new SolidColorBrush(c),
                Background      = Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(10, 4, 10, 4),
                Margin          = new Thickness(0, 10, 0, 0),
                Cursor          = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Template        = BuildBtnTemplate(c)
            };
            btn.Click += (_, _2) => onClick();
            PropsPanel.Children.Add(btn);
        }

        // ── Properties panels ─────────────────────────────────────────────────

        private void ShowChannelPanel(ChannelNode n)
        {
            ClearProps($"PROXY CHANNEL — {n.Name}");

            AddField("Name", n.Name, v =>
            {
                n.Name = v;
                UpdateNodeLabels(n);
                if (_selected == n) TbPropsHeader.Text = $"PROXY CHANNEL — {n.Name}";
            });

            AddSep();
            AddHeader("UPSTREAM — מקור הנתונים");
            AddCombo("Protocol", new[] { "Tcp", "Udp" }, n.UpProtocol, v =>
            {
                n.UpProtocol = v;
                UpdateNodeLabels(n);
            });
            AddCombo("Mode", new[] { "Client", "Server" }, n.UpMode, v =>
            {
                n.UpMode = v;
                UpdateNodeLabels(n);
            });
            AddField("Host", n.UpHost, v =>
            {
                n.UpHost = v;
                UpdateNodeLabels(n);
            });
            AddField("Port", n.UpPort.ToString(), v =>
            {
                if (int.TryParse(v, out var p)) { n.UpPort = p; UpdateNodeLabels(n); }
            });

            AddSep();
            AddHeader("DOWNSTREAM — יעד הנתונים");
            AddCombo("Protocol", new[] { "Tcp", "Udp" }, n.DsProtocol, v =>
            {
                n.DsProtocol = v;
                UpdateNodeLabels(n);
            });
            AddCombo("Mode", new[] { "Server", "Client" }, n.DsMode, v =>
            {
                n.DsMode = v;
                UpdateNodeLabels(n);
            });
            AddField("Listen IP", n.DsListenIp, v =>
            {
                n.DsListenIp = v;
                UpdateNodeLabels(n);
            });
            AddField("Port", n.DsPort.ToString(), v =>
            {
                if (int.TryParse(v, out var p)) { n.DsPort = p; UpdateNodeLabels(n); }
            });

            AddSep();
            AddButton("DELETE CHANNEL", () =>
            {
                Select(n);
                DeleteSelected();
            }, "#FFF44336");
        }

        private void ShowArrowPanel(RoutingArrow a)
        {
            string fromRole = a.FromIsUpstream ? "Upstream" : "Downstream";
            string toRole   = a.ToIsUpstream   ? "Upstream" : "Downstream";
            ClearProps("ROUTING RULE");

            AddHeader("ROUTING RULE");
            AddField("From", $"{a.From.Name}:{fromRole}", _ => { }, readOnly: true);
            AddField("To",   $"{a.To.Name}:{toRole}",    _ => { }, readOnly: true);

            AddSep();
            AddButton("DELETE RULE", () =>
            {
                Select(a);
                DeleteSelected();
            }, "#FFF44336");

            PropsPanel.Children.Add(new TextBlock
            {
                Text       = "Drag from a port ● to another port\nto create routing rules.",
                FontSize   = 10,
                Foreground = BrMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 16, 0, 0)
            });
        }

        private void ShowGlobalPanel()
        {
            ClearProps("DS SIMULATORS");

            AddHeader("סימולטורי DS");
            AddButton("+ הוסף סימולטור", () =>
            {
                _slots.Add(new DsSlot { Name = $"DS{(char)('A' + _slots.Count)}" });
                ShowGlobalPanel();
            });

            // Column headers
            if (_slots.Any())
            {
                var hdr = new Grid { Margin = new Thickness(0, 6, 0, 2) };
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                void HdrTb(int col, string txt)
                {
                    var tb = new TextBlock { Text = txt, FontSize = 8, Foreground = BrMuted };
                    Grid.SetColumn(tb, col); hdr.Children.Add(tb);
                }
                HdrTb(0, "שם");
                HdrTb(1, "מינ׳ (ms)");
                HdrTb(2, "מקס׳ (ms)");
                PropsPanel.Children.Add(hdr);
            }

            foreach (var slot in _slots.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tName = MakeSlotBox(slot.Name,            v => slot.Name  = v);
                var tMin  = MakeSlotBox(slot.MinMs.ToString(), v => { if (int.TryParse(v, out var i)) slot.MinMs = i; });
                var tMax  = MakeSlotBox(slot.MaxMs.ToString(), v => { if (int.TryParse(v, out var i)) slot.MaxMs = i; });

                var delBtn = new Button
                {
                    Content         = "×",
                    FontSize        = 13,
                    Foreground      = Br("#FFF44336"),
                    Background      = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor          = Cursors.Hand,
                    Padding         = new Thickness(4, 0, 4, 0),
                    Margin          = new Thickness(4, 0, 0, 0)
                };
                var captured = slot;
                delBtn.Click += (_, _2) => { _slots.Remove(captured); ShowGlobalPanel(); };

                Grid.SetColumn(tName,  0); row.Children.Add(tName);
                Grid.SetColumn(tMin,   1); row.Children.Add(tMin);
                Grid.SetColumn(tMax,   2); row.Children.Add(tMax);
                Grid.SetColumn(delBtn, 3); row.Children.Add(delBtn);

                PropsPanel.Children.Add(row);
            }

            AddSep();
            AddHelpPanel();
        }

        private void AddHelpPanel()
        {
            AddHeader("מדריך מהיר");

            void HelpSection(string title, string body, string color = "#FF9E9E9E")
            {
                PropsPanel.Children.Add(new TextBlock
                {
                    Text          = title,
                    FontSize      = 9,
                    FontWeight    = FontWeights.Bold,
                    Foreground    = Br(color),
                    Margin        = new Thickness(0, 8, 0, 2),
                    FlowDirection = FlowDirection.RightToLeft,
                    TextAlignment = TextAlignment.Right,
                    TextWrapping  = TextWrapping.Wrap
                });
                PropsPanel.Children.Add(new TextBlock
                {
                    Text          = body,
                    FontSize      = 9,
                    Foreground    = BrMuted,
                    TextWrapping  = TextWrapping.Wrap,
                    LineHeight    = 15,
                    FlowDirection = FlowDirection.RightToLeft,
                    TextAlignment = TextAlignment.Right
                });
            }

            HelpSection("PROXY CHANNEL — ערוץ פרוקסי", "#FF4FC3F7");
            PropsPanel.Children.Add(new TextBlock
            {
                Text          = "מייצג ערוץ אחד בפרוקסי. כל ערוץ מגדיר:\n" +
                                "  UPSTREAM — מקור הנתונים (לקוח מתחבר / שרת שמתחברים אליו)\n" +
                                "  DOWNSTREAM — יעד הנתונים (לאן הפרוקסי מעביר)",
                FontSize      = 9,
                Foreground    = BrMuted,
                TextWrapping  = TextWrapping.Wrap,
                LineHeight    = 15,
                FlowDirection = FlowDirection.RightToLeft,
                TextAlignment = TextAlignment.Right
            });

            HelpSection("DS SIMULATOR — סימולטור DS", "#FF4CAF50");
            PropsPanel.Children.Add(new TextBlock
            {
                Text          = "תוכנה שמדמה קצה DOWNSTREAM.\n" +
                                "שם — מזהה ייחודי\n" +
                                "מינ׳/מקס׳ — מרווח שליחה אקראי (ms)",
                FontSize      = 9,
                Foreground    = BrMuted,
                TextWrapping  = TextWrapping.Wrap,
                LineHeight    = 15,
                FlowDirection = FlowDirection.RightToLeft,
                TextAlignment = TextAlignment.Right
            });

            HelpSection("פעולות בקנבס", "");
            PropsPanel.Children.Add(new TextBlock
            {
                Text          = "• + PROXY CHANNEL — הוסף ערוץ חדש\n" +
                                "• גרור קופסה — הזז אותה\n" +
                                "• גרור מנקודה ● לנקודה אחרת — צור חיבור\n" +
                                "• לחץ על קופסה/חץ — ערוך מאפיינים\n" +
                                "• Delete — מחק אלמנט נבחר\n" +
                                "• SAVE CONFIG — שמור את כל קבצי הקונפיג",
                FontSize      = 9,
                Foreground    = BrMuted,
                TextWrapping  = TextWrapping.Wrap,
                LineHeight    = 15,
                FlowDirection = FlowDirection.RightToLeft,
                TextAlignment = TextAlignment.Right
            });
        }

        private static TextBox MakeSlotBox(string text, Action<string> onChange)
        {
            var tb = new TextBox
            {
                Text            = text,
                FontSize        = 10,
                Foreground      = BrText,
                Background      = Br("#FF0C0C1A"),
                BorderBrush     = BrSep,
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(4, 3, 4, 3),
                Margin          = new Thickness(0, 0, 3, 0)
            };
            tb.TextChanged += (_, _2) => onChange(tb.Text);
            return tb;
        }

        // ── Load YAML ─────────────────────────────────────────────────────────

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Open proxy.yaml",
                Filter = "YAML files (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var text = File.ReadAllText(dlg.FileName);
                LoadFromYamlText(text);
                ShowGlobalPanel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse YAML:\n{ex.Message}", "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadFromYamlText(string yaml)
        {
            // Clear canvas
            foreach (var n in _nodes.ToList())
            {
                MainCanvas.Children.Remove(n.Body);
                MainCanvas.Children.Remove(n.LeftPort);
                MainCanvas.Children.Remove(n.RightPort);
            }
            foreach (var a in _arrows.ToList())
            {
                MainCanvas.Children.Remove(a.Line);
                MainCanvas.Children.Remove(a.Head);
            }
            _nodes.Clear(); _arrows.Clear(); _slots.Clear();
            _bodyMap.Clear(); _portMap.Clear(); _arrowMap.Clear();

            // Minimal line-by-line parser for the known proxy.yaml format
            var lines = yaml.Split('\n');
            ChannelNode? cur = null;
            bool inChannels = false, inDownstreams = false, inRules = false;
            bool inUpstream = false, inDownstream = false;
            double xOff = 80;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                var trim = line.TrimStart();

                if (trim.StartsWith("channels:"))            { inChannels = true; inDownstreams = false; inRules = false; continue; }
                if (trim.StartsWith("downstreams:"))         { inChannels = false; inDownstreams = true; inRules = false; cur = null; continue; }
                if (trim.StartsWith("rules:"))               { inChannels = false; inDownstreams = false; inRules = true; continue; }
                if (trim.StartsWith("reconnect:") || trim.StartsWith("queue:") || trim.StartsWith("logging:"))
                    { inChannels = false; inDownstreams = false; inRules = false; continue; }

                if (inChannels)
                {
                    if (trim.StartsWith("- name:"))
                    {
                        string name = Val(trim);
                        cur = CreateChannelNode(xOff, 80);
                        cur.Name = name;
                        UpdateNodeLabels(cur);
                        xOff += NodeW + 60;
                        inUpstream = false; inDownstream = false;
                    }
                    else if (cur != null && trim.StartsWith("upstream:"))   { inUpstream = true; inDownstream = false; }
                    else if (cur != null && trim.StartsWith("downstream:")) { inUpstream = false; inDownstream = true; }
                    else if (cur != null && inUpstream)
                    {
                        if (trim.StartsWith("protocol:")) cur.UpProtocol = Val(trim);
                        else if (trim.StartsWith("mode:")) cur.UpMode     = Capitalize(Val(trim));
                        else if (trim.StartsWith("host:")) cur.UpHost     = Val(trim).Trim('"');
                        else if (trim.StartsWith("port:") && int.TryParse(Val(trim), out var p)) cur.UpPort = p;
                        UpdateNodeLabels(cur);
                    }
                    else if (cur != null && inDownstream)
                    {
                        if (trim.StartsWith("protocol:")) cur.DsProtocol  = Val(trim);
                        else if (trim.StartsWith("mode:"))      cur.DsMode      = Capitalize(Val(trim));
                        else if (trim.StartsWith("listenIp:"))  cur.DsListenIp  = Val(trim).Trim('"');
                        else if (trim.StartsWith("port:") && int.TryParse(Val(trim), out var p)) cur.DsPort = p;
                        UpdateNodeLabels(cur);
                    }
                }
                else if (inDownstreams && trim.StartsWith("- name:"))
                {
                    _slots.Add(new DsSlot { Name = Val(trim) });
                }
                else if (inDownstreams && _slots.Count > 0)
                {
                    var last = _slots[_slots.Count - 1];
                    if (trim.StartsWith("minIntervalMs:") && int.TryParse(Val(trim), out var mn)) last.MinMs = mn;
                    else if (trim.StartsWith("maxIntervalMs:") && int.TryParse(Val(trim), out var mx)) last.MaxMs = mx;
                }
                else if (inRules)
                {
                    if (trim.StartsWith("- from:"))
                    {
                        // next line(s) will have "to:"
                    }
                    // Arrow creation handled via "from:" + "to:" — skip complex multi-line parsing;
                    // user can draw arrows manually for loaded configs
                }
            }
        }

        private static string Val(string line)
        {
            var idx = line.IndexOf(':');
            return idx < 0 ? line : line.Substring(idx + 1).Trim().Trim('"');
        }

        private static string Capitalize(string s)
            => s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1).ToLower();

        // ── Save YAML ─────────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_nodes.Any())
            {
                MessageBox.Show("Add at least one channel node before saving.", "Nothing to save",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title      = "Save proxy.yaml",
                FileName   = "proxy.yaml",
                Filter     = "YAML files (*.yaml)|*.yaml|All files (*.*)|*.*",
                DefaultExt = "yaml"
            };
            if (dlg.ShowDialog() != true) return;

            string dir = System.IO.Path.GetDirectoryName(dlg.FileName)!;

            try
            {
                File.WriteAllText(dlg.FileName, GenerateProxyYaml(), Encoding.UTF8);
                File.WriteAllText(System.IO.Path.Combine(dir, "upstream-sim.yaml"), GenerateUpstreamSimYaml(), Encoding.UTF8);

                foreach (var slot in _slots)
                {
                    string fname = $"downstream-sim-{slot.Name.ToLower()}.yaml";
                    File.WriteAllText(System.IO.Path.Combine(dir, fname), GenerateDsSimYaml(slot), Encoding.UTF8);
                }

                int total = 2 + _slots.Count;
                MessageBox.Show(
                    $"Saved {total} config file(s) to:\n{dir}",
                    "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── YAML generation ───────────────────────────────────────────────────

        private string GenerateProxyYaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("proxy:");
            sb.AppendLine("  channels:");
            foreach (var n in _nodes)
            {
                sb.AppendLine($"    - name: {n.Name}");
                sb.AppendLine($"      upstream:");
                sb.AppendLine($"        protocol: {n.UpProtocol}");
                sb.AppendLine($"        mode: {n.UpMode}");
                if (n.UpMode == "Client")
                    sb.AppendLine($"        host: \"{n.UpHost}\"");
                else
                    sb.AppendLine($"        listenIp: \"{n.UpHost}\"");
                sb.AppendLine($"        port: {n.UpPort}");
                sb.AppendLine($"      downstream:");
                sb.AppendLine($"        protocol: {n.DsProtocol}");
                sb.AppendLine($"        mode: {n.DsMode}");
                if (n.DsMode == "Server")
                    sb.AppendLine($"        listenIp: \"{n.DsListenIp}\"");
                else
                    sb.AppendLine($"        host: \"{n.DsListenIp}\"");
                sb.AppendLine($"        port: {n.DsPort}");
            }

            sb.AppendLine();
            if (_slots.Any())
            {
                sb.AppendLine("  downstreams:");
                foreach (var s in _slots)
                {
                    sb.AppendLine($"    - name: {s.Name}");
                    sb.AppendLine($"      minIntervalMs: {s.MinMs}");
                    sb.AppendLine($"      maxIntervalMs: {s.MaxMs}");
                }
                sb.AppendLine();
            }

            if (_arrows.Any())
            {
                sb.AppendLine("  routing:");
                sb.AppendLine("    rules:");
                foreach (var a in _arrows)
                {
                    string from = $"{a.From.Name}:{(a.FromIsUpstream ? "Upstream" : "Downstream")}";
                    string to   = $"{a.To.Name}:{(a.ToIsUpstream   ? "Upstream" : "Downstream")}";
                    sb.AppendLine($"      - from: \"{from}\"");
                    sb.AppendLine($"        to: [\"{to}\"]");
                }
                sb.AppendLine();
            }

            sb.AppendLine("  reconnect:");
            sb.AppendLine("    intervalSeconds: 3");
            sb.AppendLine("    maxAttempts: 0");
            sb.AppendLine();
            sb.AppendLine("  queue:");
            sb.AppendLine("    maxSizePerConnection: 1000");
            sb.AppendLine("    overflowPolicy: DropOldest");
            sb.AppendLine();
            sb.AppendLine("  socket:");
            sb.AppendLine("    noDelay: true");
            sb.AppendLine("    receiveBufferSize: 65536");
            sb.AppendLine("    sendBufferSize: 65536");
            sb.AppendLine("    keepAlive: false");
            sb.AppendLine();
            sb.AppendLine("  buffer:");
            sb.AppendLine("    readBufferSize: 4096");
            sb.AppendLine();
            sb.AppendLine("logging:");
            sb.AppendLine("  minimumLevel: Information");
            sb.AppendLine("  console:");
            sb.AppendLine("    enabled: true");
            sb.AppendLine("  file:");
            sb.AppendLine("    enabled: true");
            sb.AppendLine("    path: \"logs/proxy-.log\"");
            sb.AppendLine("    rollingInterval: Day");
            sb.AppendLine("    retainedFileCount: 7");
            sb.AppendLine();
            sb.AppendLine("metrics:");
            sb.AppendLine("  enabled: true");
            sb.AppendLine("  reportIntervalSeconds: 5");
            sb.AppendLine("  prometheus:");
            sb.AppendLine("    enabled: false");
            sb.AppendLine("    port: 9090");
            sb.AppendLine();
            sb.AppendLine("dashboard:");
            sb.AppendLine("  enabled: true");
            sb.AppendLine("  statusHost: \"127.0.0.1\"");
            sb.AppendLine("  statusPort: 19001");
            sb.AppendLine("  pushIntervalMs: 100");
            sb.AppendLine("  commandPort: 19002");

            return sb.ToString();
        }

        private string GenerateUpstreamSimYaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("simulator:");
            sb.AppendLine("  mode: TrafficGenerator");
            sb.AppendLine("  channels:");
            foreach (var n in _nodes)
            {
                string listenIp   = n.UpMode == "Client" ? n.UpHost : n.UpHost;
                string portKey    = "listenPort";
                sb.AppendLine($"    - name: {n.Name}");
                sb.AppendLine($"      listenIp: \"{listenIp}\"");
                sb.AppendLine($"      {portKey}: {n.UpPort}");
            }
            sb.AppendLine();
            sb.AppendLine("  trafficGenerator:");
            sb.AppendLine("    minIntervalMs: 150");
            sb.AppendLine("    maxIntervalMs: 900");
            sb.AppendLine("    payloadSize: 64");
            sb.AppendLine("    packetFormat: Structured");
            sb.AppendLine();
            sb.AppendLine("  burst:");
            sb.AppendLine("    packetCount: 5000");
            sb.AppendLine("    payloadSize: 512");
            return sb.ToString();
        }

        private string GenerateDsSimYaml(DsSlot slot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("simulator:");
            sb.AppendLine($"  name: {slot.Name}");
            sb.AppendLine("  mode: Generator");
            sb.AppendLine($"  generatorMinIntervalMs: {slot.MinMs}");
            sb.AppendLine($"  generatorMaxIntervalMs: {slot.MaxMs}");
            sb.AppendLine("  channels:");
            foreach (var n in _nodes)
            {
                sb.AppendLine($"    - name: {n.Name}");
                sb.AppendLine($"      proxyHost: \"{n.DsListenIp}\"");
                sb.AppendLine($"      proxyPort: {n.DsPort}");
            }
            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static SolidColorBrush Br(string hex)
            => new((Color)ColorConverter.ConvertFromString(hex));

        private static string F(double v)
            => v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
