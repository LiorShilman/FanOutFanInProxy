using System.Collections.Generic;

namespace DownstreamSimulator
{
    // Mirror of the relevant parts of proxy.yaml for the DS simulator.
    // IgnoreUnmatchedProperties lets us reuse the proxy config file directly.

    public sealed class DsProxyConfig
    {
        public DsProxySection Proxy { get; set; } = new();
    }

    public sealed class DsProxySection
    {
        public List<DsChannelInfo>  Channels    { get; set; } = new();
        public List<DsSlotInfo>     Downstreams { get; set; } = new();
    }

    // Maps to channel.downstream in proxy.yaml
    public sealed class DsEndpointInfo
    {
        public string Protocol  { get; set; } = "Tcp";    // "Tcp" | "Udp"
        public string Mode      { get; set; } = "Server"; // "Server" | "Client"
        public string ListenIp  { get; set; } = "127.0.0.1";
        public int    Port      { get; set; }
    }

    public sealed class DsChannelInfo
    {
        public string         Name       { get; set; } = string.Empty;
        public DsEndpointInfo Downstream { get; set; } = new();
    }

    public sealed class DsSlotInfo
    {
        public string Name          { get; set; } = string.Empty;
        public int    MinIntervalMs { get; set; } = 100;
        public int    MaxIntervalMs { get; set; } = 700;
    }
}
