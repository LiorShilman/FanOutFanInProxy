using System;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using TcpProxy.Core.Models;

namespace TcpProxy.Logging
{
    public static class ProxyLoggerFactory
    {
        public static ILoggerFactory Create(LoggingConfig config)
        {
            var level = Enum.TryParse<LogEventLevel>(config.MinimumLevel, true, out var l)
                ? l : LogEventLevel.Information;

            var logConfig = new LoggerConfiguration().MinimumLevel.Is(level);

            if (config.Console.Enabled)
                logConfig = logConfig.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

            if (config.File.Enabled)
            {
                var rolling = Enum.TryParse<RollingInterval>(config.File.RollingInterval, true, out var ri)
                    ? ri : RollingInterval.Day;

                logConfig = logConfig.WriteTo.File(
                    config.File.Path,
                    rollingInterval: rolling,
                    retainedFileCountLimit: config.File.RetainedFileCount,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }

            logConfig = logConfig.WriteTo.Sink(new RecentLogSink());
            Log.Logger = logConfig.CreateLogger();

            return new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, true);
        }
    }
}
