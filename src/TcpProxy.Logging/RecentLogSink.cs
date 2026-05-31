using System;
using Serilog.Core;
using Serilog.Events;

namespace TcpProxy.Logging
{
    internal sealed class RecentLogSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var level = logEvent.Level switch
            {
                LogEventLevel.Debug       => "DBG",
                LogEventLevel.Information => "INF",
                LogEventLevel.Warning     => "WRN",
                LogEventLevel.Error       => "ERR",
                LogEventLevel.Fatal       => "FTL",
                _                         => "???"
            };

            var msg = logEvent.RenderMessage();
            var line = $"[{DateTime.Now:HH:mm:ss} {level}] {msg}";

            if (logEvent.Exception != null)
                line += $" — {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";

            RecentLogBuffer.Add(line);
        }
    }
}
