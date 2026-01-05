using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Infrastructure.Settings;

namespace WatchmenBot.Features.Settings;

/// <summary>
/// Handler for /mode command - allows users to switch chat response mode.
/// Only the bot owner can change mode; others can view current mode.
/// </summary>
public class ModeHandler(
    ITelegramBotClient bot,
    ChatSettingsStore chatSettings,
    ILogger<ModeHandler> logger)
{
    /// <summary>
    /// Only this user can change the mode
    /// </summary>
    private const string OwnerUsername = "gleb_bezrukov";
    /// <summary>
    /// Handle /mode command
    /// Usage:
    /// - /mode           → show current mode and available options
    /// - /mode business  → switch to business mode
    /// - /mode funny     → switch to funny mode
    /// </summary>
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text ?? "";

        // Parse mode argument
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var modeArg = parts.Length > 1 ? parts[1] : null;

        // Get current settings
        var currentSettings = await chatSettings.GetSettingsAsync(chatId);
        var currentMode = currentSettings.Mode;
        var language = currentSettings.Language;

        // Check if user is the owner
        var username = message.From?.Username;
        if (!string.Equals(username, OwnerUsername, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("[MODE] User @{Username} attempted to use /mode in chat {ChatId} (denied)",
                username ?? "unknown", chatId);

            await bot.SendMessage(
                chatId: chatId,
                text: "🔒 Эта команда доступна только владельцу бота.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
            return;
        }

        // If no argument - show current mode and options
        if (string.IsNullOrWhiteSpace(modeArg))
        {
            await ShowCurrentModeAsync(chatId, message.MessageId, currentMode, language, ct);
            return;
        }

        // Try to parse requested mode
        if (!ChatModeExtensions.TryParse(modeArg, out var newMode))
        {
            await bot.SendMessage(
                chatId: chatId,
                text: $"""
                    ❌ Неизвестный режим: <code>{EscapeHtml(modeArg)}</code>

                    Доступные режимы:
                    • <code>/mode business</code> — деловой
                    • <code>/mode funny</code> — весёлый
                    """,
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
            return;
        }

        // If same mode - notify user
        if (newMode == currentMode)
        {
            await bot.SendMessage(
                chatId: chatId,
                text: $"{newMode.GetEmoji()} Режим <b>{newMode.GetDisplayName(language)}</b> уже активен.",
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
            return;
        }

        // Switch mode
        try
        {
            await chatSettings.SetModeAsync(chatId, newMode);

            logger.LogInformation("[MODE] Chat {ChatId} switched from {OldMode} to {NewMode}",
                chatId, currentMode, newMode);

            await bot.SendMessage(
                chatId: chatId,
                text: $"""
                    {newMode.GetEmoji()} Режим изменён: <b>{newMode.GetDisplayName(language)}</b>

                    <i>{newMode.GetDescription(language)}</i>
                    """,
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MODE] Failed to switch mode for chat {ChatId}", chatId);

            await bot.SendMessage(
                chatId: chatId,
                text: "❌ Не удалось изменить режим. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private async Task ShowCurrentModeAsync(long chatId, int messageId, ChatMode currentMode, ChatLanguage language, CancellationToken ct)
    {
        var businessEmoji = currentMode == ChatMode.Business ? "✅" : "○";
        var funnyEmoji = currentMode == ChatMode.Funny ? "✅" : "○";

        await bot.SendMessage(
            chatId: chatId,
            text: $"""
                {currentMode.GetEmoji()} <b>Текущий режим: {currentMode.GetDisplayName(language)}</b>

                <i>{currentMode.GetDescription(language)}</i>

                ──────────────────
                <b>Доступные режимы:</b>

                {businessEmoji} <code>/mode business</code>
                   💼 <b>Деловой</b> — профессиональные ответы

                {funnyEmoji} <code>/mode funny</code>
                   🎭 <b>Весёлый</b> — ответы с юмором и подколками
                """,
            parseMode: ParseMode.Html,
            replyParameters: new ReplyParameters { MessageId = messageId },
            cancellationToken: ct);
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
