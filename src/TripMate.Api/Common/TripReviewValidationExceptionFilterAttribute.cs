using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using TripMate.Application.Features.TripReviews.Common;

using ValidationException = TripMate.Application.Common.Exceptions.ValidationException;

namespace TripMate.Api.Common;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TripReviewValidationExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not ValidationException exception)
            return;

        var problem = new ErrorCodeValidationProblemDetails
        {
            Title = "One or more validation errors occurred.",
            Status = StatusCodes.Status400BadRequest,
            ErrorCode = TripReviewErrorCodes.InvalidInput,
        };
        foreach (var error in ValidationErrorKeyNormalizer.Normalize(
                     exception.Errors,
                     JsonNamingPolicy.CamelCase))
        {
            problem.Errors.Add(error.Key, error.Value);
        }

        var result = new BadRequestObjectResult(problem);
        result.ContentTypes.Add("application/problem+json");
        context.Result = result;
        context.ExceptionHandled = true;
    }
}