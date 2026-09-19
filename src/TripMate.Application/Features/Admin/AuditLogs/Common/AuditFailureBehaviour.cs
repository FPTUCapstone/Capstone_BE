using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Common;

namespace TripMate.Application.Features.Admin.AuditLogs.Common;

/// <summary>Opt-in failure audit for the currently implemented audited commands only.</summary>
public sealed class AuditFailureBehaviour<TRequest, TResponse>(
    IAuditFailureRecorder recorder, ICurrentUserService currentUser)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var target = request switch
        {
            CreatePoiCommand => (AuditActionTypes.PoiCreate, AuditEntityTypes.PointOfInterest, (long?)null),
            ApproveOperatorApplicationCommand command =>
                (AuditActionTypes.OperatorApplicationApprove, AuditEntityTypes.OperatorProfile,
                    command.UserId > 0 ? (long?)command.UserId : null),
            _ => default,
        };
        if (target.Item1 is null || currentUser.UserId is not > 0 || currentUser.Role != "Administrator")
        {
            return await next(cancellationToken);
        }

        TResponse response;
        try
        {
            response = await next(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Supported handlers have unwound their SaveChanges / explicit transaction here.
            // Do not classify arbitrary timeouts/cancellations at commit as known failures.
            await Record(exception is DbUpdateConcurrencyException ? "audit.concurrent_update" : "audit.database_write_failed");
            throw;
        }

        if (response is Result { IsFailure: true } failure && IsStateFailure(failure.ErrorCode))
        {
            await Record(failure.ErrorCode!);
        }

        return response;

        Task Record(string errorCode) => recorder.RecordAsync(new AuditFailureEvent(
            currentUser.UserId.Value, target.Item1, target.Item2!, target.Item3, errorCode));
    }

    private static bool IsStateFailure(string? errorCode) => errorCode is
        PoiErrorCodes.ReferenceNotFound or PoiErrorCodes.PossibleDuplicate or
        TourOperatorApplicationErrorCodes.NotFound or TourOperatorApplicationErrorCodes.NotPending or
        TourOperatorApplicationErrorCodes.WrongRole;
}