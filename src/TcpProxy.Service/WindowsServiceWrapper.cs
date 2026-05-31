using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using TcpProxy.Configuration;
using TcpProxy.Logging;

namespace TcpProxy.Service
{
    public sealed class WindowsServiceWrapper : ServiceBase
    {
        public const string SvcName = "TcpFanOutProxy";

        private readonly AppSettings _settings;
        private ProxyHost? _host;
        private CancellationTokenSource? _cts;

        public WindowsServiceWrapper(AppSettings settings)
        {
            _settings           = settings;
            ServiceName         = SvcName;
            CanStop             = true;
            CanPauseAndContinue = false;
            AutoLog             = true;
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();
            var loggerFactory = ProxyLoggerFactory.Create(_settings.Logging);
            _host = new ProxyHost(_settings, loggerFactory);
            Task.Run(() => _host.StartAsync(_cts.Token));
        }

        protected override void OnStop()
        {
            _cts?.Cancel();
            _host?.StopAsync().GetAwaiter().GetResult();
        }
    }
}
