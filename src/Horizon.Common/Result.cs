namespace Horizon.Common;

/// <summary>Outcome of an operation with no return value.</summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public ErrorCode Error { get; }
    public string Message { get; }

    private Result(bool success, ErrorCode error, string message)
    {
        IsSuccess = success;
        Error = error;
        Message = message;
    }

    public static Result Ok() =>
        new(true, ErrorCode.Success, string.Empty);

    /// <summary>A successful outcome carrying an explanatory message
    /// (e.g. a controlled, non-error early stop).</summary>
    public static Result Ok(string message) =>
        new(true, ErrorCode.Success, message);

    public static Result Fail(ErrorCode code, string message) =>
        new(false, code, message);
}

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T Value { get; }
    public ErrorCode Error { get; }
    public string Message { get; }

    private Result(bool success, T value, ErrorCode error, string message)
    {
        IsSuccess = success;
        Value = value;
        Error = error;
        Message = message;
    }

    public static Result<T> Ok(T value) =>
        new(true, value, ErrorCode.Success, string.Empty);

    public static Result<T> Fail(ErrorCode code, string message) =>
        new(false, default!, code, message);
}
