using TripMate.Application.Common.Media;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Records logo storage calls for the UC-34 handler tests.</summary>
public sealed class FakeOperatorLogoStorage : IOperatorLogoStorage
{
    private int allocations;

    public List<OperatorLogoUpload> Uploads { get; } = [];

    public List<string> Deletes { get; } = [];

    public bool FailUpload { get; set; }

    /// <summary>When set, <see cref="DeleteAsync"/> throws this instead of returning.</summary>
    public Exception? DeleteThrows { get; set; }

    /// <summary>Result returned by <see cref="DeleteAsync"/> when it does not throw.</summary>
    public bool DeleteResult { get; set; } = true;

    public int CallCount => Uploads.Count + Deletes.Count;

    public string AllocatePublicId(long operatorUserId) =>
        $"tripmate/operator-logos/{operatorUserId}/new{++allocations}";

    public Task<OperatorLogoUploadResult> UploadAsync(OperatorLogoUpload upload, CancellationToken cancellationToken)
    {
        Uploads.Add(upload);
        return Task.FromResult(FailUpload
            ? OperatorLogoUploadResult.Failed("OPERATOR_LOGO_STORAGE_UNAVAILABLE")
            : OperatorLogoUploadResult.Succeeded(new Uri($"https://cdn.example.test/{upload.PublicId}.png")));
    }

    public Task<bool> DeleteAsync(string publicId, CancellationToken cancellationToken)
    {
        Deletes.Add(publicId);
        return DeleteThrows is null ? Task.FromResult(DeleteResult) : Task.FromException<bool>(DeleteThrows);
    }
}