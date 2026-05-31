namespace TcpProxy.Core.Models
{
    public sealed class MetricsConfig
    {
        public bool Enabled { get; set; } = true;
        public int ReportIntervalSeconds { get; set; } = 10;
        public PrometheusConfig Prometheus { get; set; } = new();
    }

    public sealed class PrometheusConfig
    {
        public bool Enabled { get; set; } = false;
        public int Port { get; set; } = 9090;
    }
}
