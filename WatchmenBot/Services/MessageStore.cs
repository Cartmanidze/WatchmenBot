using System.Text.RegularExpressions;
using LiteDB;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class MessageStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<MessageRecord> _messages;

    public MessageStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        _db = new LiteDatabase(databasePath);
        _messages = _db.GetCollection<MessageRecord>("messages");
        _messages.EnsureIndex(x => x.ChatId);
        _messages.EnsureIndex(x => x.DateUtc);
        _messages.EnsureIndex(x => x.FromUserId);
    }

    public Task SaveAsync(MessageRecord record)
    {
        _messages.Upsert(record);
        return Task.CompletedTask;
    }

    public Task<List<MessageRecord>> GetMessagesAsync(long chatId, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var results = _messages
            .Query()
            .Where(x => x.ChatId == chatId && x.DateUtc >= startUtc && x.DateUtc < endUtc)
            .OrderBy(x => x.DateUtc)
            .ToList();
        return Task.FromResult(results);
    }

    public Task<List<long>> GetDistinctChatIdsAsync()
    {
        var ids = _messages.Query().Select(x => x.ChatId).ToEnumerable().Distinct().ToList();
        return Task.FromResult(ids);
    }

    public static bool DetectLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pattern = @"https?://\S+";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}