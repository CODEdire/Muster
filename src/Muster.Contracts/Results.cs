namespace Muster.Contracts;

/// <summary>
/// Standard outcome envelope returned by command handlers. <see cref="Status"/> is a stable reason code
/// (e.g. a domain result enum's name) the adapters map to user-facing text / HTTP status; <see cref="Ok"/>
/// is the success flag. Use <see cref="Result{T}"/> when the command produces a value.
/// </summary>
public record Result(bool Ok, string Status)
{
    public static Result Success(string status = "Ok") => new(true, status);
    public static Result Fail(string status) => new(false, status);
}

/// <summary>A <see cref="Result"/> that carries a payload on success. Inherits Result so it can flow through
/// any consumer typed for the base — including the Wolverine audit middleware that down-casts to read Value.</summary>
public record Result<T>(bool Ok, string Status, T? Value) : Result(Ok, Status)
{
    public static Result<T> Success(T value, string status = "Ok") => new(true, status, value);
    public new static Result<T> Fail(string status) => new(false, status, default);
}
