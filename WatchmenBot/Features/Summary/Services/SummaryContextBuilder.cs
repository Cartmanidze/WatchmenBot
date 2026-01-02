using System.Text;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Models;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Builds context for LLM summary generation with budget management
/// </summary>
public class SummaryContextBuilder(ILogger<SummaryContextBuilder> logger)
{
    // Token budget for context (roughly 4 chars per token)
    private const int ContextTokenBudget = 6000;
    private const int CharsPerToken = 4;
    private const int ContextCharBudget = ContextTokenBudget * CharsPerToken; // ~24000 chars
    private const int MaxMessagesPerTopic = 12;
    private const int MaxTotalTopicMessages = 50;

    /// <summary>
    /// Build stats from message records
    /// </summary>
    public ChatStats BuildStats(List<MessageRecord> messages)
    {
        return new ChatStats
        {
            TotalMessages = messages.Count,
            UniqueUsers = messages.Select(m => m.FromUserId).Distinct().Count(),
            MessagesWithLinks = messages.Count(m => m.HasLinks),
            MessagesWithMedia = messages.Count(m => m.HasMedia)
        };
    }

    /// <summary>
    /// Get top active users from messages
    /// </summary>
    public List<string> GetTopActiveUsers(List<MessageRecord> messages, int count = 5)
    {
        return messages
            .GroupBy(m => string.IsNullOrWhiteSpace(m.DisplayName)
                ? m.Username ?? m.FromUserId.ToString()
                : m.DisplayName)
            .OrderByDescending(g => g.Count())
            .Take(count)
            .Select(g => $"{g.Key}: {g.Count()} сообщений")
            .ToList();
    }

    /// <summary>
    /// Build context string for topic-based summary with budget control
    /// </summary>
    public (string context, int messagesIncluded, int tokensEstimate) BuildTopicContext(
        Dictionary<string, List<MessageWithTime>> topicMessages,
        ChatStats stats,
        List<string> topUsers)
    {
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("СТАТИСТИКА:");
        contextBuilder.AppendLine($"- Всего сообщений: {stats.TotalMessages}");
        contextBuilder.AppendLine($"- Участников: {stats.UniqueUsers}");
        contextBuilder.AppendLine($"- Со ссылками: {stats.MessagesWithLinks}");
        contextBuilder.AppendLine($"- С медиа: {stats.MessagesWithMedia}");
        contextBuilder.AppendLine();

        if (topUsers.Count > 0)
        {
            contextBuilder.AppendLine("САМЫЕ АКТИВНЫЕ:");
            foreach (var user in topUsers)
                contextBuilder.AppendLine($"• {user}");
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("ТОПИКИ И СООБЩЕНИЯ:");
        var usedChars = contextBuilder.Length;
        var totalMessagesIncluded = 0;
        var messagesExcluded = 0;

        foreach (var (topic, messages) in topicMessages)
        {
            if (messages.Count == 0) continue;
            if (totalMessagesIncluded >= MaxTotalTopicMessages) break;

            var topicHeader = $"\n### {topic}\n";
            if (usedChars + topicHeader.Length > ContextCharBudget) break;

            contextBuilder.Append(topicHeader);
            usedChars += topicHeader.Length;

            foreach (var msg in messages)
            {
                if (totalMessagesIncluded >= MaxTotalTopicMessages)
                {
                    messagesExcluded++;
                    continue;
                }

                var timeStr = msg.Time != DateTimeOffset.MinValue
                    ? $"[{msg.Time.ToLocalTime():HH:mm}] "
                    : "";
                var line = $"{timeStr}{msg.Text}\n";

                if (usedChars + line.Length > ContextCharBudget)
                {
                    messagesExcluded++;
                    continue;
                }

                contextBuilder.Append(line);
                usedChars += line.Length;
                totalMessagesIncluded++;
            }
        }

        logger.LogInformation(
            "[SummaryContext] Built: {Included} messages, {Chars}/{Budget} chars, {Excluded} excluded by budget",
            totalMessagesIncluded, usedChars, ContextCharBudget, messagesExcluded);

        return (contextBuilder.ToString(), totalMessagesIncluded, usedChars / CharsPerToken);
    }

    /// <summary>
    /// Build context for traditional (non-topic) summary
    /// </summary>
    public (string context, int messagesIncluded, int tokensEstimate) BuildTraditionalContext(
        List<MessageRecord> messages,
        ChatStats stats,
        List<string> topUsers,
        int maxMessages = 200)
    {
        var sample = SampleMessagesUniformly(messages, maxMessages);

        var convo = new StringBuilder();
        foreach (var m in sample)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName)
                ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username)
                : m.DisplayName;
            var text = string.IsNullOrWhiteSpace(m.Text) ? $"[{m.MessageType}]" : m.Text!.Replace("\n", " ");
            convo.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
        }

        var contextPrompt = new StringBuilder();
        contextPrompt.AppendLine($"Статистика: {stats.TotalMessages} сообщений, {stats.UniqueUsers} участников");
        contextPrompt.AppendLine($"Активные: {string.Join(", ", topUsers)}");
        contextPrompt.AppendLine();
        contextPrompt.AppendLine("Переписка:");
        contextPrompt.AppendLine(convo.ToString());

        var context = contextPrompt.ToString();

        return (context, sample.Count, context.Length / CharsPerToken);
    }

    /// <summary>
    /// Sample messages uniformly across time period
    /// </summary>
    public List<MessageRecord> SampleMessagesUniformly(List<MessageRecord> messages, int maxMessages)
    {
        if (messages.Count <= maxMessages)
            return messages;

        var result = new List<MessageRecord>();
        var step = (double)messages.Count / maxMessages;

        for (var i = 0; i < maxMessages; i++)
        {
            var index = (int)(i * step);
            if (index < messages.Count)
                result.Add(messages[index]);
        }

        return result;
    }
}
