using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Application.Features.Admin.StatisticalReports.Exports;

namespace TripMate.Infrastructure.Reporting;

public sealed class StatisticalReportExportRenderer : IStatisticalReportExportRenderer
{
    public StatisticalReportFile Render(StatisticalReportDto report, string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "csv" => new StatisticalReportFile(FileName(report, "csv"), "text/csv; charset=utf-8", RenderCsv(report)),
            "xlsx" => new StatisticalReportFile(FileName(report, "xlsx"), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", RenderXlsx(report)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), "Only csv and xlsx exports are supported."),
        };
    }

    private static string FileName(StatisticalReportDto report, string extension) => $"tripmate-{report.ReportType.ToLowerInvariant()}-{report.Period.PeriodKey}.{extension}";

    private static byte[] RenderCsv(StatisticalReportDto report)
    {
        var builder = new StringBuilder();
        WriteCsv(builder, "TripMate Statistical Report", report.ReportType);
        WriteCsv(builder, "Closed period", $"{report.Period.StartDate:yyyy-MM-dd} - {report.Period.EndDate:yyyy-MM-dd}");
        WriteCsv(builder, "Generated at UTC", report.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        builder.AppendLine();
        WriteCsv(builder, "Metric", "Value", "Unit");
        foreach (var metric in report.Metrics) WriteCsv(builder, metric.Label, metric.Value.ToString(CultureInfo.InvariantCulture), metric.IsCurrency ? "VND" : "count");
        builder.AppendLine();
        WriteCsv(builder, "Segment", "Primary", "Secondary", "Tertiary");
        foreach (var row in report.Breakdown) WriteCsv(builder, row.Label, row.PrimaryValue.ToString(CultureInfo.InvariantCulture), row.SecondaryValue.ToString(CultureInfo.InvariantCulture), row.TertiaryValue.ToString(CultureInfo.InvariantCulture));
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(builder.ToString());
    }

    private static void WriteCsv(StringBuilder builder, params string[] values)
    {
        builder.AppendLine(string.Join(',', values.Select(Escape)));
    }

    private static string Escape(string value)
    {
        string trimmed = value.TrimStart();
        string sanitized = trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
            ? "'" + value : value;
        return $"\"{sanitized.Replace("\"", "\"\"")}\"";
    }

    private static byte[] RenderXlsx(StatisticalReportDto report)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = "TripMate Statistical Report";
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(2, 1).Value = "Report type"; summary.Cell(2, 2).Value = report.ReportType;
        summary.Cell(3, 1).Value = "Closed period"; summary.Cell(3, 2).Value = $"{report.Period.StartDate:dd/MM/yyyy} - {report.Period.EndDate:dd/MM/yyyy}";
        summary.Cell(5, 1).Value = "Metric"; summary.Cell(5, 2).Value = "Value"; summary.Cell(5, 3).Value = "Unit";
        summary.Range(5, 1, 5, 3).Style.Font.Bold = true;
        var row = 6;
        foreach (var metric in report.Metrics) { summary.Cell(row, 1).Value = metric.Label; summary.Cell(row, 2).Value = metric.Value; summary.Cell(row, 3).Value = metric.IsCurrency ? "VND" : "count"; row++; }
        summary.Columns().AdjustToContents();
        var detail = workbook.Worksheets.Add("Breakdown");
        string[] headers = ["Segment", "Primary", "Secondary", "Tertiary"];
        for (var i = 0; i < headers.Length; i++) { detail.Cell(1, i + 1).Value = headers[i]; detail.Cell(1, i + 1).Style.Font.Bold = true; }
        row = 2;
        foreach (var item in report.Breakdown) { detail.Cell(row, 1).Value = item.Label; detail.Cell(row, 2).Value = item.PrimaryValue; detail.Cell(row, 3).Value = item.SecondaryValue; detail.Cell(row, 4).Value = item.TertiaryValue; row++; }
        detail.Columns().AdjustToContents();
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return stream.ToArray();
    }
}
