namespace TcpProxy.Core.Models
{
    public sealed class DashboardConfig
    {
        public bool   Enabled       { get; set; } = true;
        public string StatusHost    { get; set; } = "127.0.0.1";
        public int    StatusPort    { get; set; } = 19001;
        public int    PushIntervalMs{ get; set; } = 1000;
        public int    CommandPort   { get; set; } = 19002;
        /// <summary>Optional label shown in the flow canvas upstream box (e.g. "GCCC").</summary>
        public string UpstreamLabel { get; set; } = "";
    }
}
