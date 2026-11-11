using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Application.Features.Admin.StatisticalReports.Exports;
using TripMate.Application.Features.Admin.StatisticalReports.GetOperators;
using TripMate.Application.Features.Admin.StatisticalReports.GetPeriods;
using TripMate.Application.Features.Admin.StatisticalReports.GetReport;
using TripMate.Domain.Entities;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "Administrator")]
[Route("api/v1/admin/statistical-reports")]
public sealed class AdminStatisticalReportsController(
    ISender sender,
    IStatisticalReportExportRenderer exporter,
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock) : ApiControllerBase(sender)
{
    [HttpGet("periods")]
    [ProducesResponseType(typeof(IReadOnlyList<StatisticalPeriodDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPeriods(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetClosedStatisticalPeriodsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("operators")]
    [ProducesResponseType(typeof(IReadOnlyList<StatisticalReportOperatorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOperators(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetReportingTourOperatorsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(StatisticalReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(
        [FromQuery] string reportType,
        [FromQuery] string periodKey,
        [FromQuery] string bookingType = "ALL",
        [FromQuery] long? operatorUserId = null,
        [FromQuery] long? destinationId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetStatisticalReportQuery(reportType, periodKey, bookingType, operatorUserId, destinationId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string reportType,
        [FromQuery] string periodKey,
        [FromQuery] string format,
        [FromQuery] string bookingType = "ALL",
        [FromQuery] long? operatorUserId = null,
        [FromQuery] long? destinationId = null,
        [FromQuery] Guid? exportOperationId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetStatisticalReportQuery(reportType, periodKey, bookingType, operatorUserId, destinationId), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        if (exportOperationId is not Guid operationId || operationId == Guid.Empty || currentUser.UserId is not long userId)
            return BadRequest(new ProblemDetails { Title = "A valid exportOperationId is required." });
        string normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "xlsx"))
            return BadRequest(new ProblemDetails { Title = "Only csv and xlsx exports are supported." });

        string criteriaKey = CriteriaKey(reportType, periodKey, bookingType, operatorUserId, destinationId);
        var existing = await db.StatisticalReportExportOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
        if (existing is not null && (existing.CriteriaKey != criteriaKey || !string.Equals(existing.Format, normalizedFormat, StringComparison.Ordinal)))
            return Conflict(new ProblemDetails { Title = "The export operation id was already used with different criteria." });
        long exportOperationRowId;
        if (existing is null)
        {
            var operation = StatisticalReportExportOperation.Create(operationId, criteriaKey, normalizedFormat, userId, clock.UtcNow);
            db.StatisticalReportExportOperations.Add(operation);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                exportOperationRowId = operation.Id;
            }
            catch (DbUpdateException)
            {
                // The unique operation id is the idempotency boundary. Verify the competing
                // request used the same immutable criteria before returning its export.
                db.ClearTrackedEntities();
                existing = await db.StatisticalReportExportOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
                if (existing is null || existing.CriteriaKey != criteriaKey ||
                    !string.Equals(existing.Format, normalizedFormat, StringComparison.Ordinal))
                {
                    return Conflict(new ProblemDetails { Title = "The export operation id was already used with different criteria." });
                }
                exportOperationRowId = existing.Id;
            }
        }
        else
        {
            exportOperationRowId = existing.Id;
        }

        var file = exporter.Render(result.Value, normalizedFormat);

        // BR-130: one audit row per successful export response (fresh or idempotent replay),
        // written before the file is returned so a failure here withholds the file (fail-closed),
        // matching UC-59's precedent for privacy/audit-sensitive admin exports. Only the report
        // type, period, and filters are recorded — never the generated figures or file bytes.
        string auditPayload = JsonSerializer.Serialize(new
        {
            ReportType = reportType,
            PeriodKey = periodKey,
            BookingType = bookingType,
            OperatorUserId = operatorUserId,
            DestinationId = destinationId,
            Format = normalizedFormat,
        });
        db.AuditLogs.Add(AuditLog.CreateStatisticalReportExportRequested(userId, exportOperationRowId, auditPayload, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);

        return File(file.Content, file.ContentType, file.FileName);
    }

    private static string CriteriaKey(string reportType, string periodKey, string bookingType, long? operatorUserId, long? destinationId)
    {
        string input = string.Join('|', reportType.Trim().ToUpperInvariant(), periodKey.Trim().ToUpperInvariant(), bookingType.Trim().ToUpperInvariant(), operatorUserId?.ToString() ?? "ALL", destinationId?.ToString() ?? "ALL");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}