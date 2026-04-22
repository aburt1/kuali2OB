using System.Text.Json;
using KualiOnBase.Api.Features.Jobs;
using Xunit;

namespace KualiOnBase.Api.Tests.Jobs;

public sealed class JobsEndpointTests
{
    [Fact]
    public void ParsePayload_SanitizesNestedArraysWithoutParentReuseErrors()
    {
        const string json = """
            {
              "attachments": [
                {
                  "href": "https://files.example.com/a",
                  "labels": ["safe", "https://files.example.com/should-redact"]
                },
                {
                  "value": "kept"
                }
              ],
              "signedUrl": "https://files.example.com/export",
              "message": "visible"
            }
            """;

        var payload = JobsEndpoint.ParsePayload(json);

        var exception = Record.Exception(() => JsonSerializer.Serialize(payload));

        Assert.Null(exception);
        Assert.NotNull(payload);
        Assert.Equal("[redacted]", payload!["signedUrl"]!.GetValue<string>());
        Assert.Equal("[redacted]", payload["attachments"]![0]!["href"]!.GetValue<string>());
        Assert.Equal("[redacted]", payload["attachments"]![0]!["labels"]![1]!.GetValue<string>());
        Assert.Equal("safe", payload["attachments"]![0]!["labels"]![0]!.GetValue<string>());
        Assert.Equal("visible", payload["message"]!.GetValue<string>());
    }
}
