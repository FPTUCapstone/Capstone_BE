using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Common;

public sealed class TourPackageValidatorTests
{
    private static readonly FakeDateTimeProvider Clock = new();
    private static readonly CreateTourPackageCommandValidator CreateValidator = new(Clock);
    private static readonly UpdateTourDraftCommandValidator UpdateValidator = new(Clock);

    [Fact]
    public void Create_AcceptsAWellFormedCommand()
    {
        CreateValidator.Validate(Create()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("1000000.5", false)]
    [InlineData("1000000.01", false)]
    [InlineData("1000000.00", true)]
    [InlineData("1000000", true)]
    public void Create_RequiresAWholeVndBasePrice(string price, bool expectedValid)
    {
        var command = Create() with
        {
            BasePrice = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture),
        };

        CreateValidator.Validate(command).IsValid.Should().Be(expectedValid);
        UpdateValidator.Validate(Update(command)).IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void Create_RequiresWholeVndDeparturePrices()
    {
        var schedule = Create().Schedules[0] with { AdultPrice = 1000000.5m };
        var command = Create() with { Schedules = [schedule] };

        CreateValidator.Validate(command).IsValid.Should().BeFalse();
        UpdateValidator.Validate(Update(command)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_RequiresBetweenOneAndTenUniqueDestinations()
    {
        Action<long[], bool> check = (ids, valid) =>
        {
            var command = Create() with { DestinationIds = ids };
            CreateValidator.Validate(command).IsValid.Should().Be(valid, string.Join(",", ids));
            UpdateValidator.Validate(Update(command)).IsValid.Should().Be(valid, string.Join(",", ids));
        };

        check([], false);
        check([5, 5], false);
        check([0], false);
        check([-1], false);
        check(Enumerable.Range(1, Tour.MaxDestinations + 1).Select(value => (long)value).ToArray(), false);
        check(Enumerable.Range(1, Tour.MaxDestinations).Select(value => (long)value).ToArray(), true);
        check([2, 1], true);
    }

    [Fact]
    public void Create_RequiresAPolicyTemplate()
    {
        var command = Create() with { CancellationPolicyTemplateId = 0 };

        CreateValidator.Validate(command).IsValid.Should().BeFalse();
        UpdateValidator.Validate(Update(command)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_LimitsTheTrimmedLengthOfTerms()
    {
        var atLimit = " " + new string('x', Tour.TermsMaxLength) + " ";
        var overLimit = new string('x', Tour.TermsMaxLength + 1);

        CreateValidator.Validate(Create() with { Inclusions = atLimit, Exclusions = atLimit }).IsValid
            .Should().BeTrue();
        CreateValidator.Validate(Create() with { Inclusions = overLimit }).IsValid.Should().BeFalse();
        UpdateValidator.Validate(Update(Create() with { Exclusions = overLimit })).IsValid.Should().BeFalse();
    }

    private static CreateTourPackageCommand Create()
    {
        var now = Clock.UtcNow;
        return new CreateTourPackageCommand(
            "Da Nang day tour", "A full day visiting Da Nang landmarks.", null, 1_250_000m, 1,
            [1], 1, "Meals", "Tips",
            [new CreateTourItineraryItemRequest(1, 1, 88, new TimeOnly(8, 0), 90, null)],
            [new CreateTourScheduleRequest(now.AddDays(2), now.AddDays(2).AddHours(8), "City center", 20)],
            Guid.NewGuid());
    }

    private static UpdateTourDraftCommand Update(CreateTourPackageCommand source) => new(
        1, source.Title, source.Description, source.CategoryId, source.BasePrice, source.DurationDays,
        source.DestinationIds, source.CancellationPolicyTemplateId, source.Inclusions, source.Exclusions,
        source.ItineraryItems, source.Schedules);

    [Fact]
    public void BuildDestinationSummary_JoinsNamesInTheOrderChosen()
    {
        OperatorTourValidation.BuildDestinationSummary([" Hoi An ", "Da Nang", ""]).Should().Be("Hoi An, Da Nang");
    }

    [Fact]
    public void BuildDestinationSummary_TruncatesToTheColumnLimit()
    {
        var names = Enumerable.Range(0, 60).Select(index => $"Destination {index}");

        var summary = OperatorTourValidation.BuildDestinationSummary(names);

        summary.Length.Should().Be(Tour.DestinationSummaryMaxLength);
        summary.Should().EndWith("\u2026");
    }
}