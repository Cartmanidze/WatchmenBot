namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// Registry for admin commands using Command Pattern
/// Maps command names to their handlers
/// </summary>
public class AdminCommandRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, IAdminCommand>> _commands = new();

    /// <summary>
    /// Register a command with its factory
    /// </summary>
    public void Register<TCommand>(string commandName) where TCommand : IAdminCommand
    {
        _commands[commandName.ToLowerInvariant()] = sp => sp.GetRequiredService<TCommand>();
    }

    /// <summary>
    /// Register a command with a custom factory
    /// </summary>
    public void Register(string commandName, Func<IServiceProvider, IAdminCommand> factory)
    {
        _commands[commandName.ToLowerInvariant()] = factory;
    }

    /// <summary>
    /// Get command handler by name
    /// </summary>
    public IAdminCommand? GetCommand(string commandName, IServiceProvider serviceProvider)
    {
        if (_commands.TryGetValue(commandName.ToLowerInvariant(), out var factory))
        {
            return factory(serviceProvider);
        }
        return null;
    }

    /// <summary>
    /// Check if command exists
    /// </summary>
    public bool HasCommand(string commandName)
    {
        return _commands.ContainsKey(commandName.ToLowerInvariant());
    }
}
