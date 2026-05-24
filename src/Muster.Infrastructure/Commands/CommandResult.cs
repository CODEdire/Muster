namespace Muster.Infrastructure.Commands;

/// <summary>
/// The outcome of a command, independent of any chat platform. Command services return this; thin
/// platform adapters (NetCord modules, web endpoints) decide how to present <see cref="Message"/>.
/// </summary>
public record CommandResult(string Message, bool IsError = false)
{
    public static CommandResult Ok(string message) => new(message);

    public static CommandResult Error(string message) => new(message, IsError: true);
}
