using FluentAssertions;
using KualiOnBase.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace KualiOnBase.Tests;

public class KeywordExtractionTests
{
    [Fact]
    public void ExtractKeywords_PairsOnlyMatchedIndices()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["KeywordKey1"] = "Department",
            ["KeywordValue1"] = "ITS",
            ["KeywordKey2"] = "Term",
            ["KeywordValue2"] = "Fall 2026",
            ["KeywordKey3"] = "Orphan",
            ["KeywordValue4"] = "NoKey",
        });

        var pairs = ImportEndpoint.ExtractKeywords(query);

        pairs.Should().HaveCount(2);
        pairs[0].Should().Be(new KeyValuePair<string, string>("Department", "ITS"));
        pairs[1].Should().Be(new KeyValuePair<string, string>("Term", "Fall 2026"));
    }

    [Fact]
    public void ExtractKeywords_IgnoresWhitespaceOnly()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["KeywordKey1"] = "  ",
            ["KeywordValue1"] = "v",
            ["KeywordKey2"] = "k",
            ["KeywordValue2"] = "   ",
        });

        ImportEndpoint.ExtractKeywords(query).Should().BeEmpty();
    }
}
