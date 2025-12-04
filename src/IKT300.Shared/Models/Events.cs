using System;

namespace IKT300.Shared.Models
{
    /// <summary>
    /// Event data when a user successfully logs into the system.
    /// Shared between kernel and plugins.
    /// </summary>
    public class UserLoggedInEvent
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public DateTime LoginTime { get; set; }
    }

    /// <summary>
    /// Event data related to a core data processing job completion.
    /// </summary>
    public class DataProcessedEvent
    {
        public Guid JobId { get; set; }
        public int RecordCount { get; set; }
        public TimeSpan Duration { get; set; }
        public string ProcessorName { get; set; } = string.Empty;
    }
}