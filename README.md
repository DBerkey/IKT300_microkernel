# IKT300_microkernel

## Design Overview
- **Language**: C# (.NET 8.0) for strong typing, IPC/process APIs, and cross-platform support.
- **IPC**: TCP sockets with newline-delimited JSON framing (simple for prototyping; upgrade later to length-prefix/TLS/HTTP2).
- **Serialization**: `System.Text.Json` for language-agnostic interoperability.
- **Message Schema**: Every message has `Header`, `Metadata`, and `Payload`. `Header.MessageType` drives handling logic.
- **Plugin Interface**: Plugins run as independent processes, send a Handshake on connect, then periodic Heartbeats. Kernel monitors `LastReceived` timestamps, restarts crashed or silent plugins, and exposes a console for managing plugin lifecycles.

## Message Schema
Messages are newline-delimited JSON objects sent over TCP.

### Header
- `MessageType` (`Handshake`, `Heartbeat`, `CommandRequest`, `CommandResponse`, `Error`)
- `MessageId` (GUID)
- `Timestamp` (ISO8601)
- `CorrelationId` (optional GUID for request/response linking)

### Metadata
- `PluginId` (string identifier)
- `Sender` (optional)
- `Sequence` (optional integer sequence)

### Payload
- Arbitrary JSON structure depending on `MessageType`.

**Example**
```json
{
	"Header": {
		"MessageType": "Heartbeat",
		"MessageId": "6b3f3b56-c2bf-4f1c-9afa-1b21f2f6b0e8",
		"Timestamp": "2025-12-02T10:35:00Z"
	},
	"Metadata": {
		"PluginId": "SamplePlugin",
		"Sequence": 5
	},
	"Payload": { "Time": "2025-12-02T10:35:00Z" }
}
```

## Developer Guide

### Prerequisites
- .NET SDK 8.0: <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>
- Windows PowerShell 5.1 or any shell capable of running the .NET CLI commands below.

### Build
Typical local workflow from the repo root:

```powershell
# Restore and compile every project
dotnet build src/IKT300.Solution/IKT300.sln

# Build for the event simulator for testing the working of the logger
dotnet build src/IKT300.Plugin.EventSimulator/IKT300.Plugin.EventSimulator.csproj
```

### Run Microkernel
```powershell
dotnet run --project src/IKT300.Microkernel/IKT300.Microkernel.csproj
```

The kernel reads `kernelconfig.json`, opens the configured TCP endpoint (default `127.0.0.1:9000`), and supervises plugins defined in that configuration. While running, the console accepts:
- `help [pluginId]` –  display command usage of kernel or plugin
- `list` – show known plugins and last heartbeat
- `kill <pluginId>` – stop a plugin process
- `start <pluginId> [args]` – start or restart a plugin, appending any extra CLI args after the kernel-injected defaults
- `exit` / `quit` – gracefully stop the kernel (Ctrl+C also works)

### Run Event Simulator
The simulator plugin generates `UserLoggedInEvent`/`DataProcessedEvent` traffic and heartbeats so the kernel and MetricLogger can be tested together. This only works if the EventSimulator was built.

From the kernel console:

```powershell
start EventSimulator --count 5 --interval 2
```

View simulator help via `help EventSimulator` or `EventSimulator -h` when the kernel is running.

Flags (all optional):
- `--interval <seconds>` – delay between payload types (default 3)
- `--count <n>` – total messages before exiting (0 ⇒ infinite)
- `--exitAfterSeconds <n>` – stop after the specified number of seconds, regardless of count

### Run Metric Logger
The MetricLogger plugin consumes simulator events and writes structured rows to `metrics/metrics.log`.

```powershell
start MetricLogger
```

View MetricLogger help with `help MetricLogger` or `MetricLogger -h` when the kernel is running.

Useful flag:
- `--exitAfterSeconds <n>` – stop after the specified number of seconds

### Kernel Console Commands
- `help [pluginId]` – display command usage of kernel or plugin
- `list` – show registered plugins and status
- `kill <pluginId>` – terminate plugin process (kernel may restart it)
- `start <pluginId> [args]` – start plugin if not running, optionally appending custom CLI args
- `exit` / `quit` – gracefully stop the kernel (Ctrl+C also works)

### Kernel Configuration
`src/kernelconfig.json` controls the kernel endpoint and plugin catalog. Example:

```json
{
	"host": "127.0.0.1",
	"port": 9000,
	"plugins": [
		{
			"pluginId": "MetricLogger",
			"workingDirectory": "IKT300.Plugin.MetricLogger",
			"autoStart": true
		},
		{
			"pluginId": "EventSimulator",
			"workingDirectory": "IKT300.Plugin.EventSimulator",
			"autoStart": false
		}
	]
}
```

Field highlights:
- `workingDirectory` – relative or absolute path to the plugin project/dll folder.
- `autoStart` – when `true` the kernel launches (and auto-restarts) the plugin on startup; when `false` the kernel leaves it stopped unless you issue `start <pluginId>`.
- `pluginId` – identifier used in kernel commands and metadata.

## Adding a New Plugin (Guide)
This section explains the recommended steps, conventions, and checklist to follow when creating and registering a new plugin so it integrates cleanly with the kernel.

1. Choose a name and project layout
   - Use the convention: IKT300.Plugin.<YourPluginName> (e.g. IKT300.Plugin.AuditTrail).
   - Place the project under src/ (e.g. src/IKT300.Plugin.AuditTrail).
   - Keep the plugin as an independent process (console app) that connects to the kernel over TCP.

2. Create the project and add to solution
   - From the repo root:

```powershell
dotnet new console -o src/IKT300.Plugin.YourPlugin
dotnet sln src/IKT300.Solution/IKT300.sln add src/IKT300.Plugin.YourPlugin/IKT300.Plugin.YourPlugin.csproj
```

3. Reference shared types
   - Add a project reference to IKT300.Shared so you reuse the message schema and helpers:

```powershell
dotnet add src/IKT300.Plugin.YourPlugin/IKT300.Plugin.YourPlugin.csproj reference src/IKT300.Shared/IKT300.Shared.csproj
```

4. Implement the plugin protocol
   - On startup, connect to the kernel's host/port (kernel injects connection details via CLI args or config).
   - Immediately send a Handshake message containing your PluginId and any capabilities or version metadata.
   - Send Heartbeat messages periodically (e.g., every 3–10 seconds) so the kernel can monitor liveness.
   - Listen for CommandRequest messages and respond with CommandResponse messages where appropriate.
   - Use newline-delimited JSON framing and System.Text.Json to serialize/deserialize messages.

Example minimal flow (pseudocode):
- connect()
- send(Handshake{ Metadata.PluginId = "YourPlugin" })
- loop: read incoming messages -> handle CommandRequest; periodically send Heartbeat

5. Accept standard CLI arguments
   - To make the plugin launchable by the kernel, support the standard arguments the kernel provides. Typical fields include:
     --host <kernel-host>
     --port <kernel-port>
     --pluginId <PluginId>
   - Also support a `-h`/`--help` flag and any plugin-specific flags.
   - The kernel `start <pluginId> [args]` will append extra args you provide on the command line.

6. Graceful shutdown and error handling
   - Observe Ctrl+C / SIGTERM and attempt a graceful shutdown: stop sending heartbeats, close the TCP connection, and exit with a sensible code.
   - Catch unhandled exceptions at the top-level and optionally send an `Error` message to the kernel before exiting.

7. Register the plugin with the kernel
   - Add an entry to src/kernelconfig.json for your plugin:

```json
{
  "pluginId": "YourPlugin",
  "workingDirectory": "IKT300.Plugin.YourPlugin",
  "autoStart": false
}
```

   - If you want the kernel to start your plugin automatically on kernel startup, set `autoStart` to `true`.

8. Build and test
   - Build your plugin project and ensure it runs standalone:

```powershell
dotnet build src/IKT300.Plugin.YourPlugin/IKT300.Plugin.YourPlugin.csproj
dotnet run --project src/IKT300.Plugin.YourPlugin/IKT300.Plugin.YourPlugin.csproj -- --host 127.0.0.1 --port 9000 --pluginId YourPlugin
```

   - Start the kernel and either allow `autoStart` or use the kernel console `start YourPlugin` command. Use `list` to verify heartbeats.

9. Useful practices
   - Copy the EventSimulator or MetricLogger implementation as a starting point; they demonstrate handshake, heartbeat, message handling, and CLI parsing.
   - Keep plugin responsibilities small: prefer single concern plugins that can be composed through the message bus.
   - Add logs and telemetry files to a `logs/` or `metrics/` folder inside your plugin's working directory so the kernel's supervisor can find output.
   - Add unit tests for message serialization and key logic where possible.

10. Example kernelconfig snippet (full file)

```json
{
	"host": "127.0.0.1",
	"port": 9000,
	"plugins": [
		{
			"pluginId": "MetricLogger",
			"workingDirectory": "IKT300.Plugin.MetricLogger",
			"autoStart": true
		},
		{
			"pluginId": "EventSimulator",
			"workingDirectory": "IKT300.Plugin.EventSimulator",
			"autoStart": false
		},
		{
			"pluginId": "YourPlugin",
			"workingDirectory": "IKT300.Plugin.YourPlugin",
			"autoStart": false
		}
	]
}
```

Follow this checklist when opening a pull request to add a plugin:
- [ ] Plugin project added under src/ and included in the solution.
- [ ] Reference to IKT300.Shared added.
- [ ] README or short usage notes added inside the plugin directory.
- [ ] kernelconfig.json updated (or document required manual change).
- [ ] Basic integration test run: plugin connects, handshake accepted, heartbeats received.

## Repository Structure
- `IKT300.Microkernel`: kernel host managing TCP connections, heartbeats, and plugin lifecycles.
- `IKT300.Plugin.EventSimulator`: traffic generator plugin used for local testing.
- `IKT300.Plugin.MetricLogger`: plugin that logs received events to disk and console.
- `IKT300.Shared`: shared message schema, configuration types, and helpers.

# Credits

This code was made for the microkernel assignment for IKT300 at the UIA and by Julien Bailleul, Douwe Berkeij, Mathieu Bour, Achille Papin.

### AI usage
To make the developmet more fluent there was made use of Github copilot GPT 5.1 codex (preview) for generating documentation, bug fixing and line completion. No full pieces of code were generated that replaced human work; outputs were used as assistance and curated by the authors.
