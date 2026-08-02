namespace ScribbleBot.Models
{
    /// <summary>
    /// DTO for storing messages which are in a thread
    /// </summary>
    public class ChatMessageEntity
    {
        public int Id { get; set; }
        public string? ThreadId { get; set; }
        public string Role { get; set; } = "user"; // user, assistant, system
        public DateTime Timestamp { get; set; }
        public string RichContentJson { get; set; } = "[]";

    }

    /// <summary>
    /// DTO for storing threads
    /// </summary>
    public class ChatThreadEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title { get; set; } = "New Conversation";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUpdatedAt { get; set; } = DateTime.Now;
        public string SystemSummary { get; set; } = string.Empty;

        public List<ChatMessageEntity> Messages { get; set; } = [];
    }
}
