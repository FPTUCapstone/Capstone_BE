using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts.Templates;

public sealed class LibreOfficeContractTemplateRendererTests
{
    [Fact]
    public void AnchorParser_ResolvesMarkerAgainstActualPdfPageDimensions()
    {
        var xml = "<html><body><doc><page width=\"595\" height=\"842\"><word xMin=\"50\" yMin=\"700\" xMax=\"240\" yMax=\"715\">TRIPMATE_SIGNATURE_ANCHOR</word></page></doc></body></html>";
        var anchors = ContractTemplatePdfAnchors.Parse(xml);
        anchors.Should().ContainSingle();
        anchors[0].PageNumber.Should().Be(1);
        anchors[0].X.Should().Be(50);
        anchors[0].Y.Should().Be(700);
    }

    [Theory]
    [InlineData("<html><page width=\"595\" height=\"842\"><word xMin=\"-1\" yMin=\"700\" xMax=\"240\" yMax=\"715\">TRIPMATE_SIGNATURE_ANCHOR</word></page></html>")]
    [InlineData("<html><page width=\"595\" height=\"842\"><word xMin=\"50\" yMin=\"900\" xMax=\"240\" yMax=\"915\">TRIPMATE_SIGNATURE_ANCHOR</word></page></html>")]
    [InlineData("<html><page width=\"595\" height=\"842\"><word xMin=\"50\" yMin=\"700\" xMax=\"240\" yMax=\"715\">Other text</word></page></html>")]
    public void AnchorParser_RejectsMissingOrOutOfPageMarkers(string xml) =>
        ContractTemplatePdfAnchors.Parse(xml).Should().BeEmpty();

    [Fact]
    public async Task MissingEngine_FailsClosedWithoutSyntheticPdfOrAnchorCapability()
    {
        var renderer = new LibreOfficeContractTemplateRenderer(Options.Create(new ContractTemplateRendererOptions()));
        var result = await renderer.RenderPreviewAsync([1, 2, 3], CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ContractTemplateDocumentErrors.RendererUnavailable);
    }
}