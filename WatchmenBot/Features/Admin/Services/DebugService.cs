using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Infrastructure.Settings;

namespace WatchmenBot.Features.Admin.Services;

/// <summary>
/// Service for collecting and sending debug information to admin
/// </summary>
public class DebugService(
    ITelegramBotClient bot,
    AdminSettingsStore adminSettings,
    ILogger<DebugService> logger)
{
    /// <summary>
    /// Check if debug mode is enabled
    /// </summary>
    private async Task<bool> IsEnabledAsync()
    {
        return await adminSettings.IsDebugModeEnabledAsync();
    }

    /// <summary>
    /// Send debug report to admin
    /// </summary>
    public async Task SendDebugReportAsync(DebugReport report, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync())
            return;

        var adminId = adminSettings.GetAdminUserId();
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
                await bot.SendMessage(adminId, header, parseMode: ParseMode.Html, cancellationToken: ct);

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
                await bot.SendMessage(adminId, message, parseMode: ParseMode.Html, cancellationToken: ct);
            }

            logger.LogDebug("[Debug] Sent debug report for {Command}", report.Command);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Debug] Failed to send debug report");
        }
    }

    private async Task SendLongMessage(long chatId, string text, CancellationToken ct)
    {
        if (text.Length <= 4000)
        {
            try
            {
                await bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                // Fallback to plain text
                logger.LogWarning(ex, "[DebugService] HTML parsing failed, falling back to plain text");
                var plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
                await bot.SendMessage(chatId, plain, cancellationToken: ct);
            }
            return;
        }

        // Split into chunks
        var chunks = SplitIntoChunks(text, 4000);
        foreach (var chunk in chunks)
        {
            try
            {
                await bot.SendMessage(chatId, chunk, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[DebugService] HTML chunk parsing failed, falling back to plain text");
                var plain = System.Text.RegularExpressions.Regex.Replace(chunk, "<[^>]+>", "");
                await bot.SendMessage(chatId, plain, cancellationToken: ct);
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

        // Add verdict section at the end
        sb.AppendLine(FormatVerdict(report));

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
            var filtered = report.RerankFilteredOut > 0 ? $" | ❌ отфильтровано: {report.RerankFilteredOut}" : "";
            sb.AppendLine($"📊 <b>Rerank</b> ({report.RerankTimeMs}ms, {report.RerankTokensUsed} tokens) {changed}{filtered}");
            if (report.RerankScores.Count > 0)
            {
                var scoreStrs = report.RerankScores.Take(5).Select((s, i) => $"#{i + 1}:{s}");
                sb.AppendLine($"   Scores: {string.Join(", ", scoreStrs)}");
            }
        }

        // Intent classification (LLM-based)
        if (report.IntentClassification != null)
        {
            var ic = report.IntentClassification;
            var confEmoji = ic.Confidence >= 0.8 ? "🟢" : ic.Confidence >= 0.5 ? "🟡" : "🟠";
            sb.AppendLine($"🎯 <b>Intent:</b> {ic.Intent} {confEmoji} ({ic.Confidence:F2})");

            if (ic.MentionedPeople.Count > 0)
                sb.AppendLine($"   👥 People: {string.Join(", ", ic.MentionedPeople)}");

            if (ic.Entities.Count > 0)
                sb.AppendLine($"   📌 Entities: {string.Join(", ", ic.Entities.Take(5))}");

            if (!string.IsNullOrEmpty(ic.TemporalText))
                sb.AppendLine($"   🕐 Temporal: {ic.TemporalText} ({ic.TemporalDays} days)");

            if (!string.IsNullOrEmpty(ic.Reasoning))
                sb.AppendLine($"   💭 <i>{EscapeHtml(TruncateText(ic.Reasoning, 100))}</i>");
        }
        // Legacy: Personal retrieval indicator
        else if (!string.IsNullOrEmpty(report.PersonalTarget))
        {
            var targetLabel = report.PersonalTarget == "self" ? "👤 О себе" : $"👤 О {report.PersonalTarget}";
            sb.AppendLine($"🎯 <b>Тип:</b> {targetLabel} (персональный ретривал)");
        }

        sb.AppendLine();

        // Confidence assessment with detailed explanation
        if (!string.IsNullOrEmpty(report.SearchConfidence))
        {
            sb.AppendLine(FormatConfidenceSection(report));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format confidence section with human-readable explanations
    /// </summary>
    private static string FormatConfidenceSection(DebugReport report)
    {
        var sb = new StringBuilder();

        var (emoji, label, explanation) = report.SearchConfidence switch
        {
            "High" => ("🟢", "ВЫСОКАЯ",
                "Найдены очень релевантные результаты. Ответ скорее всего точный."),
            "Medium" => ("🟡", "СРЕДНЯЯ",
                "Найдены частично релевантные результаты. Ответ может быть неполным."),
            "Low" => ("🟠", "НИЗКАЯ",
                "Мало релевантных результатов. Ответ может быть неточным или основан на косвенных данных."),
            "None" => ("🔴", "НЕТ ДАННЫХ",
                "Релевантных результатов не найдено. LLM ответит на основе общих знаний."),
            _ => ("❓", report.SearchConfidence ?? "Unknown",
                "Неизвестный уровень уверенности.")
        };

        sb.AppendLine($"<b>═══ УВЕРЕННОСТЬ В ОТВЕТЕ ═══</b>");
        sb.AppendLine($"{emoji} <b>{label}</b>");
        sb.AppendLine($"   <i>{explanation}</i>");
        sb.AppendLine();

        // Technical metrics with explanations
        sb.AppendLine("<b>📊 Метрики:</b>");
        sb.AppendLine($"   • Best Score: <b>{report.BestScore:F3}</b> {GetScoreExplanation(report.BestScore)}");
        sb.AppendLine($"   • Gap: <b>{report.ScoreGap:F3}</b> {GetGapExplanation(report.ScoreGap)}");
        sb.AppendLine($"   • FullText: {(report.HasFullTextMatch ? "✅ да" : "❌ нет")} {GetFullTextExplanation(report.HasFullTextMatch)}");

        if (!string.IsNullOrEmpty(report.SearchConfidenceReason))
        {
            sb.AppendLine();
            sb.AppendLine($"<b>💡 Причина:</b> <i>{EscapeHtml(report.SearchConfidenceReason)}</i>");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Get explanation for similarity score
    /// </summary>
    private static string GetScoreExplanation(double score) => score switch
    {
        >= 0.85 => "(отлично — почти точное совпадение)",
        >= 0.75 => "(хорошо — семантически близко)",
        >= 0.65 => "(средне — частичное совпадение)",
        >= 0.50 => "(слабо — косвенная релевантность)",
        _ => "(очень слабо — возможно нерелевантно)"
    };

    /// <summary>
    /// Get explanation for score gap
    /// </summary>
    private static string GetGapExplanation(double gap) => gap switch
    {
        >= 0.15 => "(лидер явно лучше остальных)",
        >= 0.08 => "(лидер немного лучше)",
        >= 0.03 => "(несколько похожих результатов)",
        _ => "(много одинаково релевантных)"
    };

    /// <summary>
    /// Get explanation for full-text match
    /// </summary>
    private static string GetFullTextExplanation(bool hasMatch) =>
        hasMatch
            ? "(точное совпадение слов — высокая точность)"
            : "(только семантический поиск)";

    private static string FormatSearchResults(DebugReport report)
    {
        var sb = new StringBuilder();

        var included = report.SearchResults.Where(r => r.IncludedInContext).ToList();
        var excluded = report.SearchResults.Where(r => !r.IncludedInContext).ToList();

        sb.AppendLine($"<b>═══ РЕЗУЛЬТАТЫ ПОИСКА ═══</b>");
        sb.AppendLine($"📊 Всего: {report.SearchResults.Count} | ✅ В контексте: {included.Count} | ❌ Исключено: {excluded.Count}");
        sb.AppendLine();

        var rank = 0;
        foreach (var result in report.SearchResults.Take(10))
        {
            rank++;
            var scoreBar = GetScoreBar(result.Similarity);
            var qualityLabel = GetResultQualityLabel(result.Similarity);
            var newsFlag = result.IsNewsDump ? " 📰<i>новости</i>" : "";
            var contextFlag = result.IncludedInContext
                ? "✅ в контексте"
                : $"❌ {GetExclusionReasonLabel(result.ExcludedReason)}";

            sb.AppendLine($"<b>#{rank}</b> {scoreBar} <b>{result.Similarity:F3}</b> {qualityLabel}{newsFlag}");
            sb.AppendLine($"   [{contextFlag}]");

            // Show timestamp if available
            if (result.Timestamp.HasValue)
            {
                var age = DateTime.UtcNow - result.Timestamp.Value.UtcDateTime;
                var ageStr = age.TotalDays switch
                {
                    < 1 => $"{age.Hours}ч назад",
                    < 7 => $"{(int)age.TotalDays}д назад",
                    < 30 => $"{(int)(age.TotalDays / 7)}нед назад",
                    _ => $"{(int)(age.TotalDays / 30)}мес назад"
                };
                sb.AppendLine($"   🕐 {result.Timestamp.Value:dd.MM HH:mm} ({ageStr})");
            }

            sb.AppendLine($"   💬 <i>{EscapeHtml(TruncateText(result.Text, 120))}</i>");
            sb.AppendLine();
        }

        if (report.SearchResults.Count > 10)
        {
            sb.AppendLine($"<i>... и ещё {report.SearchResults.Count - 10} результатов</i>");
            sb.AppendLine();
        }

        // Show excluded reasons summary with explanations
        if (excluded.Count > 0)
        {
            sb.AppendLine("<b>📋 Причины исключения:</b>");
            var byReason = excluded.GroupBy(r => r.ExcludedReason ?? "unknown")
                .OrderByDescending(g => g.Count());
            foreach (var group in byReason)
            {
                var label = GetExclusionReasonLabel(group.Key);
                var explanation = GetExclusionReasonExplanation(group.Key);
                sb.AppendLine($"   • {label}: {group.Count()} шт.");
                sb.AppendLine($"     <i>{explanation}</i>");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Get quality label for result based on similarity score
    /// </summary>
    private static string GetResultQualityLabel(double similarity) => similarity switch
    {
        >= 0.85 => "🌟 отлично",
        >= 0.75 => "👍 хорошо",
        >= 0.65 => "🤔 средне",
        >= 0.50 => "😐 слабо",
        _ => "❓ сомнительно"
    };

    /// <summary>
    /// Get human-readable label for exclusion reason
    /// </summary>
    private static string GetExclusionReasonLabel(string? reason) => reason switch
    {
        "ok" => "включено",
        "no_text" => "пустой текст",
        "duplicate" => "дубликат",
        "low_score" => "низкий score",
        "news_dump" => "новостной дамп",
        "not_tracked" => "не отслеживается",
        "filtered_by_rerank" => "отфильтрован rerank",
        null => "неизвестно",
        _ => reason
    };

    /// <summary>
    /// Get explanation for exclusion reason
    /// </summary>
    private static string GetExclusionReasonExplanation(string? reason) => reason switch
    {
        "no_text" => "Сообщение без текста (возможно медиа)",
        "duplicate" => "Повторяющееся содержание",
        "low_score" => "Слишком низкая релевантность для контекста",
        "news_dump" => "Автоматическая новостная рассылка",
        "not_tracked" => "Результат не попал в трекинг контекста",
        "filtered_by_rerank" => "Cross-encoder оценил как нерелевантное",
        _ => "Причина не указана"
    };

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

    /// <summary>
    /// Format final verdict section - explains why this answer was given
    /// </summary>
    private static string FormatVerdict(DebugReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<b>═══ ВЕРДИКТ ═══</b>");
        sb.AppendLine();

        // Determine answer quality based on multiple factors
        var (quality, icon, explanation, recommendations) = AnalyzeAnswerQuality(report);

        sb.AppendLine($"{icon} <b>Качество ответа: {quality}</b>");
        sb.AppendLine();
        sb.AppendLine($"<b>📋 Анализ:</b>");
        foreach (var line in explanation)
        {
            sb.AppendLine($"   • {line}");
        }

        if (recommendations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>💡 Рекомендации:</b>");
            foreach (var rec in recommendations)
            {
                sb.AppendLine($"   • {rec}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Analyze answer quality based on search results and confidence
    /// </summary>
    private static (string quality, string icon, List<string> explanation, List<string> recommendations)
        AnalyzeAnswerQuality(DebugReport report)
    {
        var explanation = new List<string>();
        var recommendations = new List<string>();
        var qualityScore = 0;

        // Factor 1: Search confidence
        switch (report.SearchConfidence)
        {
            case "High":
                qualityScore += 3;
                explanation.Add("✅ Высокая уверенность в найденных результатах");
                break;
            case "Medium":
                qualityScore += 2;
                explanation.Add("🟡 Средняя уверенность — результаты частично релевантны");
                break;
            case "Low":
                qualityScore += 1;
                explanation.Add("🟠 Низкая уверенность — мало релевантных данных");
                recommendations.Add("Попробуйте переформулировать вопрос более конкретно");
                break;
            default:
                explanation.Add("🔴 Релевантных данных не найдено");
                recommendations.Add("Вопрос может быть вне контекста чата");
                break;
        }

        // Factor 2: Best similarity score
        if (report.BestScore >= 0.85)
        {
            qualityScore += 2;
            explanation.Add($"✅ Найдено почти точное совпадение (score {report.BestScore:F3})");
        }
        else if (report.BestScore >= 0.75)
        {
            qualityScore += 1;
            explanation.Add($"👍 Хорошее семантическое совпадение (score {report.BestScore:F3})");
        }
        else if (report.BestScore >= 0.65)
        {
            explanation.Add($"🤔 Частичное совпадение (score {report.BestScore:F3})");
        }
        else if (report.BestScore > 0)
        {
            explanation.Add($"😐 Слабое совпадение (score {report.BestScore:F3})");
            recommendations.Add("Результаты могут быть косвенно связаны с вопросом");
        }

        // Factor 3: Full-text match
        if (report.HasFullTextMatch)
        {
            qualityScore += 1;
            explanation.Add("✅ Найдено точное совпадение ключевых слов");
        }

        // Factor 4: Number of results in context
        var includedCount = report.SearchResults.Count(r => r.IncludedInContext);
        if (includedCount >= 5)
        {
            qualityScore += 1;
            explanation.Add($"✅ Богатый контекст ({includedCount} сообщений)");
        }
        else if (includedCount >= 2)
        {
            explanation.Add($"👍 Достаточный контекст ({includedCount} сообщений)");
        }
        else if (includedCount == 1)
        {
            explanation.Add($"🟠 Минимальный контекст (1 сообщение)");
            recommendations.Add("Ответ основан на ограниченных данных");
        }
        else
        {
            explanation.Add("🔴 Контекст пуст");
            recommendations.Add("LLM отвечает на основе общих знаний");
        }

        // Factor 5: News dump presence
        var newsDumpCount = report.SearchResults.Count(r => r.IsNewsDump);
        if (newsDumpCount > 0)
        {
            explanation.Add($"📰 Обнаружено {newsDumpCount} новостных дампов (понижен приоритет)");
        }

        // Factor 6: Score gap (distinctiveness)
        if (report.ScoreGap >= 0.15)
        {
            qualityScore += 1;
            explanation.Add("✅ Лучший результат явно выделяется");
        }
        else if (report.ScoreGap < 0.03 && includedCount > 1)
        {
            explanation.Add("🔄 Много одинаково релевантных результатов");
            recommendations.Add("Ответ может объединять информацию из разных источников");
        }

        // Determine final quality rating
        return qualityScore switch
        {
            >= 7 => ("ОТЛИЧНОЕ", "🌟", explanation, recommendations),
            >= 5 => ("ХОРОШЕЕ", "👍", explanation, recommendations),
            >= 3 => ("СРЕДНЕЕ", "🤔", explanation, recommendations),
            >= 1 => ("НИЗКОЕ", "😐", explanation, recommendations),
            _ => ("НЕОПРЕДЕЛЁННОЕ", "❓", explanation, recommendations)
        };
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
    public List<string> QueryVariations { get; set; } = []; // Generated query variations
    public long RagFusionTimeMs { get; set; } // Total time for RAG Fusion search

    // Rerank info
    public long RerankTimeMs { get; set; }
    public int RerankTokensUsed { get; set; }
    public List<int> RerankScores { get; set; } = []; // LLM scores (0-3)
    public bool RerankOrderChanged { get; set; }
    public int RerankFilteredOut { get; set; } // Count of results filtered out due to low score

    // Search results
    public List<DebugSearchResult> SearchResults { get; set; } = [];

    // Confidence assessment
    public string? SearchConfidence { get; set; }
    public string? SearchConfidenceReason { get; set; }
    public double BestScore { get; set; }
    public double ScoreGap { get; set; }
    public bool HasFullTextMatch { get; set; }

    // Personal retrieval info
    public string? PersonalTarget { get; set; } // "self", "@username", or null

    // Intent classification (LLM-based)
    public IntentClassificationDebug? IntentClassification { get; set; }

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
    public List<DebugStage> Stages { get; set; } = [];
}

public class DebugSearchResult
{
    public double Similarity { get; set; }
    public double Distance { get; set; }
    public long[] MessageIds { get; set; } = [];
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

/// <summary>
/// Debug info for LLM-based intent classification
/// </summary>
public class IntentClassificationDebug
{
    public string Intent { get; set; } = "";
    public double Confidence { get; set; }
    public List<string> Entities { get; set; } = [];
    public List<string> MentionedPeople { get; set; } = [];
    public string? TemporalText { get; set; }
    public int? TemporalDays { get; set; }
    public string? Reasoning { get; set; }
}
