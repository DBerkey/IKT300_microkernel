using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Models;

namespace IKT300.Microkernel
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("IKT300 Microkernel starting...");

            var kernel = new KernelServer(IPAddress.Loopback, 9000);
            await kernel.StartAsync();

            Console.WriteLine("Press Enter to shut down the kernel.");
            Console.ReadLine();

            await kernel.StopAsync();
        }
    }

    public class KernelServer
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, PluginInstance> _plugins = new ConcurrentDictionary<string, PluginInstance>();
        private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(8);

        public KernelServer(IPAddress address, int port)
        {
            _listener = new TcpListener(address, port);
            Console.WriteLine($"Kernel listening on {address}:{port}");
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
            _ = Task.Run(HeartbeatMonitorLoop);
            Console.WriteLine("Kernel started.");

            // Example: Start a sample plugin process; in a real system you'll discover plugins by config
            StartPluginProcess("SamplePlugin");
            _ = Task.Run(CommandLoopAsync);
        }

        public Task StopAsync()
        {
            _cts.Cancel();
            foreach (var p in _plugins.Values)
            {
                p.Kill();
            }
            _listener.Stop();
            return Task.CompletedTask;
        }

        private void StartPluginProcess(string pluginId)
        {
            // TODO: configurable path, args etc.
            var exe = "dotnet"; // run sample plugin via dotnet run
            var workingDir = System.IO.Path.Combine("..", "IKT300.Plugin.Sample");
            workingDir = System.IO.Path.GetFullPath(workingDir);
            // NOTE: For demonstration: plugin will exit after 12 seconds to simulate a crash and test restart logic.
            // Prefer running the compiled plugin dll if present; fallback to dotnet run
            var pluginDll = System.IO.Path.Combine(workingDir, "bin", "Debug", "net8.0", "IKT300.Plugin.Sample.dll");
            bool hasDll = System.IO.File.Exists(pluginDll);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // No forced exit by default; pass exitAfterSeconds=0 to disable simulated crash
                Arguments = hasDll
                    ? $"\"{pluginDll}\" --kernelHost 127.0.0.1 --kernelPort 9000 --pluginId {pluginId} --exitAfterSeconds 0"
                    : $"run --project \"{workingDir}\" -- --kernelHost 127.0.0.1 --kernelPort 9000 --pluginId {pluginId} --exitAfterSeconds 0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            Console.WriteLine($"Starting plugin {pluginId}");
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (s, e) => { if (e.Data is not null) Console.WriteLine($"[Plugin:{pluginId}] {e.Data}"); };
            p.ErrorDataReceived += (s, e) => { if (e.Data is not null) Console.WriteLine($"[Plugin:{pluginId} ERR] {e.Data}"); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var pluginInst = new PluginInstance(pluginId, p);
            _plugins[pluginId] = pluginInst;
            Console.WriteLine($"Plugin process for {pluginId} started (pid:{p.Id}), workingDir:{workingDir}");
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    Console.WriteLine("Accept error: " + ex.Message);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var endPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Console.WriteLine($"Got client connection {endPoint}");
            using var ns = client.GetStream();
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            while (client.Connected && !_cts.IsCancellationRequested)
            {
                int read = 0;
                try
                {
                    read = await ns.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                }
                catch (Exception)
                {
                    break;
                }
                if (read == 0) break; // closed
                var s = Encoding.UTF8.GetString(buffer, 0, read);
                sb.Append(s);
                // Very simple framing: messages separated by newline
                string content = sb.ToString();
                int newlineIndex;
                while ((newlineIndex = content.IndexOf('\n')) >= 0)
                {
                    var msgJson = content.Substring(0, newlineIndex).Trim();
                    content = content.Substring(newlineIndex + 1);
                    if (!string.IsNullOrWhiteSpace(msgJson))
                    {
                        try
                        {
                            var msg = Message.FromJson(msgJson);
                            await ProcessMessageAsync(msg, ns, client);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Invalid message: " + ex.Message);
                        }
                    }
                }
                sb.Clear();
                sb.Append(content);
            }
            Console.WriteLine($"Connection closed {endPoint}");
        }

        private async Task ProcessMessageAsync(Message message, NetworkStream ns, TcpClient client)
        {
            // Simplified logic: find plugin by id, update heartbeat, etc.
            var pluginId = message.Metadata.PluginId;
            if (string.IsNullOrEmpty(pluginId)) return;

            if (!_plugins.TryGetValue(pluginId, out var plugin))
            {
                // Unknown plugin, register connection if there is a process for it
                plugin = new PluginInstance(pluginId, null) { Connection = client };
                _plugins[pluginId] = plugin;
            }

            plugin.LastReceived = DateTime.UtcNow;

            switch (message.Header.MessageType)
            {
                case MessageTypes.Handshake:
                    Console.WriteLine($"Handshake from {pluginId}");
                    plugin.Connected = true;
                    // Optionally send acknowledgement
                    var ack = new Message();
                    ack.Header.MessageType = MessageTypes.CommandResponse;
                    ack.Metadata.PluginId = "kernel";
                    ack.Payload = JsonSerializer.SerializeToElement(new { OK = true, Received = message.Header.MessageId });
                    await SendMessageOverStreamAsync(ns, ack);
                    break;
                case MessageTypes.Heartbeat:
                    Console.WriteLine($"Heartbeat from {pluginId}");
                    // update plugin last seen
                    break;
                case MessageTypes.CommandRequest:
                    Console.WriteLine($"CommandRequest from {pluginId}");
                    // handle plugin request if needed
                    break;
                default:
                    Console.WriteLine($"Unknown message type {message.Header.MessageType} from {pluginId}");
                    break;
            }
        }

        private async Task SendMessageOverStreamAsync(NetworkStream ns, Message msg)
        {
            var json = JsonSerializer.Serialize(msg);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await ns.WriteAsync(bytes, 0, bytes.Length);
        }

        private async Task HeartbeatMonitorLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _plugins)
                {
                    var id = kv.Key;
                    var inst = kv.Value;
                    if (inst.Process is not null && !inst.Process.HasExited)
                    {
                        if (inst.LastReceived + _heartbeatTimeout < now)
                        {
                            Console.WriteLine($"Plugin {id} missed heartbeat. Restarting plugin...");
                            await RestartPluginAsync(id, inst);
                        }
                    }
                    else
                    {
                        // plugin has no process or process exited: try to restart
                        if (inst.Process is not null && inst.Process.HasExited)
                        {
                            Console.WriteLine($"Process for plugin {id} has exited. Restarting...");
                            _ = Task.Run(() => StartPluginProcess(id));
                        }
                    }
                }

                await Task.Delay(1000, _cts.Token);
            }
        }

        private async Task CommandLoopAsync()
        {
            Console.WriteLine("Commands: list | kill <pluginId> | start <pluginId>");
            while (!_cts.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1] : string.Empty;
                switch (cmd)
                {
                    case "list":
                        foreach (var kv in _plugins)
                        {
                            var i = kv.Value;
                            Console.WriteLine($"{kv.Key} connected={i.Connected} lastseen={i.LastReceived} pid={i.Process?.Id}");
                        }
                        break;
                    case "kill":
                        if (_plugins.TryGetValue(arg, out var p))
                        {
                            Console.WriteLine($"Killing plugin {arg}");
                            p.Kill();
                        }
                        break;
                    case "start":
                        if (!_plugins.ContainsKey(arg)) StartPluginProcess(arg);
                        else Console.WriteLine($"Plugin {arg} exists; use kill to restart.");
                        break;
                }
            }
        }

        private Task RestartPluginAsync(string id, PluginInstance inst)
        {
            try
            {
                if (inst.Process is not null && !inst.Process.HasExited)
                {
                    Console.WriteLine($"Killing plugin process {id}");
                    inst.Kill();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error killing plugin {id}: {ex.Message}");
            }
            // Remove connection
            if (inst.Connection?.Connected == true)
            {
                try { inst.Connection.Close(); } catch { }
            }

            // Start new plugin instance
            StartPluginProcess(id);
            return Task.CompletedTask;
        }
    }

    public class PluginInstance
    {
        public string PluginId { get; set; }
        public Process? Process { get; set; }
        public TcpClient? Connection { get; set; }
        public bool Connected { get; set; }
        public DateTime LastReceived { get; set; } = DateTime.MinValue;

        public PluginInstance(string pluginId, Process? process)
        {
            PluginId = pluginId;
            Process = process;
        }

        public void Kill()
        {
            if (Process?.HasExited == false)
            {
                try
                {
                    Process.Kill(true);
                    Process.WaitForExit(5000);
                }
                catch (Exception) { }
            }
        }
    }
}
