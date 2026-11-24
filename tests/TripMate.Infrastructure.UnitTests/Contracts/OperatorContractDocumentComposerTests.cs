using System.IO.Compression;
using System.Xml.Linq;

using FluentAssertions;

using TripMate.Infrastructure.Contracts.Templates;
using TripMate.Infrastructure.UnitTests.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts;

public sealed class OperatorContractDocumentComposerTests
{
    [Fact]
    public async Task FinalDocument_UsesRealSnapshotAndReservesSignatureSpaceWithoutPreviewNotice()
    {
        var upload = DocxContractTemplateProcessorTests.Upload("<w:r><w:t>{{contract_number}} {{company_name}} {{tax_code}} {{representative_name}}</w:t></w:r></w:p><w:p><w:r><w:t>{{signature_block}}</w:t></w:r>");
        var values = new Dictionary<string, string>
        {
            ["contract_number"] = "OP-42",
            ["company_name"] = "Đối tác thử",
            ["tax_code"] = "0123456789",
            ["representative_name"] = "Nguyễn Văn A",
            ["signature_block"] = "unused"
        };
        var result = await new DocxContractTemplateProcessor().PrepareContractDocxAsync(upload, values, default);
        result.IsSuccess.Should().BeTrue();
        using var zip = new ZipArchive(new MemoryStream(result.Value));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        xml.Should().Contain("Đối tác thử").And.Contain("TRIPMATE_SIGNATURE_ANCHOR").And.NotContain("BẢN XEM TRƯỚC");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var signature = XDocument.Parse(xml).Descendants(w + "p").Single(p => p.Value.Contains("TRIPMATE_SIGNATURE_ANCHOR"));
        signature.Element(w + "pPr")!.Element(w + "spacing")!.Attribute(w + "after")!.Value.Should().Be("1200");
    }
}