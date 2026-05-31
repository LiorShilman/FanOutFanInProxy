using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TcpProxy.Dashboard.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private string _connectionStatus = "Disconnected";
        private bool _isConnected;
        private string _lastUpdate = "--";

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public string LastUpdate
        {
            get => _lastUpdate;
            set { _lastUpdate = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ChannelViewModel> Channels { get; } = new();
        public ObservableCollection<string> RecentLogs { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
