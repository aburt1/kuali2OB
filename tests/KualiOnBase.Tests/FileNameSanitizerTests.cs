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
    public void BuildContentFileName_JoinsSegmentsWithUnderscores()
    {
        var name = FileNameSanitizer.BuildContentFileName(
            serialNumber: "0014",
            schoolId: "001933711",
            lastName: "Ferguson",
            firstName: "Jason",
            onbaseDocType: "IT - PeopleSoft Access Request Form",
            extension: "pdf");

        name.Should().Be("0014_001933711_Ferguson_Jason_IT - PeopleSoft Access Request Form.pdf");
    }

    [Fact]
    public void BuildContentFileName_SanitizesEachSegmentAndExtension()
    {
        var name = FileNameSanitizer.BuildContentFileName(
            serialNumber: "0014",
            schoolId: "001/933",
            lastName: "O:Malley",
            firstName: "Jay*",
            onbaseDocType: "IT/Access Request",
            extension: ".PDF");

        name.Should().Be("0014_001-933_O-Malley_Jay_IT-Access Request.pdf");
    }

    [Fact]
    public void BuildContentFileName_SkipsEmptySegments()
    {
        var name = FileNameSanitizer.BuildContentFileName("0014", "", "Doe", "", "DocType", "pdf");
        name.Should().Be("0014_Doe_DocType.pdf");
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
