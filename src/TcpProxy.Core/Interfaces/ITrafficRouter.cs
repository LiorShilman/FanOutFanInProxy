using System.Threading;
using System.Threading.Tasks;

namespace TcpProxy.Core.Interfaces
{
    public interface ITrafficRouter
    {
        Task RouteUpstreamToDownstreamsAsync(byte[] buffer, int offset, int count, CancellationToken ct);
        Task RouteDownstreamToUpstreamAsync(string sourceName, byte[] buffer, int offset, int count, CancellationToken ct);
    }
}
