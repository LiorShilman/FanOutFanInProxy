using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTestCommon;

namespace UpstreamSimulator
{
    // Upstream is a SERVER — proxy connects to it
    public sealed class ChannelSimulator
    {
        private readonly ChannelEndpoint _endpoint;
        private readonly SimulatorSection _cfg;
        private long _txCount;
        private long _rxCount;

        public ChannelSimulator(ChannelEndpoint endpoint, SimulatorSection cfg)
        {
            _endpoint = endpoint;
            _cfg      = cfg;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Parse(_endpoint.ListenIp), _endpoint.ListenPort);
            listener.Start();
            Console.WriteLine($"[{_endpoint.Name}] Listening on {_endpoint.ListenIp}:{_endpoint.ListenPort}  mode={_cfg.Mode}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Console.WriteLine($"[{_endpoint.Name}] Proxy connected from {client.Client.RemoteEndPoint}");
                    _ = HandleClientAsync(client, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { listener.Stop(); }
        }

        private Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            client.NoDelay = true;
            return _cfg.Mode switch
            {
                "TrafficGenerator" => TrafficGeneratorLoopAsync(client, ct),
                "Burst"            => BurstLoopAsync(client, ct),
                _                  => TrafficGeneratorLoopAsync(client, ct)
            };
        }

        private async Task TrafficGeneratorLoopAsync(TcpClient client, CancellationToken ct)
        {
            var stream  = client.GetStream();
            var rng     = new Random(Guid.NewGuid().GetHashCode());
            var payload = new byte[_cfg.TrafficGenerator.PayloadSize];
            rng.NextBytes(payload);

            int minMs = _cfg.TrafficGenerator.MinIntervalMs;
            int maxMs = _cfg.TrafficGenerator.MaxIntervalMs;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Drain fan-in from proxy in background
            _ = DrainAsync(stream, linked.Token);

            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var packet = _cfg.TrafficGenerator.PacketFormat == "Structured"
                        ? StructuredPacket.Build(_endpoint.Name, _txCount + 1, payload)
                        : payload;

                    await stream.WriteAsync(packet, 0, packet.Length, linked.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _txCount);
                    PrintStats();
                    await Task.Delay(rng.Next(minMs, maxMs + 1), linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"\n[{_endpoint.Name}] Error: {ex.Message}"); }
            finally
            {
                linked.Cancel();
                client.Close();
                Console.WriteLine($"\n[{_endpoint.Name}] Proxy disconnected");
            }
        }

        private async Task BurstLoopAsync(TcpClient client, CancellationToken ct)
        {
            var stream  = client.GetStream();
            var payload = new byte[_cfg.Burst.PayloadSize];
            new Random().NextBytes(payload);

            try
            {
                for (int i = 0; i < _cfg.Burst.PacketCount && !ct.IsCancellationRequested; i++)
                {
                    await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _txCount);
                }
                Console.WriteLine($"[{_endpoint.Name}] Burst done: {_txCount} packets sent");
            }
            catch { }
            finally { client.Close(); }
        }

        private async Task DrainAsync(NetworkStream stream, CancellationToken ct)
        {
            var buf = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    Interlocked.Add(ref _rxCount, n);
                    PrintStats();
                }
            }
            catch { }
        }

        private void PrintStats()
            => Console.Write($"\r[{_endpoint.Name}] TX:{_txCount,6}  RX:{_rxCount,6}   ");
    }
}
