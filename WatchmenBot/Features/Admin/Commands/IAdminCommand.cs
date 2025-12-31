using Telegram.Bot.Types;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// Interface for admin command handlers using Command Pattern
/// </summary>
public interface IAdminCommand
{
    /// <summary>
    /// Execute the admin command
    /// </summary>
    Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct);
}

/// <summary>
/// Context passed to admin commands containing message info and parsed arguments
/// </summary>
public class AdminCommandContext
{
    public required long ChatId { get; init; }
    public required Message Message { get; init; }
    public required string[] Args { get; init; }
    public required string FullText { get; init; }
}
