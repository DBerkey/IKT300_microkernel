# Message Schema (JSON)

Messages passed between Kernel and Plugins are newline-delimited JSON objects. Each message has three sections: Header, Metadata, Payload.

Header
- MessageType (string): "Handshake", "Heartbeat", "CommandRequest", "CommandResponse", "Error"
- MessageId (string): unique identifier (GUID)
- Timestamp (ISO8601)
- CorrelationId (string | null): optional id to tie request/response

Metadata
- PluginId (string): unique plugin identifier
- Sender (string): optional sender name
- Sequence (int): optional sequence number

Payload
- Arbitrary JSON value; the structure depends on the MessageType.

Example
```
{
  "Header": {
    "MessageType":"Heartbeat",
    "MessageId":"6b3f3b56-c2bf-4f1c-9afa-1b21f2f6b0e8",
    "Timestamp":"2025-12-02T10:35:00Z"
  },
  "Metadata": {
    "PluginId":"SamplePlugin",
    "Sequence": 5
  },
  "Payload": {"Time": "2025-12-02T10:35:00Z"}
}
```

Notes
- Messages are newline-delimited to support streaming over TCP.
- To avoid partial reads, we use newline as a simple framing mechanism for prototyping. For a production system consider length-prefixing or a protocol (TLS framed records, gRPC, HTTP/2, etc.).
