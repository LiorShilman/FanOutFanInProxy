using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DownstreamSimulator
{
    /// <summary>
    /// One downstream connector: simulates a single DS slot on a single channel.
    /// Supports TCP (Client→Server) and UDP (Client→Server).
    /// Protocol is determined by the channel's downstream.protocol from proxy.yaml.
    /// </summary>
    public sealed class DownstreamConnector
    {
        private readonly string _proxyHost;
        private readonly int    _proxyPort;
        private readonly string _channelName;
        private readonly string _dsName;
        private readonly int    _minMs;
        private readonly int    _maxMs;
        private readonly bool   _isUdp;
        private long _rxCount;
        private long _txCount;

        public DownstreamConnector(
            string proxyHost, int proxyPort,
            string channelName, string dsName,
            int minMs, int maxMs,
            bool isUdp = false)
        {
            _proxyHost   = proxyHost;
            _proxyPort   = proxyPort;
            _channelName = channelName;
            _dsName      = dsName;
            _minMs       = minMs;
            _maxMs       = maxMs;
            _isUdp       = isUdp;
        }

        private string Tag => $"[{_dsName}/{_channelName}]";

        public Task RunAsync(CancellationToken ct)
            => _isUdp ? RunUdpAsync(ct) : RunTcpAsync(ct);

        // ── TCP ───────────────────────────────────────────────────────────────

        private async Task RunTcpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(_proxyHost, _proxyPort).ConfigureAwait(false);
                    Console.WriteLine($"{Tag} TCP connected to {_proxyHost}:{_proxyPort}");
                    await TcpGeneratorAsync(client, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Tag} TCP error: {ex.Message} — retrying in 3s");
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                }
                finally { client?.Close(); }
            }
        }

        private async Task TcpGeneratorAsync(TcpClient client, CancellationToken ct)
        {
            var stream = client.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Drain fan-out data from proxy in background
            var recvTask = Task.Run(async () =>
            {
                var buf = new byte[65536];
                try
                {
                    while (!linked.Token.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(buf, 0, buf.Length, linked.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        Interlocked.Add(ref _rxCount, n);
                    }
                }
                catch { }
                finally { linked.Cancel(); }
            }, linked.Token);

            long seq     = 0;
            var  rng     = new Random(Guid.NewGuid().GetHashCode());
            var  payload = Encoding.ASCII.GetBytes($"FROM-{_dsName}");
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var packet = TcpTestCommon.StructuredPacket.Build(_channelName, ++seq, payload);
                    await stream.WriteAsync(packet, 0, packet.Length, linked.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _txCount, packet.Length);
                    Console.Write($"\r{Tag} RX:{_rxCount,8}B  TX:{_txCount,8}B  seq={seq}  ");
                    await Task.Delay(rng.Next(_minMs, _maxMs + 1), linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"\n{Tag} TCP send error: {ex.Message}"); }
            finally
            {
                linked.Cancel();
                Console.WriteLine($"\n{Tag} TCP disconnected");
            }

            await recvTask.ConfigureAwait(false);
        }

        // ── UDP ───────────────────────────────────────────────────────────────

        private async Task RunUdpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpClient? udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Connect(_proxyHost, _proxyPort);
                    Console.WriteLine($"{Tag} UDP sending to {_proxyHost}:{_proxyPort}");
                    await UdpGeneratorAsync(udp, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Tag} UDP error: {ex.Message} — retrying in 3s");
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                }
                finally { udp?.Close(); }
            }
        }

        private async Task UdpGeneratorAsync(UdpClient udp, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Drain fan-out datagrams in background
            var recvTask = Task.Run(async () =>
            {
                try
                {
                    while (!linked.Token.IsCancellationRequested)
                    {
                        var result = await udp.ReceiveAsync().ConfigureAwait(false);
                        Interlocked.Add(ref _rxCount, result.Buffer.Length);
                    }
                }
                catch { }
                finally { linked.Cancel(); }
            }, linked.Token);

            long seq     = 0;
            var  rng     = new Random(Guid.NewGuid().GetHashCode());
            var  payload = Encoding.ASCII.GetBytes($"FROM-{_dsName}");
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var packet = TcpTestCommon.StructuredPacket.Build(_channelName, ++seq, payload);
                    udp.Send(packet, packet.Length);
                    Interlocked.Add(ref _txCount, packet.Length);
                    Console.Write($"\r{Tag} RX:{_rxCount,8}B  TX:{_txCount,8}B  seq={seq}  ");
                    await Task.Delay(rng.Next(_minMs, _maxMs + 1), linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"\n{Tag} UDP send error: {ex.Message}"); }
            finally
            {
                linked.Cancel();
                Console.WriteLine($"\n{Tag} UDP session ended");
            }

            await recvTask.ConfigureAwait(false);
        }
    }
}
