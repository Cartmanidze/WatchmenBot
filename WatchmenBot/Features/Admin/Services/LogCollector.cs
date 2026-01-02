using System.Collections.Concurrent;

namespace WatchmenBot.Features.Admin.Services;

public class LogCollector
{
    private readonly ConcurrentQueue<LogEntry> _errors = new();
    private readonly ConcurrentQueue<LogEntry> _warnings = new();
    private readonly Lock _statsLock = new();

    private int _messagesProcessed;
    private int _summariesGenerated;
    private int _embeddingsCreated;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastReportTime = DateTimeOffset.UtcNow;

    private const int MaxEntriesPerCategory = 100;

    public void LogError(string source, string message, Exception? ex = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Message = message,
            Exception = ex?.ToString()
        };

        _errors.Enqueue(entry);

        // Keep only last N entries
        while (_errors.Count > MaxEntriesPerCategory)
            _errors.TryDequeue(out _);
    }

    public void LogWarning(string source, string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Message = message
        };

        _warnings.Enqueue(entry);

        while (_warnings.Count > MaxEntriesPerCategory)
            _warnings.TryDequeue(out _);
    }

    public void IncrementMessages(int count = 1)
    {
        lock (_statsLock) _messagesProcessed += count;
    }

    public void IncrementSummaries(int count = 1)
    {
        lock (_statsLock) _summariesGenerated += count;
    }

    public void IncrementEmbeddings(int count = 1)
    {
        lock (_statsLock) _embeddingsCreated += count;
    }

    public LogReport GetReportSinceLastTime()
    {
        var now = DateTimeOffset.UtcNow;
        var sinceTime = _lastReportTime;

        var errors = _errors.Where(e => e.Timestamp >= sinceTime).ToList();
        var warnings = _warnings.Where(e => e.Timestamp >= sinceTime).ToList();

        int messages, summaries, embeddings;
        lock (_statsLock)
        {
            messages = _messagesProcessed;
            summaries = _summariesGenerated;
            embeddings = _embeddingsCreated;
        }

        _lastReportTime = now;

        return new LogReport
        {
            PeriodStart = sinceTime,
            PeriodEnd = now,
            UptimeSinceStart = now - _startTime,
            Errors = errors,
            Warnings = warnings,
            MessagesProcessed = messages,
            SummariesGenerated = summaries,
            EmbeddingsCreated = embeddings
        };
    }

    public LogReport GetFullReport()
    {
        var now = DateTimeOffset.UtcNow;

        int messages, summaries, embeddings;
        lock (_statsLock)
        {
            messages = _messagesProcessed;
            summaries = _summariesGenerated;
            embeddings = _embeddingsCreated;
        }

        return new LogReport
        {
            PeriodStart = _startTime,
            PeriodEnd = now,
            UptimeSinceStart = now - _startTime,
            Errors = _errors.ToList(),
            Warnings = _warnings.ToList(),
            MessagesProcessed = messages,
            SummariesGenerated = summaries,
            EmbeddingsCreated = embeddings
        };
    }

    public void ResetStats()
    {
        lock (_statsLock)
        {
            _messagesProcessed = 0;
            _summariesGenerated = 0;
            _embeddingsCreated = 0;
        }

        while (_errors.TryDequeue(out _)) { }
        while (_warnings.TryDequeue(out _)) { }

        _lastReportTime = DateTimeOffset.UtcNow;
    }
}

public class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
}

public class LogReport
{
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public TimeSpan UptimeSinceStart { get; set; }
    public List<LogEntry> Errors { get; set; } = [];
    public List<LogEntry> Warnings { get; set; } = [];
    public int MessagesProcessed { get; set; }
    public int SummariesGenerated { get; set; }
    public int EmbeddingsCreated { get; set; }

    public bool HasIssues => Errors.Count > 0 || Warnings.Count > 0;

    public string ToTelegramHtml(TimeSpan timezoneOffset)
    {
        var sb = new System.Text.StringBuilder();
        var localNow = DateTimeOffset.UtcNow.ToOffset(timezoneOffset);

        sb.AppendLine($"<b>📋 Отчёт бота</b>");
        sb.AppendLine($"<i>{localNow:dd.MM.yyyy HH:mm} (UTC{timezoneOffset:hh\\:mm})</i>");
        sb.AppendLine();

        // Uptime
        sb.AppendLine($"⏱ <b>Аптайм:</b> {UptimeSinceStart.Days}д {UptimeSinceStart.Hours}ч {UptimeSinceStart.Minutes}м");
        sb.AppendLine();

        // Stats
        sb.AppendLine("<b>📊 Статистика:</b>");
        sb.AppendLine($"• Сообщений обработано: {MessagesProcessed}");
        sb.AppendLine($"• Саммари сгенерировано: {SummariesGenerated}");
        sb.AppendLine($"• Эмбеддингов создано: {EmbeddingsCreated}");
        sb.AppendLine();

        // Status
        if (!HasIssues)
        {
            sb.AppendLine("✅ <b>Статус:</b> Всё работает штатно");
        }
        else
        {
            sb.AppendLine($"⚠️ <b>Проблемы:</b> {Errors.Count} ошибок, {Warnings.Count} предупреждений");
            sb.AppendLine();

            if (Errors.Count > 0)
            {
                sb.AppendLine("<b>❌ Ошибки:</b>");
                foreach (var error in Errors.TakeLast(5))
                {
                    var time = error.Timestamp.ToOffset(timezoneOffset);
                    sb.AppendLine($"• [{time:HH:mm}] {error.Source}: {TruncateText(error.Message, 100)}");
                }

                if (Errors.Count > 5)
                    sb.AppendLine($"<i>...и ещё {Errors.Count - 5}</i>");
                sb.AppendLine();
            }

            if (Warnings.Count > 0)
            {
                sb.AppendLine("<b>⚠️ Предупреждения:</b>");
                foreach (var warning in Warnings.TakeLast(3))
                {
                    var time = warning.Timestamp.ToOffset(timezoneOffset);
                    sb.AppendLine($"• [{time:HH:mm}] {warning.Source}: {TruncateText(warning.Message, 80)}");
                }

                if (Warnings.Count > 3)
                    sb.AppendLine($"<i>...и ещё {Warnings.Count - 3}</i>");
            }
        }

        return sb.ToString();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }
}
