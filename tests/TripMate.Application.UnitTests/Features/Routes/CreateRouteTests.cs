using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Routes;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Routes;

public class CreateRouteTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T10:00:00Z");

    [Fact]
    public async Task ValidStaffCreation_PersistsActiveRouteAndImmutableAudit_AndRejectsRepeat()
    {
        await using var db = TestDbContext.Create();
        var (staff, from, to) = await Seed(db);
        var handler = Handler(db, staff.Id);
        var command = Command(from.Id, to.Id) with { RouteNote = "  Road works  " };
        var result = await handler.Handle(command, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.MessageCode.Should().Be(RouteErrors.Msg139);
        result.Value.Status.Should().Be(nameof(RouteStatus.Active));
        result.Value.RouteNote.Should().Be("Road works");
        var stored = await db.RouteSegments.SingleAsync();
        stored.Status.Should().Be(RouteStatus.Active);
        stored.DistanceKm.Should().Be(2.35m);
        var audit = await db.AuditLogs.SingleAsync();
        audit.ActionType.Should().Be(AuditActionTypes.RouteCreate);
        audit.AffectedEntityId.Should().Be(stored.Id);
        audit.AfterData.Should().Contain("Road works");
        db.IsExecutingSerializableTransaction.Should().BeFalse();
        var repeat = await handler.Handle(command, default);
        repeat.ErrorCode.Should().Be(RouteErrors.DuplicateSubmission);
        (await db.RouteSegments.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(UserRole.Administrator, AccountStatus.Active)]
    [InlineData(UserRole.Traveler, AccountStatus.Active)]
    [InlineData(UserRole.TourOperator, AccountStatus.Active)]
    [InlineData(UserRole.Staff, AccountStatus.Inactive)]
    [InlineData(UserRole.Staff, AccountStatus.Locked)]
    public async Task NonActiveStaffRejected(UserRole role, AccountStatus status)
    {
        await using var db = TestDbContext.Create();
        var (staff, from, to) = await Seed(db);
        staff.Role = role; staff.Status = status;
        await db.SaveChangesAsync();
        var result = await Handler(db, staff.Id).Handle(Command(from.Id, to.Id), default);
        result.ErrorCode.Should().Be(RouteErrors.Msg126);
        (await db.RouteSegments.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("missingFrom", "MSG01")]
    [InlineData("missingTo", "MSG01")]
    [InlineData("missingDistance", "MSG01")]
    [InlineData("missingDuration", "MSG01")]
    [InlineData("missingMode", "MSG01")]
    [InlineData("unknownFrom", "MSG141")]
    [InlineData("unknownTo", "MSG141")]
    [InlineData("inactiveFrom", "MSG141")]
    [InlineData("inactiveTo", "MSG141")]
    [InlineData("same", "MSG141")]
    [InlineData("zeroDistance", "MSG143")]
    [InlineData("negativeDistance", "MSG143")]
    [InlineData("zeroDuration", "MSG143")]
    [InlineData("negativeDuration", "MSG143")]
    [InlineData("mode", "Routes.InvalidInput")]
    [InlineData("numericMode", "Routes.InvalidInput")]
    [InlineData("note", "Routes.InvalidInput")]
    [InlineData("precision", "Routes.InvalidInput")]
    public async Task InvalidInputNeverWrites(string scenario, string error)
    {
        await using var db = TestDbContext.Create();
        var (staff, from, to) = await Seed(db);
        var command = Command(from.Id, to.Id);
        command = scenario switch
        {
            "missingFrom" => command with { FromPoiId = null },
            "missingTo" => command with { ToPoiId = null },
            "missingDistance" => command with { DistanceKm = null },
            "missingDuration" => command with { EstimatedDurationMinutes = null },
            "missingMode" => command with { TransportMode = null },
            "unknownFrom" => command with { FromPoiId = 999 },
            "unknownTo" => command with { ToPoiId = 999 },
            "same" => command with { ToPoiId = from.Id },
            "zeroDistance" => command with { DistanceKm = 0 },
            "negativeDistance" => command with { DistanceKm = -1 },
            "zeroDuration" => command with { EstimatedDurationMinutes = 0 },
            "negativeDuration" => command with { EstimatedDurationMinutes = -1 },
            "mode" => command with { TransportMode = "driving" },
            "numericMode" => command with { TransportMode = "3" },
            "note" => command with { RouteNote = new string('x', 2001) },
            "precision" => command with { DistanceKm = 1.234m },
            _ => command,
        };
        if (scenario == "inactiveFrom") from.TryDeactivate(Now);
        if (scenario == "inactiveTo") to.TryDeactivate(Now);
        await db.SaveChangesAsync();
        var result = await Handler(db, staff.Id).Handle(command, default);
        result.ErrorCode.Should().Be(error);
        (await db.RouteSegments.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PersistenceFailureReturnsMsg127()
    {
        await using var db = TestDbContext.Create();
        var (staff, from, to) = await Seed(db);
        db.SaveChangesFailureFactory = () => new DbUpdateException("Failure");
        var result = await Handler(db, staff.Id).Handle(Command(from.Id, to.Id), default);
        result.ErrorCode.Should().Be(RouteErrors.Msg127);
        (await db.RouteSegments.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    private static CreateRouteCommand Command(long from, long to) => new()
    {
        FromPoiId = from,
        ToPoiId = to,
        DistanceKm = 2.35m,
        EstimatedDurationMinutes = 10,
        TransportMode = nameof(TransportMode.Walking),
        IdempotencyKey = Guid.NewGuid()
    };

    private static CreateRouteCommandHandler Handler(TestDbContext db, long userId) => new(db,
        new FakeCurrentUserService { UserId = userId, Role = nameof(UserRole.Staff) },
        new FakeDateTimeProvider { UtcNow = Now });

    private static async Task<(User, PointOfInterest, PointOfInterest)> Seed(TestDbContext db)
    {
        var staff = new User { Role = UserRole.Staff, Status = AccountStatus.Active, FullName = "Staff" };
        var category = PoiCategory.Create("Attraction", null);
        db.Users.Add(staff); db.PoiCategories.Add(category); await db.SaveChangesAsync();
        var from = PointOfInterest.Create(category, "Origin", 16m, 108m, staff.Id, Now);
        var to = PointOfInterest.Create(category, "Destination", 16.1m, 108.1m, staff.Id, Now);
        db.PointsOfInterest.AddRange(from, to); await db.SaveChangesAsync();
        return (staff, from, to);
    }
}