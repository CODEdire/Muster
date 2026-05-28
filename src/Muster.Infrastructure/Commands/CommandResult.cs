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

/// <summary>A <see cref="CommandResult"/> that carries a payload (e.g. the new entity's id) on success.</summary>
public record CommandResult<T>(string Message, T? Value, bool IsError = false) : CommandResult(Message, IsError)
{
    public static CommandResult<T> Ok(T value, string message) => new(message, value);
    public new static CommandResult<T> Error(string message) => new(message, default, IsError: true);
}
