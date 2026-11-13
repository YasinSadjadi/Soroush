namespace Soroush.Mediator.Abstractions;

/// <summary>Identifies the stage that produced a handler result.</summary>
public enum HandlerResultCode
{
    /// <summary>The operation succeeded.</summary>
    Success,
    /// <summary>Access was denied or access checking failed.</summary>
    HasAccess,
    /// <summary>Command validation failed.</summary>
    Validation,
    /// <summary>Execution failed.</summary>
    Execution,
    /// <summary>Stream preparation failed.</summary>
    PreProcess,
    /// <summary>Stream cleanup failed.</summary>
    PostProcess
}

/// <summary>A handler operation outcome, including an optional error message.</summary>
public class HandlerResult
{
    /// <summary>Creates an outcome.</summary>
    protected HandlerResult(HandlerResultCode code, string? message) { Code = code; Message = message; }
    /// <summary>The stage that failed, or Success.</summary>
    public HandlerResultCode Code { get; }
    /// <summary>An optional description of the failure.</summary>
    public string? Message { get; }
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess => Code == HandlerResultCode.Success;
    /// <summary>Creates a successful outcome.</summary>
    public static HandlerResult Success() => new HandlerResult(HandlerResultCode.Success, null);
    /// <summary>Creates a failed outcome.</summary>
    public static HandlerResult Failure(HandlerResultCode code, string? message = null)
    {
        if (code == HandlerResultCode.Success) throw new ArgumentException("Failure requires a failure code.", nameof(code));
        return new HandlerResult(code, message);
    }
    internal HandlerResult At(HandlerResultCode code) => IsSuccess ? this : Failure(code, Message);
}

/// <summary>A handler outcome containing a value on success.</summary>
public sealed class HandlerResult<T> : HandlerResult
{
    private HandlerResult(HandlerResultCode code, string? message, T? value) : base(code, message) => Value = value;
    /// <summary>The successful value; default on failure.</summary>
    public T? Value { get; }
    /// <summary>Creates a successful outcome.</summary>
    public static HandlerResult<T> Success(T value) => new HandlerResult<T>(HandlerResultCode.Success, null, value);
    /// <summary>Creates a failed outcome.</summary>
    public new static HandlerResult<T> Failure(HandlerResultCode code, string? message = null)
    {
        if (code == HandlerResultCode.Success) throw new ArgumentException("Failure requires a failure code.", nameof(code));
        return new HandlerResult<T>(code, message, default);
    }
}

/// <summary>Reports a stream cleanup failure when no result can be emitted.</summary>
public sealed class HandlerStageException : Exception
{
    /// <summary>Creates a stage exception.</summary>
    public HandlerStageException(HandlerResultCode code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;
    /// <summary>Creates a cleanup exception that also preserves an earlier producer exception.</summary>
    public HandlerStageException(HandlerResultCode code, string message, Exception? innerException, Exception? primaryException)
        : this(code, message, innerException) => PrimaryException = primaryException;
    /// <summary>The exception that preceded cleanup failure, including its original stack.</summary>
    public Exception? PrimaryException { get; }
    /// <summary>The stage that failed.</summary>
    public HandlerResultCode Code { get; }
}
