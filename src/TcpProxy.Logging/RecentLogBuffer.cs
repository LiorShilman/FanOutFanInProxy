using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TcpProxy.Logging
{
    // Thread-safe ring buffer of recent log lines — read by StatusBroadcaster
    public static class RecentLogBuffer
    {
        private const int MaxEntries = 60;
        private static readonly ConcurrentQueue<string> _queue = new();

        internal static void Add(string line)
        {
            _queue.Enqueue(line);
            while (_queue.Count > MaxEntries)
                _queue.TryDequeue(out _);
        }

        public static List<string> GetSnapshot()
        {
            var list = new List<string>(_queue);
            int start = list.Count > 50 ? list.Count - 50 : 0;
            return list.GetRange(start, list.Count - start);
        }
    }
}
