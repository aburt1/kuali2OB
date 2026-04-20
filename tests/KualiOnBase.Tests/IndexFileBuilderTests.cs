using FluentAssertions;
using KualiOnBase.Api.Services.Import;

namespace KualiOnBase.Tests;

public class IndexFileBuilderTests
{
    [Fact]
    public void Build_SingleFile_ContainsMandatoryFieldsAndKeywords()
    {
        var output = IndexFileBuilder.Build(
            onbaseDocType: "IT - PeopleSoft Access Request Form",
            files: [new IndexFileEntry("0014_Ferguson.pdf", "0014_001933711")],
            keywords: [new("Department", "ITS")]);

        output.Should().Contain("ONBASE_DOC_TYPE: IT - PeopleSoft Access Request Form");
        output.Should().Contain("FILENAME: 0014_Ferguson.pdf");
        output.Should().Contain("EXTERNAL_SOURCE: KUALI BUILD");
        output.Should().Contain("EXTERNAL_SOURCE_REF: 0014_001933711");
        output.Should().Contain("Department: ITS");
    }

    [Fact]
    public void Build_MultipleFiles_EmitsBlockPerFileWithBlankSeparator()
    {
        var output = IndexFileBuilder.Build(
            onbaseDocType: "Doc",
            files:
            [
                new IndexFileEntry("a.pdf", "REF_1"),
                new IndexFileEntry("b.pdf", "REF_2"),
            ],
            keywords: []);

        var lines = output.Split(Environment.NewLine, StringSplitOptions.None);
        lines.Count(l => l.StartsWith("ONBASE_DOC_TYPE")).Should().Be(2);
        lines.Count(l => l.StartsWith("FILENAME")).Should().Be(2);
        output.Should().Contain("FILENAME: a.pdf");
        output.Should().Contain("FILENAME: b.pdf");
        output.Should().Contain("EXTERNAL_SOURCE_REF: REF_1");
        output.Should().Contain("EXTERNAL_SOURCE_REF: REF_2");
    }

    [Fact]
    public void Build_SkipsIncompleteKeywordPairs()
    {
        var output = IndexFileBuilder.Build(
            onbaseDocType: "Doc",
            files: [new IndexFileEntry("a.pdf", "REF")],
            keywords:
            [
                new("Department", "ITS"),
                new("Empty", ""),
                new("", "NoKey"),
                new("  ", "   "),
            ]);

        output.Should().Contain("Department: ITS");
        output.Should().NotContain("Empty:");
        output.Should().NotContain(": NoKey");
    }

    [Fact]
    public void Build_EmptyFiles_Throws()
    {
        var act = () => IndexFileBuilder.Build("Doc", [], []);
        act.Should().Throw<ArgumentException>();
    }
}
