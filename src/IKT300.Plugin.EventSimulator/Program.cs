using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Configuration;
using IKT300.Shared.Models;

namespace IKT300.Plugin.EventSimulator
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (ContainsHelpFlag(args))
            {
                PrintUsage();
                return 0;
            }

            int intervalSeconds = 3;
            int sendCount = 0; // 0 = infinite
            int exitAfterSeconds = 0;
            string kernelHost = "127.0.0.1";
            int kernelPort = 9000;
            string pluginId = "EventSimulator";

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--interval":
                        intervalSeconds = int.Parse(args[++i]);
                        break;
                    case "--count":
                        sendCount = int.Parse(args[++i]);
                        break;
                    case "--exitAfterSeconds":
                        exitAfterSeconds = int.Parse(args[++i]);
                        break;
                }
            }

            Console.WriteLine($"EventSimulator connecting to {kernelHost}:{kernelPort} as '{pluginId}' (interval={intervalSeconds}s, count={(sendCount == 0 ? "infinite" : sendCount.ToString())})");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(kernelHost, kernelPort, cts.Token).ConfigureAwait(false);
                using var ns = client.GetStream();

                // Handshake
                var handshake = new Message();
                handshake.Header.MessageType = MessageTypes.Handshake;
                handshake.Metadata.PluginId = pluginId;
                handshake.Payload = JsonSerializer.SerializeToElement(new { Name = pluginId, Version = "0.1" });
                await SendMessage(ns, handshake, cts.Token).ConfigureAwait(false);

                // Start heartbeat in background
                _ = Task.Run(() => HeartbeatLoop(ns, pluginId, cts.Token));

                var rnd = new Random();
                var sent = 0;
                if (exitAfterSeconds > 0)
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(exitAfterSeconds), cts.Token)
                        .ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
                }
                while (!cts.IsCancellationRequested && (sendCount == 0 || sent < sendCount))
                {
                    // Alternate: send a UserLoggedInEvent then a DataProcessedEvent
                    var userEvent = new UserLoggedInEvent
                    {
                        UserId = rnd.Next(1, 10000),
                        Username = $"simuser{rnd.Next(1,100)}",
                        LoginTime = DateTime.UtcNow
                    };
                    var userMsg = new Message
                    {
                        Header = { MessageType = MessageTypes.CommandRequest },
                        Metadata = { PluginId = pluginId },
                        Payload = JsonSerializer.SerializeToElement(userEvent)
                    };
                    await SendMessage(ns, userMsg, cts.Token).ConfigureAwait(false);
                    Console.WriteLine($"Sent UserLoggedInEvent: {userEvent.Username}");

                    sent++;
                    if (sendCount != 0 && sent >= sendCount) break;
                    try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }

                    var dataEvent = new DataProcessedEvent
                    {
                        JobId = Guid.NewGuid(),
                        RecordCount = rnd.Next(10, 20000),
                        Duration = TimeSpan.FromSeconds(rnd.Next(1, 60)),
                        ProcessorName = $"sim-processor-{rnd.Next(1,5)}"
                    };
                    var dataMsg = new Message
                    {
                        Header = { MessageType = MessageTypes.CommandRequest },
                        Metadata = { PluginId = pluginId },
                        Payload = JsonSerializer.SerializeToElement(dataEvent)
                    };
                    await SendMessage(ns, dataMsg, cts.Token).ConfigureAwait(false);
                    Console.WriteLine($"Sent DataProcessedEvent: JobId={dataEvent.JobId}");

                    sent++;
                    if (sendCount != 0 && sent >= sendCount) break;
                    try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }

                Console.WriteLine("Finished sending simulated events.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Canceled.");
                return 0;
            }
            catch (ConnectionClosedException ex)
            {
                Console.WriteLine($"Connection closed by kernel: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex}");
                return 2;
            }
        }

        private static bool ContainsHelpFlag(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("EventSimulator plugin options:");
            Console.WriteLine("  --interval <seconds>     Delay between event payloads (default 3)");
            Console.WriteLine("  --count <n>              Number of payloads to send (0 = infinite)");
            Console.WriteLine("  --exitAfterSeconds <seconds>   Stop after the specified number of seconds");
            Console.WriteLine("  -h | --help              Display this usage information");
        }

        private static async Task SendMessage(NetworkStream ns, Message msg, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(msg);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            try
            {
                await ns.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await ns.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRemoteSocketClosed(ex))
            {
                throw new ConnectionClosedException(ex.Message, ex);
            }
        }

        private static async Task HeartbeatLoop(NetworkStream ns, string pluginId, CancellationToken ct)
        {
            int seq = 0;
            while (!ct.IsCancellationRequested)
            {
                var hb = new Message
                {
                    Header = { MessageType = MessageTypes.Heartbeat },
                    Metadata = { PluginId = pluginId, Sequence = ++seq },
                    Payload = JsonSerializer.SerializeToElement(new { Timestamp = DateTime.UtcNow })
                };
                try
                {
                    await SendMessage(ns, hb, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ConnectionClosedException)
                {
                    Console.WriteLine("Heartbeat loop stopped: kernel connection closed.");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Heartbeat send failed: {ex.Message}");
                    break;
                }

                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static bool IsRemoteSocketClosed(Exception ex)
        {
            return ex switch
            {
                IOException ioEx when ioEx.InnerException is SocketException sock => IsSocketClosed(sock),
                SocketException sockEx => IsSocketClosed(sockEx),
                _ => false
            };
        }

        private static bool IsSocketClosed(SocketException sock)
        {
            return sock.SocketErrorCode == SocketError.ConnectionReset
                   || sock.SocketErrorCode == SocketError.ConnectionAborted
                   || sock.SocketErrorCode == SocketError.Shutdown;
        }

        private sealed class ConnectionClosedException : Exception
        {
            public ConnectionClosedException(string message, Exception inner)
                : base(message, inner)
            {
            }
        }
    }
}