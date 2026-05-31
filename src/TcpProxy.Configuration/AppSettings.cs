using TcpProxy.Core.Models;

namespace TcpProxy.Configuration
{
    public sealed class AppSettings
    {
        public ProxyConfig Proxy { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public MetricsConfig Metrics { get; set; } = new();
        public DashboardConfig Dashboard { get; set; } = new();
    }
}
