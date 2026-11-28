using System.Security.Cryptography;

using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>
/// Processes PDF AcroForm contract templates, replacing the DOCX + LibreOffice path.
/// Admin uploads a PDF with AcroForm text fields named after the allowed variables.
/// </summary>
public sealed class PdfAcroFormContractTemplateProcessor : IContractTemplateDocumentProcessor
{
    public const string PdfContentType = "application/pdf";
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const string PreviewNotice = DocxContractTemplateProcessor.PreviewNotice;

    private static readonly HashSet<string> Allowed =
    [
        "contract_number", "contract_version", "company_name", "tax_code",
        "business_license_no", "business_address", "representative_name",
        "representative_title", "representative_capacity", "operator_email",
        "signature_block",
    ];

    private static readonly string[] Required =
    [
        "contract_number", "company_name", "tax_code", "representative_name", "signature_block",
    ];

    public Task<Result<ContractTemplateInspection>> InspectAsync(
        ContractTemplateUpload upload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (upload.Bytes.LongLength > MaximumBytes)
            return Task.FromResult(Result<ContractTemplateInspection>.Failure(
                ContractTemplateDocumentErrors.DocumentTooLarge, "The PDF exceeds the allowed size."));

        if (!IsPdfBytes(upload.Bytes) || upload.ContentType != PdfContentType)
            return Task.FromResult(Result<ContractTemplateInspection>.Failure(
                ContractTemplateDocumentErrors.InvalidDocument, "Upload a valid PDF AcroForm document.",
                new Dictionary<string, object?> { ["validationIssue"] = "not_a_pdf" }));

        try
        {
            var fields = ReadAcroFormFields(upload.Bytes);
            var missing = Required.Except(fields, StringComparer.Ordinal).Order().ToArray();
            var unknown = fields.Except(Allowed, StringComparer.Ordinal).Order().ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
                return Task.FromResult(Result<ContractTemplateInspection>.Failure(
                    ContractTemplateDocumentErrors.InvalidVariables,
                    "The PDF AcroForm contains unsupported or missing fields.",
                    new Dictionary<string, object?> { ["missingVariables"] = missing, ["unknownVariables"] = unknown }));

            var sha256 = Convert.ToHexString(SHA256.HashData(upload.Bytes)).ToLowerInvariant();
            return Task.FromResult(Result<ContractTemplateInspection>.Success(
                new(sha256, fields.Order().ToArray())));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
                                   || IsITextException(ex))
        {
            return Task.FromResult(Result<ContractTemplateInspection>.Failure(
                ContractTemplateDocumentErrors.InvalidDocument, "The PDF could not be read.",
                new Dictionary<string, object?> { ["validationIssue"] = "unreadable_pdf" }));
        }
    }

    // Returns PDF bytes (not DOCX) with preview values filled and "BẢN XEM TRƯỚC" watermark.
    // The name matches the interface contract; callers treat the result as opaque bytes.
    public Task<Result<byte[]>> PreparePreviewDocxAsync(
        ContractTemplateUpload upload,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPdfBytes(upload.Bytes) || upload.ContentType != PdfContentType)
            return Task.FromResult(Result<byte[]>.Failure(ContractTemplateDocumentErrors.InvalidDocument,
                "Upload a valid PDF AcroForm document."));
        try
        {
            var fontBytes = LoadFontBytes();
            using var output = new MemoryStream();
            using (var reader = new PdfReader(new MemoryStream(upload.Bytes)))
            using (var writer = new PdfWriter(output))
            using (var pdf = new PdfDocument(reader, writer))
            {
                var form = PdfAcroForm.GetAcroForm(pdf, false);
                if (form is not null)
                {
                    var viFont = PdfFontFactory.CreateFont(fontBytes, PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                    FillFields(form, values, viFont, skipField: "signature_block");
                    // Keep signature_block as AcroForm widget so the renderer can read its rect.
                    PartialFlattenExcept(form, "signature_block");
                }
                AddWatermarkToAllPages(pdf, fontBytes);
            }
            return Task.FromResult(Result<byte[]>.Success(output.ToArray()));
        }
        catch (Exception ex) when (IsITextException(ex) || ex is IOException or InvalidOperationException)
        {
            return Task.FromResult(Result<byte[]>.Failure(ContractTemplateDocumentErrors.InvalidDocument,
                "The PDF AcroForm could not be processed."));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HashSet<string> ReadAcroFormFields(byte[] pdfBytes)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var pdf = new PdfDocument(reader);
        var form = PdfAcroForm.GetAcroForm(pdf, false);
        if (form is null) return [];
        return new HashSet<string>(form.GetAllFormFields().Keys, StringComparer.Ordinal);
    }

    internal static void FillFields(
        PdfAcroForm form,
        IReadOnlyDictionary<string, string> values,
        PdfFont viFont,
        string? skipField = null)
    {
        foreach (var (key, value) in values)
        {
            if (key == skipField) continue;
            if (form.GetField(key) is not PdfFormField field) continue;
            field.SetFont(viFont);
            field.SetFontSize(10);
            field.SetValue(value);
        }
    }

    internal static void PartialFlattenExcept(PdfAcroForm form, string? keepField)
    {
        foreach (var name in form.GetAllFormFields().Keys)
        {
            if (name == keepField) continue;
            form.PartialFormFlattening(name);
        }
        form.FlattenFields();
    }

    private static void AddWatermarkToAllPages(PdfDocument pdf, byte[] fontBytes)
    {
        var viFont = PdfFontFactory.CreateFont(fontBytes, PdfEncodings.IDENTITY_H,
            PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
        for (var i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            var page = pdf.GetPage(i);
            var pageSize = page.GetPageSize();
            // Draw banner at top of page
            using var canvas = new Canvas(
                new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdf),
                pageSize);
            canvas.Add(new Paragraph(PreviewNotice)
                .SetFont(viFont)
                .SetFontSize(9)
                .SetFontColor(new DeviceRgb(180, 0, 0))
                .SetTextAlignment(TextAlignment.CENTER)
                .SetFixedPosition(0, pageSize.GetHeight() - 15, pageSize.GetWidth()));
            canvas.Close();
        }
    }

    internal static byte[] LoadFontBytes()
    {
        var envPath = Environment.GetEnvironmentVariable("CONTRACTS_FONT_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return File.ReadAllBytes(envPath);
        var asm = typeof(PdfAcroFormContractTemplateProcessor).Assembly;
        using var stream = asm.GetManifestResourceStream("TripMate.Contracts.NotoSans-Regular.ttf")
            ?? throw new InvalidOperationException("Embedded contract font resource not found.");
        var buf = new byte[stream.Length];
        _ = stream.Read(buf, 0, buf.Length);
        return buf;
    }

    private static bool IsPdfBytes(byte[] bytes) =>
        bytes.Length > 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8);

    private static bool IsITextException(Exception ex) =>
        ex.GetType().Namespace?.StartsWith("iText", StringComparison.Ordinal) == true;
}