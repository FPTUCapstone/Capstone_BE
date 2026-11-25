using FluentAssertions;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

using SkiaSharp;

using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Infrastructure.Contracts;

namespace TripMate.Infrastructure.UnitTests.Contracts;

public sealed class ContractPdfSignatureStamperTests
{
    [Fact]
    public void Stamp_PreservesSourceAndPages_AndAddsTheSignatureToTheActualPdf()
    {
        var bytes = Pdf();
        var original = bytes.ToArray();
        var result = new ContractPdfSignatureStamper().Stamp(bytes, Signature(), [new(1, 612, 792, 72, 600, 180, 14)]);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        bytes.Should().Equal(original);
        result.Value.Bytes.Should().NotEqual(original);
        result.Value.Sha256.Should().HaveLength(64);
        using var reopened = PdfReader.Open(new MemoryStream(result.Value.Bytes), PdfDocumentOpenMode.Import);
        reopened.PageCount.Should().Be(1);
    }

    [Fact]
    public void MissingOrOutOfBoundsPlacement_IsRejectedInsteadOfGuessingItsPosition()
    {
        var stamper = new ContractPdfSignatureStamper();
        stamper.Stamp(Pdf(), Signature(), []).ErrorCode.Should().Be("contract.signature_placement_invalid");
        stamper.Stamp(Pdf(), Signature(), [new(1, 612, 792, 72, 780, 180, 14)]).IsFailure.Should().BeTrue();
        stamper.Stamp(Pdf(), Signature(), [new(2, 612, 792, 72, 600, 180, 14)]).IsFailure.Should().BeTrue();
    }

    private static byte[] Pdf()
    {
        using var pdf = new PdfDocument();
        var page = pdf.AddPage(); page.Width = PdfSharp.Drawing.XUnit.FromPoint(612); page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
        using var stream = new MemoryStream(); pdf.Save(stream);
        return stream.ToArray();
    }

    private static InspectedContractSignature Signature()
    {
        using var bitmap = new SKBitmap(600, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 5 };
        canvas.DrawLine(20, 150, 400, 60, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        return new ContractSignatureImageInspector().Inspect(bytes.ToArray()).Value;
    }
}