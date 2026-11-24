using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>Reads coordinates from pdftotext -bbox output produced for the specific PDF.</summary>
internal static class ContractTemplatePdfAnchors
{
    public const string Marker = "TRIPMATE_SIGNATURE_ANCHOR";

    public static IReadOnlyList<ContractTemplateSignatureAnchor> Parse(string boundingXml)
    {
        try
        {
            using var text = new StringReader(boundingXml);
            // Poppler emits an XHTML doctype. Ignore it without resolving external entities.
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            var pages = document.Descendants().Where(node => node.Name.LocalName == "page").ToArray();
            if (pages.Length is 0 or > 100) return [];
            var results = new List<ContractTemplateSignatureAnchor>();
            for (var index = 0; index < pages.Length; index++)
            {
                var page = pages[index];
                if (!Number(page, "width", out var width) || !Number(page, "height", out var height) || width <= 0 || height <= 0) return [];
                foreach (var word in page.Descendants().Where(node => node.Name.LocalName == "word" && node.Value == Marker))
                {
                    if (!Number(word, "xMin", out var x) || !Number(word, "yMin", out var y) || !Number(word, "xMax", out var right) || !Number(word, "yMax", out var bottom) || x < 0 || y < 0 || right <= x || bottom <= y || right > width || bottom > height) return [];
                    results.Add(new(index + 1, width, height, x, y, right - x, bottom - y));
                }
            }
            return results.Count is > 0 and <= 10 ? results : [];
        }
        catch (XmlException) { return []; }
    }

    private static bool Number(XElement element, string attribute, out double number) =>
        double.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}