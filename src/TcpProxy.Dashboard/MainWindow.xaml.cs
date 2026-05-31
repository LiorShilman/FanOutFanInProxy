using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using TcpProxy.Dashboard.ViewModels;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new();
        private readonly StatusClient _client;
        private readonly CancellationTokenSource _cts = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            // Default: connect to localhost — can be overridden by command-line args
            var host = "127.0.0.1";
            var port = 19001;
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == "--host") host = args[i + 1];
                if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
            }

            _client = new StatusClient(host, port);
            _client.SnapshotReceived  += OnSnapshot;
            _client.ConnectionChanged += OnConnectionChanged;

            _ = _client.RunAsync(_cts.Token);
        }

        private void OnConnectionChanged(bool connected)
        {
            Dispatcher.Invoke(() =>
            {
                _vm.IsConnected       = connected;
                _vm.ConnectionStatus  = connected ? "Connected" : "Reconnecting...";
            });
        }

        private void OnSnapshot(StatusSnapshot snap)
        {
            Dispatcher.Invoke(() =>
            {
                _vm.LastUpdate = snap.Timestamp;

                // Sync channel VMs
                foreach (var ch in snap.Channels)
                {
                    var existing = FindChannel(ch.Name);
                    if (existing is null)
                    {
                        var vm = new ChannelViewModel();
                        vm.UpdateFrom(ch);
                        _vm.Channels.Add(vm);
                    }
                    else
                    {
                        existing.UpdateFrom(ch);
                    }
                }

                // Sync logs
                _vm.RecentLogs.Clear();
                var logs = snap.RecentLogs;
                int start = Math.Max(0, logs.Count - 20);
                for (int i = start; i < logs.Count; i++)
                    _vm.RecentLogs.Add(logs[i]);

                // Update flow animation
                FlowCanvas.UpdateFromSnapshot(snap);

                // Update path-switch panel
                if (snap.AllEndpoints?.Count > 0)
                    PathSwitch.UpdateRouting(snap.RoutingRules, snap.AllEndpoints, snap.CommandPort, snap.Channels);

                // Auto-scroll log
                if (LogListBox.Items.Count > 0)
                    LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            });
        }

        private ChannelViewModel? FindChannel(string name)
        {
            foreach (var c in _vm.Channels)
                if (c.Name == name) return c;
            return null;
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            base.OnClosed(e);
        }
    }
}
