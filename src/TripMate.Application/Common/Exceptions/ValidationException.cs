using FluentValidation.Results;

namespace TripMate.Application.Common.Exceptions;

public class ValidationException : Exception
{
    public ValidationException()
        : base("One or more validation failures have occurred.")
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(IEnumerable<ValidationFailure> failures)
        : this()
    {
        var failureList = failures.ToList();
        Errors = failureList
            .GroupBy(f => f.PropertyName, f => f.ErrorMessage)
            .ToDictionary(g => g.Key, g => g.ToArray());
        ErrorCodes = failureList
            .GroupBy(f => f.PropertyName, f => f.ErrorCode)
            .ToDictionary(g => g.Key, g => g.Distinct(StringComparer.Ordinal).ToArray());
    }

    public IDictionary<string, string[]> Errors { get; }
    public IDictionary<string, string[]> ErrorCodes { get; } = new Dictionary<string, string[]>();
}