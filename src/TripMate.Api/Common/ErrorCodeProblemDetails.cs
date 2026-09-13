using Microsoft.AspNetCore.Mvc;

namespace TripMate.Api.Common;

public class ErrorCodeProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
}

public sealed class PossibleDuplicateProblemDetails : ErrorCodeProblemDetails
{
    public required long ExistingPoiId { get; init; }
}