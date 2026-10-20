using ClosedXML.Excel;

using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Reporting;

/// <summary>
/// UC-45 XLSX renderer. CSV and PDF are out of scope for now (decision: no external accounting
/// system to feed, and PDF is not an accounting-friendly format) — only this renderer is
/// registered behind <see cref="IRevenueExportRenderer"/>.
/// </summary>
public sealed class XlsxRevenueExportRenderer : IRevenueExportRenderer
{
    private const string DateFormat = "dd/MM/yyyy";
    private const string VndFormat = "#,##0\" ₫\"";

    public string ContentType =>
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string FileExtension => "xlsx";

    public byte[] Render(RevenueExportDocument document)
    {
        using var workbook = new XLWorkbook();
        WriteSummarySheet(workbook, document);

        if (document.Scope == RevenueExportScope.SummaryAndDetails)
        {
            WriteDetailsSheet(workbook, document);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteSummarySheet(XLWorkbook workbook, RevenueExportDocument document)
    {
        var ws = workbook.Worksheets.Add("Summary");

        ws.Cell(1, 1).Value = "TripMate Revenue Report";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = "Generated at (Asia/Ho_Chi_Minh):";
        ws.Cell(2, 2).Value = ConvertToVietnamTime(document.GeneratedAtUtc);
        ws.Cell(2, 2).Style.DateFormat.Format = $"{DateFormat} HH:mm";

        ws.Cell(3, 1).Value = "Period:";
        ws.Cell(3, 2).Value = $"{document.Filters.StartDate:dd/MM/yyyy} - {document.Filters.EndDate:dd/MM/yyyy}";
        ws.Cell(4, 1).Value = "Granularity:";
        ws.Cell(4, 2).Value = document.Filters.Granularity;
        ws.Cell(5, 1).Value = "Tour:";
        ws.Cell(5, 2).Value = document.Filters.TourName ?? "All tours";
        ws.Cell(6, 1).Value = "Currency:";
        ws.Cell(6, 2).Value = "VND";

        int row = 8;
        WriteSummaryRow(ws, ref row, "Gross Revenue", document.Summary.GrossRevenue);
        WriteSummaryRow(ws, ref row, "Payments Received", document.Summary.PaymentsReceived);
        WriteSummaryRow(ws, ref row, "Discounts Given", document.Summary.DiscountsGiven);
        WriteSummaryRow(ws, ref row, "Refunded Amount", document.Summary.RefundedAmount);
        WriteSummaryRow(ws, ref row, "Net Sales", document.Summary.NetSales);
        WriteSummaryRow(ws, ref row, "Platform Commission", document.Summary.PlatformCommission);
        WriteSummaryRow(ws, ref row, "Net Amount", document.Summary.NetAmount);
        ws.Cell(row, 1).Value = "Total Bookings";
        ws.Cell(row, 2).Value = document.Summary.TotalBookings;
        row++;
        ws.Cell(row, 1).Value = "Total Participants";
        ws.Cell(row, 2).Value = document.Summary.TotalParticipants;

        ws.Column(1).Width = 24;
        ws.Column(2).Width = 22;
    }

    private static void WriteSummaryRow(IXLWorksheet ws, ref int row, string label, decimal value)
    {
        ws.Cell(row, 1).Value = label;
        ws.Cell(row, 2).Value = value;
        ws.Cell(row, 2).Style.NumberFormat.Format = VndFormat;
        row++;
    }

    private static void WriteDetailsSheet(XLWorkbook workbook, RevenueExportDocument document)
    {
        var ws = workbook.Worksheets.Add("Details");

        string[] headers =
        [
            "Tour Name", "Bookings", "Participants", "Gross Revenue",
            "Refunded Amount", "Commission", "Net Amount",
        ];
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var tour in document.TourDetails)
        {
            ws.Cell(row, 1).Value = tour.TourName;
            ws.Cell(row, 2).Value = tour.BookingsCount;
            ws.Cell(row, 3).Value = tour.ParticipantsCount;
            ws.Cell(row, 4).Value = tour.GrossRevenue;
            ws.Cell(row, 4).Style.NumberFormat.Format = VndFormat;
            ws.Cell(row, 5).Value = tour.RefundedAmount;
            ws.Cell(row, 5).Style.NumberFormat.Format = VndFormat;
            ws.Cell(row, 6).Value = tour.Commission;
            ws.Cell(row, 6).Style.NumberFormat.Format = VndFormat;
            ws.Cell(row, 7).Value = tour.NetAmount;
            ws.Cell(row, 7).Style.NumberFormat.Format = VndFormat;
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    private static DateTime ConvertToVietnamTime(DateTimeOffset utc)
    {
        var zone = FindVietnamTimeZone();
        return TimeZoneInfo.ConvertTime(utc, zone).DateTime;
    }

    private static TimeZoneInfo FindVietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}
