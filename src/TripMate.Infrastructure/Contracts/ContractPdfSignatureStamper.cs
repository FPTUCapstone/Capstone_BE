using System.Security.Cryptography;

using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Infrastructure.Contracts;

public sealed class ContractPdfSignatureStamper : IContractPdfSignatureStamper
{
    public Result<SignedContractPdf> Stamp(byte[] unsignedPdf, InspectedContractSignature signature, IReadOnlyList<ContractSignaturePlacement> placements)
    {
        if (unsignedPdf.Length is 0 or > 50 * 1024 * 1024 || placements.Count is 0 or > 10)
            return Invalid();
        try
        {
            using var document = PdfReader.Open(new MemoryStream(unsignedPdf), PdfDocumentOpenMode.Modify);
            if (document.PageCount is 0 or > 100) return Invalid();
            using var image = XImage.FromStream(new MemoryStream(signature.PngBytes));
            foreach (var placement in placements)
            {
                if (placement.PageNumber < 1 || placement.PageNumber > document.PageCount) return Invalid();
                var page = document.Pages[placement.PageNumber - 1];
                var width = page.Width.Point;
                var height = page.Height.Point;
                var imageWidth = Math.Min(180, placement.Width);
                var imageHeight = imageWidth * signature.Height / signature.Width;
                if (imageHeight > 60) { imageWidth *= 60 / imageHeight; imageHeight = 60; }
                if (!double.IsFinite(placement.X + placement.Y + placement.Width + placement.Height + placement.PageWidth + placement.PageHeight)
                    || page.Rotate != 0 || Math.Abs(width - placement.PageWidth) > 1 || Math.Abs(height - placement.PageHeight) > 1
                    || placement.X < 0 || placement.Y < 0 || placement.Width <= 0 || placement.Height <= 0
                    || placement.X + placement.Width > width || placement.Y + Math.Max(imageHeight, placement.Height) > height)
                    return Invalid();
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                // Erase only the reserved marker; the surrounding accepted document is retained.
                graphics.DrawRectangle(XBrushes.White, placement.X, placement.Y, placement.Width, placement.Height);
                graphics.DrawImage(image, placement.X, placement.Y, imageWidth, imageHeight);
            }
            using var output = new MemoryStream();
            document.Save(output);
            var bytes = output.ToArray();
            if (bytes.Length > 50 * 1024 * 1024) return Invalid();
            return Result.Success(new SignedContractPdf(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        catch (Exception exception) when (exception is PdfReaderException or ArgumentException or InvalidOperationException or IOException)
        { return Invalid(); }
    }

    private static Result<SignedContractPdf> Invalid() => Result.Failure<SignedContractPdf>(
        "contract.signature_placement_invalid", "The signature cannot be placed on this contract. Reload the contract and try again.");
}