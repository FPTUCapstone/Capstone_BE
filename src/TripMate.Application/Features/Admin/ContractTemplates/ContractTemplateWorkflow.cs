using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.ContractTemplates;

public sealed record PublicationHeadDto(string? CurrentPublishedId, long Generation);
public sealed record ContractTemplateDto(string Id, int? Version, int DraftRevision, string Title, string Status,
    string RowVersion, string SourceFileName, string SourceSha256, string ValidationStatus,
    IReadOnlyList<string> Variables, IReadOnlyList<string> MissingVariables, IReadOnlyList<string> UnknownVariables,
    string RenderState, int? PreviewRevision, string? PreviewErrorCode, int? PreviewAcknowledgedRevision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? PublishedAtUtc,
    string CreatedBy, string UpdatedBy, string? PublishedBy, PublicationHeadDto PublicationHead);
public sealed record ContractTemplatePage(IReadOnlyList<ContractTemplateDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record PreviewContractTemplateInput(int ExpectedRevision, string RowVersion);
public sealed record PublishContractTemplateInput(int ExpectedRevision, string ExpectedSourceSha256, string RowVersion,
    string? ExpectedCurrentPublishedId, long ExpectedPublicationGeneration, bool PreviewAcknowledged, Guid IdempotencyKey);
public sealed record ContractTemplateDocument(byte[] Bytes, string ContentType, string FileName);

public static class ContractTemplateErrors
{
    public const string Forbidden = "contract_template.forbidden";
    public const string NotFound = "contract_template.not_found";
    public const string Conflict = "contract_template.conflict";
    public const string Validation = "contract_template.validation";
}

/// <summary>Application coordination for private artifacts, revision checks and atomic publication.</summary>
public sealed class ContractTemplateWorkflow(IApplicationDbContext db, ICurrentUserService user, IDateTimeProvider clock,
    IContractTemplateDocumentProcessor processor, IContractTemplateArtifactStorage storage, IContractTemplatePdfRenderer renderer)
{
    private bool IsAdmin => user.UserId is > 0 && user.Role == nameof(UserRole.Administrator);
    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static Result<T> Denied<T>() => Result.Failure<T>(ContractTemplateErrors.Forbidden, "Administrator access is required.");
    private static Result<T> Missing<T>() => Result.Failure<T>(ContractTemplateErrors.NotFound, "Contract template was not found.");
    private static Result<T> Conflict<T>() => Result.Failure<T>(ContractTemplateErrors.Conflict, "The template changed or is not ready for this action. Reload and try again.");
    private static Result<T> Fail<T>(Result failure) => Result.Failure<T>(failure.ErrorCode!, failure.ErrorMessage!, failure.ErrorMetadata);
    private static bool ValidTitle(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= OperatorContractTemplate.TitleMaxLength;
    private static bool Matches(OperatorContractTemplate template, int revision, string rowVersion) =>
        template.DraftRevision == revision && Convert.ToBase64String(template.RowVersion) == rowVersion;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private Task<ContractTemplatePublicationHead?> Head(CancellationToken ct) => db.ContractTemplatePublicationHeads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);

    private static ContractTemplateDto Dto(OperatorContractTemplate value, ContractTemplatePublicationHead? head) => new(
        Id(value.Id), value.Version, value.DraftRevision, value.Title, value.Status.ToString(),
        Convert.ToBase64String(value.RowVersion), value.SourceFileName, value.SourceSha256, value.ValidationValid ? "Valid" : "Invalid",
        JsonSerializer.Deserialize<string[]>(value.VariablesManifestJson) ?? [], [], [], value.RenderState.ToString(),
        value.PreviewRevision, value.RenderErrorCode, value.PreviewAcknowledgedRevision, value.CreatedAtUtc,
        value.UpdatedAtUtc, value.PublishedAtUtc, Id(value.CreatedByUserId), Id(value.UpdatedByUserId),
        value.PublishedByUserId is long publisher ? Id(publisher) : null,
        new(head?.CurrentPublishedTemplateId is long current ? Id(current) : null, head?.Generation ?? 0));

    private void Audit(string action, OperatorContractTemplate template) => db.AuditLogs.Add(new AuditLog(
        user.UserId, action, nameof(OperatorContractTemplate), template.Id, clock.UtcNow,
        afterData: JsonSerializer.Serialize(new { template.DraftRevision, template.SourceSha256, template.Status, template.Version })));

    public async Task<Result<ContractTemplateDto>> GetAsync(long id, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        var template = await db.OperatorContractTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return template is null ? Missing<ContractTemplateDto>() : Result.Success(Dto(template, await Head(ct)));
    }

    public async Task<Result<ContractTemplatePage>> ListAsync(string? status, int page, int pageSize, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplatePage>();
        if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 100 ||
            (status is not null && !Enum.TryParse<ContractTemplateStatus>(status, out _)))
            return Result.Failure<ContractTemplatePage>(ContractTemplateErrors.Validation, "Invalid template filter or page.");
        var query = db.OperatorContractTemplates.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == Enum.Parse<ContractTemplateStatus>(status));
        var count = await query.CountAsync(ct);
        var templates = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var head = await Head(ct);
        return Result.Success(new ContractTemplatePage(templates.Select(x => Dto(x, head)).ToArray(), page, pageSize, count, (count + pageSize - 1) / pageSize));
    }

    public async Task<Result<ContractTemplateDto>> CreateAsync(string title, ContractTemplateUpload upload, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        if (!ValidTitle(title)) return Result.Failure<ContractTemplateDto>(ContractTemplateErrors.Validation, "A title of at most 200 characters is required.");
        var inspected = await processor.InspectAsync(upload, ct);
        if (inspected.IsFailure) return Fail<ContractTemplateDto>(inspected);
        var artifact = await storage.StoreAsync(upload.Bytes, ct);
        if (artifact.IsFailure) return Fail<ContractTemplateDto>(artifact);
        if (!string.Equals(artifact.Value.Sha256, inspected.Value.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            await storage.DeleteAsync(artifact.Value.StorageKey, ct);
            return Result.Failure<ContractTemplateDto>(ContractTemplateDocumentErrors.StorageUnavailable, "Stored document integrity could not be verified.");
        }
        var template = OperatorContractTemplate.CreateDraft(title, artifact.Value.StorageKey, inspected.Value.SourceSha256,
            JsonSerializer.Serialize(inspected.Value.Variables), true, user.UserId!.Value, clock.UtcNow, Path.GetFileName(upload.FileName));
        try
        {
            await db.ExecuteInTransactionAsync(async token =>
            {
                db.OperatorContractTemplates.Add(template);
                await db.SaveChangesAsync(token);
                Audit("ContractTemplateDraftCreated", template);
                await db.SaveChangesAsync(token);
                return true;
            }, ct);
        }
        catch
        {
            // The artifact is not exposed until its DB reference commits.
            // Leave it private on uncertain commits; retention cleanup reconciles unreferenced files.
            throw;
        }
        return Result.Success(Dto(template, await Head(ct)));
    }

    public async Task<Result<ContractTemplateDto>> UpdateAsync(long id, string title, int expectedRevision, string rowVersion,
        ContractTemplateUpload? upload, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        if (!ValidTitle(title)) return Result.Failure<ContractTemplateDto>(ContractTemplateErrors.Validation, "A valid title is required.");
        var template = await db.OperatorContractTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null) return Missing<ContractTemplateDto>();
        if (template.Status != ContractTemplateStatus.Draft || !Matches(template, expectedRevision, rowVersion)) return Conflict<ContractTemplateDto>();
        var key = template.SourceStorageKey; var hash = template.SourceSha256;
        var variables = template.VariablesManifestJson; var fileName = template.SourceFileName;
        if (upload is not null)
        {
            var inspection = await processor.InspectAsync(upload, ct);
            if (inspection.IsFailure) return Fail<ContractTemplateDto>(inspection);
            var artifact = await storage.StoreAsync(upload.Bytes, ct);
            if (artifact.IsFailure) return Fail<ContractTemplateDto>(artifact);
            if (!string.Equals(artifact.Value.Sha256, inspection.Value.SourceSha256, StringComparison.OrdinalIgnoreCase)) return Result.Failure<ContractTemplateDto>(ContractTemplateDocumentErrors.StorageUnavailable, "Document integrity check failed.");
            key = artifact.Value.StorageKey; hash = inspection.Value.SourceSha256;
            variables = JsonSerializer.Serialize(inspection.Value.Variables); fileName = Path.GetFileName(upload.FileName);
        }
        if (!template.TryUpdateDraft(expectedRevision, title, key, hash, variables, true, user.UserId!.Value, clock.UtcNow, fileName))
            return Conflict<ContractTemplateDto>();
        try
        {
            await db.ExecuteInTransactionAsync(async token => { Audit("ContractTemplateDraftUpdated", template); await db.SaveChangesAsync(token); return true; }, ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict<ContractTemplateDto>(); }
        return Result.Success(Dto(template, await Head(ct)));
    }

    public async Task<Result<ContractTemplateDto>> CreateVersionAsync(long id, string? title, string rowVersion, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        var original = await db.OperatorContractTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (original is null) return Missing<ContractTemplateDto>();
        if (original.Status == ContractTemplateStatus.Draft || Convert.ToBase64String(original.RowVersion) != rowVersion)
            return Conflict<ContractTemplateDto>();
        var nextTitle = title ?? original.Title;
        if (!ValidTitle(nextTitle)) return Result.Failure<ContractTemplateDto>(ContractTemplateErrors.Validation, "A valid title is required.");
        // Shared immutable artifact keys are intentionally retained until no template references them.
        var draft = OperatorContractTemplate.CreateDraft(nextTitle, original.SourceStorageKey, original.SourceSha256,
            original.VariablesManifestJson, original.ValidationValid, user.UserId!.Value, clock.UtcNow, original.SourceFileName);
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.OperatorContractTemplates.Add(draft); await db.SaveChangesAsync(token);
            Audit("ContractTemplateVersionCreated", draft); await db.SaveChangesAsync(token); return true;
        }, ct);
        return Result.Success(Dto(draft, await Head(ct)));
    }

    public async Task<Result<ContractTemplateDto>> PreviewAsync(long id, PreviewContractTemplateInput input, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        var template = await db.OperatorContractTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null) return Missing<ContractTemplateDto>();
        if (!Matches(template, input.ExpectedRevision, input.RowVersion)) return Conflict<ContractTemplateDto>();
        if (template.RenderState == ContractTemplateRenderState.Processing) return Result.Success(Dto(template, await Head(ct)));
        if (!template.RequestPreview(input.ExpectedRevision)) return Conflict<ContractTemplateDto>();
        var job = await db.ContractTemplateRenderJobs.SingleOrDefaultAsync(x => x.TemplateId == id && x.Revision == input.ExpectedRevision, ct);
        if (job is not null && (job.AttemptCount >= 3 || !job.TryRequeue())) return Conflict<ContractTemplateDto>();
        if (job is null) db.ContractTemplateRenderJobs.Add(ContractTemplateRenderJob.Create(template, clock.UtcNow));
        try
        {
            await db.ExecuteInTransactionAsync(async token => { Audit("ContractTemplatePreviewRequested", template); await db.SaveChangesAsync(token); return true; }, ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict<ContractTemplateDto>(); }
        return Result.Success(Dto(template, await Head(ct)));
    }

    public async Task<Result<ContractTemplateDocument>> DownloadAsync(long id, string kind, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDocument>();
        var template = await db.OperatorContractTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null) return Missing<ContractTemplateDocument>();
        var preview = kind == "preview";
        if (kind != "source" && !preview) return Result.Failure<ContractTemplateDocument>(ContractTemplateErrors.Validation, "Invalid document kind.");
        if (preview && (template.RenderState != ContractTemplateRenderState.Ready || template.PreviewRevision != template.DraftRevision))
            return Conflict<ContractTemplateDocument>();
        var bytes = await storage.ReadAsync(preview ? template.PreviewStorageKey! : template.SourceStorageKey, ct);
        if (bytes.IsFailure) return Fail<ContractTemplateDocument>(bytes);
        if (!string.Equals(Hash(bytes.Value), preview ? template.PreviewSha256 : template.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ContractTemplateDocument>(ContractTemplateDocumentErrors.StorageUnavailable, "Document integrity check failed.");
        return Result.Success(new ContractTemplateDocument(bytes.Value,
            preview ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            preview ? $"contract-template-{id}-preview.pdf" : template.SourceFileName));
    }

    public async Task<Result<ContractTemplateDto>> PublishAsync(long id, PublishContractTemplateInput input, CancellationToken ct)
    {
        if (!IsAdmin) return Denied<ContractTemplateDto>();
        if (!input.PreviewAcknowledged || input.IdempotencyKey == Guid.Empty || input.ExpectedPublicationGeneration < 0)
            return Result.Failure<ContractTemplateDto>(ContractTemplateErrors.Validation, "Confirm the current preview before publication.");
        long? expectedCurrent = null;
        if (input.ExpectedCurrentPublishedId is not null)
        {
            if (!long.TryParse(input.ExpectedCurrentPublishedId, out var parsed) || parsed <= 0) return Conflict<ContractTemplateDto>();
            expectedCurrent = parsed;
        }
        // Verify the private artifacts outside the publication transaction.
        var template = await db.OperatorContractTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (template is null) return Missing<ContractTemplateDto>();
        if (template.PublicationIdempotencyKey == input.IdempotencyKey)
        {
            if (template.DraftRevision != input.ExpectedRevision || template.SourceSha256 != input.ExpectedSourceSha256 ||
                template.PublicationExpectedGeneration != input.ExpectedPublicationGeneration || template.PublicationExpectedCurrentId != expectedCurrent)
                return Conflict<ContractTemplateDto>();
            return Result.Success(Dto(template, await Head(ct)));
        }
        if (template.Status != ContractTemplateStatus.Draft || !Matches(template, input.ExpectedRevision, input.RowVersion) ||
            template.SourceSha256 != input.ExpectedSourceSha256 || template.RenderState != ContractTemplateRenderState.Ready)
            return Conflict<ContractTemplateDto>();
        var source = await DownloadAsync(id, "source", ct); if (source.IsFailure) return Fail<ContractTemplateDto>(source);
        var previewDocument = await DownloadAsync(id, "preview", ct); if (previewDocument.IsFailure) return Fail<ContractTemplateDto>(previewDocument);
        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                await db.OperatorContractTemplates.Entry(template).ReloadAsync(token);
                if (template.Status != ContractTemplateStatus.Draft || !Matches(template, input.ExpectedRevision, input.RowVersion) ||
                    template.SourceSha256 != input.ExpectedSourceSha256 || !template.ValidationValid ||
                    template.RenderState != ContractTemplateRenderState.Ready || template.PreviewRevision != input.ExpectedRevision ||
                    template.PreviewStorageKey is null || template.PreviewSha256 is null)
                    return Conflict<ContractTemplateDto>();
                var head = await db.ContractTemplatePublicationHeads.SingleOrDefaultAsync(x => x.Id == 1, token);
                if (head is null) { head = ContractTemplatePublicationHead.Create(); db.ContractTemplatePublicationHeads.Add(head); }
                if (!head.TryAdvance(input.ExpectedPublicationGeneration, expectedCurrent, id)) return Conflict<ContractTemplateDto>();
                if (await db.OperatorContractTemplates.AnyAsync(x => x.Id != id && x.PublicationIdempotencyKey == input.IdempotencyKey, token))
                    return Conflict<ContractTemplateDto>();
                if (expectedCurrent is long previous)
                {
                    var old = await db.OperatorContractTemplates.SingleOrDefaultAsync(x => x.Id == previous, token);
                    if (old is null || !old.TryArchive(clock.UtcNow)) return Conflict<ContractTemplateDto>();
                    // Explicit ordering avoids SQL Server checking the filtered unique index before the old row is archived.
                    await db.SaveChangesAsync(token);
                }
                if (!template.TryPublish(input.ExpectedRevision, head.CurrentVersion, user.UserId!.Value, clock.UtcNow,
                    input.IdempotencyKey, input.ExpectedPublicationGeneration, expectedCurrent))
                    throw new InvalidOperationException("Validated template state changed during atomic publication.");
                Audit("ContractTemplatePublished", template);
                await db.SaveChangesAsync(token);
                return Result.Success(Dto(template, head));
            }, ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict<ContractTemplateDto>(); }
        catch (Exception exception) when (db.IsContractTemplatePublicationConflict(exception))
        { return Conflict<ContractTemplateDto>(); }
    }

    public async Task<bool> ProcessNextPreviewAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var exhausted = await db.ContractTemplateRenderJobs.Include(x => x.Template)
            .Where(x => x.Status == ContractTemplateRenderJobStatus.Processing &&
                x.AttemptCount >= 3 && x.LeaseExpiresAtUtc <= now)
            .OrderBy(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (exhausted is not null && exhausted.TryFailExhausted(now, 3, "contract_template.render_attempts_exhausted"))
        {
            exhausted.Template.TryFailPreview(exhausted.Revision, "contract_template.render_attempts_exhausted");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
            return true;
        }
        var job = await db.ContractTemplateRenderJobs.Include(x => x.Template)
            .Where(x => x.AttemptCount < 3 && (x.Status == ContractTemplateRenderJobStatus.Queued ||
                (x.Status == ContractTemplateRenderJobStatus.Processing && x.LeaseExpiresAtUtc <= now)))
            .OrderBy(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (job is null) return false;
        var claim = Guid.NewGuid();
        if (!job.TryClaim(claim, now, TimeSpan.FromMinutes(5))) return false;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return true; }
        var revision = job.Revision; var hash = job.SourceSha256;
        var source = await storage.ReadAsync(job.Template.SourceStorageKey, ct);
        Result<RenderedContractTemplate> rendered;
        if (source.IsFailure) rendered = Fail<RenderedContractTemplate>(source);
        else if (!string.Equals(Hash(source.Value), hash, StringComparison.OrdinalIgnoreCase)) rendered = Result.Failure<RenderedContractTemplate>(ContractTemplateDocumentErrors.InvalidDocument, "Document integrity check failed.");
        else
        {
            var values = new Dictionary<string, string>
            {
                ["contract_number"] = "PREVIEW-0001",
                ["contract_version"] = "PREVIEW",
                ["company_name"] = "DOANH NGHIEP MAU",
                ["tax_code"] = "0000000000",
                ["business_license_no"] = "MAU-0001",
                ["business_address"] = "DIA CHI MAU",
                ["representative_name"] = "NGUOI DAI DIEN MAU",
                ["representative_title"] = "Giam doc",
                ["representative_capacity"] = "Du lieu mau",
                ["operator_email"] = "preview@example.invalid",
                ["signature_block"] = "TRIPMATE_SIGNATURE_ANCHOR"
            };
            var prepared = await processor.PreparePreviewDocxAsync(new(job.Template.SourceFileName,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", source.Value), values, ct);
            rendered = prepared.IsFailure ? Fail<RenderedContractTemplate>(prepared) : await renderer.RenderPreviewAsync(prepared.Value, ct);
        }
        StoredContractTemplateArtifact? stored = null;
        string? failure = rendered.ErrorCode;
        if (rendered.IsSuccess)
        {
            if (!rendered.Value.SupportsFinalSignatureAnchors) failure = "contract_template.signature_anchor_unavailable";
            else if (!string.Equals(Hash(rendered.Value.PdfBytes), rendered.Value.PdfSha256, StringComparison.OrdinalIgnoreCase)) failure = ContractTemplateDocumentErrors.InvalidDocument;
            else
            {
                var artifact = await storage.StoreAsync(rendered.Value.PdfBytes, ct);
                if (artifact.IsFailure) failure = artifact.ErrorCode;
                else stored = artifact.Value;
            }
        }
        // Re-read after external I/O; another admin may have replaced the draft or a worker reclaimed the job.
        db.ContractTemplateRenderJobs.Entry(job).State = EntityState.Detached;
        db.OperatorContractTemplates.Entry(job.Template).State = EntityState.Detached;
        job = await db.ContractTemplateRenderJobs.Include(x => x.Template).SingleAsync(x => x.Id == job.Id, ct);
        if (!job.TryFinish(claim, stored is not null, failure ?? ContractTemplateDocumentErrors.RendererUnavailable, clock.UtcNow)) return true;
        var accepted = stored is not null && job.Template.TryCompletePreview(revision, hash, stored.StorageKey, stored.Sha256, clock.UtcNow);
        if (!accepted) job.Template.TryFailPreview(revision, failure ?? ContractTemplateErrors.Conflict);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return true; }
        // Unreferenced staged artifacts remain private; reference-aware cleanup can reclaim them.
        return true;
    }
}