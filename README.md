# IKT300_microkernel

## Design Overview
- **Language**: C# (.NET 8.0) for strong typing, IPC/process APIs, and cross-platform support.
- **IPC**: TCP sockets with newline-delimited JSON framing (simple for prototyping; upgrade later to length-prefix/TLS/HTTP2).
- **Serialization**: `System.Text.Json` for language-agnostic interoperability.
- **Message Schema**: Every message has `Header`, `Metadata`, and `Payload`. `Header.MessageType` drives handling logic.
- **Plugin Interface**: Plugins run as independent processes, send a Handshake on connect, then periodic Heartbeats. Kernel monitors `LastReceived` timestamps, restarts crashed or silent plugins, and offers interactive commands (`list`, `kill <id>`, `start <id>`).

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
The simulator plugin generates `UserLoggedInEvent`/`DataProcessedEvent` traffic and heartbeats so the kernel and MetricLogger can be tested together.

From the kernel console:

```powershell
start EventSimulator --count 5 --interval 2
```

Run it directly during development:

```powershell
dotnet run --project src/IKT300.Plugin.EventSimulator/IKT300.Plugin.EventSimulator.csproj -- --interval 2 --count 5
```

View simulator help via `help EventSimulator`, `EventSimulator -h`, or `dotnet run -- … -- --help`.

Flags (all optional):
- `--interval <seconds>` – delay between payload types (default 3)
- `--count <n>` – total messages before exiting (0 ⇒ infinite)
- `--exitAfterSeconds <n>` – stop after the specified number of seconds, regardless of count

### Run Metric Logger
The MetricLogger plugin consumes simulator events and writes structured rows to `metrics/metrics.log`.

```powershell
start MetricLogger
```

Or run directly from the command line:

```powershell
dotnet run --project src/IKT300.Plugin.MetricLogger/IKT300.Plugin.MetricLogger.csproj -- --pluginId MetricLogger --exitAfterSeconds 20
```

View MetricLogger help with `help MetricLogger` or `MetricLogger -h` when the kernel is running.

Useful flags:
- `--kernelHost <host>` / `--kernelPort <port>` – override the connection target
- `--pluginId <id>` – change the identity reported to the kernel
- `--exitAfterSeconds <n>` – stop after the specified number of seconds
- `--config <path>` – load kernel defaults from another config file

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

## Repository Structure
- `IKT300.Microkernel`: kernel host managing TCP connections, heartbeats, and plugin lifecycles.
- `IKT300.Plugin.EventSimulator`: traffic generator plugin used for local testing.
- `IKT300.Plugin.MetricLogger`: plugin that logs received events to disk and console.
- `IKT300.Shared`: shared message schema, configuration types, and helpers.

## Notes
- Messages are newline-delimited for simplicity; production systems should use robust framing (length-prefix, TLS records, gRPC, HTTP/2, etc.).
- Kernel monitors plugin processes and restarts them if a heartbeat timeout occurs.
