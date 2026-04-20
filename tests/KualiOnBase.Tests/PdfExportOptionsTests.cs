using FluentAssertions;
using KualiOnBase.Api.Models;

namespace KualiOnBase.Tests;

public class PdfExportOptionsTests
{
    [Theory]
    [InlineData("form", "Form")]
    [InlineData("Form", "Form")]
    [InlineData("FORM", "Form")]
    [InlineData("combined", "Combined")]
    [InlineData("Combined", "Combined")]
    [InlineData("attachments", "Attachments")]
    [InlineData("Attachments", "Attachments")]
    [InlineData("  combined  ", "Combined")]
    public void TryNormalize_AcceptsCanonicalAndLooseCasing(string input, string expected)
    {
        PdfExportOptions.TryNormalize(input, out var canonical).Should().BeTrue();
        canonical.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_TreatsNullAndEmptyAsUnset(string? input)
    {
        PdfExportOptions.TryNormalize(input, out var canonical).Should().BeTrue();
        canonical.Should().BeNull();
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("form+attachments")]
    [InlineData("junk")]
    public void TryNormalize_RejectsUnknown(string input)
    {
        PdfExportOptions.TryNormalize(input, out var canonical).Should().BeFalse();
        canonical.Should().BeNull();
    }
}
