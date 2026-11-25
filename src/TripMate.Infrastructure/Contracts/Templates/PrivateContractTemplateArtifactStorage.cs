using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

public sealed class PrivateContractTemplateArtifactStorage(IOptions<ContractTemplateArtifactOptions> options) : IContractTemplateArtifactStorage
{
    private const string Prefix = "contract:";

    public async Task<Result<StoredContractTemplateArtifact>> StoreAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryRoot(out var root) || bytes.Length == 0) return Unavailable<StoredContractTemplateArtifact>();
        var identifier = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(root, identifier + ".staging");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(root, identifier));
            return Result<StoredContractTemplateArtifact>.Success(new(Prefix + identifier, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Unavailable<StoredContractTemplateArtifact>(); }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<Result<byte[]>> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPath(storageKey, out var path)) return Result<byte[]>.Failure(ContractTemplateDocumentErrors.DocumentNotFound, "Document is unavailable.");
        try
        {
            if (!File.Exists(path)) return Result<byte[]>.Failure(ContractTemplateDocumentErrors.DocumentNotFound, "Document is unavailable.");
            return Result<byte[]>.Success(await File.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Unavailable<byte[]>(); }
    }

    public Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryPath(storageKey, out var path)) return Task.FromResult(Result.Failure(ContractTemplateDocumentErrors.DocumentNotFound, "Document is unavailable."));
        try { File.Delete(path); return Task.FromResult(Result.Success()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Task.FromResult(Result.Failure(ContractTemplateDocumentErrors.StorageUnavailable, "Document storage is temporarily unavailable.")); }
    }

    private bool TryRoot(out string root)
    {
        root = string.Empty;
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured)) return false;
        root = Path.GetFullPath(configured);
        return !root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("wwwroot", StringComparer.OrdinalIgnoreCase);
    }

    private bool TryPath(string key, out string path)
    {
        path = string.Empty;
        if (!TryRoot(out var root) || !key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length != Prefix.Length + 32 || !key[Prefix.Length..].All(Uri.IsHexDigit)) return false;
        path = Path.Combine(root, key[Prefix.Length..]);
        return true;
    }

    private static Result<T> Unavailable<T>() => Result<T>.Failure(ContractTemplateDocumentErrors.StorageUnavailable, "Document storage is temporarily unavailable.");
}