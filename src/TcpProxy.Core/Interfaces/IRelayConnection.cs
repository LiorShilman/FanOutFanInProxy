using System;
using System.Threading;
using System.Threading.Tasks;

namespace TcpProxy.Core.Interfaces
{
    public interface IRelayConnection : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        event Action<string, byte[], int, int>? DataReceived;
        event Action? Disconnected;
        Task SendAsync(byte[] buffer, int offset, int count, CancellationToken ct);
        void StartReceiving(CancellationToken ct);
    }
}
