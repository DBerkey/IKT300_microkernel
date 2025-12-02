# Design Overview

This file summarizes the choices made for the microkernel proof-of-concept.

Language: C# (.NET 8.0)
- Rationale: strong typing, good IPC and process APIs, cross-platform.

IPC: TCP sockets
- Pros: cross-environment, simple to implement and debug.
- Implementation detail: newline-delimited JSON messages for framing; later iteration to length-prefix/TLS.

Serialization
- JSON (System.Text.Json) for readability, language-agnostic plugin support.

Message schema
- Each message contains `Header`, `Metadata` and `Payload`.
- `Header.MessageType` determines how the message is processed.

Plugin interface
- Plugins are independent processes.
- Handshake: sent by plugin to identify itself.
- Heartbeat: plugins send heartbeat messages at regular intervals.
- Fault isolation: kernel kills and restarts crashing plugins; kernel only affects the particular process.

Fault handling & restart policy
- Kernel monitors `LastReceived` timestamp for each plugin.
- When heartbeat not received within a timeout, kernel kills process if alive and starts a new one.

Future improvements
- TLS to encrypt IPC
- Better message framing; binary formats (protobuf) for performance
- Plugin capabilities discovery and dynamic registration
- Backoff restart policy and supervisor strategies
- Integration testing harness and unit tests
