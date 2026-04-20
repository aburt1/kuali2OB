using System.Text.Json.Nodes;
using FluentAssertions;
using KualiOnBase.Api.Services.Kuali;

namespace KualiOnBase.Tests;

public class KualiAttachmentExtractionTests
{
    [Fact]
    public void ExtractAttachments_RecognizesKualiFileUploadShape_WithPermaLink()
    {
        // Shape observed from a real Kuali Build file-upload field: a sibling
        // object under `data` keyed by the form-field id, carrying filename +
        // permaLink (JWT-carrying, non-expiring) and a temporaryUrl fallback.
        var data = JsonNode.Parse("""
            {
              "V6mty1F1Kv": {
                "contentType": "application/pdf",
                "filename": "OnBase SSL Update Guide.pdf",
                "filesize": 123456,
                "permaLink": "/app/forms/api/v2/files/perma/eyJhbG...",
                "retrievalId": "250e1c7d-3e1e-48d4-84de-5e8b50650ef8",
                "temporaryUrl": "/app/forms/api/v2/files/69e6b065d27aef02866a638e/..."
              }
            }
            """)!.AsObject();

        var attachments = KualiClient.ExtractAttachments(data);

        attachments.Should().ContainSingle();
        var a = attachments[0];
        a.FileName.Should().Be("OnBase SSL Update Guide.pdf");
        a.Url.Should().StartWith("/app/forms/api/v2/files/perma/");
        a.Id.Should().Be("250e1c7d-3e1e-48d4-84de-5e8b50650ef8");
        a.FieldPath.Should().Be("V6mty1F1Kv");
    }

    [Fact]
    public void ExtractAttachments_PrefersPermaLinkOverTemporaryUrl()
    {
        var data = JsonNode.Parse("""
            {
              "upload": {
                "filename": "a.pdf",
                "permaLink": "/perma/abc",
                "temporaryUrl": "/temp/abc"
              }
            }
            """)!.AsObject();

        var a = KualiClient.ExtractAttachments(data).Single();
        a.Url.Should().Be("/perma/abc");
    }

    [Fact]
    public void ExtractAttachments_FallsBackToTemporaryUrl_WhenPermaLinkMissing()
    {
        var data = JsonNode.Parse("""
            {
              "upload": {
                "filename": "a.pdf",
                "temporaryUrl": "/temp/abc"
              }
            }
            """)!.AsObject();

        KualiClient.ExtractAttachments(data).Single().Url.Should().Be("/temp/abc");
    }

    [Fact]
    public void ExtractAttachments_AlsoRecognizesGenericUrlKeys()
    {
        var data = JsonNode.Parse("""
            {
              "outer": {
                "inner": {
                  "fileName": "b.pdf",
                  "downloadUrl": "https://example/b"
                }
              }
            }
            """)!.AsObject();

        var a = KualiClient.ExtractAttachments(data).Single();
        a.FileName.Should().Be("b.pdf");
        a.Url.Should().Be("https://example/b");
        a.FieldPath.Should().Be("outer.inner");
    }

    [Fact]
    public void ExtractAttachments_SkipsNonAttachmentObjects()
    {
        var data = JsonNode.Parse("""
            {
              "firstName": "Andrew",
              "notes": { "text": "not a file" },
              "file": { "filename": "x.pdf", "permaLink": "/perma/x" }
            }
            """)!.AsObject();

        var list = KualiClient.ExtractAttachments(data);
        list.Should().ContainSingle();
        list[0].FileName.Should().Be("x.pdf");
    }

    [Fact]
    public void ExtractAttachments_WalksArrays()
    {
        var data = JsonNode.Parse("""
            {
              "uploads": [
                { "filename": "one.pdf", "permaLink": "/p/one" },
                { "filename": "two.pdf", "permaLink": "/p/two" }
              ]
            }
            """)!.AsObject();

        var list = KualiClient.ExtractAttachments(data);
        list.Should().HaveCount(2);
        list.Select(x => x.FieldPath).Should().BeEquivalentTo(new[] { "uploads[0]", "uploads[1]" });
    }

    [Fact]
    public void ExtractAttachments_EmptyForNullOrNoFiles()
    {
        KualiClient.ExtractAttachments(null).Should().BeEmpty();
        KualiClient.ExtractAttachments(new JsonObject()).Should().BeEmpty();
    }
}
