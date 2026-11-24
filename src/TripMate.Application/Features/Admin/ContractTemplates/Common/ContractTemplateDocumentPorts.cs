using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.ContractTemplates.Common;

public sealed record ContractTemplateUpload(string FileName, string ContentType, byte[] Bytes);

public sealed record ContractTemplateInspection(string SourceSha256, IReadOnlyList<string> Variables);

public interface IContractTemplateDocumentProcessor
{
    Task<Result<ContractTemplateInspection>> InspectAsync(ContractTemplateUpload upload, CancellationToken cancellationToken);

    Task<Result<byte[]>> PreparePreviewDocxAsync(
        ContractTemplateUpload upload,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken);
}

public sealed record StoredContractTemplateArtifact(string StorageKey, string Sha256, long ByteLength);

/// <summary>Private artifact references. Authorization is enforced by the consuming use case.</summary>
public interface IContractTemplateArtifactStorage
{
    Task<Result<StoredContractTemplateArtifact>> StoreAsync(byte[] bytes, CancellationToken cancellationToken);
    Task<Result<byte[]>> ReadAsync(string storageKey, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

/// <summary>Anchor support is true only for an engine proven to resolve anchors on each final PDF.</summary>
public sealed record RenderedContractTemplate(byte[] PdfBytes, string PdfSha256, bool SupportsFinalSignatureAnchors)
{
    public IReadOnlyList<ContractTemplateSignatureAnchor> SignatureAnchors { get; init; } = [];
}

public sealed record ContractTemplateSignatureAnchor(int PageNumber, double PageWidth, double PageHeight, double X, double Y, double Width, double Height);

public interface IContractTemplatePdfRenderer
{
    Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] docxBytes, CancellationToken cancellationToken);
}

public static class ContractTemplateDocumentErrors
{
    public const string InvalidDocument = "contract_template.invalid_document";
    public const string InvalidVariables = "contract_template.invalid_variables";
    public const string DocumentTooLarge = "contract_template.document_too_large";
    public const string StorageUnavailable = "contract_template.storage_unavailable";
    public const string DocumentNotFound = "contract_template.document_not_found";
    public const string RendererUnavailable = "contract_template.renderer_unavailable";
}

public sealed class ContractTemplateArtifactOptions
{
    public string RootPath { get; set; } = string.Empty;
}

public sealed class ContractTemplateRendererOptions
{
    public string DockerExecutablePath { get; set; } = string.Empty;
    public string DockerImage { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string PdfTextExtractorPath { get; set; } = string.Empty;
    // The rendering deployment must deny network access and enforce CPU/RAM limits.
    public bool IsolatedExecutionVerified { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}