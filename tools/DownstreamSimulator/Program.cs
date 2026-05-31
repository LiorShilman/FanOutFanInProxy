using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DownstreamSimulator
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

            var cfg   = deserializer.Deserialize<DsProxyConfig>(File.ReadAllText(configPath));
            var proxy = cfg.Proxy;

            if (proxy.Downstreams.Count == 0)
                throw new Exception("proxy.yaml has no 'downstreams:' section.");
            if (proxy.Channels.Count == 0)
                throw new Exception("proxy.yaml has no 'channels:' section.");

            Console.WriteLine("=== Downstream Simulator ===");
            Console.WriteLine($"DS slots : {string.Join(", ", proxy.Downstreams.ConvertAll(d => d.Name))}");

            foreach (var ch in proxy.Channels)
            {
                var ep = ch.Downstream;
                Console.WriteLine($"Channel  : {ch.Name}  {ep.Protocol}/{ep.Mode}  {ep.ListenIp}:{ep.Port}");
            }

            Console.WriteLine($"Total    : {proxy.Downstreams.Count * proxy.Channels.Count} sockets");
            Console.WriteLine("Press Ctrl+C to stop.");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var tasks = new List<Task>();
            foreach (var ds in proxy.Downstreams)
                foreach (var ch in proxy.Channels)
                {
                    var ep    = ch.Downstream;
                    bool isUdp = ep.Protocol.Equals("Udp", StringComparison.OrdinalIgnoreCase);

                    tasks.Add(new DownstreamConnector(
                        ep.ListenIp, ep.Port,
                        ch.Name, ds.Name,
                        ds.MinIntervalMs, ds.MaxIntervalMs,
                        isUdp
                    ).RunAsync(cts.Token));
                }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        static string FindConfig(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--config" || args[i] == "-c")
                    return string.Join(" ", args, i + 1, args.Length - i - 1);
            throw new Exception("Usage: DownstreamSimulator.exe --config <proxy.yaml>");
        }
    }
}
