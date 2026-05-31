using System.Collections.Generic;

namespace UpstreamSimulator
{
    public sealed class SimulatorConfig
    {
        public SimulatorSection Simulator { get; set; } = new();
    }

    public sealed class SimulatorSection
    {
        public string Mode { get; set; } = "TrafficGenerator";
        public List<ChannelEndpoint> Channels { get; set; } = new();
        public TrafficGeneratorSettings TrafficGenerator { get; set; } = new();
        public BurstSettings Burst { get; set; } = new();
    }

    public sealed class ChannelEndpoint
    {
        public string Name { get; set; } = string.Empty;
        public string ListenIp { get; set; } = "0.0.0.0";
        public int ListenPort { get; set; }
    }

    public sealed class TrafficGeneratorSettings
    {
        public int MinIntervalMs { get; set; } = 150;
        public int MaxIntervalMs { get; set; } = 800;
        public int PayloadSize { get; set; } = 128;
        public string PacketFormat { get; set; } = "Structured";
    }

    public sealed class BurstSettings
    {
        public int PacketCount { get; set; } = 10000;
        public int PayloadSize { get; set; } = 1024;
    }
}
