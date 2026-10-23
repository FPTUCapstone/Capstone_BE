using ClosedXML.Excel;

using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.GetReport;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Reporting;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Reporting;

public sealed class XlsxRevenueExportRendererTests
{
    private static readonly OperatorRevenueSummaryDto Summary = new(
        GrossRevenue: 14_600_000m,
        PaymentsReceived: 14_000_000m,
        DiscountsGiven: 600_000m,
        RefundedAmount: 1_400_000m,
        NetSales: 12_600_000m,
        PlatformCommission: 1_260_000m,
        NetAmount: 11_340_000m,
        TotalBookings: 4,
        TotalParticipants: 10,
        CommissionRatePercent: 10m);

    private static readonly OperatorTourRevenueDto TourRow = new(
        TourId: 142, TourCode: "TOUR-142", TourName: "Ba Na Hills Full-Day Tour",
        BookingsCount: 2, ParticipantsCount: 6, GrossRevenue: 8_000_000m,
        RefundedAmount: 1_400_000m, Commission: 800_000m, NetAmount: 5_800_000m);

    private static RevenueExportDocument BuildDocument(RevenueExportScope scope) => new(
        Filters: new RevenueExportFilterSnapshot(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), "monthly", 142, "Ba Na Hills Full-Day Tour"),
        Summary: Summary,
        TourDetails: scope == RevenueExportScope.SummaryAndDetails
            ? [TourRow]
            : [],
        Scope: scope,
        GeneratedAtUtc: new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Render_ReportsCorrectContentTypeAndExtension()
    {
        var renderer = new XlsxRevenueExportRenderer();

        renderer.FileExtension.Should().Be("xlsx");
        renderer.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [Fact]
    public void Render_SummaryScope_WritesSummarySheetOnlyWithAllSevenFinancialFields()
    {
        var renderer = new XlsxRevenueExportRenderer();

        var bytes = renderer.Render(BuildDocument(RevenueExportScope.Summary));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        workbook.Worksheets.Select(w => w.Name).Should().ContainSingle().Which.Should().Be("Summary");

        var ws = workbook.Worksheet("Summary");
        var labels = ws.Column(1).CellsUsed().Select(c => c.GetString()).ToList();
        labels.Should().Contain([
            "Gross Revenue", "Payments Received", "Discounts Given",
            "Refunded Amount", "Net Sales", "Platform Commission", "Net Amount",
        ]);

        FindValueCell(ws, "Gross Revenue").GetValue<decimal>().Should().Be(14_600_000m);
        FindValueCell(ws, "Net Amount").GetValue<decimal>().Should().Be(11_340_000m);
    }

    [Fact]
    public void Render_SummaryAndDetailsScope_AddsDetailsSheetWithTourRows()
    {
        var renderer = new XlsxRevenueExportRenderer();

        var bytes = renderer.Render(BuildDocument(RevenueExportScope.SummaryAndDetails));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        workbook.Worksheets.Select(w => w.Name).Should().BeEquivalentTo(["Summary", "Details"]);

        var details = workbook.Worksheet("Details");
        details.Cell(1, 1).GetString().Should().Be("Tour Name");
        details.Cell(2, 1).GetString().Should().Be("Ba Na Hills Full-Day Tour");
        details.Cell(2, 4).GetValue<decimal>().Should().Be(8_000_000m);
    }

    [Fact]
    public void Render_AppliesAppliedFilterSnapshotNotLiveInput()
    {
        var renderer = new XlsxRevenueExportRenderer();

        var bytes = renderer.Render(BuildDocument(RevenueExportScope.Summary));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var ws = workbook.Worksheet("Summary");
        var periodRow = ws.Column(1).CellsUsed().First(c => c.GetString() == "Period:").Address.RowNumber;
        ws.Cell(periodRow, 2).GetString().Should().Be("01/08/2026 - 31/08/2026");
    }

    private static IXLCell FindValueCell(IXLWorksheet ws, string label)
    {
        var row = ws.Column(1).CellsUsed().First(c => c.GetString() == label).Address.RowNumber;
        return ws.Cell(row, 2);
    }
}