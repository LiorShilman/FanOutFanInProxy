using TcpProxy.Core.Models;

namespace TcpProxy.Core.Interfaces
{
    public interface IChannelStatusProvider
    {
        ChannelStatus GetStatus();
    }
}
