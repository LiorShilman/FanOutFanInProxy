using System;
using System.Collections.Generic;

namespace TcpProxy.Core.Models
{
    public sealed class StatusSnapshot
    {
        public string                    Timestamp     { get; set; } = string.Empty;
        public List<ChannelStatus>       Channels      { get; set; } = new();
        public List<string>              RecentLogs    { get; set; } = new();
        public List<RoutingRuleSnapshot> RoutingRules  { get; set; } = new();
        public List<string>              AllEndpoints  { get; set; } = new();
        public int                       CommandPort   { get; set; } = 19002;
        /// <summary>Optional label for the upstream box in the flow canvas (e.g. "GCCC").</summary>
        public string                    UpstreamLabel { get; set; } = "";
    }

    public sealed class RoutingRuleSnapshot
    {
        public string       From { get; set; } = string.Empty;
        public List<string> To   { get; set; } = new();
    }

    public sealed class ChannelStatus
    {
        public string Name { get; set; } = string.Empty;
        public bool   UpstreamConnected { get; set; }
        public List<DownstreamStatus> Downstreams { get; set; } = new();
        public long   RxBytesTotal  { get; set; }
        public long   TxBytesTotal  { get; set; }
        public long   ReconnectCount { get; set; }
        public long   DroppedPackets { get; set; }
        public double RxBytesPerSec { get; set; }
        public double TxBytesPerSec { get; set; }
        /// <summary>Label for anonymous incoming connections (from ChannelConfig.DownstreamLabel).</summary>
        public string DownstreamLabel          { get; set; } = "";
        /// <summary>Count of currently active anonymous (non-named-slot) downstream connections.</summary>
        public int    AnonymousDownstreamCount { get; set; }
        /// <summary>Cumulative bytes received from anonymous downstream connections (e.g. from FOFI AIR).</summary>
        public long   AnonymousRxBytesTotal    { get; set; }
    }

    public sealed class DownstreamStatus
    {
        public string Name { get; set; } = string.Empty;
        public bool Connected { get; set; }
        public long RxBytesTotal { get; set; }
        public long TxBytesTotal { get; set; }
    }
}
