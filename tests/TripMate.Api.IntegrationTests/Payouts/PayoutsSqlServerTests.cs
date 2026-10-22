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

            -- UC-46 allows only one active payout per operator. The approved record below
            -- therefore belongs to a third operator rather than @op1, which already owns a
            -- PendingReview request in this fixture.
            DECLARE @op3 BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'po3@example.com', N'PO Three', 'Active');
            SET @op3 = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@op3, N'Hue Heritage', N'TAX-UC63-3', N'LIC-UC63-3');

            DECLARE @staff BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Staff', N'payout-staff@example.com', N'Payout Staff', 'Active');
            SET @staff = SCOPE_IDENTITY();

            DECLARE @administrator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Administrator', N'payout-admin@example.com', N'Payout Admin', 'Active');
            SET @administrator = SCOPE_IDENTITY();

            INSERT payment.Payouts(operator_user_id, period_start, period_end,
                gross_revenue, commission_amount, net_amount, status, requested_at,
                reviewed_by, reviewed_at, rejection_reason, settlement_authorized_by,
                settlement_authorized_at, gateway_reference, gateway_failure_reason,
                gateway_attempt_count, last_gateway_attempt_at, payout_code)
            VALUES (@op1, '2026-08-01', '2026-08-31', 10000000, 1000000, 9000000, 'PendingReview', '2026-09-01T02:30:00',
                    NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, 'PO-1'),
                   (@op2, '2026-08-01', '2026-08-31', 200000, 10000, 190000, 'PendingReview', NULL,
                    NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, 'PO-2'),
                   (@op3, '2026-07-01', '2026-07-31', 500000, 62500, 437500, 'ApprovedForSettlement', NULL,
                    @staff, '2026-08-01T00:00:00', NULL, NULL, NULL, NULL, NULL, 0, NULL, 'PO-3'),
                   (@op2, '2026-07-01', '2026-07-31', 300000, 15000, 285000, 'Rejected', NULL,
                    @staff, '2026-08-01T00:00:00', N'Invalid settlement evidence', NULL, NULL, NULL, NULL, 0, NULL, 'PO-4'),
                   (@op1, '2026-05-01', '2026-05-31', 100000, 5000, 95000, 'Settled', '2026-06-01T00:00:00',
                    @staff, '2026-06-01T00:00:00', NULL, @administrator, '2026-06-01T01:00:00', N'gateway-5', NULL, 1, '2026-06-01T01:01:00', 'PO-5');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetPayoutsQueryHandler(db, new Staff());

        // 1. The default Staff scope is PendingReview with deterministic ordering.
        var all = await handler.Handle(new GetPayoutsQuery(), default);
        all.IsSuccess.Should().BeTrue();
        all.Value.TotalCount.Should().Be(2);
        var codes = all.Value.Items.Select(item => item.PayoutCode).ToArray();
        codes.Should().Equal("PO-2", "PO-1");
        all.Value.Items[0].PayoutId.Should().Be("2", "same period, higher operator_user_id first");
        all.Value.Items[0].Operator.CompanyName.Should().Be("Hoi An Walks");
        all.Value.Items[0].Operator.UserId.Should().Be(all.Value.Items[0].Operator.UserId, "IDs are strings");
        all.Value.TotalAmount.Should().Be(9190000m, "9,000,000 + 190,000");

        // 2. Staff history filters are limited to ApprovedForSettlement and Rejected.
        var approved = await handler.Handle(new GetPayoutsQuery(Status: "ApprovedForSettlement"), default);
        approved.Value.TotalCount.Should().Be(1);
        approved.Value.Items.Should().ContainSingle().Which.PayoutCode.Should().Be("PO-3");
        approved.Value.TotalAmount.Should().Be(437500m);

        // 3. Keyword matches the derived payout code and the operator company name.
        var byCode = await handler.Handle(new GetPayoutsQuery(Status: "ApprovedForSettlement", Keyword: "PO-3"), default);
        byCode.Value.TotalCount.Should().Be(1);
        byCode.Value.Items[0].PayoutCode.Should().Be("PO-3");
        byCode.Value.Items[0].PayoutId.Should().Be("3");

        var byName = await handler.Handle(new GetPayoutsQuery(Keyword: "da nang"), default);
        byName.Value.TotalCount.Should().Be(1, "the default scope exposes only PendingReview");
        byName.Value.Items.Select(item => item.PayoutId).Should().OnlyContain(id => id == "1");

        // 4. Period window composes with the other semantics.
        var august = await handler.Handle(new GetPayoutsQuery(PeriodFrom: new DateOnly(2026, 8, 1)), default);
        august.Value.TotalCount.Should().Be(2);
        var july = await handler.Handle(new GetPayoutsQuery(PeriodTo: new DateOnly(2026, 7, 31)), default);
        july.Value.TotalCount.Should().Be(0);

        // 5. Pagination applies after filtering with a deterministic split.
        var page = await handler.Handle(new GetPayoutsQuery(PageSize: 2, PageNumber: 2), default);
        page.Value.Items.Should().BeEmpty();
        page.Value.TotalPages.Should().Be(1);

        // 6. Periods serialize as yyyy-MM-dd calendar dates; requested_at is UTC with null preserved.
        var requested = all.Value.Items.Single(item => item.PayoutCode == "PO-1");
        requested.PeriodStart.Should().Be(new DateOnly(2026, 8, 1));
        requested.RequestedAtUtc.Should().NotBeNull();
        var unrequested = all.Value.Items.Single(item => item.PayoutCode == "PO-2");
        unrequested.RequestedAtUtc.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_ReturnsPersistedPayoutCodeAndPeriodCount_NotAnIdDerivedCode()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @op BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'code-op@example.com', N'Code Op', 'Active');
            SET @op = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@op, N'Code Op Tours', N'TAX-CODE-1', N'LIC-CODE-1');

            DECLARE @staff BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Staff', N'code-staff@example.com', N'Code Staff', 'Active');
            SET @staff = SCOPE_IDENTITY();

            INSERT payment.Payouts(operator_user_id, period_start, period_end,
                gross_revenue, commission_amount, net_amount, status, requested_at,
                gateway_attempt_count, payout_code, period_count)
            VALUES (@op, '2026-08-01', '2026-10-31', 30000000, 3000000, 27000000, 'PendingReview', '2026-10-23T02:00:00',
                    0, 'PO-202610-000042', 3);
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetPayoutsQueryHandler(db, new Staff());

        var listed = await handler.Handle(new GetPayoutsQuery(), default);
        var item = listed.Value.Items.Should().ContainSingle().Subject;
        item.PayoutCode.Should().Be("PO-202610-000042", "list must echo the code UC-46 persisted, not PO-{id}");
        item.PeriodCount.Should().Be(3, "a multi-month available-balance payout must show how many months it covers");

        var byPersistedCode = await handler.Handle(new GetPayoutsQuery(Keyword: "PO-202610"), default);
        byPersistedCode.Value.TotalCount.Should().Be(1, "keyword search matches the persisted code, not the id");
    }

    private sealed class Staff : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => "Staff";
    }
}