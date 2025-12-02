# IKT300_microkernel - Developer README

This repository contains a minimal proof-of-concept microkernel architecture for the IKT300 course using C# and TCP socket IPC.

Components:
- `IKT300.Microkernel` – Kernel process that accepts TCP connections from plugin processes, monitors heartbeat, and restarts crashed plugins.
- `IKT300.Plugin.Sample` – Sample plugin that connects to the kernel, performs handshake, and sends regular heartbeats.
- `IKT300.Shared` – Message schema used for JSON serialization between kernel and plugins.

Design choices:
- Language: C# (.NET 8.0)
- IPC: TCP sockets (newline-delimited JSON messages)
- Serialization: JSON (`System.Text.Json`)
- Heartbeat & restart: Kernel monitors timestamp of the last heartbeat and restarts plugin process.

How to build & run (Windows PowerShell):

1) Ensure you have .NET 8 installed: https://dotnet.microsoft.com/en-us/download/dotnet/8.0

2) From repo root, build projects:

```powershell
cd .\src\IKT300.Solution
# Use dotnet build on the projects
dotnet build ..\IKT300.Microkernel\IKT300.Microkernel.csproj
dotnet build ..\IKT300.Plugin.Sample\IKT300.Plugin.Sample.csproj
```

3) Start the kernel (this will attempt to start the sample plugin automatically):

```powershell
cd ..\IKT300.Microkernel
dotnet run --project .\IKT300.Microkernel.csproj
```

4) If you want to run the sample plugin manually in another console:

```powershell
cd ..\IKT300.Plugin.Sample
# Plugin run via `dotnet run` (builds and runs the project)
dotnet run --project .\IKT300.Plugin.Sample.csproj -- --kernelHost 127.0.0.1 --kernelPort 9000 --pluginId SamplePlugin
```

Or run the compiled plugin DLL directly (recommended when running from the kernel to ensure plugin stdout/stderr are captured correctly):

```powershell
dotnet ..\IKT300.Plugin.Sample\bin\Debug\net8.0\IKT300.Plugin.Sample.dll --kernelHost 127.0.0.1 --kernelPort 9000 --pluginId SamplePlugin
```

Kernel interactive commands (enter in kernel console):
- `list` — list registered plugins and status
- `kill <pluginId>` — kill the plugin process (kernel may automatically restart it)
- `start <pluginId>` — start a plugin process if not running
```

Notes & Next steps:
- Improve message framing and error handling
- Add TLS option for secure IPC
- Implement robust plugin discovery and configuration
- Add a plugin CLI to manage plugins from kernel

