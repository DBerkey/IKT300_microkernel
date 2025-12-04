using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Configuration;
using IKT300.Shared.Models;

namespace IKT300.Plugin.UserWatcher
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            string? kernelHostOverride = null;
            int? kernelPortOverride = null;
            string pluginId = "UserWatcher";
            int pollSeconds = 2;
            string? configPathOverride = null;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--kernelHost":
                        kernelHostOverride = args[++i];
                        break;
                    case "--kernelPort":
                        kernelPortOverride = int.Parse(args[++i]);
                        break;
                    case "--pluginId":
                        pluginId = args[++i];
                        break;
                    case "--pollSeconds":
                        pollSeconds = int.Parse(args[++i]);
                        break;
                    case "--config":
                        configPathOverride = args[++i];
                        break;
                }
            }

            KernelConfig? sharedConfig = null;
            try
            {
                sharedConfig = KernelConfig.LoadFromDefaultLocations(configPathOverride);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Config load warning: {ex.Message}");
            }

            var kernelHost = kernelHostOverride ?? sharedConfig?.Host ?? "127.0.0.1";
            var kernelPort = kernelPortOverride ?? sharedConfig?.Port ?? 9000;

            Console.WriteLine($"Plugin {pluginId} connecting to {kernelHost}:{kernelPort}, polling every {pollSeconds}s");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(kernelHost, kernelPort, cts.Token);
                using var ns = client.GetStream();

                // Handshake
                var handshake = new Message();
                handshake.Header.MessageType = MessageTypes.Handshake;
                handshake.Metadata.PluginId = pluginId;
                handshake.Payload = JsonSerializer.SerializeToElement(new { Name = pluginId, Version = "0.1" });
                await SendMessage(ns, handshake, cts.Token);

                // Start heartbeat and watcher loops
                var hbTask = Task.Run(() => HeartbeatLoop(ns, pluginId, cts.Token));
                var watchTask = Task.Run(() => WatchUserLoop(ns, pluginId, pollSeconds, cts.Token));

                await Task.WhenAny(hbTask, watchTask);
                cts.Cancel();
                await Task.WhenAll(hbTask, watchTask);
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled exception: {ex}");
                return 2;
            }
        }

        private static async Task SendMessage(NetworkStream ns, Message msg, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(msg);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await ns.WriteAsync(bytes, 0, bytes.Length, ct);
            await ns.FlushAsync(ct);
        }

        // Poll-based watcher: detect change in Environment.UserName
        private static async Task WatchUserLoop(NetworkStream ns, string pluginId, int pollSeconds, CancellationToken ct)
        {
            string lastUser = Environment.UserName ?? string.Empty;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var current = Environment.UserName ?? string.Empty;
                    if (!string.Equals(current, lastUser, StringComparison.Ordinal))
                    {
                        lastUser = current;
                        var ev = new UserLoggedInEvent
                        {
                            UserId = current.GetHashCode(),
                            Username = current,
                            LoginTime = DateTime.UtcNow
                        };

                        var msg = new Message();
                        msg.Header.MessageType = MessageTypes.CommandRequest;
                        msg.Metadata.PluginId = pluginId;
                        msg.Payload = JsonSerializer.SerializeToElement(ev);

                        try
                        {
                            await SendMessage(ns, msg, ct);
                            Console.WriteLine($"Sent UserLoggedInEvent for '{current}'");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to send event: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Watcher error: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static async Task HeartbeatLoop(NetworkStream ns, string pluginId, CancellationToken ct)
        {
            int seq = 0;
            while (!ct.IsCancellationRequested)
            {
                var hb = new Message();
                hb.Header.MessageType = MessageTypes.Heartbeat;
                hb.Metadata.PluginId = pluginId;
                hb.Metadata.Sequence = ++seq;
                hb.Payload = JsonSerializer.SerializeToElement(new { Timestamp = DateTime.UtcNow });
                try
                {
                    await SendMessage(ns, hb, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Heartbeat send failed: {ex.Message}");
                    break;
                }

                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}