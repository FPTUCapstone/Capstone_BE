using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts.Templates;

public sealed class DockerContractTemplateRendererTests
{
    [DockerRendererFact]
    public async Task RealEngine_RendersVietnamesePreviewAndResolvesSignatureCoordinates()
    {
        var upload = DocxContractTemplateProcessorTests.Upload("<w:r><w:t>{{contract_number}} {{company_name}} {{tax_code}} {{representative_name}} {{signature_block}}</w:t></w:r>");
        var values = new Dictionary<string, string>
        {
            ["contract_number"] = "PREVIEW-0001",
            ["company_name"] = "Công ty mẫu Đà Nẵng",
            ["tax_code"] = "0000000000",
            ["representative_name"] = "Nguyễn Văn Mẫu",
            ["signature_block"] = "unused"
        };
        var prepared = await new DocxContractTemplateProcessor().PreparePreviewDocxAsync(upload, values, default);
        prepared.IsSuccess.Should().BeTrue();
        var renderer = new DockerContractTemplateRenderer(Options.Create(new ContractTemplateRendererOptions
        {
            DockerExecutablePath = Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_DOCKER")!,
            DockerImage = "tripmate-contract-renderer:local"
        }));
        var result = await renderer.RenderPreviewAsync(prepared.Value, default);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.PdfBytes.Take(5).Should().Equal("%PDF-"u8.ToArray());
        result.Value.SignatureAnchors.Should().NotBeEmpty();
        result.Value.SignatureAnchors.Should().OnlyContain(anchor => anchor.X >= 0 && anchor.Y >= 0 && anchor.Width > 0 && anchor.Height > 0);
    }

    [Fact]
    public async Task MissingDockerConfiguration_FailsClosedWithoutSyntheticPdf()
    {
        var renderer = new DockerContractTemplateRenderer(Options.Create(new ContractTemplateRendererOptions()));
        var result = await renderer.RenderPreviewAsync([1], CancellationToken.None);
        result.ErrorCode.Should().Be(ContractTemplateDocumentErrors.RendererUnavailable);
    }

    [Fact]
    public void ContainerArguments_AlwaysIsolateNetworkAndBoundResources()
    {
        var arguments = DockerContractTemplateRenderer.Arguments("tripmate-render-test", "tripmate-contract-renderer:local", "C:/private/input", "C:/private/output");
        arguments.Should().ContainInOrder("--network", "none");
        arguments.Should().ContainInOrder("--memory", "768m");
        arguments.Should().ContainInOrder("--cpus", "1");
        arguments.Should().Contain("--read-only").And.Contain("--cap-drop=ALL").And.Contain("--security-opt=no-new-privileges");
        arguments.Should().NotContain(argument => argument.Contains("docker.sock", StringComparison.Ordinal) || argument == "--privileged" || argument == "-p");
    }
}

internal sealed class DockerRendererFactAttribute : FactAttribute
{
    public DockerRendererFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_DOCKER")))
            Skip = "Set TRIPMATE_RENDERER_DOCKER to an absolute Docker CLI path to verify the real isolated engine.";
    }
}