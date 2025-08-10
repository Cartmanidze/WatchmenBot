namespace WatchmenBot.Models;

public class MessageRecord
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public long? ThreadId { get; set; }
    public long FromUserId { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset DateUtc { get; set; }
    public bool HasLinks { get; set; }
    public bool HasMedia { get; set; }
    public long? ReplyToMessageId { get; set; }
    public string MessageType { get; set; } = "text";
}