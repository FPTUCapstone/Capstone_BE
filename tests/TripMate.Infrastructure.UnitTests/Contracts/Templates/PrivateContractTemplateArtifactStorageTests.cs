using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts.Templates;

public sealed class PrivateContractTemplateArtifactStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tripmate-contract-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoreReadDelete_UsesOpaquePrivateKeysAndDurableBytes()
    {
        var storage = Create();
        var stored = await storage.StoreAsync([1, 2, 3], CancellationToken.None);
        stored.IsSuccess.Should().BeTrue();
        stored.Value.StorageKey.Should().StartWith("contract:").And.NotContain(root);
        stored.Value.Sha256.Should().HaveLength(64);
        (await storage.ReadAsync(stored.Value.StorageKey, CancellationToken.None)).Value.Should().Equal(1, 2, 3);
        (await storage.DeleteAsync(stored.Value.StorageKey, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await storage.ReadAsync(stored.Value.StorageKey, CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("contract:../../outside")]
    [InlineData("https://example.invalid/private")]
    public async Task ReadDelete_RejectsUnsafeOrForeignKeys(string key)
    {
        (await Create().ReadAsync(key, CancellationToken.None)).IsFailure.Should().BeTrue();
        (await Create().DeleteAsync(key, CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task MissingConfiguration_FailsClosedRatherThanWritingToPublicOrTemporaryRoot()
    {
        var storage = new PrivateContractTemplateArtifactStorage(Options.Create(new ContractTemplateArtifactOptions()));
        (await storage.StoreAsync([1], CancellationToken.None)).ErrorCode.Should().Be(ContractTemplateDocumentErrors.StorageUnavailable);
    }

    [Fact]
    public async Task CancelledWrite_DoesNotPublishPartialArtifact()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().StoreAsync([1], cancellation.Token));
        Directory.Exists(root).Should().BeFalse();
    }

    private PrivateContractTemplateArtifactStorage Create() => new(Options.Create(new ContractTemplateArtifactOptions { RootPath = root }));

    public void Dispose()
    {
        // This path is a unique test-owned directory, never a configured application root.
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}