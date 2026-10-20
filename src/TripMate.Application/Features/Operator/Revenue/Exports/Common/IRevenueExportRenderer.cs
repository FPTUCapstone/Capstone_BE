namespace TripMate.Application.Features.Operator.Revenue.Exports.Common;

/// <summary>Renders a formatter-neutral <see cref="RevenueExportDocument"/> to file bytes.</summary>
public interface IRevenueExportRenderer
{
    string ContentType { get; }

    /// <summary>Lowercase, no leading dot — e.g. "xlsx".</summary>
    string FileExtension { get; }

    byte[] Render(RevenueExportDocument document);
}
