using KualiOnBase.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace KualiOnBase.Api.Tests.Import;

/// <summary>
/// X-Response-URL decides where this server will later POST, and it arrives from
/// the caller — the same class of sink as a download URL, so it gets the same guard.
/// </summary>
public sealed class OutboundUrlTests
{
    [Theory]
    [InlineData("https://csub.kualibuild.com/app/api/v0/callback/abc")]
    [InlineData("https://kuali.example.edu/hooks/1")]
    public void PublicHttpsUrls_AreAccepted(string url)
    {
        Assert.Null(OutboundUrl.DescribeProblem(url, "X-Response-URL"));
    }

    [Theory]
    [InlineData("http://csub.kualibuild.com/callback", "must use https")]
    [InlineData("ftp://files.example.com/x", "must use https")]
    [InlineData("not a url at all", "not an absolute URL")]
    [InlineData("", "is empty")]
    public void UnacceptableUrls_AreRejectedWithAReason(string url, string expected)
    {
        var problem = OutboundUrl.DescribeProblem(url, "X-Response-URL");

        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    // A bare path is rejected either way, but the REASON is platform-dependent:
    // on Unix "/x" parses as an absolute file:// URI (caught by the scheme check),
    // on Windows it is relative (caught by the absolute check). Assert the outcome,
    // not the wording.
    [Theory]
    [InlineData("/relative/path")]
    [InlineData(@"\\fileserver\share\x")]
    [InlineData("file:///etc/passwd")]
    public void NonHttpTargets_AreRejectedRegardlessOfPlatform(string url)
    {
        Assert.NotNull(OutboundUrl.DescribeProblem(url, "X-Response-URL"));
    }

    [Theory]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://192.168.1.1/x")]
    [InlineData("https://172.20.0.1/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/x")]
    public void InternalAddresses_AreRejected(string url)
    {
        var problem = OutboundUrl.DescribeProblem(url, "X-Response-URL");

        Assert.NotNull(problem);
        Assert.Contains("loopback, link-local or private", problem);
    }
}

/// <summary>
/// The DIP index file is a directive-per-line format consumed by OnBase, so a
/// keyword that can forge a directive can change how a document is filed.
/// </summary>
public sealed class IndexFileInjectionTests
{
    private static readonly string[] Roots = OperatingSystem.IsWindows()
        ? new[] { @"C:\onbase\drop" }
        : new[] { "/onbase/drop" };

    private static IQueryCollection Query(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) dict[k] = v;
        return new QueryCollection(dict);
    }

    private static (string, string)[] Valid(params (string, string)[] extra) =>
    [
        ("documentId", "6a73c32cf8d95f02a01e422b"),
        ("onbaseDocType", "IT - Access"),
        ("targetFolderPath", Roots[0]),
        ("downloadMode", "pdf"),
        ("deleteAttachments", "false"),
        .. extra,
    ];

    [Theory]
    [InlineData("ONBASE_DOC_TYPE")]
    [InlineData("onbase_doc_type")]
    [InlineData("FILENAME")]
    [InlineData("EXTERNAL_SOURCE")]
    [InlineData("EXTERNAL_SOURCE_REF")]
    public void KeywordKeyThatForgesAnIndexDirective_IsRejected(string key)
    {
        var result = ImportRequestValidator.Validate(
            Query(Valid(("KeywordKey1", key), ("KeywordValue1", "Invoice"))), Roots);

        Assert.Contains(result.Errors, e => e.Contains("reserved OnBase index directive"));
    }

    [Fact]
    public void KeywordKeyContainingTheDirectiveSeparator_IsRejected()
    {
        var result = ImportRequestValidator.Validate(
            Query(Valid(("KeywordKey1", "Dept: ONBASE_DOC_TYPE"), ("KeywordValue1", "x"))), Roots);

        Assert.Contains(result.Errors, e => e.Contains("contains ':'"));
    }

    [Fact]
    public void OrdinaryKeywordKeys_StillPass()
    {
        var result = ImportRequestValidator.Validate(
            Query(Valid(("KeywordKey1", "Department"), ("KeywordValue1", "ITS"))), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    // char.IsControl does not classify U+2028/U+2029, but readers treat them as
    // line breaks — enough to open an extra record in the index file.
    [Theory]
    [InlineData("value\u2028ONBASE_DOC_TYPE: Payroll")]
    [InlineData("value\u2029ONBASE_DOC_TYPE: Payroll")]
    [InlineData("value\r\nONBASE_DOC_TYPE: Payroll")]
    [InlineData("value\u0085ONBASE_DOC_TYPE: Payroll")]
    public void UnicodeAndAsciiLineBreaks_AreFlattened(string hostile)
    {
        var cleaned = IndexFileBuilder.StripLineBreaks(hostile);

        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\u2028', cleaned);
        Assert.DoesNotContain('\u2029', cleaned);
        Assert.DoesNotContain('\u0085', cleaned);
        Assert.Contains("ONBASE_DOC_TYPE", cleaned); // still one line, just neutralised
    }
}

public sealed class DocumentIdAnchoringTests
{
    private static readonly string[] Roots = OperatingSystem.IsWindows()
        ? new[] { @"C:\onbase\drop" }
        : new[] { "/onbase/drop" };

    private static IQueryCollection Query(string documentId)
    {
        var dict = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["documentId"] = documentId,
            ["onbaseDocType"] = "Test",
            ["targetFolderPath"] = Roots[0],
            ["downloadMode"] = "pdf",
            ["deleteAttachments"] = "false",
        };
        return new QueryCollection(dict);
    }

    // .NET's '$' also matches immediately before a trailing newline, so the old
    // "^...$" pattern accepted a 25th character.
    [Theory]
    [InlineData("6a73c32cf8d95f02a01e422b\n")]
    [InlineData("6a73c32cf8d95f02a01e422b\r\n")]
    public void DocumentIdWithTrailingNewline_IsRejected(string documentId)
    {
        var result = ImportRequestValidator.Validate(Query(documentId), Roots);

        Assert.Contains(result.Errors, e => e.Contains("not a valid Kuali document id"));
    }

    [Fact]
    public void CleanDocumentId_IsAccepted()
    {
        var result = ImportRequestValidator.Validate(Query("6a73c32cf8d95f02a01e422b"), Roots);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    // Error messages are returned to the caller AND written to the log file; a raw
    // CR/LF in either forges a log line (CWE-117).
    [Fact]
    public void ErrorMessagesDoNotCarryCallerSuppliedLineBreaks()
    {
        var result = ImportRequestValidator.Validate(
            Query("bogus\r\n2026-01-01 00:00:00 [INF] Forged log line"), Roots);

        Assert.NotEmpty(result.Errors);
        foreach (var e in result.Errors)
        {
            Assert.DoesNotContain('\r', e);
            Assert.DoesNotContain('\n', e);
        }
    }
}
