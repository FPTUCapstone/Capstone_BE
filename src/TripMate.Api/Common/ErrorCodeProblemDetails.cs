using Microsoft.AspNetCore.Mvc;

namespace TripMate.Api.Common;

public class ErrorCodeProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
}

public sealed class ErrorCodeValidationProblemDetails : ValidationProblemDetails
{
    public required string ErrorCode { get; init; }
}

public sealed class PossibleDuplicateProblemDetails : ErrorCodeProblemDetails
{
    public required long ExistingPoiId { get; init; }
}

public sealed class NavigationStartConflictProblemDetails : ErrorCodeProblemDetails
{
    public long? ActiveSessionId { get; init; }

    public string? ActiveSessionLocation { get; init; }
}