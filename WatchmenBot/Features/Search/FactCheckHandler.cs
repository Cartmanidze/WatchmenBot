using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Features.Search;

public class FactCheckHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly MessageStore _messageStore;
    private readonly LlmRouter _llmRouter;
    private readonly PromptSettingsStore _promptSettings;
    private readonly ILogger<FactCheckHandler> _logger;

    public FactCheckHandler(
        ITelegramBotClient bot,
        MessageStore messageStore,
        LlmRouter llmRouter,
        PromptSettingsStore promptSettings,
        ILogger<FactCheckHandler> logger)
    {
        _bot = bot;
        _messageStore = messageStore;
        _llmRouter = llmRouter;
        _promptSettings = promptSettings;
        _logger = logger;
    }

    /// <summary>
    /// Handle /truth command - fact-check last N messages
    /// </summary>
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;

        // Parse optional count from command (default 15)
        var count = ParseCount(message.Text, defaultCount: 15, maxCount: 30);

        try
        {
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            _logger.LogInformation("[TRUTH] Checking last {Count} messages in chat {ChatId}", count, chatId);

            // Get latest messages
            var messages = await _messageStore.GetLatestMessagesAsync(chatId, count);

            if (messages.Count == 0)
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "Не нашёл сообщений для проверки.",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
                return;
            }

            // Build conversation context
            var context = BuildContext(messages);

            // Get prompt settings
            var settings = await _promptSettings.GetSettingsAsync("truth");

            var userPrompt = $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}

                Последние {messages.Count} сообщений из чата:

                {context}

                Проанализируй и проверь факты.
                """;

            var response = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = settings.SystemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.5 // Balanced for accuracy + humor
                },
                preferredTag: settings.LlmTag,
                ct: ct);

            _logger.LogDebug("[TRUTH] Used provider: {Provider}", response.Provider);

            // Send response
            try
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: response.Content,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException)
            {
                // Fallback to plain text
                var plainText = System.Text.RegularExpressions.Regex.Replace(response.Content, "<[^>]+>", "");
                await _bot.SendMessage(
                    chatId: chatId,
                    text: plainText,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }

            _logger.LogInformation("[TRUTH] Completed fact-check for {Count} messages", messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TRUTH] Failed to fact-check in chat {ChatId}", chatId);

            await _bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при проверке фактов. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private static int ParseCount(string? text, int defaultCount, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return defaultCount;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return defaultCount;

        if (int.TryParse(parts[1], out var count) && count > 0)
            return Math.Min(count, maxCount);

        return defaultCount;
    }

    private static string BuildContext(List<Models.MessageRecord> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName)
                ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username)
                : m.DisplayName;

            var text = string.IsNullOrWhiteSpace(m.Text) ? "[медиа]" : m.Text;
            sb.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
        }
        return sb.ToString();
    }
}
