using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

using DomainAuditLog = TripMate.Domain.Entities.AuditLog;
using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.Features.TourMedia.UpdateMetadata;

public sealed class UpdateTourMediaMetadataCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<UpdateTourMediaMetadataCommand, Result<TourMediaDto>>
{
    public async Task<Result<TourMediaDto>> Handle(
        UpdateTourMediaMetadataCommand command,
        CancellationToken cancellationToken)
    {
        TourMediaAccess access = await TourMediaAccessResolver.AuthorizeAsync(
            dbContext, command.TourId, command.CurrentUserId, cancellationToken);
        if (!access.IsAuthorized)
        {
            return Result.Failure<TourMediaDto>(access.FailureCode!, "The tour is unavailable.");
        }

        DomainTourMedia? media = await dbContext.TourMedia.SingleOrDefaultAsync(candidate =>
            candidate.Id == command.TourMediaId && candidate.TourId == command.TourId,
            cancellationToken);
        if (media is null || media.LifecycleStatus != Domain.Enums.TourMediaLifecycleStatus.Active)
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.TourMediaNotFound, "The media is unavailable.");
        }

        string? normalizedCaption = NormalizeCaption(command.Caption);
        if (!HasValidMetadata(normalizedCaption, command.AltText))
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.InvalidRequest, "The media metadata is invalid.");
        }

        // Approved spec treats alt-text-only accessibility corrections as minor edits.
        // Caption changes remain material, and Pending tours freeze both fields.
        if (access.Tour!.Status == TourStatus.Pending ||
            (!TourMediaAccessResolver.AllowsMaterialChange(access.Tour.Status) &&
             !string.Equals(media.Caption, normalizedCaption, StringComparison.Ordinal)))
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.TourMediaChangeLocked,
                "The tour media cannot be changed in its current state.");
        }

        string previousCaption = media.Caption ?? string.Empty;
        string previousAltText = media.AltText;
        try
        {
            media.UpdateMetadata(normalizedCaption, command.AltText, dateTimeProvider.UtcNow);
        }
        catch (ArgumentException)
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.InvalidRequest, "The media metadata is invalid.");
        }

        dbContext.AuditLogs.Add(DomainAuditLog.CreateRecordedOutcome(
            command.CurrentUserId,
            AuditActionTypes.TourMediaMetadataUpdated,
            AuditEntityTypes.TourMedia,
            media.Id,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Caption = previousCaption, AltText = previousAltText }),
            afterData: JsonSerializer.Serialize(new { media.Caption, media.AltText })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(TourMediaProjection.ToDto(media));
    }

    private static string? NormalizeCaption(string? caption) =>
        string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();

    private static bool HasValidMetadata(string? caption, string altText) =>
        !string.IsNullOrWhiteSpace(altText) &&
        altText.Trim().Length <= DomainTourMedia.AltTextMaxLength &&
        (caption is null || caption.Length <= DomainTourMedia.CaptionMaxLength);
}