using System.Data.Common;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.SupportChat;

public sealed class SendSupportChatMessageHandler(
    ICurrentUserService currentUser, ISender sender,
    ISupportChatKnowledge knowledge, ISupportChatModel model,
    ISupportChatPlanner planner, ISupportChatLiveDataReader liveData)
    : IRequestHandler<SendSupportChatMessage, Result<SupportChatResponse>>
{
    private const int MaxRetrievalQueryLength = 4000;
    private const int MaxRetrievalHistoryTurns = 3;

    private static readonly JsonSerializerOptions ResourceJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task<Result<SupportChatResponse>> Handle(SendSupportChatMessage request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || userId <= 0)
        {
            return Result.Failure<SupportChatResponse>(SupportChatErrors.Unauthorized, "Please sign in to use support chat.");
        }
        if (currentUser.Role is not (nameof(UserRole.Traveler) or nameof(UserRole.TourOperator)))
        {
            return Result.Failure<SupportChatResponse>(SupportChatErrors.Forbidden,
                "A Traveler or Tour Operator account is required to use support chat.");
        }

        // Keep direct callers safe as well as callers passing through the validation pipeline.
        if (!(await new SendSupportChatMessageValidator().ValidateAsync(request, cancellationToken)).IsValid)
        {
            return Result.Failure<SupportChatResponse>(SupportChatErrors.InvalidRequest, "The chat request is invalid.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            string? resourceData = null;
            IReadOnlyList<ChatAction> actions = [];
            if (request.Context is { } context)
            {
                var id = long.Parse(context.Id, CultureInfo.InvariantCulture);
                if (context.Kind == "refund")
                {
                    var refund = await sender.Send(new GetBookingRefundProgressQuery(id), token);
                    if (refund.IsFailure)
                        return Result.Failure<SupportChatResponse>(refund.ErrorCode!, refund.ErrorMessage!);
                    resourceData = JsonSerializer.Serialize(new
                    {
                        kind = context.Kind,
                        Currency = PaymentTransaction.DefaultCurrency,
                        refund.Value.BookingCode,
                        refund.Value.RefundAmount,
                        refund.Value.Status,
                        refund.Value.RequestedAtUtc,
                        refund.Value.ProcessedAtUtc,
                    }, ResourceJsonOptions);
                }
                else if (context.Kind == "itinerary")
                {
                    if (currentUser.Role != nameof(UserRole.Traveler))
                        return Result.Failure<SupportChatResponse>(SupportChatErrors.Forbidden,
                            "A Traveler account is required to view an itinerary.");
                    // Itinerary access includes owner/group membership checks in the existing query.
                    var itinerary = await sender.Send(new GetItineraryDetailQuery(id, userId), token);
                    if (itinerary.IsFailure)
                        return Result.Failure<SupportChatResponse>(itinerary.ErrorCode!, itinerary.ErrorMessage!);
                    resourceData = JsonSerializer.Serialize(new
                    {
                        kind = context.Kind,
                        Currency = PaymentTransaction.DefaultCurrency,
                        itinerary.Value.Status,
                        itinerary.Value.TotalDurationMinutes,
                        itinerary.Value.TotalEstimatedCost,
                        Items = itinerary.Value.Items.Take(50).Select(item => new
                        {
                            item.SequenceNo,
                            item.PoiName,
                            item.PlannedArrival,
                            item.PlannedDeparture,
                            item.IsUnavailable,
                        }),
                    }, ResourceJsonOptions);
                }
                else
                {
                    var tour = await liveData.ReadAsync(new ChatQueryPlan(ChatIntents.TourDetail, ResourceId: context.Id),
                        userId, currentUser.Role!, context, token);
                    if (tour.IsFailure)
                        return Result.Failure<SupportChatResponse>(tour.ErrorCode!, tour.ErrorMessage!);
                    resourceData = tour.Value.Facts;
                    actions = tour.Value.Actions;
                }
            }

            token.ThrowIfCancellationRequested();
            if (SupportChatSocialReplies.GetReply(request.Message) is { } socialReply)
                return Result.Success(new SupportChatResponse(socialReply, [], request.Context));

            var plan = await planner.PlanAsync(request.Message.Trim(), request.History ?? [], request.Context, token);
            if (!new ChatQueryPlanValidator().Validate(plan).IsValid) return Unavailable();
            if (plan.Intent != ChatIntents.General)
            {
                var live = await liveData.ReadAsync(plan, userId, currentUser.Role!, request.Context, token);
                if (live.IsFailure)
                    return Result.Failure<SupportChatResponse>(live.ErrorCode!, live.ErrorMessage!);
                resourceData = live.Value.Facts;
                actions = live.Value.Actions;
                // Known page navigation needs no document retrieval or generated answer.
                if (ChatIntents.Navigation.Contains(plan.Intent))
                {
                    if (actions.Count != 1) return Unavailable();
                    return Result.Success(new SupportChatResponse(
                        $"Bạn có thể mở trang {actions[0].Label} bằng nút bên dưới.", [], request.Context, actions));
                }
            }

            if (plan.Intent == ChatIntents.General)
                actions = [];

            // History supplies retrieval context. Retrieved passages remain the factual evidence.
            var retrievalQuery = string.Join("\n", new[] { request.Message.Trim() }
                .Concat((request.History ?? []).Where(message => message.Role == "user")
                    .Reverse().Take(MaxRetrievalHistoryTurns).Select(message => message.Content.Trim())));
            var retrievalQueryLength = Math.Min(retrievalQuery.Length, MaxRetrievalQueryLength);
            if (retrievalQueryLength < retrievalQuery.Length &&
                char.IsSurrogatePair(retrievalQuery, retrievalQueryLength - 1))
            {
                retrievalQueryLength--;
            }
            retrievalQuery = retrievalQuery[..retrievalQueryLength];
            // Live query responses are grounded in authorized current records. Embeddings are
            // required for documented policies and guidance, not for reading these records.
            IReadOnlyList<KnowledgePassage> passages = plan.Intent == ChatIntents.General
                ? await knowledge.SearchAsync(retrievalQuery, token) : [];
            if (passages.Count == 0 && resourceData is null)
            {
                return NoInformation(request.Context);
            }

            var answer = await model.AnswerAsync(new ChatGrounding(request.Message.Trim(),
                request.History ?? [], passages, resourceData, currentUser.Role), token);
            if (answer is null || string.IsNullOrWhiteSpace(answer.Answer) || answer.Answer.Length > 6000 ||
                answer.SourceIds is null ||
                answer.SourceIds.Any(id => passages.All(p => p.Source.Id != id)))
            {
                return Unavailable();
            }
            if (resourceData is null && answer.SourceIds.Count == 0)
                return NoInformation(request.Context);

            var sources = passages.Where(p => answer.SourceIds.Contains(p.Source.Id))
                .Select(p => p.Source.Id.StartsWith("srs-", StringComparison.Ordinal)
                    ? p.Source with { Title = p.Source.Title.Split('·')[0].Trim().Replace("SRS ", "", StringComparison.Ordinal) }
                    : p.Source).DistinctBy(source => source.Id).ToArray();
            return Result.Success(new SupportChatResponse(answer.Answer.Trim(), sources, request.Context, actions));
        }
        catch (SupportChatUnavailableException)
        {
            return Unavailable();
        }
        catch (DbException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    private static Result<SupportChatResponse> Unavailable() => Result.Failure<SupportChatResponse>(
        SupportChatErrors.Unavailable, "Support chat is temporarily unavailable. Please try again later.");

    private static Result<SupportChatResponse> NoInformation(ChatResourceContext? context) =>
        Result.Success(new SupportChatResponse(
            "Mình chưa tìm thấy thông tin phù hợp trong tài liệu TripMate. Bạn có thể mô tả rõ chức năng cần hỗ trợ hơn không?",
            [], context));
}