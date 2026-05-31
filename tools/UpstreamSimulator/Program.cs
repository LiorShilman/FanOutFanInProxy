using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UpstreamSimulator
{
    internal static class Program
    {
        static async Task Main(string[] args)
        {
            var configPath = FindConfig(args);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var cfg = deserializer.Deserialize<SimulatorConfig>(File.ReadAllText(configPath));

            Console.WriteLine("=== Upstream Simulator ===");
            Console.WriteLine($"Mode: {cfg.Simulator.Mode}");
            Console.WriteLine("Press Ctrl+C to stop.");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var tasks = new List<Task>();
            foreach (var ch in cfg.Simulator.Channels)
                tasks.Add(new ChannelSimulator(ch, cfg.Simulator).RunAsync(cts.Token));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        static string FindConfig(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] is "--config" or "-c") return args[i + 1];
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "upstream-sim.yaml");
            if (File.Exists(p)) return p;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "config", "upstream-sim.yaml"));
        }
    }
}
