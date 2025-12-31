using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for handling confidence gates and context building with early returns
/// </summary>
public class ConfidenceGateService
{
    private readonly ITelegramBotClient _bot;
    private readonly ContextBuilderService _contextBuilder;
    private readonly DebugReportCollector _debugCollector;
    private readonly DebugService _debugService;

    public ConfidenceGateService(
        ITelegramBotClient bot,
        ContextBuilderService contextBuilder,
        DebugReportCollector debugCollector,
        DebugService debugService)
    {
        _bot = bot;
        _contextBuilder = contextBuilder;
        _debugCollector = debugCollector;
        _debugService = debugService;
    }

    /// <summary>
    /// Process search results: check confidence, build context, handle early returns
    /// Returns: (context, confidenceWarning, contextTracker, shouldContinue)
    /// shouldContinue = false means early return was sent to user
    /// </summary>
    public async Task<(string? context, string? confidenceWarning, Dictionary<long, (bool included, string reason)> tracker, bool shouldContinue)>
        ProcessSearchResultsAsync(
            string command,
            long chatId,
            Message message,
            SearchResponse searchResponse,
            DebugReport debugReport,
            CancellationToken ct)
    {
        var results = searchResponse.Results;
        string? context = null;
        string? confidenceWarning = null;
        var contextTracker = new Dictionary<long, (bool included, string reason)>();

        // Collect debug info for search response
        _debugCollector.CollectSearchResponseDebugInfo(debugReport, searchResponse);

        if (command == "smart")
        {
            // /smart — без контекста, прямой запрос к Perplexity
            context = null;
            foreach (var r in results)
                contextTracker[r.MessageId] = (false, "smart_no_context");

            return (context, confidenceWarning, contextTracker, true);
        }

        // /ask requires context from chat
        if (searchResponse.Confidence == SearchConfidence.None)
        {
            foreach (var r in results)
                contextTracker[r.MessageId] = (false, "confidence_none");

            // Collect debug info before early return
            _debugCollector.CollectSearchDebugInfo(debugReport, results, contextTracker, personalTarget: null);

            await _bot.SendMessage(
                chatId: chatId,
                text: "🤷 В истории чата про это не нашёл. Попробуй уточнить вопрос или период.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);

            await _debugService.SendDebugReportAsync(debugReport, ct);

            // Signal early return
            return (null, null, contextTracker, false);
        }

        if (searchResponse.Confidence == SearchConfidence.Low)
        {
            confidenceWarning = "⚠️ <i>Контекст слабый, ответ может быть неточным</i>\n\n";
        }

        (context, contextTracker) = await _contextBuilder.BuildContextWithWindowsAsync(chatId, results, ct);

        return (context, confidenceWarning, contextTracker, true);
    }
}
