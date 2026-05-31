using System.Threading;
using System.Threading.Tasks;

namespace TcpProxy.Core.Interfaces
{
    public interface ITcpChannelProxy
    {
        string ChannelName { get; }
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }
}
