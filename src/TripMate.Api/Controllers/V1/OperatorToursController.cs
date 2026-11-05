using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Delete;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;
using TripMate.Application.Features.Operator.Tours.Get;
using TripMate.Application.Features.Operator.Tours.List;
using TripMate.Application.Features.Operator.Tours.Policies;
using TripMate.Application.Features.Operator.Tours.SubmitReview;
using TripMate.Application.Features.Operator.Tours.Update;
using TripMate.Application.Features.Operator.Tours.Weather;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/tours")]
public sealed class OperatorToursController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(OperatorTourListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ListOperatorToursQuery.DefaultPageSize)
    {
        var result = await Sender.Send(new ListOperatorToursQuery(status, page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{tourId:long}")]
    [ProducesResponseType(typeof(OperatorTourDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorTourQuery(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpDelete("{tourId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DeleteOperatorTourCommand(tourId), cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

    [HttpPut("{tourId:long}")]
    [ProducesResponseType(typeof(UpdateTourDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        long tourId,
        UpdateTourDraftCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { TourId = tourId }, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{tourId:long}/weather-readiness")]
    [ProducesResponseType(typeof(ReviewTourWeatherReadinessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReviewWeatherReadiness(
        long tourId,
        ReviewTourWeatherReadinessCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { TourId = tourId }, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{tourId:long}/schedules/{scheduleId:long}/emergency-impact")]
    [ProducesResponseType(typeof(EmergencyTourImpactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetEmergencyImpact(
        long tourId,
        long scheduleId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new GetEmergencyTourImpactQuery(tourId, scheduleId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/schedules/{scheduleId:long}/cancel-before-departure")]
    [ProducesResponseType(typeof(EmergencyTourActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelBeforeDeparture(
        long tourId,
        long scheduleId,
        CancelBeforeDepartureRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CancelBeforeDepartureCommand(
            tourId, scheduleId, idempotencyKey, request.ImpactVersion, request.Reason,
            request.ReasonDetails, request.EmergencyContact, request.EvidenceReference,
            request.EvidenceUrl, request.AuthorityReference, request.AddEvidenceLater),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/schedules/{scheduleId:long}/emergency-stop")]
    [EnableRateLimiting(EmergencyStopRateLimiter.PolicyName)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(EmergencyTourActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EmergencyStop(
        long tourId,
        long scheduleId,
        EmergencyStopActiveRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new EmergencyStopActiveCommand(
            tourId, scheduleId, idempotencyKey, request.ImpactVersion, request.CurrentPassword,
            request.Reason, request.ReasonDetails, request.ImmediateInstructions, request.EmergencyContact,
            request.EvidenceReference, request.EvidenceUrl,
            request.AuthorityReference, request.AddEvidenceLater), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{tourId:long}/schedules/{scheduleId:long}/emergency-evidence")]
    [ProducesResponseType(typeof(EmergencyTourEvidenceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEmergencyEvidence(
        long tourId,
        long scheduleId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new GetEmergencyTourEvidenceQuery(tourId, scheduleId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{tourId:long}/schedules/{scheduleId:long}/emergency-evidence")]
    [ProducesResponseType(typeof(AddEmergencyTourEvidenceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddEmergencyEvidence(
        long tourId,
        long scheduleId,
        AddEmergencyTourEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new AddEmergencyTourEvidenceCommand(
            tourId, scheduleId, request.EvidenceUrl,
            request.AuthorityReference, request.EvidenceReference, request.WithdrawEvidence,
            request.ExpectedVersion),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{tourId:long}/cancellation-policy/{cancellationPolicyTemplateId:long}")]
    [ProducesResponseType(typeof(AssignTourCancellationPolicyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AssignCancellationPolicy(long tourId, long cancellationPolicyTemplateId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new AssignTourCancellationPolicyCommand(tourId, cancellationPolicyTemplateId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("cancellation-policies")]
    [ProducesResponseType(typeof(IReadOnlyList<CancellationPolicyTemplateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCancellationPolicies(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ListCancellationPolicyTemplatesQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateTourPackageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        CreateTourPackageCommand command,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { IdempotencyKey = idempotencyKey }, cancellationToken);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/submit-review")]
    [ProducesResponseType(typeof(SubmitTourForReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SubmitForReview(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new SubmitTourForReviewCommand(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
