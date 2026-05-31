namespace TcpProxy.Core.Interfaces
{
    public interface IMetricsCollector
    {
        void RecordBytesReceived(string channel, string source, long bytes);
        void RecordBytesSent(string channel, string destination, long bytes);
        void RecordReconnect(string channel, string target);
        void RecordQueueDrop(string channel, string target);
        void RecordConnectionOpened(string channel, string name);
        void RecordConnectionClosed(string channel, string name);
        ChannelMetricsSnapshot GetSnapshot(string channel);
        long GetDsBytesReceived(string channel, string name);
        long GetDsBytesSent(string channel, string name);
    }

    public sealed class ChannelMetricsSnapshot
    {
        public long TotalBytesReceived { get; set; }
        public long TotalBytesSent { get; set; }
        public long ReconnectCount { get; set; }
        public long DroppedPackets { get; set; }
        public int ActiveConnections { get; set; }
    }
}
