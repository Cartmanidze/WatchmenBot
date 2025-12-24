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

        // Show rewritten query if different
        if (!string.IsNullOrEmpty(report.RewrittenQuery) && report.RewrittenQuery != report.Query)
        {
            sb.AppendLine($"🔄 Rewritten ({report.QueryRewriteTimeMs}ms):");
            sb.AppendLine($"   <i>{EscapeHtml(TruncateText(report.RewrittenQuery, 300))}</i>");
        }

        // Show RAG Fusion variations
        if (report.QueryVariations.Count > 0)
        {
            sb.AppendLine($"🔀 <b>RAG Fusion</b> ({report.RagFusionTimeMs}ms):");
            for (var i = 0; i < report.QueryVariations.Count; i++)
            {
                sb.AppendLine($"   {i + 1}. <i>{EscapeHtml(TruncateText(report.QueryVariations[i], 100))}</i>");
            }
        }

        // Show Rerank info
        if (report.RerankTimeMs > 0)
        {
            var changed = report.RerankOrderChanged ? "🔄 порядок изменился" : "✓ порядок сохранён";
            sb.AppendLine($"📊 <b>Rerank</b> ({report.RerankTimeMs}ms, {report.RerankTokensUsed} tokens) {changed}");
            if (report.RerankScores.Count > 0)
            {
                var scoreStrs = report.RerankScores.Take(5).Select((s, i) => $"#{i + 1}:{s}");
                sb.AppendLine($"   Scores: {string.Join(", ", scoreStrs)}");
            }
        }

        // Personal retrieval indicator
        if (!string.IsNullOrEmpty(report.PersonalTarget))
        {
            var targetLabel = report.PersonalTarget == "self" ? "👤 О себе" : $"👤 О {report.PersonalTarget}";
            sb.AppendLine($"🎯 <b>Тип:</b> {targetLabel} (персональный ретривал)");
        }

        sb.AppendLine();

        // Confidence assessment
        if (!string.IsNullOrEmpty(report.SearchConfidence))
        {
            var confEmoji = report.SearchConfidence switch
            {
                "High" => "🟢",
                "Medium" => "🟡",
                "Low" => "🟠",
                _ => "🔴"
            };
            sb.AppendLine($"{confEmoji} <b>Confidence:</b> {report.SearchConfidence}");
            sb.AppendLine($"   Best: {report.BestScore:F3} | Gap: {report.ScoreGap:F3} | FullText: {report.HasFullTextMatch}");
            if (!string.IsNullOrEmpty(report.SearchConfidenceReason))
                sb.AppendLine($"   <i>{EscapeHtml(report.SearchConfidenceReason)}</i>");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatSearchResults(DebugReport report)
    {
        var sb = new StringBuilder();

        var included = report.SearchResults.Where(r => r.IncludedInContext).ToList();
        var excluded = report.SearchResults.Where(r => !r.IncludedInContext).ToList();

        sb.AppendLine($"<b>🎯 Результаты поиска ({report.SearchResults.Count}):</b>");
        sb.AppendLine($"   ✅ В контексте: {included.Count} | ❌ Исключено: {excluded.Count}");
        sb.AppendLine();

        foreach (var result in report.SearchResults.Take(10))
        {
            var scoreBar = GetScoreBar(result.Similarity);
            var newsFlag = result.IsNewsDump ? " 📰" : "";
            var contextFlag = result.IncludedInContext ? "✅" : $"❌{result.ExcludedReason}";
            sb.AppendLine($"{scoreBar} sim=<b>{result.Similarity:F3}</b> dist={result.Distance:F3}{newsFlag} [{contextFlag}]");
            sb.AppendLine($"   ids: {string.Join(",", result.MessageIds.Take(3))}");
            sb.AppendLine($"   <i>{EscapeHtml(TruncateText(result.Text, 100))}</i>");
        }

        if (report.SearchResults.Count > 10)
        {
            sb.AppendLine($"   ... и ещё {report.SearchResults.Count - 10}");
        }

        // Show excluded reasons summary
        if (excluded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>❌ Исключённые:</b>");
            var byReason = excluded.GroupBy(r => r.ExcludedReason ?? "unknown")
                .OrderByDescending(g => g.Count());
            foreach (var group in byReason)
            {
                var ids = string.Join(",", group.SelectMany(r => r.MessageIds).Take(5));
                sb.AppendLine($"   {group.Key}: {group.Count()} (ids: {ids})");
            }
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
    public string? RewrittenQuery { get; set; } // Query after LLM rewrite for better search
    public long QueryRewriteTimeMs { get; set; }

    // RAG Fusion info
    public List<string> QueryVariations { get; set; } = new(); // Generated query variations
    public long RagFusionTimeMs { get; set; } // Total time for RAG Fusion search

    // Rerank info
    public long RerankTimeMs { get; set; }
    public int RerankTokensUsed { get; set; }
    public List<int> RerankScores { get; set; } = new(); // LLM scores (0-3)
    public bool RerankOrderChanged { get; set; }

    // Search results
    public List<DebugSearchResult> SearchResults { get; set; } = new();

    // Confidence assessment
    public string? SearchConfidence { get; set; }
    public string? SearchConfidenceReason { get; set; }
    public double BestScore { get; set; }
    public double ScoreGap { get; set; }
    public bool HasFullTextMatch { get; set; }

    // Personal retrieval info
    public string? PersonalTarget { get; set; } // "self", "@username", or null

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
    public double Distance { get; set; }
    public long[] MessageIds { get; set; } = Array.Empty<long>();
    public string Text { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public bool IsNewsDump { get; set; }

    // Context inclusion tracking
    public bool IncludedInContext { get; set; }
    public string? ExcludedReason { get; set; } // "ok", "no_text", "duplicate", etc.
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
