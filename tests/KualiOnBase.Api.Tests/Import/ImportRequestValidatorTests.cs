using KualiOnBase.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace KualiOnBase.Api.Tests.Import;

public sealed class ImportRequestValidatorTests
{
    private static readonly string[] Roots = OperatingSystem.IsWindows()
        ? new[] { @"C:\onbase\drop" }
        : new[] { "/onbase/drop" };

    private static string Root => Roots[0];

    private static IQueryCollection Query(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) dict[key] = value;
        return new QueryCollection(dict);
    }

    private static (string, string)[] ValidRequest() =>
    [
        ("documentId", "6a73c32cf8d95f02a01e422b"),
        ("onbaseDocType", "IT - Access"),
        ("targetFolderPath", Root),
        ("downloadMode", "pdf"),
        ("deleteAttachments", "false"),
    ];

    private static (string, string)[] With(params (string, string)[] extra)
        => [.. ValidRequest(), .. extra];

    private static (string, string)[] Replacing(string key, string value)
        => [.. ValidRequest().Where(p => p.Item1 != key), (key, value)];

    [Fact]
    public void Validate_AcceptsAWellFormedRequest()
    {
        var result = ImportRequestValidator.Validate(Query(ValidRequest()), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.False(result.DeleteAttachments);
        Assert.False(result.DeleteDocument);
    }

    [Fact]
    public void Validate_AcceptsSubfoldersOfAnAllowedRoot()
    {
        var nested = Path.Combine(Root, "incoming");

        var result = ImportRequestValidator.Validate(
            Query(Replacing("targetFolderPath", nested)), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsTargetPathOutsideAllowedRootsAndNamesTheRoots()
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("targetFolderPath", "/target")), Roots);

        var error = Assert.Single(result.Errors);
        Assert.Contains("not under any configured", error);
        // The operator should not have to read server env vars to learn what would work.
        Assert.Contains(Root, error);
    }

    [Fact]
    public void Validate_RejectsSiblingOfAnAllowedRoot()
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("targetFolderPath", Root + "-other")), Roots);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var result = ImportRequestValidator.Validate(
            Query(
                ("documentId", "nope"),
                ("targetFolderPath", "/target"),
                ("downloadMode", "zip")),
            Roots);

        Assert.Contains(result.Errors, e => e.Contains("documentId"));
        Assert.Contains(result.Errors, e => e.Contains("onbaseDocType is required"));
        Assert.Contains(result.Errors, e => e.Contains("not under any configured"));
        Assert.Contains(result.Errors, e => e.Contains("downloadMode must be one of"));
        Assert.Contains(result.Errors, e => e.Contains("deleteAttachments is required"));
    }

    [Theory]
    [InlineData("{{document.id}}")]
    [InlineData("{{data.docId}}")]
    public void Validate_CatchesUnsubstitutedKualiTokens(string documentId)
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("documentId", documentId)), Roots);

        Assert.Contains(result.Errors, e => e.Contains("unsubstituted Kuali template token"));
    }

    [Fact]
    public void Validate_CatchesUnsubstitutedTokenInKeywordValue()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("KeywordKey1", "Department"), ("KeywordValue1", "{{data.department}}"))),
            Roots);

        Assert.Contains(result.Errors, e => e.Contains("KeywordValue1") && e.Contains("unsubstituted"));
    }

    [Fact]
    public void Validate_RejectsShortDocumentId()
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("documentId", "abc123")), Roots);

        Assert.Contains(result.Errors, e => e.Contains("not a valid Kuali document id"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Validate_AcceptsRealisticBooleanSpellings(string raw, bool expected)
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("deleteAttachments", raw)), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Equal(expected, result.DeleteAttachments);
    }

    [Fact]
    public void Validate_RejectsUnparseableBooleanInsteadOfDefaultingToFalse()
    {
        var result = ImportRequestValidator.Validate(
            Query(Replacing("deleteAttachments", "yes please")), Roots);

        Assert.Contains(result.Errors, e => e.Contains("deleteAttachments must be true or false"));
    }

    [Fact]
    public void Validate_TreatsDeleteDocumentAsOptionalDefaultingToFalse()
    {
        var result = ImportRequestValidator.Validate(Query(ValidRequest()), Roots);

        Assert.True(result.IsValid);
        Assert.False(result.DeleteDocument);
    }

    [Fact]
    public void Validate_RejectsKeywordKeyWithoutItsValue()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("KeywordKey1", "Department"))), Roots);

        var error = Assert.Single(result.Errors);
        Assert.Contains("KeywordKey1", error);
        Assert.Contains("KeywordValue1", error);
    }

    [Fact]
    public void Validate_RejectsKeywordValueWithoutItsKey()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("KeywordValue2", "ITS"))), Roots);

        Assert.Contains(result.Errors, e => e.Contains("KeywordValue2") && e.Contains("KeywordKey2"));
    }

    [Fact]
    public void Validate_IgnoresKualiPipeSentinelInUnusedKeywordSlots()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("KeywordKey3", "|"), ("KeywordValue3", "|"))), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsMisspelledParameterInsteadOfIgnoringIt()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("targetFolder", @"\\somewhere\else"))), Roots);

        Assert.Contains(result.Errors, e => e.Contains("'targetFolder' is not a recognized parameter"));
    }

    [Fact]
    public void Validate_AllowsAllTwentyKeywordSlots()
    {
        var pairs = new List<(string, string)>(ValidRequest());
        for (var i = 1; i <= 20; i++)
        {
            pairs.Add(($"KeywordKey{i}", $"k{i}"));
            pairs.Add(($"KeywordValue{i}", $"v{i}"));
        }

        var result = ImportRequestValidator.Validate(Query(pairs.ToArray()), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsKeywordSlotBeyondTheSupportedRange()
    {
        var result = ImportRequestValidator.Validate(
            Query(With(("KeywordKey21", "k"), ("KeywordValue21", "v"))), Roots);

        Assert.Contains(result.Errors, e => e.Contains("KeywordKey21") && e.Contains("not a recognized"));
    }

    [Fact]
    public void DescribeTargetPathProblem_FlagsUnconfiguredRoots()
    {
        var problem = ImportRequestValidator.DescribeTargetPathProblem(Root, Array.Empty<string>());

        Assert.NotNull(problem);
        Assert.Contains("AllowedTargetRoots is not configured", problem);
    }
}
