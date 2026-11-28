using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Admin.Users;

public sealed class UserAdministrationNotificationOutboxSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimDueAsync_IncludesAccountModerationAndProviderCommissionEmails()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        const string providerEmail = "outbox-provider@example.test";
        const string staffEmail = "outbox-staff@example.test";
        const string unrelatedType = "OtherEmail";

        await using (var seed = database.CreateDbContext())
        {
            var providerAccount = NewUser(providerEmail, UserRole.ServiceProvider);
            var staff = NewUser(staffEmail, UserRole.Staff);
            seed.Users.AddRange(providerAccount, staff);
            var provider = ServiceProvider.Create("Outbox provider", CommercialService.CategoryRestaurant,
                ServiceProvider.StatusActive);
            provider.AssignAccount(providerAccount);
            seed.ServiceProviders.Add(provider);
            await seed.SaveChangesAsync();

            seed.ServiceCommissionStatements.Add(ServiceCommissionStatement.Create(
                "STMT-OUTBOX-1", provider.Id, "2026-09", 1, 100_000m, 10_000m,
                new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8)));
            foreach (var type in new[]
            {
                UserAdministrationNotificationTypes.AccountLocked,
                UserAdministrationNotificationTypes.AccountUnlocked,
                unrelatedType,
            })
                seed.Notifications.Add(new Notification
                {
                    UserId = staff.Id,
                    Channel = NotificationChannel.Email,
                    Type = type,
                    Title = type,
                    RelatedEntityType = AuditEntityTypes.User,
                    RelatedEntityId = staff.Id,
                    CreatedAtUtc = now,
                });
            await seed.SaveChangesAsync();

            var service = new CommissionStatementService(seed);
            var firstOverdue = now.AddDays(-14);
            var overdue = await service.ProcessOverdueStatementsAsync(firstOverdue);
            overdue.MarkedOverdueCount.Should().Be(1);
            overdue.AccountsLockedCount.Should().Be(0);
            (await service.ProcessOverdueStatementsAsync(firstOverdue)).MarkedOverdueCount.Should().Be(0);
            (await service.ProcessOverdueStatementsAsync(now)).AccountsLockedCount.Should().Be(1);
        }

        await using var verification = database.CreateDbContext();
        var outbox = new SqlServerUserAdministrationNotificationOutboxStore(verification);
        var claims = await outbox.ClaimDueAsync(now.AddMinutes(1), TimeSpan.FromMinutes(2), 20, CancellationToken.None);
        claims.Should().Contain(claim => claim.Subject == UserAdministrationNotificationTypes.AccountLocked);
        claims.Should().Contain(claim => claim.Subject == UserAdministrationNotificationTypes.AccountUnlocked);
        claims.Should().NotContain(claim => claim.Subject == unrelatedType);
        claims.Should().ContainSingle(claim => claim.DestinationEmail == providerEmail
            && claim.Subject == "Service Provider account locked" && claim.Body.Contains("STMT-OUTBOX-1"));
        claims.Should().ContainSingle(claim => claim.DestinationEmail == staffEmail
            && claim.Subject == "Service Provider account locked" && claim.Body.Contains("STMT-OUTBOX-1"));
        claims.Should().ContainSingle(claim => claim.DestinationEmail == providerEmail
            && claim.Subject == "Commission statement overdue" && claim.Body.Contains("STMT-OUTBOX-1"));
        claims.Should().NotContain(claim => claim.DestinationEmail == staffEmail
            && claim.Subject == "Commission statement overdue");

        User NewUser(string email, UserRole role) => new()
        {
            FullName = email,
            Email = email,
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }
}
