using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Extensions;

/// <summary>
/// Extension methods for safe Telegram Bot API calls with automatic 403 handling.
/// All methods automatically deactivate chat when bot is kicked.
/// </summary>
public static partial class TelegramBotSafeExtensions
{
    // Cached regex for stripping HTML tags (used in fallback)
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
    /// <summary>
    /// Send typing indicator with automatic 403 handling.
    /// Returns false if chat was deactivated, true otherwise.
    /// </summary>
    public static async Task<bool> TrySendChatActionAsync(
        this ITelegramBotClient bot,
        ChatStatusService chatStatusService,
        long chatId,
        ChatAction action,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            await bot.SendChatAction(chatId, action, cancellationToken: ct);
            return true;
        }
        catch (ApiRequestException ex) when (ex.ShouldDeactivateChat())
        {
            var reason = ex.GetDeactivationReason();
            await chatStatusService.DeactivateChatAsync(chatId, reason, ct);
            logger.LogWarning("[Telegram] Chat {ChatId} deactivated on {Action}: {Reason}",
                chatId, action, reason);
            return false;
        }
    }

    /// <summary>
    /// Send message with automatic 403 handling.
    /// Throws ChatDeactivatedException if chat was deactivated.
    /// </summary>
    public static async Task<Message> SendMessageSafeAsync(
        this ITelegramBotClient bot,
        ChatStatusService chatStatusService,
        long chatId,
        string text,
        ILogger logger,
        ParseMode? parseMode = null,
        int? replyToMessageId = null,
        bool disableLinkPreview = true,
        CancellationToken ct = default)
    {
        try
        {
            // Note: parseMode must be passed conditionally as it's non-nullable in Telegram.Bot API
            if (parseMode.HasValue)
            {
                return await bot.SendMessage(
                    chatId: chatId,
                    text: text,
                    parseMode: parseMode.Value,
                    linkPreviewOptions: disableLinkPreview ? new LinkPreviewOptions { IsDisabled = true } : null,
                    replyParameters: replyToMessageId.HasValue
                        ? new ReplyParameters { MessageId = replyToMessageId.Value }
                        : null,
                    cancellationToken: ct);
            }
            else
            {
                return await bot.SendMessage(
                    chatId: chatId,
                    text: text,
                    linkPreviewOptions: disableLinkPreview ? new LinkPreviewOptions { IsDisabled = true } : null,
                    replyParameters: replyToMessageId.HasValue
                        ? new ReplyParameters { MessageId = replyToMessageId.Value }
                        : null,
                    cancellationToken: ct);
            }
        }
        catch (ApiRequestException ex) when (ex.ShouldDeactivateChat())
        {
            var reason = ex.GetDeactivationReason();
            await chatStatusService.DeactivateChatAsync(chatId, reason, ct);
            logger.LogWarning("[Telegram] Chat {ChatId} deactivated on SendMessage: {Reason}",
                chatId, reason);
            throw new ChatDeactivatedException(chatId, reason, ex);
        }
    }

    /// <summary>
    /// Send message with HTML, falling back to plain text on parse errors.
    /// Handles 403 at both stages.
    /// Throws ChatDeactivatedException if chat was deactivated.
    /// </summary>
    public static async Task<Message> SendHtmlMessageSafeAsync(
        this ITelegramBotClient bot,
        ChatStatusService chatStatusService,
        long chatId,
        string htmlText,
        ILogger logger,
        int? replyToMessageId = null,
        CancellationToken ct = default)
    {
        try
        {
            return await bot.SendMessage(
                chatId: chatId,
                text: htmlText,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: replyToMessageId.HasValue
                    ? new ReplyParameters { MessageId = replyToMessageId.Value }
                    : null,
                cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.ShouldDeactivateChat())
        {
            var reason = ex.GetDeactivationReason();
            await chatStatusService.DeactivateChatAsync(chatId, reason, ct);
            logger.LogWarning("[Telegram] Chat {ChatId} deactivated on SendHtmlMessage: {Reason}",
                chatId, reason);
            throw new ChatDeactivatedException(chatId, reason, ex);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[Telegram] HTML parsing failed for chat {ChatId}, retrying as plain text", chatId);

            // Strip HTML tags and retry (using cached regex for performance)
            var plainText = HtmlTagRegex().Replace(htmlText, "");

            try
            {
                return await bot.SendMessage(
                    chatId: chatId,
                    text: plainText,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyParameters: replyToMessageId.HasValue
                        ? new ReplyParameters { MessageId = replyToMessageId.Value }
                        : null,
                    cancellationToken: ct);
            }
            catch (ApiRequestException innerEx) when (innerEx.ShouldDeactivateChat())
            {
                var reason = innerEx.GetDeactivationReason();
                await chatStatusService.DeactivateChatAsync(chatId, reason, ct);
                logger.LogWarning("[Telegram] Chat {ChatId} deactivated on fallback: {Reason}",
                    chatId, reason);
                throw new ChatDeactivatedException(chatId, reason, innerEx);
            }
        }
    }
}

/// <summary>
/// Exception thrown when a chat is deactivated due to 403 error.
/// Allows callers to handle chat deactivation specifically.
/// </summary>
public class ChatDeactivatedException : Exception
{
    public long ChatId { get; }
    public string DeactivationReason { get; }

    public ChatDeactivatedException()
        : base("Chat was deactivated")
    {
        DeactivationReason = "Unknown";
    }

    public ChatDeactivatedException(string message)
        : base(message)
    {
        DeactivationReason = message;
    }

    public ChatDeactivatedException(string message, Exception innerException)
        : base(message, innerException)
    {
        DeactivationReason = message;
    }

    public ChatDeactivatedException(long chatId, string reason, Exception? innerException = null)
        : base($"Chat {chatId} was deactivated: {reason}", innerException)
    {
        ChatId = chatId;
        DeactivationReason = reason;
    }
}
