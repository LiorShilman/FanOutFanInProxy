using System.Collections.Generic;

namespace TcpProxy.Core.Models
{
    public enum SocketProtocol { Tcp, Udp }
    public enum SocketMode    { Client, Server }

    public sealed class EndpointConfig
    {
        public SocketProtocol Protocol { get; set; } = SocketProtocol.Tcp;
        public SocketMode     Mode     { get; set; } = SocketMode.Client;

        // Client mode — address to connect to
        public string Host     { get; set; } = string.Empty;

        // Server mode — address to bind
        public string ListenIp { get; set; } = "0.0.0.0";

        // Both modes
        public int Port { get; set; }
    }

    public sealed class RoutingRule
    {
        public string       From { get; set; } = string.Empty;  // "MC:Upstream"
        public List<string> To   { get; set; } = new();         // ["DATA:Downstream"]
    }

    public sealed class RoutingConfig
    {
        public List<RoutingRule> Rules { get; set; } = new();
    }

    public sealed class ProxyConfig
    {
        public List<ChannelConfig>        Channels    { get; set; } = new();
        public List<DownstreamSlotConfig> Downstreams { get; set; } = new();
        public RoutingConfig              Routing     { get; set; } = new();
        public ReconnectConfig            Reconnect   { get; set; } = new();
        public QueueConfig                Queue       { get; set; } = new();
        public SocketConfig               Socket      { get; set; } = new();
        public BufferConfig               Buffer      { get; set; } = new();
    }

    public sealed class DownstreamSlotConfig
    {
        public string Name          { get; set; } = string.Empty;
        public int    MinIntervalMs { get; set; } = 100;
        public int    MaxIntervalMs { get; set; } = 700;
    }

    public sealed class ChannelConfig
    {
        public string         Name            { get; set; } = string.Empty;
        public EndpointConfig Upstream        { get; set; } = new() { Mode = SocketMode.Client };
        public EndpointConfig Downstream      { get; set; } = new() { Mode = SocketMode.Server };
        /// <summary>Display label for anonymous incoming connections (e.g. "FOFI AIR").</summary>
        public string         DownstreamLabel { get; set; } = "";
    }

    public sealed class ReconnectConfig
    {
        public int IntervalSeconds { get; set; } = 5;
        public int MaxAttempts     { get; set; } = 0;
    }

    public sealed class QueueConfig
    {
        public int            MaxSizePerConnection { get; set; } = 10000;
        public OverflowPolicy OverflowPolicy       { get; set; } = OverflowPolicy.DropOldest;
    }

    public enum OverflowPolicy { DropOldest, DisconnectClient }

    public sealed class SocketConfig
    {
        public bool NoDelay           { get; set; } = true;
        public int  ReceiveBufferSize { get; set; } = 1048576;
        public int  SendBufferSize    { get; set; } = 1048576;
        public bool KeepAlive        { get; set; } = false;
    }

    public sealed class BufferConfig
    {
        public int ReadBufferSize { get; set; } = 65536;
    }
}
