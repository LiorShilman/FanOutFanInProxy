using System.Threading;
using System.Threading.Tasks;

namespace TcpProxy.Core.Interfaces
{
    public interface IConnectionManager
    {
        Task<IRelayConnection> ConnectWithRetryAsync(string host, int port, string name, string channel, CancellationToken ct);
    }
}
