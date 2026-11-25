using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class OperatorContractTemplateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-23T03:00:00Z");
    private static readonly string SourceHash = new('a', 64);
    private static readonly string PreviewHash = new('b', 64);

    private static OperatorContractTemplate Draft() => OperatorContractTemplate.CreateDraft(
        "Partnership", "source/key", SourceHash, "[\"company_name\"]", true, 9, Now);

    [Fact]
    public void NewDraft_HasNoPublishedVersionOrPreview()
    {
        var template = Draft();
        template.Status.Should().Be(ContractTemplateStatus.Draft);
        template.DraftRevision.Should().Be(1);
        template.Version.Should().BeNull();
        template.RenderState.Should().Be(ContractTemplateRenderState.NotRequested);
        template.TryPublish(1, 1, 9, Now).Should().BeFalse();
    }

    [Fact]
    public void FileChange_InvalidatesPreview_AndLateJobCannotOverwriteNewRevision()
    {
        var template = Draft();
        template.RequestPreview(1).Should().BeTrue();
        template.TryUpdateDraft(1, "Updated", "source/new", new string('c', 64), "[]", true, 9, Now).Should().BeTrue();
        template.DraftRevision.Should().Be(2);
        template.RenderState.Should().Be(ContractTemplateRenderState.NotRequested);
        template.TryCompletePreview(1, SourceHash, "preview/old", PreviewHash, Now).Should().BeFalse();
        template.PreviewStorageKey.Should().BeNull();
    }

    [Fact]
    public void PublishedAndArchivedContent_IsImmutable()
    {
        var template = Draft();
        template.RequestPreview(1).Should().BeTrue();
        template.TryCompletePreview(1, SourceHash, "preview/key", PreviewHash, Now).Should().BeTrue();
        template.TryPublish(1, 4, 9, Now).Should().BeTrue();
        template.Version.Should().Be(4);
        template.TryUpdateDraft(1, "Altered", "other", SourceHash, "[]", true, 9, Now).Should().BeFalse();
        template.RequestPreview(1).Should().BeFalse();
        template.TryArchive(Now).Should().BeTrue();
        template.Status.Should().Be(ContractTemplateStatus.Archived);
        template.TryPublish(1, 5, 9, Now).Should().BeFalse();
        template.SourceStorageKey.Should().Be("source/key");
    }

    [Fact]
    public void PreviewResult_MustMatchBothRevisionAndSourceHash()
    {
        var template = Draft();
        template.RequestPreview(1).Should().BeTrue();
        template.TryCompletePreview(1, new string('c', 64), "preview/key", PreviewHash, Now).Should().BeFalse();
        template.TryPublish(1, 1, 9, Now).Should().BeFalse();
    }

    [Fact]
    public void InvalidVariables_CannotRequestPreviewOrPublish()
    {
        var template = OperatorContractTemplate.CreateDraft("Test", "source", SourceHash, "[]", false, 9, Now);
        template.RequestPreview(1).Should().BeFalse();
        template.TryPublish(1, 1, 9, Now).Should().BeFalse();
    }

    [Fact]
    public void PublicationHead_RejectsAnotherDraftPublishingAgainstAnOldGeneration()
    {
        var head = ContractTemplatePublicationHead.Create();
        head.TryAdvance(0, null, 101).Should().BeTrue();
        head.CurrentPublishedTemplateId.Should().Be(101);
        head.CurrentVersion.Should().Be(1);
        head.TryAdvance(0, null, 102).Should().BeFalse();
        head.TryAdvance(1, 101, 102).Should().BeTrue();
        head.Generation.Should().Be(2);
        head.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public void RenderJob_LeaseProtectsCompletion_AndExpiredLeaseCanBeReclaimed()
    {
        var template = Draft();
        var job = ContractTemplateRenderJob.Create(template, Now);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        job.TryClaim(first, Now, TimeSpan.FromMinutes(2)).Should().BeTrue();
        job.TryClaim(second, Now.AddMinutes(1), TimeSpan.FromMinutes(2)).Should().BeFalse();
        job.TryClaim(second, Now.AddMinutes(3), TimeSpan.FromMinutes(2)).Should().BeTrue();
        job.TryFinish(first, true, null, Now.AddMinutes(3)).Should().BeFalse();
        job.TryFinish(second, true, null, Now.AddMinutes(3)).Should().BeTrue();
        job.AttemptCount.Should().Be(2);
        job.TryRequeue().Should().BeTrue();
        job.Status.Should().Be(ContractTemplateRenderJobStatus.Queued);
        job.TryRequeue().Should().BeFalse();
    }

    [Fact]
    public void RenderJob_ExhaustedExpiredLease_FailsWithoutLeavingTemplateProcessingForever()
    {
        var job = ContractTemplateRenderJob.Create(Draft(), Now);
        job.TryClaim(Guid.NewGuid(), Now, TimeSpan.FromMinutes(1)).Should().BeTrue();
        job.TryFailExhausted(Now, 1, "render_attempts_exhausted").Should().BeFalse();
        job.TryFailExhausted(Now.AddMinutes(2), 2, "render_attempts_exhausted").Should().BeFalse();
        job.TryFailExhausted(Now.AddMinutes(2), 1, "render_attempts_exhausted").Should().BeTrue();
        job.Status.Should().Be(ContractTemplateRenderJobStatus.Failed);
        job.ClaimToken.Should().BeNull();
        job.LeaseExpiresAtUtc.Should().BeNull();
        job.TryClaim(Guid.NewGuid(), Now.AddMinutes(3), TimeSpan.FromMinutes(1)).Should().BeFalse();
    }
}