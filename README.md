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

### Build
```powershell
cd .\src\IKT300.Solution
dotnet build ..\IKT300.Microkernel\IKT300.Microkernel.csproj
dotnet build ..\IKT300.Plugin.Sample\IKT300.Plugin.Sample.csproj
```

### Run Kernel (auto-starts sample plugin)
```powershell
cd ..\IKT300.Microkernel
dotnet run --project .\IKT300.Microkernel.csproj
```

### Kernel Console Commands
- `list` – show registered plugins and status
- `kill <pluginId>` – terminate plugin process (kernel may restart it)
- `start <pluginId>` – start plugin if not running

## Repository Structure
- `IKT300.Microkernel`: kernel process handling TCP connections, heartbeats, restart policy.
- `IKT300.Plugin.Sample`: sample plugin connecting to kernel and emitting heartbeats.
- `IKT300.Shared`: shared message schema and configuration utilities.

## Notes
- Messages are newline-delimited for simplicity; production systems should use robust framing (length-prefix, TLS records, gRPC, HTTP/2, etc.).
- Kernel monitors plugin processes and restarts them if a heartbeat timeout occurs.
