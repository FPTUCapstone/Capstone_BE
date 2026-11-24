using System.IO.Compression;
using System.Text;

using FluentAssertions;

using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts.Templates;

public sealed class DocxContractTemplateProcessorTests
{
    [Fact]
    public async Task Inspect_ExplainsExternalContentWithoutDisclosingItsTarget()
    {
        var upload = Upload(AllVariables(), ("word/_rels/document.xml.rels", "<Relationships><Relationship TargetMode=\"External\" Target=\"https://private.invalid/example\" /></Relationships>"));
        var result = await new DocxContractTemplateProcessor().InspectAsync(upload, default);
        result.ErrorCode.Should().Be(ContractTemplateDocumentErrors.InvalidDocument);
        result.ErrorMetadata.Should().ContainKey("validationIssue").WhoseValue.Should().Be("external_content_or_fields");
        result.ErrorMessage.Should().NotContain("private.invalid");
    }

    [Fact]
    public async Task Inspect_AcceptsSplitRunsAndReportsUniqueWhitelistedVariables()
    {
        var upload = Upload("<w:r><w:t>{{contract_</w:t></w:r><w:r><w:t>number}} {{company_name}} {{tax_code}} {{representative_name}} {{signature_block}}</w:t></w:r>");
        var result = await new DocxContractTemplateProcessor().InspectAsync(upload, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Variables.Should().BeEquivalentTo("contract_number", "company_name", "tax_code", "representative_name", "signature_block");
        result.Value.SourceSha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task Inspect_RejectsMissingAndUnknownVariablesWithSafeMetadata()
    {
        var result = await new DocxContractTemplateProcessor().InspectAsync(Upload("<w:r><w:t>{{company_name}} {{evil}}</w:t></w:r>"), CancellationToken.None);
        result.ErrorCode.Should().Be("contract_template.invalid_variables");
        result.ErrorMetadata.Keys.Should().Contain(["missingVariables", "unknownVariables"]);
    }

    [Theory]
    [InlineData("../../outside", "x")]
    [InlineData("word/vbaProject.bin", "macro")]
    [InlineData("word/embeddings/oleObject1.bin", "embedded")]
    [InlineData("word/_rels/document.xml.rels", "<Relationships><Relationship TargetMode=\"External\" Target=\"https://evil.invalid/x\" /></Relationships>")]
    public async Task Inspect_RejectsUnsafePackageEntries(string entry, string content)
    {
        var upload = Upload(AllVariables(), (entry, content));
        (await new DocxContractTemplateProcessor().InspectAsync(upload, CancellationToken.None)).ErrorCode.Should().Be("contract_template.invalid_document");
    }

    [Theory]
    [InlineData("template.docm", DocxContractTemplateProcessor.DocxContentType)]
    [InlineData("template.docx", "application/pdf")]
    public async Task Inspect_DoesNotTrustFileNameOrMime(string name, string type)
    {
        var upload = Upload(AllVariables()) with { FileName = name, ContentType = type };
        (await new DocxContractTemplateProcessor().InspectAsync(upload, CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Inspect_RejectsDtdAndExecutableWordFields()
    {
        var upload = Upload(AllVariables() + "<w:fldSimple w:instr=\"INCLUDETEXT file:///etc/passwd\" />");
        (await new DocxContractTemplateProcessor().InspectAsync(upload, CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Prepare_ReplacesSplitRunsWithoutDestroyingTheirFormattingAndAddsPreviewNotice()
    {
        var upload = Upload("<w:r><w:rPr><w:b/></w:rPr><w:t>{{company_</w:t></w:r><w:r><w:t>name}}</w:t></w:r>" + AllVariables());
        var values = new Dictionary<string, string> { ["company_name"] = "Công ty mẫu & đối tác", ["contract_number"] = "PREVIEW", ["tax_code"] = "0000000000", ["representative_name"] = "Người ký mẫu", ["signature_block"] = "SIGNATURE_ANCHOR" };
        var result = await new DocxContractTemplateProcessor().PreparePreviewDocxAsync(upload, values, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        using var archive = new ZipArchive(new MemoryStream(result.Value));
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        xml.Should().Contain("Công ty mẫu &amp; đối tác").And.Contain("<w:b").And.Contain("BẢN XEM TRƯỚC").And.NotContain("{{");
        archive.Entries.Should().Contain(entry => entry.FullName.StartsWith("word/headerTripMatePreview", StringComparison.Ordinal));
        xml.Should().Contain("headerReference");
    }

    [Fact]
    public async Task Inspect_RejectsDuplicateZipNamesAndMalformedTemplateDelimiters()
    {
        var duplicate = Upload(AllVariables(), ("word/document.xml", "duplicate"));
        (await new DocxContractTemplateProcessor().InspectAsync(duplicate, CancellationToken.None)).IsFailure.Should().BeTrue();
        (await new DocxContractTemplateProcessor().InspectAsync(Upload(AllVariables() + "<w:r><w:t>{{oops</w:t></w:r>"), CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    private static string AllVariables() => "<w:r><w:t>{{contract_number}} {{company_name}} {{tax_code}} {{representative_name}} {{signature_block}}</w:t></w:r>";

    internal static ContractTemplateUpload Upload(string runs, params (string Name, string Content)[] additional)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            Add(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add(archive, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add(archive, "word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>{runs}</w:p></w:body></w:document>");
            foreach (var item in additional) Add(archive, item.Name, item.Content);
        }
        return new("template.docx", DocxContractTemplateProcessor.DocxContentType, bytes.ToArray());
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}