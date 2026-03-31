namespace DmarcAnalyzer.Api.Application.Common;

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, string? error, int statusCode)
    {
        Value = value;
        Error = error;
        StatusCode = statusCode;
    }

    public T? Value { get; }
    public string? Error { get; }
    public int StatusCode { get; }
    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Success(T value) => new(value, null, 200);
    public static ServiceResult<T> Failure(string error, int statusCode) => new(default, error, statusCode);
}
