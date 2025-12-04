using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Configuration;
using IKT300.Shared.Models;

namespace IKT300.Plugin.MetricLogger
{
    internal static class Program
    {
        private static readonly object _metricsFileLock = new();
        private static readonly string _metricsLog = Path.Combine(AppContext.BaseDirectory, "metriclogger-metrics.log");
        private static int _userLoggedInCount;
        private static int _dataProcessedCount;

        private static async Task<int> Main(string[] args)
        {
            string? kernelHostOverride = null;
            int? kernelPortOverride = null;
            int? exitAfterSecondsOverride = null;
            string? configPathOverride = null;
            string pluginId = "MetricLogger";

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
                    case "--exitAfterSeconds":
                        exitAfterSecondsOverride = int.Parse(args[++i]);
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

            var exitAfterSeconds = exitAfterSecondsOverride
                ?? sharedConfig?.Plugins
                    .FirstOrDefault(p => string.Equals(p.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))?
                    .ExitAfterSeconds
                ?? 0;

            Console.WriteLine($"Plugin {pluginId} connecting to {kernelHost}:{kernelPort}");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(kernelHost, kernelPort, cts.Token);
                using var ns = client.GetStream();

                // Send Handshake
                var handshake = new Message();
                handshake.Header.MessageType = MessageTypes.Handshake;
                handshake.Metadata.PluginId = pluginId;
                handshake.Payload = JsonSerializer.SerializeToElement(new { Name = "MetricLogger", Version = "0.1" });
                await SendMessage(ns, handshake, cts.Token);

                // Start read loop and heartbeat loop � both run until cancellation.
                var readTask = Task.Run(() => ReadLoop(ns, pluginId, cts.Token));
                var hbTask = Task.Run(() => HeartbeatLoop(ns, pluginId, cts.Token));

                if (exitAfterSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(exitAfterSeconds), cts.Token);
                    cts.Cancel();
                }

                await Task.WhenAny(readTask, hbTask);
                cts.Cancel();
                await Task.WhenAll(readTask, hbTask);
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

        private static async Task ReadLoop(NetworkStream ns, string pluginId, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await ns.ReadAsync(buffer, 0, buffer.Length, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Read error: {ex.Message}");
                    break;
                }

                if (read == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                string content = sb.ToString();
                int newlineIndex;
                while ((newlineIndex = content.IndexOf('\n')) >= 0)
                {
                    var line = content.Substring(0, newlineIndex).Trim();
                    content = content.Substring(newlineIndex + 1);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            var msg = Message.FromJson(line);
                            Console.WriteLine($"Kernel -> {msg.Header.MessageType}");

                            // Only handle CommandRequest event messages targeted at this plugin or broadcasted.
                            if (string.Equals(msg.Header.MessageType, MessageTypes.CommandRequest, StringComparison.OrdinalIgnoreCase))
                            {
                                if (msg.Payload.HasValue && msg.Payload.Value.ValueKind == JsonValueKind.Object)
                                {
                                    // Pass the whole message so we can include metadata.sender in logs
                                    DispatchEventPayload(msg);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Invalid message from kernel: {ex.Message}");
                        }
                    }
                }
                sb.Clear();
                sb.Append(content);
            }
            Console.WriteLine("Read loop ended.");
        }

        private static void DispatchEventPayload(Message msg)
        {
            var payload = msg.Payload;
            var sender = string.IsNullOrWhiteSpace(msg.Metadata.PluginId) ? "unknown" : msg.Metadata.PluginId;
            if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("Dispatch: payload missing or not an object");
                return;
            }

            try
            {
                var obj = payload.Value;

                // Match UserLoggedInEvent shape
                if (obj.TryGetProperty("UserId", out _) && obj.TryGetProperty("Username", out _) && obj.TryGetProperty("LoginTime", out _))
                {
                    try
                    {
                        var ev = obj.Deserialize<UserLoggedInEvent>();
                        if (ev is not null) HandleUserLoggedIn(ev, sender);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to deserialize UserLoggedInEvent: {ex.Message}");
                    }
                    return;
                }

                // Match DataProcessedEvent shape
                if (obj.TryGetProperty("JobId", out _) && obj.TryGetProperty("RecordCount", out _) && obj.TryGetProperty("Duration", out _))
                {
                    try
                    {
                        var ev = obj.Deserialize<DataProcessedEvent>();
                        if (ev is not null) HandleDataProcessed(ev, sender);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to deserialize DataProcessedEvent: {ex.Message}");
                    }
                    return;
                }

                // Unknown event
                Console.WriteLine($"Received unknown event payload from {sender}: " + obj.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error dispatching payload: {ex.Message}");
            }
        }

        private static void HandleUserLoggedIn(UserLoggedInEvent ev, string sender)
        {
            Interlocked.Increment(ref _userLoggedInCount);
            var line = $"{DateTime.UtcNow:O}\tUserLoggedIn\tSender={sender}\tUserId={ev.UserId}\tUsername={ev.Username}\tLoginTime={ev.LoginTime:O}\tCount={_userLoggedInCount}";
            Console.WriteLine(line);
            AppendMetricLine(line);
        }

        private static void HandleDataProcessed(DataProcessedEvent ev, string sender)
        {
            Interlocked.Increment(ref _dataProcessedCount);
            var line = $"{DateTime.UtcNow:O}\tDataProcessed\tSender={sender}\tJobId={ev.JobId}\tRecordCount={ev.RecordCount}\tDuration={ev.Duration}\tProcessor={ev.ProcessorName}\tCount={_dataProcessedCount}";
            Console.WriteLine(line);
            AppendMetricLine(line);
        }

        private static void AppendMetricLine(string line)
        {
            try
            {
                lock (_metricsFileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_metricsLog) ?? AppContext.BaseDirectory);
                    File.AppendAllText(_metricsLog, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write metrics log: {ex.Message}");
            }
        }

        private static async Task HeartbeatLoop(NetworkStream ns, string pluginId, CancellationToken ct)
        {
            int seq = 0;
            while (!ct.IsCancellationRequested)
            {
                var msg = new Message();
                msg.Header.MessageType = MessageTypes.Heartbeat;
                msg.Metadata.PluginId = pluginId;
                msg.Metadata.Sequence = ++seq;
                msg.Payload = JsonSerializer.SerializeToElement(new { Timestamp = DateTime.UtcNow });
                try
                {
                    await SendMessage(ns, msg, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Heartbeat send failed: {ex.Message}");
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            Console.WriteLine("Heartbeat loop ended.");
        }
    }
}