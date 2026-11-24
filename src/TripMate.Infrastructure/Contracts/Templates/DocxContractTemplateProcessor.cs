using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>Inspects untrusted OOXML before any renderer receives it.</summary>
public sealed partial class DocxContractTemplateProcessor : IContractTemplateDocumentProcessor
{
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const int MaximumCompressedBytes = 10 * 1024 * 1024;
    public const long MaximumExpandedBytes = 50 * 1024 * 1024;
    public const int MaximumEntries = 2000;
    public const string PreviewNotice = "BẢN XEM TRƯỚC — KHÔNG DÙNG ĐỂ KÝ";
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly HashSet<string> Allowed = ["contract_number", "contract_version", "company_name", "tax_code", "business_license_no", "business_address", "representative_name", "representative_title", "representative_capacity", "operator_email", "signature_block"];
    private static readonly string[] Required = ["contract_number", "company_name", "tax_code", "representative_name", "signature_block"];

    public Task<Result<ContractTemplateInspection>> InspectAsync(ContractTemplateUpload upload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (upload.Bytes.LongLength > MaximumCompressedBytes)
            return Task.FromResult(Result<ContractTemplateInspection>.Failure(ContractTemplateDocumentErrors.DocumentTooLarge, "The DOCX exceeds the allowed size."));
        try
        {
            var package = ReadPackage(upload, cancellationToken);
            var variables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in package.Values)
            {
                foreach (var paragraph in document.Descendants(Word + "p"))
                {
                    var text = string.Concat(paragraph.Descendants(Word + "t").Select(node => node.Value));
                    foreach (Match match in VariablePattern().Matches(text)) variables.Add(match.Groups[1].Value);
                    var remainder = VariablePattern().Replace(text, string.Empty);
                    if (remainder.Contains("{{", StringComparison.Ordinal) || remainder.Contains("}}", StringComparison.Ordinal))
                        throw new InvalidDataException("Malformed variable delimiter.");
                }
            }
            var missing = Required.Except(variables, StringComparer.Ordinal).Order().ToArray();
            var unknown = variables.Except(Allowed, StringComparer.Ordinal).Order().ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
                return Task.FromResult(Result<ContractTemplateInspection>.Failure(ContractTemplateDocumentErrors.InvalidVariables,
                    "The template contains unsupported or missing variables.", new Dictionary<string, object?> { ["missingVariables"] = missing, ["unknownVariables"] = unknown }));
            return Task.FromResult(Result<ContractTemplateInspection>.Success(new(Convert.ToHexString(SHA256.HashData(upload.Bytes)).ToLowerInvariant(), variables.Order().ToArray())));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or ArgumentException or NotSupportedException or OverflowException)
        {
            var issue = exception.Message is "External content or executable fields are not supported." or "External relationship target."
                ? "external_content_or_fields" : "invalid_package";
            return Task.FromResult(Result<ContractTemplateInspection>.Failure(ContractTemplateDocumentErrors.InvalidDocument,
                "Choose a safe, valid DOCX document without embedded or external content.",
                new Dictionary<string, object?> { ["validationIssue"] = issue }));
        }
    }

    public Task<Result<byte[]>> PreparePreviewDocxAsync(ContractTemplateUpload upload, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        PrepareDocxAsync(upload, values, true, cancellationToken);

    public Task<Result<byte[]>> PrepareContractDocxAsync(ContractTemplateUpload upload, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        PrepareDocxAsync(upload, values, false, cancellationToken);

    private async Task<Result<byte[]>> PrepareDocxAsync(ContractTemplateUpload upload, IReadOnlyDictionary<string, string> values, bool preview, CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(upload, cancellationToken);
        if (inspection.IsFailure) return Result<byte[]>.Failure(inspection.ErrorCode!, inspection.ErrorMessage!, inspection.ErrorMetadata);
        if (inspection.Value.Variables.Any(variable => !values.ContainsKey(variable)))
            return Result<byte[]>.Failure(ContractTemplateDocumentErrors.InvalidVariables, "Preview values are incomplete.");

        var documents = ReadPackage(upload, cancellationToken);
        var previewValues = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["signature_block"] = ContractTemplatePdfAnchors.Marker,
        };
        foreach (var document in documents.Values)
        {
            foreach (var paragraph in document.Descendants(Word + "p"))
            {
                if (!preview && string.Concat(paragraph.Descendants(Word + "t").Select(node => node.Value)).Contains("{{signature_block}}", StringComparison.Ordinal))
                {
                    var properties = paragraph.Element(Word + "pPr");
                    if (properties is null) { properties = new XElement(Word + "pPr"); paragraph.AddFirst(properties); }
                    var spacing = properties.Element(Word + "spacing");
                    if (spacing is null) { spacing = new XElement(Word + "spacing"); properties.Add(spacing); }
                    var after = int.TryParse((string?)spacing.Attribute(Word + "after"), out var existing) ? Math.Max(existing, 1200) : 1200;
                    spacing.SetAttributeValue(Word + "after", after);
                }
                ReplaceVariables(paragraph, previewValues);
            }
        }
        var main = documents["word/document.xml"];
        var body = main.Root!.Element(Word + "body")!;
        if (preview)
        {
            body.AddFirst(new XElement(Word + "p", new XElement(Word + "r", new XElement(Word + "rPr", new XElement(Word + "b")), new XElement(Word + "t", PreviewNotice))));
            AddPreviewHeaders(documents, body);
        }
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(upload.Bytes), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var entry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var destination = target.CreateEntry(entry.FullName).Open();
                if (documents.TryGetValue(entry.FullName, out var document)) document.Save(destination, SaveOptions.DisableFormatting);
                else { using var original = entry.Open(); await original.CopyToAsync(destination, cancellationToken); }
            }
            var existing = source.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var added in documents.Where(document => !existing.Contains(document.Key)))
            {
                using var destination = target.CreateEntry(added.Key).Open();
                added.Value.Save(destination, SaveOptions.DisableFormatting);
            }
        }
        return Result<byte[]>.Success(output.ToArray());
    }

    private static Dictionary<string, XDocument> ReadPackage(ContractTemplateUpload upload, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(upload.FileName), ".docx", StringComparison.OrdinalIgnoreCase) || upload.ContentType != DocxContentType || upload.Bytes.Length == 0)
            throw new InvalidDataException("Unsupported document.");
        using var archive = new ZipArchive(new MemoryStream(upload.Bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("Too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!names.Add(name) || name.Contains('\\') || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or "..") || name.Contains("vbaproject", StringComparison.OrdinalIgnoreCase) || name.Contains("embeddings/", StringComparison.OrdinalIgnoreCase) || name.Contains("activex/", StringComparison.OrdinalIgnoreCase) || name.Contains("customui/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe package path or object.");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes) throw new InvalidDataException("Expanded package too large.");
            using var input = entry.Open();
            using var expanded = new MemoryStream();
            var buffer = new byte[8192];
            long actual = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual += read;
                if (actual > entry.Length || actual > MaximumExpandedBytes) throw new InvalidDataException("Invalid expanded size.");
                expanded.Write(buffer, 0, read);
            }
            if (actual != entry.Length) throw new InvalidDataException("Invalid entry size.");
            if (!(name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))) continue;
            expanded.Position = 0;
            using var reader = XmlReader.Create(expanded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumExpandedBytes });
            var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (xml.Descendants().Any(element => element.Name.LocalName is "altChunk" or "object" or "oleObject" or "OLEObject" or "fldSimple" or "instrText" || element.Attributes().Any(attribute => attribute.Name.LocalName == "TargetMode" && attribute.Value.Equals("External", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("External content or executable fields are not supported.");
            if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) && xml.Descendants().Any(element => element.Name.LocalName == "Relationship" && element.Attributes().Any(attribute => attribute.Name.LocalName == "Target" && (attribute.Value.Contains(':') || attribute.Value.Contains('\\') || attribute.Value.StartsWith("//", StringComparison.Ordinal)))))
                throw new InvalidDataException("External relationship target.");
            if (name == "[Content_Types].xml" && xml.Descendants().Any(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "ContentType" && (attribute.Value.Contains("macro", StringComparison.OrdinalIgnoreCase) || attribute.Value.Contains("oleObject", StringComparison.OrdinalIgnoreCase)))))
                throw new InvalidDataException("Unsafe content type.");
            documents[name] = xml;
        }
        if (!documents.TryGetValue("word/document.xml", out var main) || main.Root?.Name != Word + "document" || main.Root.Element(Word + "body") is null || !documents.ContainsKey("[Content_Types].xml") || !documents.ContainsKey("_rels/.rels"))
            throw new InvalidDataException("Missing OOXML structure.");
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        if (documents["[Content_Types].xml"].Root?.Name != types + "Types" ||
            !documents["[Content_Types].xml"].Descendants(types + "Override").Any(element => (string?)element.Attribute("PartName") == "/word/document.xml" && (string?)element.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml") ||
            !documents["_rels/.rels"].Descendants(relationships + "Relationship").Any(element => (string?)element.Attribute("Type") == "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" && (string?)element.Attribute("Target") == "word/document.xml"))
            throw new InvalidDataException("Invalid OOXML document type or relationship.");
        return documents;
    }

    private static void ReplaceVariables(XElement paragraph, IReadOnlyDictionary<string, string> values)
    {
        var nodes = paragraph.Descendants(Word + "t").ToArray();
        var original = string.Concat(nodes.Select(node => node.Value));
        var starts = new int[nodes.Length];
        for (var i = 1; i < nodes.Length; i++) starts[i] = starts[i - 1] + nodes[i - 1].Value.Length;
        foreach (Match match in VariablePattern().Matches(original).Cast<Match>().Reverse())
        {
            var first = Array.FindLastIndex(starts, start => start <= match.Index);
            var last = Array.FindLastIndex(starts, start => start < match.Index + match.Length);
            var prefix = nodes[first].Value[..(match.Index - starts[first])];
            var suffix = nodes[last].Value[(match.Index + match.Length - starts[last])..];
            nodes[first].Value = prefix + values[match.Groups[1].Value] + (first == last ? suffix : string.Empty);
            nodes[first].SetAttributeValue(XNamespace.Xml + "space", "preserve");
            for (var i = first + 1; i <= last; i++) nodes[i].Value = i == last ? suffix : string.Empty;
        }
    }

    private static void AddPreviewHeaders(Dictionary<string, XDocument> documents, XElement body)
    {
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace documentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        static XElement Notice() => new(Word + "p", new XElement(Word + "r", new XElement(Word + "rPr", new XElement(Word + "b")), new XElement(Word + "t", PreviewNotice)));
        foreach (var header in documents.Values.Where(document => document.Root?.Name == Word + "hdr")) header.Root!.AddFirst(Notice());
        var relationshipPart = "word/_rels/document.xml.rels";
        if (!documents.TryGetValue(relationshipPart, out var relationships))
        {
            relationships = new XDocument(new XElement(packageRelationships + "Relationships"));
            documents.Add(relationshipPart, relationships);
        }
        var index = 1;
        while (documents.ContainsKey($"word/headerTripMatePreview{index}.xml")) index++;
        var headerPath = $"word/headerTripMatePreview{index}.xml";
        var relationshipId = $"rIdTripMatePreview{index}";
        while (relationships.Descendants(packageRelationships + "Relationship").Any(element => (string?)element.Attribute("Id") == relationshipId)) relationshipId += "x";
        documents.Add(headerPath, new XDocument(new XElement(Word + "hdr", Notice())));
        relationships.Root!.Add(new XElement(packageRelationships + "Relationship", new XAttribute("Id", relationshipId), new XAttribute("Type", documentRelationships.NamespaceName + "/header"), new XAttribute("Target", Path.GetFileName(headerPath))));
        documents["[Content_Types].xml"].Root!.Add(new XElement(contentTypes + "Override", new XAttribute("PartName", "/" + headerPath), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml")));
        var sections = body.Descendants(Word + "sectPr").ToArray();
        if (sections.Length == 0)
        {
            var section = new XElement(Word + "sectPr");
            body.Add(section);
            sections = [section];
        }
        var inherited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            foreach (var kind in new[] { "default", "first", "even" })
            {
                if (section.Elements(Word + "headerReference").Any(reference => (string?)reference.Attribute(Word + "type") == kind)) inherited.Add(kind);
                else if (!inherited.Contains(kind))
                {
                    section.AddFirst(new XElement(Word + "headerReference", new XAttribute(Word + "type", kind), new XAttribute(documentRelationships + "id", relationshipId)));
                    inherited.Add(kind);
                }
            }
        }
    }

    [GeneratedRegex(@"\{\{([a-z_][a-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}