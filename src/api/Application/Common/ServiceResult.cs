namespace DmarcAnalyzer.Api.Application.Common;

/// <summary>
/// A service outcome the module layer can map straight onto an HTTP response:
/// a value, or an error message plus the status code it should travel with.
/// The convention that keeps Carter modules thin.
/// </summary>
public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, string? error, int statusCode)
    {
        Value = value;
        Error = error;
        StatusCode = statusCode;
    }

    /// <summary>The payload; null on failure.</summary>
    public T? Value { get; }

    /// <summary>The user-safe error message; null on success.</summary>
    public string? Error { get; }

    /// <summary>200 on success; the intended HTTP status otherwise.</summary>
    public int StatusCode { get; }

    /// <summary>True when <see cref="Error"/> is null.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Wraps a value as a 200 result.</summary>
    public static ServiceResult<T> Success(T value) => new(value, null, 200);

    /// <summary>Wraps an error and the status code the endpoint should return.</summary>
    public static ServiceResult<T> Failure(string error, int statusCode) => new(default, error, statusCode);
}
