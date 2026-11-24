using FluentAssertions;

using TripMate.Application.Features.Admin.TourOperatorApplications.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.TourOperatorApplications;

public class GetOperatorApplicationListTests
{
    [Fact]
    public async Task Handle_ReturnsPersistedProfilesAndFiltersStatus()
    {
        using var db = TestDbContext.Create();
        var user = new User { Email = "queue@example.invalid", FullName = "Queue", Role = UserRole.TourOperator, Status = AccountStatus.PendingApproval };
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, User = user, CompanyName = "Queue operator", TaxCode = "0312345678", BusinessLicenseNo = "test", CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(CancellationToken.None);
        var handler = new GetOperatorApplicationListQueryHandler(db);
        var result = await handler.Handle(new(1, "Pending", "Queue"), CancellationToken.None);
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().ApplicationStatus.Should().Be("Pending");
        var approved = await handler.Handle(new(1, "Approved", null), CancellationToken.None);
        approved.Value.Items.Should().BeEmpty();
    }
}