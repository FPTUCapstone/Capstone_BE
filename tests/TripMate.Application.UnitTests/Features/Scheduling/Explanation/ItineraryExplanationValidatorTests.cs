using FluentAssertions;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationValidatorTests
{
    private static readonly DateTimeOffset StartAtUtc =
        new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryValidate_ValidResult_AcceptsWholeResultAndTrimsText()
    {
        ItineraryExplanationInput input = CreateInput();
        var result = new ItineraryExplanationResult(
        [
            new(1, 42, "  Phù hợp với sở thích văn hóa.  "),
            new(2, null, "Thời gian nghỉ ngơi hợp lý."),
        ]);

        bool isValid = ItineraryExplanationValidator.TryValidate(
            input,
            result,
            out IReadOnlyDictionary<int, string> explanations);

        isValid.Should().BeTrue();
        explanations.Should().BeEquivalentTo(new Dictionary<int, string>
        {
            [1] = "Phù hợp với sở thích văn hóa.",
            [2] = "Thời gian nghỉ ngơi hợp lý.",
        });
    }

    [Fact]
    public void TryValidate_NullResult_RejectsWholeResult() =>
        AssertInvalid(null);

    [Fact]
    public void TryValidate_NullItems_RejectsWholeResult() =>
        AssertInvalid(new ItineraryExplanationResult(null!));

    [Fact]
    public void TryValidate_WrongCount_RejectsWholeResult() =>
        AssertInvalid(new ItineraryExplanationResult([]));

    [Fact]
    public void TryValidate_MissingItem_RejectsWholeResult() =>
        AssertInvalid(new ItineraryExplanationResult(
        [
            new(1, 42, "Phù hợp với sở thích văn hóa."),
        ]));

    [Fact]
    public void TryValidate_UnknownSequence_RejectsWholeResult() =>
        AssertInvalid(new ItineraryExplanationResult(
        [
            new(1, 42, "Phù hợp với sở thích văn hóa."),
            new(99, null, "Thời gian nghỉ ngơi hợp lý."),
        ]));

    [Fact]
    public void TryValidate_DuplicateSequence_RejectsWholeResult() =>
        AssertInvalid(new ItineraryExplanationResult(
        [
            new(1, 42, "Phù hợp với sở thích văn hóa."),
            new(1, 42, "Nội dung trùng lặp."),
        ]));

    [Fact]
    public void TryValidate_PoiIdMismatch_RejectsWholeResult() =>
        AssertInvalid(ValidResult(firstPoiId: 43));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryValidate_NullAndNonNullPoiIdMismatch_RejectsWholeResult(
        bool nullUsedForPoiBackedItem)
    {
        ItineraryExplanationResult result = nullUsedForPoiBackedItem
            ? ValidResult(firstPoiId: null)
            : ValidResult(secondPoiId: 42);

        AssertInvalid(result);
    }

    [Fact]
    public void TryValidate_NullPoiIdForPoiLessRest_AcceptsWholeResult()
    {
        bool isValid = ItineraryExplanationValidator.TryValidate(
            CreateInput(),
            ValidResult(),
            out IReadOnlyDictionary<int, string> explanations);

        isValid.Should().BeTrue();
        explanations.Should().ContainKey(2);
    }

    [Fact]
    public void TryValidate_NullExplanation_RejectsWholeResult() =>
        AssertInvalid(ValidResult(firstExplanation: null!));

    [Fact]
    public void TryValidate_EmptyExplanation_RejectsWholeResult() =>
        AssertInvalid(ValidResult(firstExplanation: string.Empty));

    [Fact]
    public void TryValidate_WhitespaceExplanation_RejectsWholeResult() =>
        AssertInvalid(ValidResult(firstExplanation: "   "));

    [Fact]
    public void TryValidate_Exactly500Characters_AcceptsWholeResult()
    {
        string explanation = new('x', ItineraryItem.FriendlyExplanationMaxLength);

        bool isValid = ItineraryExplanationValidator.TryValidate(
            CreateInput(),
            ValidResult(firstExplanation: explanation),
            out IReadOnlyDictionary<int, string> explanations);

        isValid.Should().BeTrue();
        explanations[1].Should().HaveLength(ItineraryItem.FriendlyExplanationMaxLength);
    }

    [Fact]
    public void TryValidate_501Characters_RejectsWholeResult() =>
        AssertInvalid(ValidResult(firstExplanation: new string(
            'x',
            ItineraryItem.FriendlyExplanationMaxLength + 1)));

    private static void AssertInvalid(ItineraryExplanationResult? result)
    {
        bool isValid = ItineraryExplanationValidator.TryValidate(
            CreateInput(),
            result,
            out IReadOnlyDictionary<int, string> explanations);

        isValid.Should().BeFalse();
        explanations.Should().BeEmpty();
    }

    private static ItineraryExplanationResult ValidResult(
        long? firstPoiId = 42,
        long? secondPoiId = null,
        string firstExplanation = "Phù hợp với sở thích văn hóa.") => new(
    [
        new(1, firstPoiId, firstExplanation),
        new(2, secondPoiId, "Thời gian nghỉ ngơi hợp lý."),
    ]);

    private static ItineraryExplanationInput CreateInput() => new(
    [
        new(
            1,
            42,
            "Bảo tàng Đà Lạt",
            "Văn hóa",
            ["Lịch sử"],
            ItineraryItemKind.Visit,
            false,
            StartAtUtc,
            StartAtUtc.AddMinutes(60),
            60,
            50_000m,
            15,
            "Suggested nearby location"),
        new(
            2,
            null,
            null,
            null,
            [],
            ItineraryItemKind.Rest,
            false,
            StartAtUtc.AddMinutes(75),
            StartAtUtc.AddMinutes(105),
            30,
            null,
            null,
            "Free rest time"),
    ],
        new(["culture"], StartAtUtc, "Asia/Ho_Chi_Minh", 105));
}