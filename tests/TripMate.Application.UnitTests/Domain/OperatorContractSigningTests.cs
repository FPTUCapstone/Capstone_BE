using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class OperatorContractSigningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T00:00:00Z");
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Issued_contract_starts_unsigned_and_needs_confirmed_signer_details()
    {
        var contract = Create();
        Assert.Equal(OperatorContractStatus.PendingSignature, contract.Status);
        Assert.Equal(OperatorContractDocumentState.NeedsSignerDetails, contract.DocumentState);
        Assert.Null(contract.SignedDocumentStorageKey);
        Assert.Null(contract.CurrentSigningChallengeId);
    }

    [Fact]
    public void Preparation_requires_current_revision_and_signature_cannot_precede_durable_document()
    {
        var contract = Create();
        Assert.False(contract.TryStartPreparation(999, "Nguyễn Văn A", "Giám đốc", "Đại diện", Now));
        Assert.False(Sign(contract, Guid.NewGuid()));
        Assert.True(contract.TryStartPreparation(1, "Nguyễn Văn A", "Giám đốc", "Đại diện", Now));
        Assert.Equal(2, contract.Revision);
        var token = Guid.NewGuid();
        Assert.True(contract.TryClaimPreparation(token, Now, TimeSpan.FromMinutes(2)));
        Assert.False(contract.TryCompletePreparation(Guid.NewGuid(), 2, Hash, "unsigned", Hash, "[]", Now));
        Assert.True(contract.TryCompletePreparation(token, 2, Hash, "unsigned", Hash, "[]", Now));
        var challenge = Guid.NewGuid();
        Assert.True(contract.TryReserveChallenge(challenge, Now, TimeSpan.FromMinutes(1)));
        Assert.False(contract.TryReserveChallenge(Guid.NewGuid(), Now.AddSeconds(20), TimeSpan.FromMinutes(1)));
        Assert.False(Sign(contract, Guid.NewGuid()));
        Assert.True(Sign(contract, challenge));
        Assert.Equal(OperatorContractStatus.Signed, contract.Status);
        Assert.NotNull(contract.SignedAtUtc);
        Assert.False(Sign(contract, challenge));
        Assert.False(contract.TryStartPreparation(contract.Revision, "Other", "Other", "Other", Now));
        Assert.False(contract.TryVoid(Now));
    }

    [Fact]
    public void Expired_final_preparation_lease_is_terminal_and_old_worker_cannot_finish()
    {
        var contract = Create();
        contract.TryStartPreparation(1, "Name", "Title", "Capacity", Now);
        var token = Guid.Empty;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token = Guid.NewGuid();
            Assert.True(contract.TryClaimPreparation(token, Now.AddMinutes(attempt * 2), TimeSpan.FromMinutes(1)));
        }
        Assert.True(contract.TryFailExhaustedPreparation(Now.AddMinutes(6), 3, "contract.preparation_exhausted"));
        Assert.Equal(OperatorContractDocumentState.Failed, contract.DocumentState);
        Assert.False(contract.TryCompletePreparation(token, 2, Hash, "unsigned", Hash, "[]", Now.AddMinutes(6)));
    }

    [Fact]
    public void Challenge_must_be_delivered_unexpired_and_can_be_consumed_only_once()
    {
        var challenge = OperatorContractSigningChallenge.Create(Guid.NewGuid(), 3, 7, Hash, "protected", Now, TimeSpan.FromMinutes(5));
        Assert.False(challenge.TryConsume(Now));
        Assert.True(challenge.TryMarkSent(Now));
        Assert.True(challenge.TryConsume(Now.AddMinutes(1)));
        Assert.False(challenge.TryConsume(Now.AddMinutes(1)));
        var expired = OperatorContractSigningChallenge.Create(Guid.NewGuid(), 3, 7, Hash, "protected", Now, TimeSpan.FromMinutes(5));
        expired.TryMarkSent(Now);
        Assert.False(expired.TryConsume(Now.AddMinutes(5)));
    }

    [Fact]
    public void Five_wrong_attempts_exhaust_challenge()
    {
        var challenge = OperatorContractSigningChallenge.Create(Guid.NewGuid(), 3, 7, Hash, "protected", Now, TimeSpan.FromMinutes(5));
        challenge.TryMarkSent(Now);
        for (var attempt = 0; attempt < 5; attempt++) Assert.True(challenge.RegisterFailedAttempt(Now));
        Assert.Equal(OperatorContractChallengeStatus.Failed, challenge.Status);
        Assert.False(challenge.TryConsume(Now));
        Assert.False(challenge.RegisterFailedAttempt(Now));
    }

    [Fact]
    public void Reviewed_document_freezes_signer_identity_and_signing_binds_to_unsigned_pdf_hash()
    {
        var contract = Create();
        Assert.True(contract.TryStartPreparation(1, "Name", "Title", "Capacity", Now));
        var claim = Guid.NewGuid();
        contract.TryClaimPreparation(claim, Now, TimeSpan.FromMinutes(1));
        var snapshotHash = new string('b', 64);
        contract.TryCompletePreparation(claim, 2, snapshotHash, "unsigned", Hash, "[]", Now);
        Assert.False(contract.TryStartPreparation(2, "Changed", "Changed", "Changed", Now));
        var challenge = Guid.NewGuid();
        contract.TryReserveChallenge(challenge, Now, TimeSpan.FromMinutes(1));
        Assert.False(contract.TrySign(challenge, Guid.NewGuid(), Hash, snapshotHash, "signed", Hash, "signature", Hash, "v1", Now));
        Assert.True(Sign(contract, challenge));
    }

    [Fact]
    public void Only_pending_contract_account_can_activate_and_old_sessions_are_invalidated()
    {
        var user = new User { Role = UserRole.TourOperator, Status = AccountStatus.PendingContractSignature };
        Assert.True(user.TryActivateAfterContractSignature(Now));
        Assert.Equal(AccountStatus.Active, user.Status);
        Assert.Equal(1, user.SessionVersion);
        Assert.False(user.TryActivateAfterContractSignature(Now));
        foreach (var status in new[] { AccountStatus.Locked, AccountStatus.Inactive, AccountStatus.PendingApproval })
            Assert.False(new User { Role = UserRole.TourOperator, Status = status }.TryActivateAfterContractSignature(Now));
    }

    private static OperatorContract Create() => OperatorContract.Create(7, 1, 1, "source", Hash,
        "Partnership", "Example company", "0000000000", "LIC", "Address", "operator@example.invalid", 9, Now);

    private static bool Sign(OperatorContract contract, Guid challenge) => contract.TrySign(challenge,
        Guid.NewGuid(), Hash, Hash, "signed", Hash, "signature", Hash, "v1", Now);
}