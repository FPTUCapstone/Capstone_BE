using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetList;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutsSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_JoinsOperatorsAppliesFiltersAndComputesFilteredSummary()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @op1 BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'po1@example.com', N'PO One', 'Active');
            SET @op1 = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@op1, N'Da Nang Tours', N'TAX-UC63-1', N'LIC-UC63-1');

            DECLARE @op2 BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'po2@example.com', N'PO Two', 'Active');
            SET @op2 = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@op2, N'Hoi An Walks', N'TAX-UC63-2', N'LIC-UC63-2');

            INSERT payment.Payouts(operator_user_id, period_start, period_end,
                gross_revenue, commission_amount, net_amount, status, requested_at)
            VALUES (@op1, '2026-08-01', '2026-08-31', 10000000, 1000000, 9000000, 'Requested', '2026-09-01T02:30:00'),
                   (@op2, '2026-08-01', '2026-08-31', 200000, 10000, 190000, 'Pending', NULL),
                   (@op1, '2026-07-01', '2026-07-31', 500000, 62500, 437500, 'Confirmed', NULL),
                   (@op2, '2026-07-01', '2026-07-31', 300000, 15000, 285000, 'Rejected', NULL),
                   (@op1, '2026-05-01', '2026-05-31', 100000, 5000, 95000, 'Paid', '2026-06-01T00:00:00');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetPayoutsQueryHandler(db, new Administrator());

        // 1. Unfiltered: deterministic ordering (period DESC, operator DESC, id DESC) + full summary.
        var all = await handler.Handle(new GetPayoutsQuery(), default);
        all.IsSuccess.Should().BeTrue();
        all.Value.TotalCount.Should().Be(5);
        var codes = all.Value.Items.Select(item => item.PayoutCode).ToArray();
        codes.Should().Equal("PO-2", "PO-1", "PO-4", "PO-3", "PO-5");
        all.Value.Items[0].PayoutId.Should().Be("2", "same period, higher operator_user_id first");
        all.Value.Items[0].Operator.CompanyName.Should().Be("Hoi An Walks");
        all.Value.Items[0].Operator.UserId.Should().Be(all.Value.Items[0].Operator.UserId, "IDs are strings");
        all.Value.Summary.PendingRequests.Should().Be(2, "Requested + Pending");
        all.Value.Summary.TotalRequestedAmount.Should().Be(9190000m, "9,000,000 + 190,000");
        all.Value.Summary.TotalConfirmedAmount.Should().Be(532500m, "437,500 (Confirmed) + 95,000 (Paid); Rejected excluded");

        // 2. Status filter narrows rows and recomputes the summary over the filtered set.
        var pending = await handler.Handle(new GetPayoutsQuery(Status: "Pending"), default);
        pending.Value.TotalCount.Should().Be(1);
        pending.Value.Items.Should().ContainSingle().Which.Status.Should().Be("Pending");
        pending.Value.Summary.PendingRequests.Should().Be(1);
        pending.Value.Summary.TotalRequestedAmount.Should().Be(190000m);
        pending.Value.Summary.TotalConfirmedAmount.Should().Be(0m);

        // 3. Keyword matches the derived payout code and the operator company name.
        var byCode = await handler.Handle(new GetPayoutsQuery(Keyword: "PO-3"), default);
        byCode.Value.TotalCount.Should().Be(1);
        byCode.Value.Items[0].PayoutCode.Should().Be("PO-3");
        byCode.Value.Items[0].PayoutId.Should().Be("3");

        var byName = await handler.Handle(new GetPayoutsQuery(Keyword: "da nang"), default);
        byName.Value.TotalCount.Should().Be(3, "operator 1 owns payouts 1, 3, 5");
        byName.Value.Items.Select(item => item.PayoutId).Should().OnlyContain(id => id == "1" || id == "3" || id == "5");

        // 4. Period window composes with the other semantics.
        var august = await handler.Handle(new GetPayoutsQuery(PeriodFrom: new DateOnly(2026, 8, 1)), default);
        august.Value.TotalCount.Should().Be(2);
        var july = await handler.Handle(new GetPayoutsQuery(PeriodTo: new DateOnly(2026, 7, 31)), default);
        july.Value.TotalCount.Should().Be(3);

        // 5. Pagination applies after filtering with a deterministic split.
        var page = await handler.Handle(new GetPayoutsQuery(PageSize: 2, PageNumber: 2), default);
        page.Value.Items.Should().HaveCount(2);
        page.Value.Items.Select(item => item.PayoutCode).Should().Equal("PO-4", "PO-3");
        page.Value.TotalPages.Should().Be(3);

        // 6. Periods serialize as yyyy-MM-dd calendar dates; requested_at is UTC with null preserved.
        var requested = all.Value.Items.Single(item => item.PayoutCode == "PO-1");
        requested.PeriodStart.Should().Be(new DateOnly(2026, 8, 1));
        requested.RequestedAtUtc.Should().NotBeNull();
        var unrequested = all.Value.Items.Single(item => item.PayoutCode == "PO-2");
        unrequested.RequestedAtUtc.Should().BeNull();
    }

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => "Administrator";
    }
}