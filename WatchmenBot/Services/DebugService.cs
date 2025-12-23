using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Services;

/// <summary>
/// Service for collecting and sending debug information to admin
/// </summary>
public class DebugService
{
    private readonly ITelegramBotClient _bot;
    private readonly AdminSettingsStore _adminSettings;
    private readonly ILogger<DebugService> _logger;

    public DebugService(
        ITelegramBotClient bot,
        AdminSettingsStore adminSettings,
        ILogger<DebugService> logger)
    {
        _bot = bot;
        _adminSettings = adminSettings;
        _logger = logger;
    }

    /// <summary>
    /// Check if debug mode is enabled
    /// </summary>
    public async Task<bool> IsEnabledAsync()
    {
        return await _adminSettings.IsDebugModeEnabledAsync();
    }

    /// <summary>
    /// Send debug report to admin
    /// </summary>
    public async Task SendDebugReportAsync(DebugReport report, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync())
            return;

        var adminId = _adminSettings.GetAdminUserId();
        if (adminId == 0)
            return;

        try
        {
            var message = FormatReport(report);

            // Split if too long (Telegram limit ~4096)
            if (message.Length > 4000)
            {
                // Send header
                var header = FormatHeader(report);
                await _bot.SendMessage(adminId, header, parseMode: ParseMode.Html, cancellationToken: ct);

                // Send search results
                if (report.SearchResults.Count > 0)
                {
                    var searchPart = FormatSearchResults(report);
                    await SendLongMessage(adminId, searchPart, ct);
                }

                // Send context
                if (!string.IsNullOrEmpty(report.ContextSent))
                {
                    var contextPart = $"<b>📝 Контекст для LLM:</b>\n<pre>{EscapeHtml(TruncateText(report.ContextSent, 3000))}</pre>";
                    await SendLongMessage(adminId, contextPart, ct);
                }

                // Send prompts
                if (!string.IsNullOrEmpty(report.SystemPrompt) || !string.IsNullOrEmpty(report.UserPrompt))
                {
                    var promptsPart = FormatPrompts(report);
                    await SendLongMessage(adminId, promptsPart, ct);
                }

                // Send LLM response
                if (!string.IsNullOrEmpty(report.LlmResponse))
                {
                    var responsePart = FormatLlmResponse(report);
                    await SendLongMessage(adminId, responsePart, ct);
                }
            }
            else
            {
                await _bot.SendMessage(adminId, message, parseMode: ParseMode.Html, cancellationToken: ct);
            }

            _logger.LogDebug("[Debug] Sent debug report for {Command}", report.Command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Debug] Failed to send debug report");
        }
    }

    private async Task SendLongMessage(long chatId, string text, CancellationToken ct)
    {
        if (text.Length <= 4000)
        {
            try
            {
                await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch
            {
                // Fallback to plain text
                var plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
                await _bot.SendMessage(chatId, plain, cancellationToken: ct);
            }
            return;
        }

        // Split into chunks
        var chunks = SplitIntoChunks(text, 4000);
        foreach (var chunk in chunks)
        {
            try
            {
                await _bot.SendMessage(chatId, chunk, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch
            {
                var plain = System.Text.RegularExpressions.Regex.Replace(chunk, "<[^>]+>", "");
                await _bot.SendMessage(chatId, plain, cancellationToken: ct);
            }
        }
    }

    private static List<string> SplitIntoChunks(string text, int maxLength)
    {
        var chunks = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining);
                break;
            }

            // Find a good break point (newline)
            var breakPoint = remaining.LastIndexOf('\n', maxLength);
            if (breakPoint < maxLength / 2)
                breakPoint = maxLength;

            chunks.Add(remaining[..breakPoint]);
            remaining = remaining[breakPoint..].TrimStart('\n');
        }

        return chunks;
    }

    private string FormatReport(DebugReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine(FormatHeader(report));

        if (report.SearchResults.Count > 0)
        {
            sb.AppendLine(FormatSearchResults(report));
        }

        if (!string.IsNullOrEmpty(report.ContextSent))
        {
            sb.AppendLine($"<b>📝 Контекст ({report.ContextTokensEstimate} tokens, {report.ContextMessagesCount} msg):</b>");
            sb.AppendLine($"<pre>{EscapeHtml(TruncateText(report.ContextSent, 500))}</pre>");
        }

        sb.AppendLine(FormatPrompts(report));
        sb.AppendLine(FormatLlmResponse(report));

        return sb.ToString();
    }

    private static string FormatHeader(DebugReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🔍 <b>DEBUG: /{report.Command}</b>");
        sb.AppendLine($"📍 Chat: <code>{report.ChatId}</code>");
        sb.AppendLine($"❓ Query: <i>{EscapeHtml(report.Query)}</i>");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatSearchResults(DebugReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>🎯 Результаты поиска ({report.SearchResults.Count}):</b>");

        foreach (var result in report.SearchResults.Take(10))
        {
            var scoreBar = GetScoreBar(result.Similarity);
            sb.AppendLine($"{scoreBar} <b>{result.Similarity:F3}</b> | ids: {string.Join(",", result.MessageIds.Take(3))}");
            sb.AppendLine($"   <i>{EscapeHtml(TruncateText(result.Text, 100))}</i>");
        }

        if (report.SearchResults.Count > 10)
        {
            sb.AppendLine($"   ... и ещё {report.SearchResults.Count - 10}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatPrompts(DebugReport report)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(report.SystemPrompt))
        {
            sb.AppendLine($"<b>🤖 System ({report.SystemPrompt.Length} chars):</b>");
            sb.AppendLine($"<pre>{EscapeHtml(TruncateText(report.SystemPrompt, 500))}</pre>");
        }

        if (!string.IsNullOrEmpty(report.UserPrompt))
        {
            sb.AppendLine($"<b>👤 User ({report.UserPrompt.Length} chars):</b>");
            sb.AppendLine($"<pre>{EscapeHtml(TruncateText(report.UserPrompt, 500))}</pre>");
        }

        return sb.ToString();
    }

    private static string FormatLlmResponse(DebugReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"<b>💬 LLM Response:</b>");
        sb.AppendLine($"   Provider: {report.LlmProvider} | Model: {report.LlmModel}");
        sb.AppendLine($"   Tag: {report.LlmTag ?? "default"} | Temp: {report.Temperature}");
        sb.AppendLine($"   Tokens: {report.PromptTokens} + {report.CompletionTokens} = {report.TotalTokens}");
        sb.AppendLine($"   Time: {report.LlmTimeMs}ms");

        if (report.IsMultiStage)
        {
            sb.AppendLine($"   Stages: {report.StageCount}");
        }

        sb.AppendLine();
        sb.AppendLine($"<pre>{EscapeHtml(TruncateText(report.LlmResponse ?? "", 800))}</pre>");

        return sb.ToString();
    }

    private static string GetScoreBar(double score)
    {
        // Score 0.0-1.0 -> bar visualization
        var filled = (int)(score * 5);
        return new string('█', filled) + new string('░', 5 - filled);
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}

/// <summary>
/// Debug report data structure
/// </summary>
public class DebugReport
{
    public string Command { get; set; } = "";
    public long ChatId { get; set; }
    public string Query { get; set; } = "";

    // Search results
    public List<DebugSearchResult> SearchResults { get; set; } = new();

    // Context sent to LLM
    public string? ContextSent { get; set; }
    public int ContextTokensEstimate { get; set; }
    public int ContextMessagesCount { get; set; }

    // Prompts
    public string? SystemPrompt { get; set; }
    public string? UserPrompt { get; set; }

    // LLM response
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public string? LlmTag { get; set; }
    public double Temperature { get; set; }
    public string? LlmResponse { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public long LlmTimeMs { get; set; }

    // Multi-stage info
    public bool IsMultiStage { get; set; }
    public int StageCount { get; set; }
    public List<DebugStage> Stages { get; set; } = new();
}

public class DebugSearchResult
{
    public double Similarity { get; set; }
    public long[] MessageIds { get; set; } = Array.Empty<long>();
    public string Text { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
}

public class DebugStage
{
    public int StageNumber { get; set; }
    public string Name { get; set; } = "";
    public double Temperature { get; set; }
    public string? SystemPrompt { get; set; }
    public string? UserPrompt { get; set; }
    public string? Response { get; set; }
    public int Tokens { get; set; }
    public long TimeMs { get; set; }
}
