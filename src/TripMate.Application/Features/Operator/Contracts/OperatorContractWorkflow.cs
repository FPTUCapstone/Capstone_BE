using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Contracts;

public sealed class OperatorContractWorkflow(
    IApplicationDbContext db, ICurrentUserService caller, IDateTimeProvider clock, IOptions<OperatorContractOptions> options,
    IContractTemplateArtifactStorage storage, IOperatorContractDocumentComposer composer, IContractTemplatePdfRenderer renderer,
    IContractSignatureImageInspector inspector, IContractPdfSignatureStamper stamper, IOperatorContractOtpProtector protector,
    IOperatorContractOtpEmailSender email, IOtpCodeGenerator codes, TimeProvider? securityTime = null)
{
    private DateTimeOffset SecurityNow => (securityTime ?? TimeProvider.System).GetUtcNow();
    private bool CallerAllowed => caller.Role == nameof(UserRole.TourOperator) && caller.UserId is > 0;
    private static Result<T> Failure<T>(string code, string message) => Result.Failure<T>(code, message);
    private static Result<T> Conflict<T>() => Failure<T>("contract.conflict", "The contract changed. Reload it before continuing.");
    private static Result<T> Missing<T>() => Failure<T>("contract.not_found", "No contract is available for this account.");
    private static Result<T> Copy<T>(Result result) => Result.Failure<T>(result.ErrorCode!, result.ErrorMessage!, result.ErrorMetadata);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool SameHash(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Matches(OperatorContract contract, string rowVersion) => Convert.ToBase64String(contract.RowVersion) == rowVersion;

    private async Task<Result<User>> Owner(CancellationToken ct)
    {
        if (!CallerAllowed) return Failure<User>("contract.forbidden", "Only the contract owner may continue.");
        if (!options.Value.Enabled) return Failure<User>("contract.unavailable", "Contract signing is not enabled.");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == caller.UserId, ct);
        if (user is null || user.Role != UserRole.TourOperator) return Failure<User>("contract.forbidden", "Only the contract owner may continue.");
        if (user.Status is not (AccountStatus.PendingContractSignature or AccountStatus.Active)
            || user.EmailVerifiedAtUtc is not { } verified || verified < user.CreatedAtUtc || verified > clock.UtcNow)
            return Failure<User>("contract.forbidden", "The account cannot sign or view this contract in its current state.");
        if (!await db.OperatorProfiles.AnyAsync(x => x.UserId == user.Id && x.ApprovalStatus == OperatorApprovalStatus.Approved, ct))
            return Failure<User>("contract.forbidden", "The operator application has not been approved.");
        return Result.Success(user);
    }

    private OperatorContractDto Dto(OperatorContract contract, User owner) => new(
        contract.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), contract.Status.ToString(), contract.DocumentState.ToString(),
        contract.Title, contract.TemplateVersion, contract.CompanyName, contract.TaxCode, contract.BusinessLicenseNo, contract.BusinessAddress,
        contract.OperatorEmail, contract.SignerName, contract.SignerTitle, contract.SignerCapacity,
        contract.UnsignedDocumentSha256, contract.UnsignedDocumentSha256, contract.SignedDocumentSha256, Convert.ToBase64String(contract.RowVersion),
        owner.Status == AccountStatus.PendingContractSignature && contract.Status == OperatorContractStatus.PendingSignature &&
            contract.DocumentState is OperatorContractDocumentState.NeedsSignerDetails or OperatorContractDocumentState.Failed,
        owner.Status == AccountStatus.PendingContractSignature && contract.Status == OperatorContractStatus.PendingSignature && contract.DocumentState == OperatorContractDocumentState.Ready,
        contract.ApprovedAtUtc, contract.SignedAtUtc, contract.PreparationErrorCode,
        new((int)OperatorContractSigningPolicy.OtpTimeToLive.TotalSeconds, (int)OperatorContractSigningPolicy.ResendCooldown.TotalSeconds,
            OperatorContractSigningPolicy.MaximumFailedAttempts, OperatorContractSigningPolicy.ConsentVersion));

    public async Task<Result<OperatorContractDto>> GetCurrentAsync(CancellationToken ct)
    {
        var owner = await Owner(ct); if (owner.IsFailure) return Copy<OperatorContractDto>(owner);
        var contract = await db.OperatorContracts.AsNoTracking().Where(x => x.OperatorUserId == owner.Value.Id)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return contract is null ? Missing<OperatorContractDto>() : Result.Success(Dto(contract, owner.Value));
    }

    public async Task<Result<OperatorContractDto>> PrepareAsync(long id, PrepareOperatorContractInput input, CancellationToken ct)
    {
        var owner = await Owner(ct); if (owner.IsFailure) return Copy<OperatorContractDto>(owner);
        var contract = await db.OperatorContracts.SingleOrDefaultAsync(x => x.Id == id && x.OperatorUserId == owner.Value.Id, ct);
        if (contract is null) return Missing<OperatorContractDto>();
        if (owner.Value.Status != AccountStatus.PendingContractSignature || contract.Status != OperatorContractStatus.PendingSignature) return Conflict<OperatorContractDto>();
        if (string.IsNullOrWhiteSpace(input.SignerName) || input.SignerName.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(input.SignerTitle) || input.SignerTitle.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(input.SignerCapacity) || input.SignerCapacity.Trim().Length > 500)
            return Failure<OperatorContractDto>("contract.validation", "Confirm the signer's full name, title and authority to sign.");
        var sameIdentity = contract.SignerName == input.SignerName.Trim() && contract.SignerTitle == input.SignerTitle.Trim() && contract.SignerCapacity == input.SignerCapacity.Trim();
        if (contract.DocumentState is OperatorContractDocumentState.Processing or OperatorContractDocumentState.Ready)
            return sameIdentity ? Result.Success(Dto(contract, owner.Value)) : Conflict<OperatorContractDto>();
        if (!Matches(contract, input.RowVersion) || !contract.TryStartPreparation(contract.Revision, input.SignerName, input.SignerTitle, input.SignerCapacity, clock.UtcNow))
            return Conflict<OperatorContractDto>();
        Audit("OperatorContractPreparationRequested", contract, caller.UserId);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<OperatorContractDto>(); }
        return Result.Success(Dto(contract, owner.Value));
    }

    public async Task<Result<OperatorContractDocument>> DownloadAsync(long id, string kind, CancellationToken ct)
    {
        var owner = await Owner(ct); if (owner.IsFailure) return Copy<OperatorContractDocument>(owner);
        var contract = await db.OperatorContracts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OperatorUserId == owner.Value.Id, ct);
        if (contract is null) return Missing<OperatorContractDocument>();
        if (contract.Status == OperatorContractStatus.Voided) return Conflict<OperatorContractDocument>();
        var key = kind == "signed" && contract.Status == OperatorContractStatus.Signed ? contract.SignedDocumentStorageKey
            : kind == "unsigned" && contract.DocumentState == OperatorContractDocumentState.Ready ? contract.UnsignedDocumentStorageKey : null;
        var expected = kind == "signed" ? contract.SignedDocumentSha256 : contract.UnsignedDocumentSha256;
        if (key is null) return Missing<OperatorContractDocument>();
        var document = await storage.ReadAsync(key, ct); if (document.IsFailure) return Copy<OperatorContractDocument>(document);
        if (!SameHash(Hash(document.Value), expected)) return Failure<OperatorContractDocument>("contract.document_unavailable", "Document integrity could not be verified.");
        return Result.Success(new OperatorContractDocument(document.Value, $"operator-contract-{id}-{kind}.pdf"));
    }

    public async Task<Result<OperatorContractChallengeDto>> CreateChallengeAsync(long id, OperatorContractChallengeInput input, CancellationToken ct)
    {
        var owner = await Owner(ct); if (owner.IsFailure) return Copy<OperatorContractChallengeDto>(owner);
        var contract = await db.OperatorContracts.SingleOrDefaultAsync(x => x.Id == id && x.OperatorUserId == owner.Value.Id, ct);
        if (contract is null) return Missing<OperatorContractChallengeDto>();
        if (owner.Value.Status != AccountStatus.PendingContractSignature || contract.Status != OperatorContractStatus.PendingSignature ||
            contract.DocumentState != OperatorContractDocumentState.Ready || !SameHash(contract.UnsignedDocumentSha256, input.ExpectedContentSha256) ||
            !Matches(contract, input.RowVersion) || !string.Equals(owner.Value.Email, contract.OperatorEmail, StringComparison.OrdinalIgnoreCase))
            return Conflict<OperatorContractChallengeDto>();
        var now = SecurityNow;
        if (contract.LastOtpIssuedAtUtc is { } last && now < last.Add(OperatorContractSigningPolicy.ResendCooldown)) return Cooldown();
        var challengeId = Guid.NewGuid(); var otp = codes.Generate();
        string protectedOtp;
        try { protectedOtp = protector.Protect(otp, owner.Value.Id, contract.Id, contract.UnsignedDocumentSha256!, challengeId, now); }
        catch (InvalidOperationException) { return Failure<OperatorContractChallengeDto>("contract.unavailable", "Confirmation codes are temporarily unavailable."); }
        var challenge = OperatorContractSigningChallenge.Create(challengeId, contract.Id, owner.Value.Id, contract.UnsignedDocumentSha256!,
            protectedOtp, now, OperatorContractSigningPolicy.OtpTimeToLive);
        try
        {
            var reserved = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                await db.OperatorContracts.Entry(contract).ReloadAsync(token);
                if (!Matches(contract, input.RowVersion) || !SameHash(contract.UnsignedDocumentSha256, input.ExpectedContentSha256)) return false;
                var cutoff = now.AddMinutes(-15);
                if (await db.OperatorContractSigningChallenges.CountAsync(x => x.OperatorUserId == owner.Value.Id && x.CreatedAtUtc >= cutoff, token)
                    >= OperatorContractSigningPolicy.MaximumChallengesPerFifteenMinutes) return false;
                if (!contract.TryReserveChallenge(challengeId, now, OperatorContractSigningPolicy.ResendCooldown)) return false;
                db.OperatorContractSigningChallenges.Add(challenge);
                await db.SaveChangesAsync(token); return true;
            }, ct);
            if (!reserved) return Cooldown();
        }
        catch (DbUpdateConcurrencyException) { return Conflict<OperatorContractChallengeDto>(); }
        catch (Exception exception) when (db.IsDeadlock(exception)) { return Conflict<OperatorContractChallengeDto>(); }
        EmailDeliveryResult delivery;
        try { delivery = await email.SendAsync(contract.OperatorEmail, otp, contract.Id, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { delivery = EmailDeliveryResult.Unknown; }
        // The raw OTP exists only in this request. A crash leaves an unusable Sending challenge.
        var delivered = delivery.Status == EmailDeliveryStatus.Delivered && challenge.TryMarkSent(SecurityNow);
        if (!delivered) challenge.TryMarkDeliveryFailed(SecurityNow);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict<OperatorContractChallengeDto>(); }
        if (!delivered) return Failure<OperatorContractChallengeDto>("contract.otp_delivery_unavailable", "The confirmation email could not be delivered. Try again after the resend delay.");
        return Result.Success(new OperatorContractChallengeDto(challenge.Id, challenge.ExpiresAtUtc,
            challenge.CreatedAtUtc.Add(OperatorContractSigningPolicy.ResendCooldown), Convert.ToBase64String(contract.RowVersion), MaskEmail(contract.OperatorEmail)));
    }

    private static Result<OperatorContractChallengeDto> Cooldown() => Failure<OperatorContractChallengeDto>("contract.otp_cooldown", "Wait before requesting another confirmation code.");

    public async Task<Result<OperatorContractDto>> SignAsync(long id, SignOperatorContractInput input, CancellationToken ct)
    {
        var owner = await Owner(ct); if (owner.IsFailure) return Copy<OperatorContractDto>(owner);
        var contract = await db.OperatorContracts.SingleOrDefaultAsync(x => x.Id == id && x.OperatorUserId == owner.Value.Id, ct);
        if (contract is null) return Missing<OperatorContractDto>();
        if (!input.AcceptedTerms || input.ConsentVersion != OperatorContractSigningPolicy.ConsentVersion || input.IdempotencyKey == Guid.Empty ||
            input.ChallengeId == Guid.Empty || input.Otp is null || input.Otp.Length != 6 || input.Otp.Any(c => c is < '0' or > '9') || string.IsNullOrWhiteSpace(input.SignerName))
            return Failure<OperatorContractDto>("contract.validation", "Confirm the contract, signer and six-digit email code.");
        var inspected = inspector.Inspect(input.Signature); if (inspected.IsFailure) return Copy<OperatorContractDto>(inspected);
        var fingerprint = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            input.ExpectedContentSha256,
            Name = input.SignerName.Trim(),
            input.AcceptedTerms,
            input.ConsentVersion,
            input.ChallengeId,
            input.Otp,
            Signature = inspected.Value.SourceSha256
        })));
        if (contract.Status == OperatorContractStatus.Signed)
            return contract.SigningOperationId == input.IdempotencyKey && SameHash(contract.SigningRequestSha256, fingerprint)
                ? Result.Success(Dto(contract, owner.Value)) : Conflict<OperatorContractDto>();
        if (owner.Value.Status != AccountStatus.PendingContractSignature || contract.Status != OperatorContractStatus.PendingSignature ||
            contract.DocumentState != OperatorContractDocumentState.Ready || contract.SignerName != input.SignerName.Trim() ||
            !SameHash(contract.UnsignedDocumentSha256, input.ExpectedContentSha256) || !Matches(contract, input.RowVersion) || contract.CurrentSigningChallengeId != input.ChallengeId)
            return Conflict<OperatorContractDto>();
        var challenge = await db.OperatorContractSigningChallenges.SingleOrDefaultAsync(x => x.Id == input.ChallengeId && x.ContractId == id && x.OperatorUserId == owner.Value.Id, ct);
        if (challenge is null) return Failure<OperatorContractDto>("contract.otp_invalid", "The confirmation code is incorrect or no longer usable.");
        try
        {
            var verification = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                // Hold an update lock before comparing the secret, so parallel guesses cannot
                // test extra codes against a shared pre-increment attempt count.
                await db.LockOperatorContractChallengeAsync(challenge.Id, token);
                await db.OperatorContracts.Entry(contract).ReloadAsync(token);
                await db.OperatorContractSigningChallenges.Entry(challenge).ReloadAsync(token);
                if (contract.Status != OperatorContractStatus.PendingSignature || contract.CurrentSigningChallengeId != challenge.Id ||
                    !SameHash(contract.UnsignedDocumentSha256, input.ExpectedContentSha256))
                    return Result.Failure("contract.conflict", "The contract changed. Reload it before continuing.");
                var valid = ValidateChallenge(challenge, contract);
                if (valid.IsFailure) return valid;
                if (protector.Verify(input.Otp, owner.Value.Id, contract.Id, contract.UnsignedDocumentSha256!, challenge.Id, challenge.CreatedAtUtc, challenge.ProtectedOtp))
                    return Result.Success();
                challenge.RegisterFailedAttempt(SecurityNow, OperatorContractSigningPolicy.MaximumFailedAttempts);
                await db.SaveChangesAsync(token);
                return Result.Failure("contract.otp_invalid", "The confirmation code is incorrect or no longer usable.");
            }, ct);
            if (verification.IsFailure) return Copy<OperatorContractDto>(verification);
        }
        catch (DbUpdateConcurrencyException) { return Conflict<OperatorContractDto>(); }
        catch (Exception exception) when (db.IsDeadlock(exception)) { return Conflict<OperatorContractDto>(); }
        catch (InvalidOperationException) { return Failure<OperatorContractDto>("contract.unavailable", "Confirmation codes are temporarily unavailable."); }
        var source = await storage.ReadAsync(contract.UnsignedDocumentStorageKey!, ct); if (source.IsFailure) return Copy<OperatorContractDto>(source);
        if (!SameHash(Hash(source.Value), input.ExpectedContentSha256)) return Failure<OperatorContractDto>("contract.document_unavailable", "Document integrity could not be verified.");
        IReadOnlyList<ContractSignaturePlacement>? placements;
        try { placements = JsonSerializer.Deserialize<List<ContractSignaturePlacement>>(contract.SignatureAnchorsJson!); }
        catch (JsonException) { return Failure<OperatorContractDto>("contract.document_unavailable", "The contract signature placement is unavailable."); }
        var signed = stamper.Stamp(source.Value, inspected.Value, placements ?? []); if (signed.IsFailure) return Copy<OperatorContractDto>(signed);
        if (!SameHash(Hash(signed.Value.Bytes), signed.Value.Sha256)) return Failure<OperatorContractDto>("contract.document_unavailable", "Signed document integrity could not be verified.");
        var signatureFile = await storage.StoreAsync(inspected.Value.PngBytes, ct); if (signatureFile.IsFailure) return Copy<OperatorContractDto>(signatureFile);
        var pdfFile = await storage.StoreAsync(signed.Value.Bytes, ct); if (pdfFile.IsFailure) { await storage.DeleteAsync(signatureFile.Value.StorageKey, ct); return Copy<OperatorContractDto>(pdfFile); }
        if (!SameHash(signatureFile.Value.Sha256, inspected.Value.CanonicalSha256) || !SameHash(pdfFile.Value.Sha256, signed.Value.Sha256))
            return Failure<OperatorContractDto>("contract.document_unavailable", "Stored document integrity could not be verified.");
        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                await db.LockOperatorContractChallengeAsync(challenge.Id, token);
                await db.OperatorContracts.Entry(contract).ReloadAsync(token);
                await db.Users.Entry(owner.Value).ReloadAsync(token);
                await db.OperatorContractSigningChallenges.Entry(challenge).ReloadAsync(token);
                if (contract.Status == OperatorContractStatus.Signed)
                    return contract.SigningOperationId == input.IdempotencyKey && SameHash(contract.SigningRequestSha256, fingerprint)
                        ? Result.Success(Dto(contract, owner.Value)) : Conflict<OperatorContractDto>();
                if (!Matches(contract, input.RowVersion) || owner.Value.Status != AccountStatus.PendingContractSignature ||
                    owner.Value.EmailVerifiedAtUtc is not { } verified || verified < owner.Value.CreatedAtUtc || verified > clock.UtcNow ||
                    !string.Equals(owner.Value.Email, contract.OperatorEmail, StringComparison.OrdinalIgnoreCase) ||
                    contract.CurrentSigningChallengeId != input.ChallengeId || !SameHash(contract.UnsignedDocumentSha256, input.ExpectedContentSha256) ||
                    !await db.OperatorProfiles.AnyAsync(x => x.UserId == owner.Value.Id && x.ApprovalStatus == OperatorApprovalStatus.Approved, token))
                    return Conflict<OperatorContractDto>();
                var checkedChallenge = ValidateChallenge(challenge, contract);
                if (checkedChallenge.IsFailure) return Copy<OperatorContractDto>(checkedChallenge);
                if (!challenge.TryConsume(SecurityNow) || !contract.TrySign(challenge.Id, input.IdempotencyKey, fingerprint, input.ExpectedContentSha256,
                    pdfFile.Value.StorageKey, pdfFile.Value.Sha256, signatureFile.Value.StorageKey, signatureFile.Value.Sha256,
                    input.ConsentVersion, clock.UtcNow) || !owner.Value.TryActivateAfterContractSignature(clock.UtcNow))
                    throw new InvalidOperationException("Validated contract signing state changed inside its final transaction.");
                Audit("OperatorContractSigned", contract, owner.Value.Id);
                Audit("OperatorAccountActivatedAfterContract", contract, owner.Value.Id);
                await db.SaveChangesAsync(token);
                return Result.Success(Dto(contract, owner.Value));
            }, ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict<OperatorContractDto>(); }
        catch (Exception exception) when (db.IsOperatorContractUniqueConflict(exception)) { return Conflict<OperatorContractDto>(); }
        catch (Exception exception) when (db.IsDeadlock(exception)) { return Conflict<OperatorContractDto>(); }
        // Private artifacts are retained on uncertain commits; never delete an artifact that may be committed.
    }

    private Result ValidateChallenge(OperatorContractSigningChallenge? challenge, OperatorContract contract)
    {
        if (challenge is null || !SameHash(challenge.AcceptedContentSha256, contract.UnsignedDocumentSha256) || challenge.Status != OperatorContractChallengeStatus.Sent)
            return Result.Failure("contract.otp_invalid", "The confirmation code is incorrect or no longer usable.");
        return SecurityNow >= challenge.ExpiresAtUtc
            ? Result.Failure("contract.otp_expired", "The confirmation code has expired. Request a new code.") : Result.Success();
    }

    private void Audit(string action, OperatorContract contract, long? actor) => db.AuditLogs.Add(new AuditLog(actor, action,
        nameof(OperatorContract), contract.Id, clock.UtcNow, afterData: JsonSerializer.Serialize(new
        {
            contract.TemplateVersion,
            contract.UnsignedDocumentSha256,
            contract.SignedDocumentSha256,
            contract.Status,
            contract.ConsentVersion
        })));
    private static string MaskEmail(string value) { var index = value.IndexOf('@'); return index > 0 ? value[0] + "***" + value[index..] : "***"; }

    public async Task<bool> ProcessNextPreparationAsync(CancellationToken ct)
    {
        var now = SecurityNow;
        var exhausted = await db.OperatorContracts.Where(x => x.Status == OperatorContractStatus.PendingSignature && x.DocumentState == OperatorContractDocumentState.Processing &&
            x.PreparationAttempts >= 3 && x.PreparationLeaseExpiresAtUtc <= now).OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (exhausted is not null && exhausted.TryFailExhaustedPreparation(now, 3, "contract.preparation_attempts_exhausted"))
        { try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { } return true; }
        var contract = await db.OperatorContracts.Where(x => x.Status == OperatorContractStatus.PendingSignature && x.DocumentState == OperatorContractDocumentState.Processing &&
            x.PreparationAttempts < 3 && (x.PreparationLeaseExpiresAtUtc == null || x.PreparationLeaseExpiresAtUtc <= now))
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (contract is null) return false;
        var claim = Guid.NewGuid(); if (!contract.TryClaimPreparation(claim, now, TimeSpan.FromMinutes(5))) return true;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return true; }
        var revision = contract.Revision;
        var source = await storage.ReadAsync(contract.TemplateSourceStorageKey, ct);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_number"] = $"TM-OP-{contract.Id:D8}",
            ["contract_version"] = contract.TemplateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["company_name"] = contract.CompanyName,
            ["tax_code"] = contract.TaxCode,
            ["business_license_no"] = contract.BusinessLicenseNo,
            ["business_address"] = contract.BusinessAddress,
            ["representative_name"] = contract.SignerName,
            ["representative_title"] = contract.SignerTitle,
            ["representative_capacity"] = contract.SignerCapacity,
            ["operator_email"] = contract.OperatorEmail,
            ["signature_block"] = "TRIPMATE_SIGNATURE_ANCHOR"
        };
        var snapshot = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { contract.TemplateSourceSha256, Fields = values })));
        Result<RenderedContractTemplate> rendered;
        if (source.IsFailure) rendered = Copy<RenderedContractTemplate>(source);
        else if (!SameHash(Hash(source.Value), contract.TemplateSourceSha256)) rendered = Failure<RenderedContractTemplate>("contract.document_unavailable", "Source document integrity could not be verified.");
        else
        {
            var prepared = await composer.PrepareAsync(source.Value, "contract.docx", values, ct);
            rendered = prepared.IsFailure ? Copy<RenderedContractTemplate>(prepared) : await renderer.RenderPreviewAsync(prepared.Value, ct);
        }
        StoredContractTemplateArtifact? artifact = null;
        string? failure = rendered.ErrorCode;
        string anchorsJson = "[]";
        if (rendered.IsSuccess)
        {
            if (!rendered.Value.SupportsFinalSignatureAnchors || rendered.Value.SignatureAnchors.Count == 0 || !SameHash(Hash(rendered.Value.PdfBytes), rendered.Value.PdfSha256))
                failure = "contract.signature_placement_invalid";
            else
            {
                anchorsJson = JsonSerializer.Serialize(rendered.Value.SignatureAnchors.Select(x => new ContractSignaturePlacement(x.PageNumber, x.PageWidth, x.PageHeight, x.X, x.Y, x.Width, x.Height)));
                var stored = await storage.StoreAsync(rendered.Value.PdfBytes, ct);
                if (stored.IsFailure) failure = stored.ErrorCode;
                else if (!SameHash(stored.Value.Sha256, rendered.Value.PdfSha256)) failure = "contract.document_unavailable";
                else artifact = stored.Value;
            }
        }
        db.OperatorContracts.Entry(contract).State = EntityState.Detached;
        contract = await db.OperatorContracts.SingleAsync(x => x.Id == contract.Id, ct);
        var accepted = artifact is not null && contract.TryCompletePreparation(claim, revision, snapshot, artifact.StorageKey, artifact.Sha256, anchorsJson, SecurityNow);
        if (!accepted) contract.TryFailPreparation(claim, revision, failure ?? "contract.conflict", SecurityNow);
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return true; }
        return true;
    }
}