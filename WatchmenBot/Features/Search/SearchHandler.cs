using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

public class SearchHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly EmbeddingService _embeddingService;
    private readonly ILogger<SearchHandler> _logger;

    public SearchHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        ILogger<SearchHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var query = ParseQuery(message.Text);

        if (string.IsNullOrWhiteSpace(query))
        {
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "Использование: <code>/search запрос</code>\n\nПример: <code>/search что решили про релиз</code>",
                parseMode: ParseMode.Html,
                replyToMessageId: message.MessageId,
                cancellationToken: ct);
            return;
        }

        try
        {
            await _bot.SendChatActionAsync(chatId, ChatAction.Typing, cancellationToken: ct);

            _logger.LogInformation("[Search] Query: {Query} in chat {ChatId}", query, chatId);

            var results = await _embeddingService.SearchSimilarAsync(chatId, query, limit: 1, ct);

            if (results.Count == 0)
            {
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Ничего не найдено. Возможно, эмбеддинги ещё не созданы для этого чата.",
                    replyToMessageId: message.MessageId,
                    cancellationToken: ct);
                return;
            }

            var response = FormatResults(query, results);

            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Html,
                disableWebPagePreview: true,
                replyToMessageId: message.MessageId,
                cancellationToken: ct);

            _logger.LogInformation("[Search] Found {Count} results for query: {Query}", results.Count, query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Search] Failed for query: {Query}", query);

            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "Произошла ошибка при поиске. Попробуйте позже.",
                replyToMessageId: message.MessageId,
                cancellationToken: ct);
        }
    }

    private static string ParseQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // /search query or /search@botname query
        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
            return string.Empty;

        return text[(spaceIndex + 1)..].Trim();
    }

    private static string FormatResults(string query, List<SearchResult> results)
    {
        var result = results.First();
        var text = TruncateText(result.ChunkText, 500);

        return $"<b>🔍 Долбоёб найден:</b>\n\n{EscapeHtml(text)}";
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Remove timestamp prefix if present [2024-01-01 12:00]
        if (text.StartsWith("[") && text.Length > 20)
        {
            var closeBracket = text.IndexOf(']');
            if (closeBracket > 0 && closeBracket < 25)
                text = text[(closeBracket + 1)..].TrimStart();
        }

        if (text.Length <= maxLength)
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
