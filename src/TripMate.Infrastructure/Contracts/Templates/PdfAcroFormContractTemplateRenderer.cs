using iText.Forms;
using iText.Kernel.Pdf;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>
/// "Renderer" for the AcroForm path: the input bytes are already a PDF (from
/// <see cref="PdfAcroFormContractTemplateProcessor.PreparePreviewDocxAsync"/>).
/// This step reads the <c>signature_block</c> AcroForm widget rectangle and returns
/// it as a <see cref="ContractTemplateSignatureAnchor"/> so the stamper can place
/// the signature image at the correct position.
/// </summary>
public sealed class PdfAcroFormContractTemplateRenderer : IContractTemplatePdfRenderer
{
    public Task<Result<RenderedContractTemplate>> RenderPreviewAsync(
        byte[] docxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (docxBytes.Length is 0 or > 20 * 1024 * 1024)
            return Task.FromResult(Result<RenderedContractTemplate>.Failure(
                ContractTemplateDocumentErrors.RendererUnavailable, "The PDF is empty or too large."));

        try
        {
            var anchors = ExtractSignatureAnchors(docxBytes);
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(docxBytes))
                .ToLowerInvariant();
            var rendered = new RenderedContractTemplate(docxBytes, sha256, SupportsFinalSignatureAnchors: true)
            {
                SignatureAnchors = anchors,
            };
            return Task.FromResult(Result<RenderedContractTemplate>.Success(rendered));
        }
        catch (Exception ex) when (IsITextException(ex) || ex is IOException or InvalidOperationException)
        {
            return Task.FromResult(Result<RenderedContractTemplate>.Failure(
                ContractTemplateDocumentErrors.RendererUnavailable, "Could not read signature anchor from PDF."));
        }
    }

    private static IReadOnlyList<ContractTemplateSignatureAnchor> ExtractSignatureAnchors(byte[] pdfBytes)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var pdf = new PdfDocument(reader);

        var form = PdfAcroForm.GetAcroForm(pdf, false);
        if (form is null) return [];

        var sigField = form.GetField("signature_block");
        if (sigField is null) return [];

        var widgets = sigField.GetWidgets();
        if (widgets.Count == 0) return [];

        var widget = widgets[0];
        var rect = widget.GetRectangle()?.ToRectangle();
        if (rect is null) return [];

        // Determine page number for the widget
        var widgetPage = widget.GetPage();
        int pageNumber = 1;
        if (widgetPage is not null)
        {
            var pageRef = widgetPage.GetPdfObject().GetIndirectReference();
            for (var i = 1; i <= pdf.GetNumberOfPages(); i++)
            {
                if (pdf.GetPage(i).GetPdfObject().GetIndirectReference()?.Equals(pageRef) == true)
                {
                    pageNumber = i;
                    break;
                }
            }
        }

        var page = pdf.GetPage(pageNumber);
        var pageSize = page.GetPageSize();
        double pageWidth = pageSize.GetWidth();
        double pageHeight = pageSize.GetHeight();

        // iText7 PDF rect: origin at bottom-left, Y increases upward.
        // ContractPdfSignatureStamper uses PDFsharp top-left origin (Y increases downward).
        // Convert: screenY = pageHeight - pdfUrY
        double x = rect.GetX();
        double pdfUrY = rect.GetY() + rect.GetHeight();
        double y = pageHeight - pdfUrY;
        double w = rect.GetWidth();
        double h = rect.GetHeight();

        return [new ContractTemplateSignatureAnchor(pageNumber, pageWidth, pageHeight, x, y, w, h)];
    }

    private static bool IsITextException(Exception ex) =>
        ex.GetType().Namespace?.StartsWith("iText", StringComparison.Ordinal) == true;
}
