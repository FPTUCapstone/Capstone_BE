using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.DevSearch;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Application.Features.Tours.SemanticPoc;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.Services.SemanticPoc;

/// <summary>Bounded, read-only SQL authority for the explicitly enabled development endpoint.</summary>
public sealed class SqlDevTourCatalog(ApplicationDbContext context) : IDevTourCatalog
{
    private const string Collation = TourSearchV2IndependentReader.DestinationSearchCollation;
    public async Task<DevTourSnapshot> ReadAsync(TourSearchV2Request request, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        var validation = await new TourSearchV2RequestValidator(context).ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) throw new TripMate.Application.Common.Exceptions.ValidationException(validation.Errors);
        return await context.ExecuteInSerializableTransactionAsync(async token =>
        {
            var visible = context.Tours.AsNoTracking().Where(t =>
                (t.Status == TourStatus.Published || t.Status == TourStatus.Approved && t.PublishedAtUtc.HasValue && t.PublishedAtUtc <= asOfUtc)
                && t.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
                && t.OperatorProfile.User.Status == AccountStatus.Active);
            var count = await visible.CountAsync(token);
            if (count > SemanticModel.MaxCorpus)
                throw new DevSearchCorpusTooLargeException();
            var corpus = await visible.OrderBy(t => t.Title).ThenBy(t => t.Id).Select(t => new
            {
                t.Id,
                t.Title,
                t.DurationDays,
                t.BasePrice,
                Operator = t.OperatorProfile.CompanyName,
                Destinations = t.Destinations.OrderBy(d => d.SequenceNo).Select(d => d.Destination.Name).ToArray(),
            }).ToArrayAsync(token);
            var documents = corpus.Select(t => new SemanticTourDocument(t.Id, t.Title, t.Destinations, t.Operator, true)).ToArray();
            var filtered = visible;
            if (request.DurationDays.HasValue) filtered = filtered.Where(t => t.DurationDays == request.DurationDays);
            if (request.Category is not null) filtered = filtered.Where(t => t.Category != null && t.Category.IsActive && t.Category.Code == request.Category);
            if (request.MinPrice.HasValue) filtered = filtered.Where(t => t.BasePrice >= request.MinPrice);
            if (request.MaxPrice.HasValue) filtered = filtered.Where(t => t.BasePrice <= request.MaxPrice);
            var destination = request.Destination?.Trim().Normalize();
            if (!string.IsNullOrEmpty(destination)) filtered = filtered.Where(t => t.Destinations.Any(d => EF.Functions.Collate(d.Destination.Name, Collation).Contains(destination)));
            var hasDates = request.StartDate.HasValue || request.EndDate.HasValue;
            var lower = request.StartDate.HasValue ? LocalBound(request.StartDate.Value, false) : DateTimeOffset.MinValue;
            var upper = request.EndDate.HasValue ? LocalBound(request.EndDate.Value, true) : DateTimeOffset.MaxValue;
            if (hasDates) filtered = filtered.Where(t => t.Schedules.Any(s => s.Status != TourScheduleStatus.Cancelled
                && s.EndAtUtc > s.StartAtUtc && s.StartAtUtc >= lower && s.StartAtUtc < upper));
            var ratings = await TripReviewAggregateReader.QueryTourRatings(context, visible).ToDictionaryAsync(r => r.TourId, token);
            if (request.MinimumRating.HasValue)
            {
                var minimum = request.MinimumRating.Value;
                var ratedIds = TripReviewAggregateReader.QueryTourRatings(context, visible)
                    .Where(r => r.RawAverage.HasValue && r.RawAverage >= minimum).Select(r => r.TourId);
                filtered = filtered.Where(t => ratedIds.Contains(t.Id));
            }
            var eligible = (await filtered.Select(t => t.Id).ToArrayAsync(token)).ToHashSet();
            var schedules = await visible.SelectMany(t => t.Schedules.Where(s => s.Status != TourScheduleStatus.Cancelled
                    && s.EndAtUtc > s.StartAtUtc && (hasDates ? s.StartAtUtc >= lower && s.StartAtUtc < upper : s.StartAtUtc >= asOfUtc))
                .OrderBy(s => s.StartAtUtc).ThenBy(s => s.Id).Take(1), (t, s) => new
                { TourId = t.Id, s.Id, s.StartAtUtc, s.TotalCapacity, s.ReservedCapacity }).ToDictionaryAsync(s => s.TourId, token);
            var thumbnails = await context.TourMedia.AsNoTracking().Where(m => visible.Select(t => t.Id).Contains(m.TourId) && m.IsPrimary)
                .ToDictionaryAsync(m => m.TourId, m => m.DeliveryUrl, token);
            var words = (request.Keyword ?? "").Trim().Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
            var lexical = await ReadLexicalAsync(documents.Select(d => d.TourId).ToArray(), request.Keyword?.Trim().Normalize() ?? "", words, token);
            var rows = corpus.Select((t, order) =>
            {
                var s = schedules.GetValueOrDefault(t.Id);
                var validCapacity = s is not null && s.TotalCapacity > 0 && s.ReservedCapacity >= 0 && s.ReservedCapacity <= s.TotalCapacity;
                var item = new TourSearchV2ItemDto(t.Id.ToString(CultureInfo.InvariantCulture), t.Title,
                    thumbnails.GetValueOrDefault(t.Id), t.Destinations, t.DurationDays, checked((long)t.BasePrice), "VND",
                    ratings[t.Id].DisplayAverage,
                    validCapacity ? s!.TotalCapacity - s.ReservedCapacity : null,
                    s is null ? "noUpcomingSchedule" : !validCapacity ? "unknown" : s.ReservedCapacity < s.TotalCapacity ? "available" : "soldOut",
                    s?.Id.ToString(CultureInfo.InvariantCulture), s?.StartAtUtc.UtcDateTime, t.Operator);
                return new DevTourRow(documents[order], item, ratings[t.Id].RawAverage, order, lexical[t.Id]);
            }).Where(r => eligible.Contains(r.Document.TourId)).ToArray();
            return new DevTourSnapshot(documents, rows);
        }, cancellationToken);
    }

    // JSON parameters keep SQL parameter/expression counts bounded even for 250 tokens.
    // SQL collation remains authoritative; CHARINDEX/LEFT treat LIKE metacharacters literally.
    private async Task<Dictionary<long, int>> ReadLexicalAsync(long[] ids, string keyword, string[] words, CancellationToken token)
    {
        var idJson = JsonSerializer.Serialize(ids);
        var wordJson = JsonSerializer.Serialize(words);
        var rows = await context.Database.SqlQuery<LexicalRow>($"""
            SELECT t.tour_id AS Id, COALESCE(r.match_class, 0) AS Class
            FROM commerce.Tours AS t
            JOIN dbo.OperatorProfiles AS o ON o.user_id=t.operator_user_id
            OUTER APPLY (
                SELECT MAX(ranked.match_class) AS match_class FROM (SELECT CASE
                    WHEN t.title COLLATE Latin1_General_100_CI_AI = {keyword} COLLATE Latin1_General_100_CI_AI THEN 0
                    WHEN LEFT(t.title,LEN(w.value)) COLLATE Latin1_General_100_CI_AI = w.value COLLATE Latin1_General_100_CI_AI THEN 1
                    WHEN CHARINDEX(w.value COLLATE Latin1_General_100_CI_AI,t.title COLLATE Latin1_General_100_CI_AI)>0 THEN 2
                    WHEN EXISTS (SELECT 1 FROM commerce.TourDestinations AS td JOIN catalog.Destinations AS d ON d.destination_id=td.destination_id
                        WHERE td.tour_id=t.tour_id AND CHARINDEX(w.value COLLATE Latin1_General_100_CI_AI,d.name COLLATE Latin1_General_100_CI_AI)>0) THEN 3
                    WHEN CHARINDEX(w.value COLLATE Latin1_General_100_CI_AI,o.company_name COLLATE Latin1_General_100_CI_AI)>0 THEN 4
                    ELSE 5 END AS match_class
                FROM OPENJSON({wordJson}) WITH (value nvarchar(500) '$') AS w
                ) AS ranked
            ) AS r
            WHERE t.tour_id IN (SELECT value FROM OPENJSON({idJson}) WITH (value bigint '$'))
            """).ToArrayAsync(token);
        return rows.ToDictionary(r => r.Id, r => r.Class);
    }
    public sealed class LexicalRow { public long Id { get; set; } public int Class { get; set; } }
    private static DateTimeOffset LocalBound(DateOnly date, bool end)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        return new DateTimeOffset(Math.Clamp(local.Ticks - zone.GetUtcOffset(local).Ticks + (end ? TimeSpan.TicksPerDay : 0),
            DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);
    }
}