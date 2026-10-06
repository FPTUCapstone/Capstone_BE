using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Coupons;

public sealed class CreateCouponSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Vouchers_RejectsCaseInsensitiveDuplicateCodes()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT commerce.Vouchers(
                code, discount_type, discount_value, max_discount_amount,
                min_order_amount, used_count, valid_from, valid_to, status)
            VALUES (
                'SUMMER10', 'Percentage', 10.00, 100000.00,
                0.00, 0, '2026-10-01T00:00:00', '2026-10-31T00:00:00', 'Active');
            """);

        var insertDuplicate = () => database.ExecuteNonQueryAsync("""
            INSERT commerce.Vouchers(
                code, discount_type, discount_value, max_discount_amount,
                min_order_amount, used_count, valid_from, valid_to, status)
            VALUES (
                'summer10', 'Percentage', 10.00, 100000.00,
                0.00, 0, '2026-10-01T00:00:00', '2026-10-31T00:00:00', 'Active');
            """);

        await insertDuplicate.Should().ThrowAsync<SqlException>();
    }
}