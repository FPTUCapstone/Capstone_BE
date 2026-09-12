using System.Collections.ObjectModel;

namespace TripMate.Application.Common.Models;

public class Result
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyErrorMetadata =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    protected Result(
        bool isSuccess,
        string? errorCode,
        string? errorMessage,
        IReadOnlyDictionary<string, object?>? errorMetadata = null)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ErrorMetadata = errorMetadata is null
            ? EmptyErrorMetadata
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(errorMetadata));
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public IReadOnlyDictionary<string, object?> ErrorMetadata { get; }

    public static Result Success() => new(true, null, null);

    public static Result Failure(
        string errorCode,
        string errorMessage,
        IReadOnlyDictionary<string, object?>? errorMetadata = null) =>
        new(false, errorCode, errorMessage, errorMetadata);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(
        string errorCode,
        string errorMessage,
        IReadOnlyDictionary<string, object?>? errorMetadata = null) =>
        Result<T>.Failure(errorCode, errorMessage, errorMetadata);
}

public class Result<T> : Result
{
    private readonly T? _value;

    private Result(
        bool isSuccess,
        T? value,
        string? errorCode,
        string? errorMessage,
        IReadOnlyDictionary<string, object?>? errorMetadata = null)
        : base(isSuccess, errorCode, errorMessage, errorMetadata)
    {
        _value = value;
    }

    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public static Result<T> Success(T value) => new(true, value, null, null);

    public static new Result<T> Failure(
        string errorCode,
        string errorMessage,
        IReadOnlyDictionary<string, object?>? errorMetadata = null) =>
        new(false, default, errorCode, errorMessage, errorMetadata);
}