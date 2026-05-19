using System.Text.Json;
using KualiOnBase.Api.Controllers;
using KualiOnBase.Api.Services;
using Xunit;

namespace KualiOnBase.Api.Tests.Jobs;

public sealed class JobsControllerTests
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

        var payload = JobsController.ParsePayload(json);

        var serialized = JsonSerializer.Serialize(payload);

        Assert.NotEmpty(serialized);
        Assert.NotNull(payload);
        Assert.Equal("[redacted]", payload!["signedUrl"]!.GetValue<string>());
        Assert.Equal("[redacted]", payload["attachments"]![0]!["href"]!.GetValue<string>());
        Assert.Equal("[redacted]", payload["attachments"]![0]!["labels"]![1]!.GetValue<string>());
        Assert.Equal("safe", payload["attachments"]![0]!["labels"]![0]!.GetValue<string>());
        Assert.Equal("visible", payload["message"]!.GetValue<string>());
    }
}
