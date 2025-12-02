using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Models;

namespace IKT300.Plugin.Sample
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Simple args parser
            string kernelHost = "127.0.0.1";
            int kernelPort = 9000;
            string pluginId = "SamplePlugin";

            int exitAfterSeconds = 0;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--kernelHost": kernelHost = args[++i]; break;
                    case "--kernelPort": kernelPort = int.Parse(args[++i]); break;
                    case "--pluginId": pluginId = args[++i]; break;
                    case "--exitAfterSeconds": exitAfterSeconds = int.Parse(args[++i]); break;
                }
            }

            Console.WriteLine($"Plugin {pluginId} connecting to {kernelHost}:{kernelPort}");

            var cts = new CancellationTokenSource();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(kernelHost, kernelPort);
                using var ns = client.GetStream();

                // Send Handshake
                var handshake = new Message();
                handshake.Header.MessageType = MessageTypes.Handshake;
                handshake.Metadata.PluginId = pluginId;
                handshake.Payload = JsonSerializer.SerializeToElement(new { Name = "SamplePlugin", Version = "0.1" });
                await SendMessage(ns, handshake);

                // Start read loop and heartbeat loop
                _ = Task.Run(() => ReadLoop(ns, cts.Token));
                _ = Task.Run(() => HeartbeatLoop(ns, pluginId, cts.Token));

                if (exitAfterSeconds > 0)
                {
                    Console.WriteLine($"Plugin will exit after {exitAfterSeconds} seconds (simulated crash)");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(exitAfterSeconds), cts.Token);
                    }
                    catch (TaskCanceledException) { }
                    Console.WriteLine("Exiting now (simulated crash)");
                    cts.Cancel();
                }
                else
                {
                    Console.WriteLine("Plugin running; waiting indefinitely. Use Ctrl+C or kernel to kill.");
                    try
                    {
                        await Task.Delay(-1, cts.Token);
                    }
                    catch (TaskCanceledException) { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plugin error: {ex}");
                return 1;
            }
            return 0;
        }

        static async Task HeartbeatLoop(NetworkStream ns, string pluginId, CancellationToken token)
        {
            var seq = 0;
            while (!token.IsCancellationRequested)
            {
                var hb = new Message();
                hb.Header.MessageType = MessageTypes.Heartbeat;
                hb.Metadata.PluginId = pluginId;
                hb.Metadata.Sequence = ++seq;
                hb.Payload = JsonSerializer.SerializeToElement(new { Time = DateTime.UtcNow });
                await SendMessage(ns, hb);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }

        static async Task ReadLoop(NetworkStream ns, CancellationToken token)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            while (!token.IsCancellationRequested)
            {
                int read = 0;
                try
                {
                    read = await ns.ReadAsync(buffer, 0, buffer.Length, token);
                }
                catch
                {
                    break;
                }
                if (read == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var content = sb.ToString();
                int newlineIndex;
                while ((newlineIndex = content.IndexOf('\n')) >= 0)
                {
                    var msgJson = content.Substring(0, newlineIndex).Trim();
                    content = content.Substring(newlineIndex + 1);
                    if (!string.IsNullOrEmpty(msgJson))
                    {
                        try
                        {
                            var msg = Message.FromJson(msgJson);
                            Console.WriteLine($"Received: {msg.Header.MessageType}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Invalid incoming message: {ex.Message}");
                        }
                    }
                }
                sb.Clear();
                sb.Append(content);
            }
            Console.WriteLine("Read loop ended.");
        }

        static async Task SendMessage(NetworkStream ns, Message msg)
        {
            var json = JsonSerializer.Serialize(msg);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            try
            {
                await ns.WriteAsync(bytes, 0, bytes.Length);
                await ns.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send error: {ex.Message}");
            }
        }
    }
}
