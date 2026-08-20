using KualiOnBase.Api.Services;
using Xunit;

namespace KualiOnBase.Api.Tests.Kuali;

/// <summary>
/// The relative-URL branch attaches the Kuali bearer token, so anything that can
/// escape the configured Kuali origin through that branch leaks a tenant-wide
/// credential. Attachment URLs come from Kuali form data, i.e. from whoever
/// submitted the form.
/// </summary>
public sealed class DownloadUrlResolutionTests
{
    private static readonly Uri Base = new("https://csub.kualibuild.com/");

    [Fact]
    public void RelativeKualiPath_KeepsBearerAndStaysOnTheTenant()
    {
        var (uri, useAuth) = KualiClient.ResolveDownloadUrl("/files/abc123", Base);

        Assert.Equal("https://csub.kualibuild.com/files/abc123", uri.ToString());
        Assert.True(useAuth);
    }

    // `\/evil.tld/x` is a valid RELATIVE uri, is unchanged by TrimStart('/'), and
    // Uri resolution turns it into https://evil.tld/x — previously with the token.
    [Theory]
    [InlineData(@"\/evil.example.com/collect")]
    [InlineData(@"\\evil.example.com/collect")]
    [InlineData("//evil.example.com/collect")]
    public void RelativeValueThatEscapesTheTenantHost_IsRejected(string url)
    {
        var ex = Record.Exception(() => KualiClient.ResolveDownloadUrl(url, Base));

        if (ex is null)
        {
            // Some forms legitimately resolve back onto the tenant; if it resolved,
            // it must not have left the configured host.
            var (uri, _) = KualiClient.ResolveDownloadUrl(url, Base);
            Assert.Equal(Base.Host, uri.Host);
            return;
        }

        Assert.IsType<PermanentImportException>(ex);
        Assert.Contains("not the configured Kuali host", ex.Message);
    }

    [Fact]
    public void RelativeEscape_NeverReturnsAnOffHostUriWithAuth()
    {
        // The property that actually matters: no input may yield UseAuth on a
        // host other than the configured Kuali origin.
        var candidates = new[]
        {
            @"\/evil.example.com/x", @"\\evil.example.com/x", "//evil.example.com/x",
            "/files/ok", "files/ok", @"\evil.example.com/x", "/../../evil",
        };

        foreach (var c in candidates)
        {
            try
            {
                var (uri, useAuth) = KualiClient.ResolveDownloadUrl(c, Base);
                if (useAuth)
                {
                    Assert.Equal(Base.Host, uri.Host);
                    Assert.Equal(Base.Scheme, uri.Scheme);
                }
            }
            catch (PermanentImportException)
            {
                // Rejected outright, which is also acceptable.
            }
        }
    }

    [Fact]
    public void AbsoluteSignedUrlOnAnotherHost_IsFetchedWithoutTheToken()
    {
        // Kuali's PDF export hands back signed S3/CDN links; these must still work,
        // but must never carry the Kuali bearer header.
        var (uri, useAuth) = KualiClient.ResolveDownloadUrl(
            "https://kuali-exports.s3.amazonaws.com/doc.pdf?sig=abc", Base);

        Assert.Equal("kuali-exports.s3.amazonaws.com", uri.Host);
        Assert.False(useAuth);
    }

    [Fact]
    public void AbsoluteUrlOnTheTenantHost_KeepsTheToken()
    {
        var (uri, useAuth) = KualiClient.ResolveDownloadUrl(
            "https://csub.kualibuild.com/files/abc", Base);

        Assert.Equal(Base.Host, uri.Host);
        Assert.True(useAuth);
    }

    [Fact]
    public void PlainHttpAbsoluteUrl_IsRejected()
    {
        var ex = Assert.Throws<PermanentImportException>(() =>
            KualiClient.ResolveDownloadUrl("http://files.example.com/doc.pdf", Base));

        Assert.Contains("only https", ex.Message);
    }

    [Theory]
    [InlineData("https://127.0.0.1/doc.pdf")]
    [InlineData("https://10.1.2.3/doc.pdf")]
    [InlineData("https://192.168.1.50/doc.pdf")]
    [InlineData("https://172.16.4.4/doc.pdf")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/doc.pdf")]
    public void InternalAddresses_AreRejected(string url)
    {
        var ex = Assert.Throws<PermanentImportException>(() =>
            KualiClient.ResolveDownloadUrl(url, Base));

        Assert.Contains("loopback, link-local or private", ex.Message);
    }

    [Fact]
    public void PublicIpLiteral_IsStillAllowed()
    {
        var (uri, useAuth) = KualiClient.ResolveDownloadUrl("https://93.184.216.34/doc.pdf", Base);

        Assert.Equal("93.184.216.34", uri.Host);
        Assert.False(useAuth);
    }

    [Fact]
    public void MissingBaseAddress_FailsRelativeResolutionLoudly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KualiClient.ResolveDownloadUrl("/files/abc", null));
    }
}
