using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Application.Features.Admin.StatisticalReports.GetOperators;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.StatisticalReports;

public sealed class GetReportingTourOperatorsQueryHandlerTests
{
    [Fact]
    public async Task Handle_RoleWithoutAccess_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetReportingTourOperatorsQueryHandler(db, new CurrentUser(4, "Staff"));

        var result = await handler.Handle(new GetReportingTourOperatorsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(StatisticalReportErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_ReturnsOnlyApprovedOperatorsOrderedByCompanyName()
    {
        await using var db = TestDbContext.Create();
        await SeedOperatorAsync(db, "Zebra Tours", OperatorApprovalStatus.Approved);
        await SeedOperatorAsync(db, "Alpha Travel", OperatorApprovalStatus.Approved);
        await SeedOperatorAsync(db, "Pending Co", OperatorApprovalStatus.PendingApproval);
        await SeedOperatorAsync(db, "Rejected Co", OperatorApprovalStatus.Rejected);
        var handler = new GetReportingTourOperatorsQueryHandler(db, new CurrentUser(1, "Administrator"));

        var result = await handler.Handle(new GetReportingTourOperatorsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(x => x.Name).Should().Equal("Alpha Travel", "Zebra Tours");
    }

    private static async Task SeedOperatorAsync(TestDbContext db, string companyName, OperatorApprovalStatus status)
    {
        var user = new User
        {
            FullName = companyName,
            Email = $"{Guid.NewGuid():N}@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        db.OperatorProfiles.Add(new OperatorProfile
        {
            User = user,
            CompanyName = companyName,
            TaxCode = "TAX-" + Guid.NewGuid().ToString("N")[..8],
            BusinessLicenseNo = "LIC-" + Guid.NewGuid().ToString("N")[..8],
            ApprovalStatus = status,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;
}