using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IKT300.Shared.Models
{
    public class Header
    {
        public string MessageType { get; set; } = string.Empty; // e.g., Handshake, Heartbeat, CommandRequest, CommandResponse, Error
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? CorrelationId { get; set; }
    }

    public class Metadata
    {
        // Plugin identifier, version, etc.
        public string PluginId { get; set; } = string.Empty;
        public string? Sender { get; set; }
        public int Sequence { get; set; }
    }

    public class Message
    {
        public Header Header { get; set; } = new Header();
        public Metadata Metadata { get; set; } = new Metadata();
        public JsonElement? Payload { get; set; }

        public override string ToString() => JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        public static Message FromJson(string json) => JsonSerializer.Deserialize<Message>(json) ?? throw new ArgumentException("Cannot deserialize message");
    }

    public static class MessageTypes
    {
        public const string Handshake = "Handshake";
        public const string Heartbeat = "Heartbeat";
        public const string CommandRequest = "CommandRequest";
        public const string CommandResponse = "CommandResponse";
        public const string Error = "Error";
    }
}
