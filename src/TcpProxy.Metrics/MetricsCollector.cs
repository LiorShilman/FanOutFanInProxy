using System;
using System.Collections.Concurrent;
using System.Threading;
using TcpProxy.Core.Interfaces;

namespace TcpProxy.Metrics
{
    public sealed class MetricsCollector : IMetricsCollector
    {
        private readonly ConcurrentDictionary<string, ChannelCounters> _channels = new();
        private readonly ConcurrentDictionary<string, long> _dsRxBytes = new();
        private readonly ConcurrentDictionary<string, long> _dsTxBytes = new();

        public void RecordBytesReceived(string channel, string source, long bytes)
        {
            Interlocked.Add(ref GetOrAdd(channel).BytesReceived, bytes);
            var key = channel + ":" + source;
            _dsRxBytes.AddOrUpdate(key, bytes, (_, v) => v + bytes);
        }

        public void RecordBytesSent(string channel, string destination, long bytes)
        {
            Interlocked.Add(ref GetOrAdd(channel).BytesSent, bytes);
            var key = channel + ":" + destination;
            _dsTxBytes.AddOrUpdate(key, bytes, (_, v) => v + bytes);
        }

        public void RecordReconnect(string channel, string target)
            => Interlocked.Increment(ref GetOrAdd(channel).ReconnectCount);

        public void RecordQueueDrop(string channel, string target)
            => Interlocked.Increment(ref GetOrAdd(channel).DroppedPackets);

        public void RecordConnectionOpened(string channel, string name)
            => Interlocked.Increment(ref GetOrAdd(channel).ActiveConnections);

        public void RecordConnectionClosed(string channel, string name)
            => Interlocked.Decrement(ref GetOrAdd(channel).ActiveConnections);

        public ChannelMetricsSnapshot GetSnapshot(string channel)
        {
            var c = GetOrAdd(channel);
            return new ChannelMetricsSnapshot
            {
                TotalBytesReceived = Interlocked.Read(ref c.BytesReceived),
                TotalBytesSent     = Interlocked.Read(ref c.BytesSent),
                ReconnectCount     = Interlocked.Read(ref c.ReconnectCount),
                DroppedPackets     = Interlocked.Read(ref c.DroppedPackets),
                ActiveConnections  = c.ActiveConnections
            };
        }

        public long GetDsBytesReceived(string channel, string name)
            => _dsRxBytes.TryGetValue(channel + ":" + name, out var v) ? v : 0;

        public long GetDsBytesSent(string channel, string name)
            => _dsTxBytes.TryGetValue(channel + ":" + name, out var v) ? v : 0;

        private ChannelCounters GetOrAdd(string channel)
            => _channels.GetOrAdd(channel, _ => new ChannelCounters());

        private sealed class ChannelCounters
        {
            public long BytesReceived;
            public long BytesSent;
            public long ReconnectCount;
            public long DroppedPackets;
            public int  ActiveConnections;
        }
    }
}
