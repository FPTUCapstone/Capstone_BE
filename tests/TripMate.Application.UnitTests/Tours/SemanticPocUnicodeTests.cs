using FluentAssertions;

using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Application.UnitTests.Tours;

public sealed class SemanticPocUnicodeTests
{
    [Fact]
    public void Fingerprint_UsesTheSameUtf8FramingAsPythonForAstralCombiningAndControlText()
    {
        var document = new SemanticTourDocument(1, "Tour \U0001f600\u2028a\u0301\n",
            ["\U0001f600", "\ue000", "Đà Nẵng", "Đà Nẵng"], "<Op>&\t\"", true);

        // Independently generated with Python hashlib/unicodedata using NFC and UTF-8-byte sorting.
        document.Fingerprint.Should().Be("a131db8d80d13dcb3245281124e62e3390b5263a6ebc2eaa4ee2ae406811c6eb");
        document.Text.Should().Be("Title: 15:Tour \U0001f600\u2028á\n\nDestinations: 11:Đà Nẵng3:\ue0004:\U0001f600\nOperator: 7:<Op>&\t\"");
    }
}