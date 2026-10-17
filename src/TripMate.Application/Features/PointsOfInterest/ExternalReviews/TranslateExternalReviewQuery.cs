using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

/// <summary>Machine translation of one Tripadvisor review of a POI, on the traveler's request.</summary>
public sealed record TranslateExternalReviewQuery(
    long PoiId,
    long ReviewId,
    string Target = TranslateExternalReviewQuery.Vietnamese)
    : IRequest<Result<TranslatedExternalReviewDto>>
{
    public const string Vietnamese = "vi";

    public static readonly string[] SupportedTargets = [Vietnamese];
}

public sealed record TranslatedExternalReviewDto(
    long ReviewId,
    string Language,
    string? Title,
    string? Text,
    bool MachineTranslated);