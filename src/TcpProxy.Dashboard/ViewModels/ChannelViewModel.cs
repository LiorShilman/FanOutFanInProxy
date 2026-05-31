using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard.ViewModels
{
    public sealed class ChannelViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private bool _upstreamConnected;
        private long _rxBytesTotal;
        private long _txBytesTotal;
        private long _reconnectCount;
        private long _droppedPackets;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public bool UpstreamConnected
        {
            get => _upstreamConnected;
            set { _upstreamConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpstreamLabel)); }
        }

        public string UpstreamLabel => _upstreamConnected ? "CONNECTED" : "DISCONNECTED";

        public long RxBytesTotal
        {
            get => _rxBytesTotal;
            set { _rxBytesTotal = value; OnPropertyChanged(); OnPropertyChanged(nameof(RxDisplay)); }
        }

        public long TxBytesTotal
        {
            get => _txBytesTotal;
            set { _txBytesTotal = value; OnPropertyChanged(); OnPropertyChanged(nameof(TxDisplay)); }
        }

        public long ReconnectCount
        {
            get => _reconnectCount;
            set { _reconnectCount = value; OnPropertyChanged(); }
        }

        public long DroppedPackets
        {
            get => _droppedPackets;
            set { _droppedPackets = value; OnPropertyChanged(); }
        }

        public string RxDisplay => FormatBytes(_rxBytesTotal);
        public string TxDisplay => FormatBytes(_txBytesTotal);

        public ObservableCollection<DownstreamViewModel> Downstreams { get; } = new();

        public void UpdateFrom(ChannelStatus status)
        {
            Name              = status.Name;
            UpstreamConnected = status.UpstreamConnected;
            RxBytesTotal      = status.RxBytesTotal;
            TxBytesTotal      = status.TxBytesTotal;
            ReconnectCount    = status.ReconnectCount;
            DroppedPackets    = status.DroppedPackets;

            // Sync downstreams
            foreach (var ds in status.Downstreams)
            {
                var existing = FindDownstream(ds.Name);
                if (existing is null)
                    Downstreams.Add(new DownstreamViewModel { Name = ds.Name, Connected = ds.Connected });
                else
                    existing.Connected = ds.Connected;
            }
        }

        private DownstreamViewModel? FindDownstream(string name)
        {
            foreach (var d in Downstreams)
                if (d.Name == name) return d;
            return null;
        }

        private static string FormatBytes(long b)
        {
            if (b >= 1073741824L) return $"{b / 1073741824.0:F1} GB";
            if (b >= 1048576L)    return $"{b / 1048576.0:F1} MB";
            if (b >= 1024L)       return $"{b / 1024.0:F1} KB";
            return $"{b} B";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class DownstreamViewModel : INotifyPropertyChanged
    {
        private bool _connected;

        public string Name { get; set; } = string.Empty;

        public bool Connected
        {
            get => _connected;
            set { _connected = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); }
        }

        public string StatusLabel => _connected ? "OK" : "DOWN";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
