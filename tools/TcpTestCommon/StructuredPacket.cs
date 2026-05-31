using System;
using System.Text;

namespace TcpTestCommon
{
    // Wire format: MC|000001|2026-05-07T10:00:00.000Z|<payload>\n
    public static class StructuredPacket
    {
        public static byte[] Build(string channel, long counter, byte[] payload)
        {
            var header = $"{channel}|{counter:D6}|{DateTime.UtcNow:O}|";
            var prefix = Encoding.ASCII.GetBytes(header);
            var result = new byte[prefix.Length + payload.Length + 1];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, result, prefix.Length, payload.Length);
            result[result.Length - 1] = (byte)'\n';
            return result;
        }

        public static bool TryParse(byte[] data, int offset, int count,
            out string channel, out long counter, out DateTime timestamp, out int payloadOffset)
        {
            channel = string.Empty; counter = 0; timestamp = default; payloadOffset = 0;
            var s = Encoding.ASCII.GetString(data, offset, count);
            var parts = s.Split('|');
            if (parts.Length < 4) return false;
            channel = parts[0];
            if (!long.TryParse(parts[1], out counter)) return false;
            if (!DateTime.TryParse(parts[2], out timestamp)) return false;
            payloadOffset = offset + parts[0].Length + 1 + parts[1].Length + 1 + parts[2].Length + 1;
            return true;
        }
    }
}
