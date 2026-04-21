using FluentAssertions;
using KualiOnBase.Api.Services.Import;

namespace KualiOnBase.Tests;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("IT/Access", "IT-Access")]
    [InlineData("IT:Access*?", "IT-Access")]
    [InlineData("a<b>c|d\"e", "a-b-c-d-e")]
    [InlineData("   spaced   ", "spaced")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Sanitize_StripsInvalidCharacters(string? input, string expected)
    {
        FileNameSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_AllInvalidChars_FallsBackToPlaceholder()
    {
        FileNameSanitizer.Sanitize("???").Should().Be("file");
    }

    [Fact]
    public void BuildContentFileName_UsesDocumentIdAsStem()
    {
        var name = FileNameSanitizer.BuildContentFileName(
            documentId: "69e79b2017521d02859ccdfa",
            extension: "pdf");

        name.Should().Be("69e79b2017521d02859ccdfa.pdf");
    }

    [Fact]
    public void BuildContentFileName_NormalizesExtensionCaseAndLeadingDot()
    {
        FileNameSanitizer.BuildContentFileName("doc-abc", ".PDF").Should().Be("doc-abc.pdf");
        FileNameSanitizer.BuildContentFileName("doc-abc", "DOCX").Should().Be("doc-abc.docx");
    }

    [Fact]
    public void BuildContentFileName_SanitizesDocumentIdDefensively()
    {
        // Kuali ids are hex today, but sanitize defensively so a surprise char
        // in a future id scheme can't produce an invalid Windows path.
        FileNameSanitizer.BuildContentFileName("abc/def:ghi", "pdf")
            .Should().Be("abc-def-ghi.pdf");
    }

    [Fact]
    public void BuildContentFileName_NoExtension_ReturnsStemOnly()
    {
        FileNameSanitizer.BuildContentFileName("abc123", "").Should().Be("abc123");
    }

    [Fact]
    public void MakeUnique_AppendsCounterOnCollision()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileNameSanitizer.MakeUnique("a.pdf", seen).Should().Be("a.pdf");
        FileNameSanitizer.MakeUnique("a.pdf", seen).Should().Be("a_2.pdf");
        FileNameSanitizer.MakeUnique("a.pdf", seen).Should().Be("a_3.pdf");
        FileNameSanitizer.MakeUnique("b", seen).Should().Be("b");
        FileNameSanitizer.MakeUnique("b", seen).Should().Be("b_2");
    }
}
