using System;
using System.IO;
using System.ServiceProcess;
using TcpProxy.Configuration;
using TcpProxy.Logging;

namespace TcpProxy.Service
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            var configPath = ResolveConfigPath(args);
            var settings   = YamlConfigLoader.Load(configPath);
            var loggerFact = ProxyLoggerFactory.Create(settings.Logging);

            bool isService = !Environment.UserInteractive;
            if (isService)
            {
                ServiceBase.Run(new WindowsServiceWrapper(settings));
            }
            else
            {
                var host = new ProxyHost(settings, loggerFact);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    host.StopAsync().GetAwaiter().GetResult();
                };
                Console.WriteLine("TcpProxy running. Press Ctrl+C to stop.");
                host.StartAsync().GetAwaiter().GetResult();
            }
        }

        private static string ResolveConfigPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--config" || args[i] == "-c")
                    return args[i + 1];

            var exeDir  = AppDomain.CurrentDomain.BaseDirectory;
            var relative = Path.Combine(exeDir, "config", "proxy.yaml");
            if (File.Exists(relative)) return relative;

            var devPath = Path.Combine(exeDir, "..", "..", "..", "..", "config", "proxy.yaml");
            if (File.Exists(devPath)) return Path.GetFullPath(devPath);

            throw new FileNotFoundException("proxy.yaml not found. Use --config <path>.");
        }
    }
}
