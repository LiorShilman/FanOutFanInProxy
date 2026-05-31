using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TcpProxy.Core.Models;

namespace TcpProxy.Dashboard
{
    public sealed class StatusClient
    {
        private readonly string _host;
        private readonly int _port;

        public event Action<StatusSnapshot>? SnapshotReceived;
        public event Action<bool>? ConnectionChanged;

        public StatusClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(_host, _port).ConfigureAwait(false);
                    ConnectionChanged?.Invoke(true);

                    using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break;

                        var snap = JsonConvert.DeserializeObject<StatusSnapshot>(line);
                        if (snap != null)
                            SnapshotReceived?.Invoke(snap);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
                finally
                {
                    client?.Close();
                    ConnectionChanged?.Invoke(false);
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }
}
