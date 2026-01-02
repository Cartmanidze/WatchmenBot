namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A sliding window of messages for context embedding
/// </summary>
public class MessageWindow
{
    public long CenterMessageId { get; set; }
    public long WindowStartId { get; set; }
    public long WindowEndId { get; set; }
    public List<WindowMessage> Messages { get; set; } = [];
}
