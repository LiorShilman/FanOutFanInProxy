using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard.Views
{
    public partial class RoutingPanelControl : UserControl
    {
        private readonly ObservableCollection<SourceGroupVM> _groups = new();
        private readonly Dictionary<string, DateTime> _suppressUntil = new(StringComparer.OrdinalIgnoreCase);
        private int  _commandPort = 19002;
        private bool _updating;

        public RoutingPanelControl()
        {
            InitializeComponent();
            GroupsList.ItemsSource = _groups;
        }

        public void UpdateRouting(
            List<RoutingRuleSnapshot> rules,
            List<string>              allEndpoints,
            int                       commandPort)
        {
            _commandPort = commandPort;
            _updating    = true;
            try
            {
                // Rebuild groups when the endpoint set changes
                if (_groups.Count != allEndpoints.Count ||
                    !_groups.Select(g => g.Source).SequenceEqual(allEndpoints))
                {
                    _groups.Clear();
                    foreach (var ep in allEndpoints)
                    {
                        var g = new SourceGroupVM(ep);
                        foreach (var target in allEndpoints)
                        {
                            if (target == ep) continue;
                            g.Targets.Add(new TargetVM(target, g, PostUpdate));
                        }
                        _groups.Add(g);
                    }
                }

                // Sync active state from rules — skip sources the user just changed
                var ruleMap = rules.ToDictionary(
                    r => r.From,
                    r => (IReadOnlyList<string>)r.To,
                    StringComparer.OrdinalIgnoreCase);

                var now = DateTime.UtcNow;
                foreach (var g in _groups)
                {
                    // Don't override a toggle the user just clicked — wait for the proxy
                    // to confirm the change in its next snapshot (within ~600 ms)
                    if (_suppressUntil.TryGetValue(g.Source, out var until) && now < until)
                        continue;

                    var active = ruleMap.TryGetValue(g.Source, out var to)
                        ? to : Array.Empty<string>();
                    foreach (var t in g.Targets)
                        t.SetActiveInternal(active.Contains(t.Label, StringComparer.OrdinalIgnoreCase));
                    g.RefreshCount();
                }
            }
            finally { _updating = false; }
        }

        private void BtnBlockAll_Click(object sender, RoutedEventArgs e)
        {
            // Atomically uncheck every target across every source group and send [] to proxy
            foreach (var group in _groups)
            {
                bool hadActive = group.Targets.Any(t => t.IsActive);
                foreach (var target in group.Targets)
                    target.SetActiveInternal(false);
                group.RefreshCount();
                _suppressUntil[group.Source] = DateTime.UtcNow.AddMilliseconds(1200);
                _ = PostAsync(BuildJson(group.Source, new List<string>()));
            }
        }

        private void PostUpdate(SourceGroupVM group)
        {
            if (_updating) return;
            group.RefreshCount();
            var activeTargets = group.Targets
                .Where(t => t.IsActive)
                .Select(t => t.Label)
                .ToList();

            // Suppress snapshot overrides for this source until the proxy confirms the change
            _suppressUntil[group.Source] = DateTime.UtcNow.AddMilliseconds(800);

            _ = PostAsync(BuildJson(group.Source, activeTargets));
        }

        private async Task PostAsync(string json)
        {
            bool ok = false;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var connectTask = client.ConnectAsync("127.0.0.1", _commandPort);
                if (await Task.WhenAny(connectTask, Task.Delay(2000)).ConfigureAwait(false) != connectTask)
                {
                    SetCmdStatus(false, "timeout");
                    return;
                }
                await connectTask.ConfigureAwait(false); // rethrow if connect failed

                var stream = client.GetStream();
                var bytes  = Encoding.UTF8.GetBytes(json + "\n");
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

                // Graceful half-close — signals EOF so proxy's ReadLineAsync returns cleanly,
                // preventing the RST that would occur if we Dispose with unread data in the buffer.
                client.Client.Shutdown(SocketShutdown.Send);

                // Read proxy's acknowledgement before closing
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 256, leaveOpen: true);
                var response = await reader.ReadLineAsync().ConfigureAwait(false);
                ok = response?.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex)
            {
                SetCmdStatus(false, ex.Message.Length > 20 ? ex.Message.Substring(0, 20) : ex.Message);
                return;
            }

            SetCmdStatus(ok, ok ? "OK" : "err");
        }

        private void SetCmdStatus(bool ok, string label)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CmdStatusDot.Fill  = new System.Windows.Media.SolidColorBrush(
                    ok ? System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)   // green
                       : System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)); // red
                CmdStatusText.Text = label;
            }));
        }

        private static string BuildJson(string from, List<string> to)
        {
            var sb = new StringBuilder();
            sb.Append("{\"from\":\"").Append(from).Append("\",\"to\":[");
            for (int i = 0; i < to.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(to[i]).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    // ── View models ───────────────────────────────────────────────────────────

    internal sealed class SourceGroupVM : INotifyPropertyChanged
    {
        private int _activeCount;

        public string                         Source  { get; }
        public string                         Channel { get; }
        public string                         Role    { get; }
        public ObservableCollection<TargetVM> Targets { get; } = new();

        public int  ActiveCount => _activeCount;
        public bool HasActive   => _activeCount > 0;

        public SourceGroupVM(string source)
        {
            Source  = source;
            var idx = source.IndexOf(':');
            Channel = idx > 0 ? source.Substring(0, idx)     : source;
            Role    = idx > 0 ? source.Substring(idx + 1)    : string.Empty;
        }

        public void RefreshCount()
        {
            var count = Targets.Count(t => t.IsActive);
            if (count == _activeCount) return;
            _activeCount = count;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActive)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class TargetVM : INotifyPropertyChanged
    {
        private bool                          _isActive;
        private readonly SourceGroupVM        _group;
        private readonly Action<SourceGroupVM> _onChange;

        public string Label         { get; }
        public string TargetChannel { get; }
        public string TargetRole    { get; }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                _onChange(_group);
            }
        }

        public TargetVM(string label, SourceGroupVM group, Action<SourceGroupVM> onChange)
        {
            Label    = label;
            _group   = group;
            _onChange = onChange;
            var idx   = label.IndexOf(':');
            TargetChannel = idx > 0 ? label.Substring(0, idx)  : label;
            TargetRole    = idx > 0 ? label.Substring(idx + 1) : string.Empty;
        }

        internal void SetActiveInternal(bool active)
        {
            _isActive = active;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ── Converter ─────────────────────────────────────────────────────────────

    [ValueConversion(typeof(bool), typeof(Visibility))]
    internal sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
