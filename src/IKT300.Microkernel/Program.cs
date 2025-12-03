using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IKT300.Shared.Configuration;
using IKT300.Shared.Models;

namespace IKT300.Microkernel
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("IKT300 Microkernel starting...");

            string? configPath = args.Length > 0 ? args[0] : null;
            KernelConfig config;
            try
            {
                config = KernelConfig.LoadFromDefaultLocations(configPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load configuration: {ex.Message}");
                return;
            }

            var kernel = new KernelServer(config);
            await kernel.StartAsync();

            Console.WriteLine("Press Enter to shut down the kernel.");
            Console.ReadLine();

            await kernel.StopAsync();
        }
    }

    public class KernelServer
    {
        private readonly KernelConfig _config;
        private readonly Dictionary<string, PluginConfig> _pluginConfigs;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, PluginInstance> _plugins = new ConcurrentDictionary<string, PluginInstance>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(8);

        public KernelServer(KernelConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _pluginConfigs = _config.Plugins
                .Where(p => !string.IsNullOrWhiteSpace(p.PluginId))
                .ToDictionary(p => p.PluginId, StringComparer.OrdinalIgnoreCase);

            if (!IPAddress.TryParse(_config.Host, out var address))
            {
                Console.WriteLine($"Invalid host '{_config.Host}'. Falling back to loopback.");
                address = IPAddress.Loopback;
            }

            _listener = new TcpListener(address, _config.Port);
            Console.WriteLine($"Kernel listening on {address}:{_config.Port}");
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
            _ = Task.Run(HeartbeatMonitorLoop);
            Console.WriteLine("Kernel started.");

            foreach (var plugin in _pluginConfigs.Values.Where(p => p.Enabled))
            {
                StartPluginProcess(plugin.PluginId);
            }
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
            if (!_pluginConfigs.TryGetValue(pluginId, out var pluginConfig))
            {
                Console.WriteLine($"No configuration found for plugin '{pluginId}'.");
                return;
            }

            try
            {
                var psi = CreatePluginStartInfo(pluginConfig);
                Console.WriteLine($"Starting plugin {pluginId}");
                var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (s, e) => { if (e.Data is not null) Console.WriteLine($"[Plugin:{pluginId}] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data is not null) Console.WriteLine($"[Plugin:{pluginId} ERR] {e.Data}"); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var pluginInst = new PluginInstance(pluginId, process);
                _plugins[pluginId] = pluginInst;
                Console.WriteLine($"Plugin process for {pluginId} started (pid:{process.Id}), workingDir:{psi.WorkingDirectory}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start plugin {pluginId}: {ex.Message}");
            }
        }

        private ProcessStartInfo CreatePluginStartInfo(PluginConfig pluginConfig)
        {
            var workingDir = ResolvePath(pluginConfig.WorkingDirectory);
            if (!Directory.Exists(workingDir))
            {
                throw new DirectoryNotFoundException($"Plugin directory not found: {workingDir}");
            }

            var compiledDll = ResolveCompiledDllPath(pluginConfig, workingDir);
            var hasCompiledDll = compiledDll is not null && File.Exists(compiledDll);

            var configArg = string.IsNullOrWhiteSpace(_config.ConfigPath)
                ? string.Empty
                : $" --config \"{_config.ConfigPath}\"";
            var commonArgs = $"--kernelHost {_config.Host} --kernelPort {_config.Port} --pluginId {pluginConfig.PluginId} --exitAfterSeconds {pluginConfig.ExitAfterSeconds}{configArg}";

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                Arguments = hasCompiledDll
                    ? $"\"{compiledDll}\" {commonArgs}"
                    : $"run --project \"{workingDir}\" -- {commonArgs}"
            };

            if (!hasCompiledDll)
            {
                var message = compiledDll is not null
                    ? $"Compiled plugin not found at {compiledDll}."
                    : "No compiled plugin DLL detected.";
                Console.WriteLine($"{message} Falling back to 'dotnet run'.");
            }

            return psi;
        }

        private string? ResolveCompiledDllPath(PluginConfig pluginConfig, string workingDir)
        {
            if (!string.IsNullOrWhiteSpace(pluginConfig.DllRelativePath))
            {
                return Path.GetFullPath(Path.Combine(workingDir, pluginConfig.DllRelativePath));
            }

            var probableAssemblyName = Path.GetFileName(workingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var candidateDirs = new[]
            {
                Path.Combine(workingDir, "bin", "Debug", "net8.0"),
                Path.Combine(workingDir, "bin", "Release", "net8.0"),
                Path.Combine(workingDir, "bin", "Debug"),
                Path.Combine(workingDir, "bin", "Release")
            };

            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;

                string? dll = TryCandidateDlls(dir, probableAssemblyName, pluginConfig.PluginId);
                if (dll is not null)
                {
                    return dll;
                }
            }

            return null;
        }

        private static string? TryCandidateDlls(string directory, string? folderName, string pluginId)
        {
            var candidates = new List<string?>();
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                candidates.Add(Path.Combine(directory, folderName + ".dll"));
            }
            if (!string.Equals(folderName, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(directory, pluginId + ".dll"));
            }

            foreach (var candidate in candidates)
            {
                if (candidate is not null && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            try
            {
                var anyDll = Directory.GetFiles(directory, "*.dll").FirstOrDefault();
                return anyDll;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("Plugin working directory is not set in configuration.");
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            string? firstCandidate = null;
            foreach (var baseDir in EnumerateSearchBases())
            {
                if (string.IsNullOrWhiteSpace(baseDir)) continue;
                var candidate = Path.GetFullPath(Path.Combine(baseDir, path));
                firstCandidate ??= candidate;
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return firstCandidate ?? Path.GetFullPath(path);
        }

        private IEnumerable<string?> EnumerateSearchBases()
        {
            var dir = _config.ConfigDirectory;
            while (!string.IsNullOrWhiteSpace(dir))
            {
                yield return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            yield return Directory.GetCurrentDirectory();
            yield return AppContext.BaseDirectory;
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
                        if (string.IsNullOrWhiteSpace(arg))
                        {
                            Console.WriteLine("Usage: start <pluginId>");
                            break;
                        }

                        if (!_pluginConfigs.ContainsKey(arg))
                        {
                            Console.WriteLine($"Unknown plugin '{arg}'. Known plugins: {string.Join(", ", _pluginConfigs.Keys)}");
                            break;
                        }

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
            if (_pluginConfigs.ContainsKey(id))
            {
                StartPluginProcess(id);
            }
            else
            {
                Console.WriteLine($"Cannot restart plugin {id}: no configuration found.");
            }
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
