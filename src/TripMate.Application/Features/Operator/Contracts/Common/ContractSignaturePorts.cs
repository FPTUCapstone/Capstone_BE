using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Contracts.Common;

public sealed record InspectedContractSignature(byte[] PngBytes, string SourceSha256, string CanonicalSha256, int Width, int Height);

public interface IContractSignatureImageInspector
{
    Result<InspectedContractSignature> Inspect(byte[] bytes);
}

public sealed record ContractSignaturePlacement(int PageNumber, double PageWidth, double PageHeight, double X, double Y, double Width, double Height);
public sealed record SignedContractPdf(byte[] Bytes, string Sha256);

public interface IContractPdfSignatureStamper
{
    Result<SignedContractPdf> Stamp(byte[] unsignedPdf, InspectedContractSignature signature, IReadOnlyList<ContractSignaturePlacement> placements);
}

public interface IOperatorContractDocumentComposer
{
    Task<Result<byte[]>> PrepareAsync(byte[] sourceDocx, string fileName, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
}