using TripMate.Application.Features.Admin.StatisticalReports.Common;

namespace TripMate.Application.Features.Admin.StatisticalReports.Exports;

public sealed record StatisticalReportFile(string FileName, string ContentType, byte[] Content);
public interface IStatisticalReportExportRenderer
{
    StatisticalReportFile Render(StatisticalReportDto report, string format);
}
