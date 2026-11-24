using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates;
using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.ContractTemplates;

public sealed class ContractTemplateWorkflowTests
{
    [Fact]
    public async Task Expired_final_attempt_fails_preview_instead_of_leaving_it_processing()
    {
        await using var fixture = new Fixture();
        var draft = (await fixture.Workflow.CreateAsync("Partnership", Fixture.Upload, default)).Value;
        await fixture.Workflow.PreviewAsync(long.Parse(draft.Id), new(draft.DraftRevision, draft.RowVersion), default);
        var job = await fixture.Db.ContractTemplateRenderJobs.SingleAsync();
        var past = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.True(job.TryClaim(Guid.NewGuid(), past.AddMinutes(attempt * 2), TimeSpan.FromMinutes(1)));
        await fixture.Db.SaveChangesAsync();
        Assert.True(await fixture.Workflow.ProcessNextPreviewAsync(default));
        var result = (await fixture.Workflow.GetAsync(long.Parse(draft.Id), default)).Value;
        Assert.Equal("Failed", result.RenderState);
        Assert.Equal("contract_template.render_attempts_exhausted", result.PreviewErrorCode);
    }

    [Fact]
    public async Task Publishing_v2_archives_and_locks_v1_without_changing_its_document()
    {
        await using var fixture = new Fixture(renderer: new WorkingRenderer());
        var draft = (await fixture.Workflow.CreateAsync("Version one", Fixture.Upload, default)).Value;
        await fixture.Workflow.PreviewAsync(long.Parse(draft.Id), new(draft.DraftRevision, draft.RowVersion), default);
        await fixture.Workflow.ProcessNextPreviewAsync(default);
        var ready = (await fixture.Workflow.GetAsync(long.Parse(draft.Id), default)).Value;
        var input = new PublishContractTemplateInput(ready.DraftRevision, ready.SourceSha256, ready.RowVersion, null, 0, true, Guid.NewGuid());
        var v1 = await fixture.Workflow.PublishAsync(long.Parse(draft.Id), input, default);
        Assert.True(v1.IsSuccess, v1.ErrorMessage);
        Assert.Equal(1, v1.Value.Version);
        Assert.True((await fixture.Workflow.PublishAsync(long.Parse(draft.Id), input, default)).IsSuccess);
        var v2Draft = (await fixture.Workflow.CreateVersionAsync(long.Parse(draft.Id), "Version two", v1.Value.RowVersion, default)).Value;
        await fixture.Workflow.PreviewAsync(long.Parse(v2Draft.Id), new(v2Draft.DraftRevision, v2Draft.RowVersion), default);
        await fixture.Workflow.ProcessNextPreviewAsync(default);
        var ready2 = (await fixture.Workflow.GetAsync(long.Parse(v2Draft.Id), default)).Value;
        var v2 = await fixture.Workflow.PublishAsync(long.Parse(v2Draft.Id), new(ready2.DraftRevision,
            ready2.SourceSha256, ready2.RowVersion, draft.Id, 1, true, Guid.NewGuid()), default);
        Assert.True(v2.IsSuccess, v2.ErrorMessage);
        Assert.Equal(2, v2.Value.Version);
        var archived = (await fixture.Workflow.GetAsync(long.Parse(draft.Id), default)).Value;
        Assert.Equal("Archived", archived.Status);
        Assert.Equal(ready.SourceSha256, archived.SourceSha256);
        Assert.Equal(Fixture.Upload.Bytes, (await fixture.Workflow.DownloadAsync(long.Parse(draft.Id), "source", default)).Value.Bytes);
        var edited = await fixture.Workflow.UpdateAsync(long.Parse(draft.Id), "Cannot edit", archived.DraftRevision, archived.RowVersion, null, default);
        Assert.Equal("contract_template.conflict", edited.ErrorCode);
    }

    [Fact]
    public async Task Different_drafts_cannot_publish_against_the_same_stale_head()
    {
        await using var fixture = new Fixture(renderer: new WorkingRenderer());
        var first = (await fixture.Workflow.CreateAsync("First", Fixture.Upload, default)).Value;
        var second = (await fixture.Workflow.CreateAsync("Second", Fixture.Upload, default)).Value;
        foreach (var draft in new[] { first, second })
        {
            await fixture.Workflow.PreviewAsync(long.Parse(draft.Id), new(draft.DraftRevision, draft.RowVersion), default);
            await fixture.Workflow.ProcessNextPreviewAsync(default);
        }
        var ready1 = (await fixture.Workflow.GetAsync(long.Parse(first.Id), default)).Value;
        var ready2 = (await fixture.Workflow.GetAsync(long.Parse(second.Id), default)).Value;
        Assert.True((await fixture.Workflow.PublishAsync(long.Parse(first.Id), new(ready1.DraftRevision, ready1.SourceSha256, ready1.RowVersion, null, 0, true, Guid.NewGuid()), default)).IsSuccess);
        var stale = await fixture.Workflow.PublishAsync(long.Parse(second.Id), new(ready2.DraftRevision, ready2.SourceSha256, ready2.RowVersion, null, 0, true, Guid.NewGuid()), default);
        Assert.Equal("contract_template.conflict", stale.ErrorCode);
        Assert.Equal("Published", (await fixture.Workflow.GetAsync(long.Parse(first.Id), default)).Value.Status);
        Assert.Equal("Draft", (await fixture.Workflow.GetAsync(long.Parse(second.Id), default)).Value.Status);
    }

    [Fact]
    public async Task Staff_cannot_create_or_list_templates()
    {
        await using var fixture = new Fixture("Staff");
        var created = await fixture.Workflow.CreateAsync("Partnership", Fixture.Upload, default);
        Assert.Equal("contract_template.forbidden", created.ErrorCode);
        Assert.Equal("contract_template.forbidden", (await fixture.Workflow.ListAsync(null, 1, 20, default)).ErrorCode);
        Assert.Equal(0, fixture.Storage.Writes);
    }

    [Fact]
    public async Task Creation_is_private_draft_and_publishing_without_preview_is_rejected()
    {
        await using var fixture = new Fixture();
        var created = await fixture.Workflow.CreateAsync("Partnership", Fixture.Upload, default);
        Assert.True(created.IsSuccess, created.ErrorMessage);
        Assert.Equal("Draft", created.Value.Status);
        var id = long.Parse(created.Value.Id);
        var published = await fixture.Workflow.PublishAsync(id, new PublishContractTemplateInput(
            created.Value.DraftRevision, created.Value.SourceSha256, created.Value.RowVersion,
            null, 0, true, Guid.NewGuid()), default);
        Assert.Equal("contract_template.conflict", published.ErrorCode);
        Assert.Equal("Draft", (await fixture.Workflow.GetAsync(id, default)).Value.Status);
    }

    [Fact]
    public async Task Unavailable_renderer_never_publishes_or_changes_account_status()
    {
        await using var fixture = new Fixture();
        var created = (await fixture.Workflow.CreateAsync("Partnership", Fixture.Upload, default)).Value;
        var preview = await fixture.Workflow.PreviewAsync(long.Parse(created.Id),
            new PreviewContractTemplateInput(created.DraftRevision, created.RowVersion), default);
        Assert.True(preview.IsSuccess, preview.ErrorMessage);
        Assert.Equal("Processing", preview.Value.RenderState);
        Assert.True(await fixture.Workflow.ProcessNextPreviewAsync(default));
        var reloaded = (await fixture.Workflow.GetAsync(long.Parse(created.Id), default)).Value;
        Assert.Equal("Draft", reloaded.Status);
        Assert.Equal("Failed", reloaded.RenderState);
        Assert.Equal(ContractTemplateDocumentErrors.RendererUnavailable, reloaded.PreviewErrorCode);
        Assert.Empty(fixture.Db.Users);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly ContractTemplateUpload Upload = new("sample.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [1, 2, 3]);
        public TemplateDb Db { get; } = new();
        public MemoryStorage Storage { get; } = new();
        public ContractTemplateWorkflow Workflow { get; }
        public Fixture(string role = "Administrator", IContractTemplatePdfRenderer? renderer = null) => Workflow = new(Db,
            new FakeCurrentUserService { UserId = 77, Role = role },
            new FakeDateTimeProvider { UtcNow = DateTimeOffset.Parse("2026-11-23T00:00:00Z") },
            new Processor(), Storage, renderer ?? new UnavailableRenderer());
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TemplateDb() : TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options), IApplicationDbContext
    {
        public DbSet<OperatorContractTemplate> OperatorContractTemplates => Set<OperatorContractTemplate>();
        public DbSet<ContractTemplatePublicationHead> ContractTemplatePublicationHeads => Set<ContractTemplatePublicationHead>();
        public DbSet<ContractTemplateRenderJob> ContractTemplateRenderJobs => Set<ContractTemplateRenderJob>();
    }

    private sealed class Processor : IContractTemplateDocumentProcessor
    {
        public Task<Result<ContractTemplateInspection>> InspectAsync(ContractTemplateUpload upload, CancellationToken ct) =>
            Task.FromResult(Result.Success(new ContractTemplateInspection(Hash(upload.Bytes), ["company_name", "signature_block"])));
        public Task<Result<byte[]>> PreparePreviewDocxAsync(ContractTemplateUpload upload, IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
            Task.FromResult(Result.Success(upload.Bytes));
    }

    private sealed class UnavailableRenderer : IContractTemplatePdfRenderer
    {
        public Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] bytes, CancellationToken ct) =>
            Task.FromResult(Result.Failure<RenderedContractTemplate>(ContractTemplateDocumentErrors.RendererUnavailable, "Renderer unavailable."));
    }

    private sealed class WorkingRenderer : IContractTemplatePdfRenderer
    {
        public Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] bytes, CancellationToken ct)
        {
            byte[] pdf = "%PDF-test-only"u8.ToArray();
            return Task.FromResult(Result.Success(new RenderedContractTemplate(pdf, Hash(pdf), true)));
        }
    }

    private sealed class MemoryStorage : IContractTemplateArtifactStorage
    {
        private readonly Dictionary<string, byte[]> files = [];
        public int Writes { get; private set; }
        public Task<Result<StoredContractTemplateArtifact>> StoreAsync(byte[] bytes, CancellationToken ct)
        {
            var key = (++Writes).ToString(); files[key] = bytes;
            return Task.FromResult(Result.Success(new StoredContractTemplateArtifact(key, Hash(bytes), bytes.Length)));
        }
        public Task<Result<byte[]>> ReadAsync(string key, CancellationToken ct) => Task.FromResult(Result.Success(files[key]));
        public Task<Result> DeleteAsync(string key, CancellationToken ct) { files.Remove(key); return Task.FromResult(Result.Success()); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}