namespace Horizon.Common;

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
