using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// Base class for admin commands with common dependencies and utility methods
/// </summary>
public abstract class AdminCommandBase : IAdminCommand
{
    protected readonly ITelegramBotClient Bot;
    protected readonly ILogger Logger;

    protected AdminCommandBase(ITelegramBotClient bot, ILogger logger)
    {
        Bot = bot;
        Logger = logger;
    }

    public abstract Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct);

    /// <summary>
    /// Send HTML-formatted message to chat
    /// </summary>
    protected async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        await Bot.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    /// <summary>
    /// Escape HTML special characters
    /// </summary>
    protected static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    /// <summary>
    /// Generate progress bar from percentage
    /// </summary>
    protected static string GenerateProgressBar(double percentage, int length)
    {
        var filled = (int)Math.Round(percentage / 100 * length);
        var empty = length - filled;
        return new string('█', filled) + new string('░', empty);
    }
}
