using System.IO;
using TcpProxy.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TcpProxy.Configuration
{
    public static class YamlConfigLoader
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public static AppSettings Load(string yamlPath)
        {
            var yaml = File.ReadAllText(yamlPath);
            var raw = Deserializer.Deserialize<RawRoot>(yaml);

            return new AppSettings
            {
                Proxy     = raw.Proxy     ?? new ProxyConfig(),
                Logging   = raw.Logging   ?? new LoggingConfig(),
                Metrics   = raw.Metrics   ?? new MetricsConfig(),
                Dashboard = raw.Dashboard ?? new DashboardConfig()
            };
        }

        // Intermediate type matching the YAML root keys
        private sealed class RawRoot
        {
            public ProxyConfig?     Proxy     { get; set; }
            public LoggingConfig?   Logging   { get; set; }
            public MetricsConfig?   Metrics   { get; set; }
            public DashboardConfig? Dashboard { get; set; }
        }
    }
}
