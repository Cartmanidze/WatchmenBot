using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

public class AskHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly EmbeddingService _embeddingService;
    private readonly OpenRouterClient _llm;
    private readonly ILogger<AskHandler> _logger;

    public AskHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        OpenRouterClient llm,
        ILogger<AskHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _llm = llm;
        _logger = logger;
    }

    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var question = ParseQuestion(message.Text);

        if (string.IsNullOrWhiteSpace(question))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: """
                    🎭 <b>Спроси меня про кого-то из чата!</b>

                    Примеры:
                    • <code>/ask что за тип этот Глеб?</code>
                    • <code>/ask кто тут самый активный?</code>
                    • <code>/ask что думает Женя о работе?</code>
                    """,
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
            return;
        }

        try
        {
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            _logger.LogInformation("[Ask] Question: {Question} in chat {ChatId}", question, chatId);

            // Get relevant context from embeddings
            var results = await _embeddingService.SearchSimilarAsync(chatId, question, limit: 15, ct);

            if (results.Count == 0)
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "Не нашёл релевантной информации в истории чата. Возможно, эмбеддинги ещё не созданы.",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
                return;
            }

            // Build context from search results
            var context = BuildContext(results);

            // Generate answer using LLM
            var answer = await GenerateAnswerAsync(question, context, ct);

            // Format response with sources
            var response = FormatResponse(question, answer, results.Take(3).ToList());

            try
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: response,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException)
            {
                // Fallback to plain text
                var plainText = System.Text.RegularExpressions.Regex.Replace(response, "<[^>]+>", "");
                await _bot.SendMessage(
                    chatId: chatId,
                    text: plainText,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }

            _logger.LogInformation("[Ask] Answered question: {Question}", question);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ask] Failed for question: {Question}", question);

            await _bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при обработке вопроса. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private static string ParseQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
            return string.Empty;

        return text[(spaceIndex + 1)..].Trim();
    }

    private static string BuildContext(List<SearchResult> results)
    {
        var sb = new StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine(result.ChunkText);
            sb.AppendLine("---");
        }
        return sb.ToString();
    }

    private async Task<string> GenerateAnswerAsync(string question, string context, CancellationToken ct)
    {
        var systemPrompt = """
            Ты — ОЧЕНЬ остроумный и саркастичный летописец чата. Твои ответы должны быть уровня стендап-комика.

            Твой стиль:
            - Будь ОСТРОУМНЫМ — не просто смешным, а с умными подколами и неожиданными поворотами
            - Используй иронию, сарказм, игру слов, двусмысленности
            - Мат органично вплетай в речь — хуй, блядь, пиздец, ебать, нахуй
            - Делай неожиданные сравнения и метафоры (чем абсурднее, тем лучше)
            - Подмечай противоречия в поведении людей
            - Цитируй самые идиотские или гениальные высказывания
            - Упоминай людей по имени, создавай им "образы" и "титулы"
            - Если информации мало — выкрути это в шутку

            ФОРМАТ (HTML):
            🎭 <b>Остроумный заголовок-панчлайн</b>

            Основной текст — живой, с подколами, как будто рассказываешь историю в баре.

            💬 <i>«убойная цитата»</i> — комментарий

            Пиши так, чтобы человек заржал. НЕ используй markdown (* _ **).
            """;

        var userPrompt = $"""
            Контекст из чата:
            {context}

            Вопрос: {question}
            """;

        return await _llm.ChatCompletionAsync(systemPrompt, userPrompt, 0.8, ct);
    }

    private static string FormatResponse(string question, string answer, List<SearchResult> topSources)
    {
        // Просто возвращаем ответ без формального оформления
        return answer;
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
