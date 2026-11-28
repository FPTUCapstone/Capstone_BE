using iText.Forms;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.Contracts;

/// <summary>
/// Fills a PDF AcroForm contract template with TO-specific values and flattens
/// non-signature fields, producing a ready-to-sign PDF.
/// Replaces <see cref="OperatorContractDocumentComposer"/> for the AcroForm path.
/// </summary>
public sealed class PdfAcroFormContractDocumentComposer : IOperatorContractDocumentComposer
{
    public Task<Result<byte[]>> PrepareAsync(
        byte[] sourceDocx,
        string fileName,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sourceDocx.Length is 0 or > 20 * 1024 * 1024)
            return Task.FromResult(Result<byte[]>.Failure(
                "contract.template_invalid", "The contract template is empty or too large."));

        if (!IsPdfBytes(sourceDocx))
            return Task.FromResult(Result<byte[]>.Failure(
                "contract.template_invalid", "The contract template is not a valid PDF."));

        try
        {
            var fontBytes = PdfAcroFormContractTemplateProcessor.LoadFontBytes();
            using var output = new MemoryStream();
            using (var reader = new PdfReader(new MemoryStream(sourceDocx)))
            using (var writer = new PdfWriter(output))
            using (var pdf = new PdfDocument(reader, writer))
            {
                var form = PdfAcroForm.GetAcroForm(pdf, false);
                if (form is not null)
                {
                    var viFont = PdfFontFactory.CreateFont(fontBytes, PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                    PdfAcroFormContractTemplateProcessor.FillFields(form, values, viFont, skipField: "signature_block");
                    // Flatten all data fields; keep signature_block so the stamper can place the image.
                    PdfAcroFormContractTemplateProcessor.PartialFlattenExcept(form, "signature_block");
                }
            }
            return Task.FromResult(Result<byte[]>.Success(output.ToArray()));
        }
        catch (Exception ex) when (IsITextException(ex) || ex is IOException or InvalidOperationException)
        {
            return Task.FromResult(Result<byte[]>.Failure(
                "contract.template_invalid", "The contract template could not be processed."));
        }
    }

    private static bool IsPdfBytes(byte[] bytes) =>
        bytes.Length > 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8);

    private static bool IsITextException(Exception ex) =>
        ex.GetType().Namespace?.StartsWith("iText", StringComparison.Ordinal) == true;
}
