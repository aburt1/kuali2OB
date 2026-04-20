namespace KualiOnBase.Api.Options;

public sealed class KualiOptions
{
    public const string SectionName = "Kuali";

    public string BaseUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;

    // The externally-reachable base URL for THIS API, e.g. "https://kuali-onbase.csub.edu".
    // Kuali Build POSTs the signed PDF URL back to {PublicBaseUrl}/api/kuali-export-callback/{id}.
    public string PublicBaseUrl { get; set; } = string.Empty;

    // Shared secret used to HMAC-sign callback URLs so untrusted parties cannot inject fake URLs.
    public string CallbackSecret { get; set; } = string.Empty;

    // Time zone string forwarded to exportDocument so generated PDF timestamps match local expectations.
    public string ExportTimeZone { get; set; } = "America/Los_Angeles";

    // How long ImportOrchestrator waits for Kuali's callback before giving up.
    public int ExportCallbackTimeoutSeconds { get; set; } = 180;

    // How often the orchestrator checks SQLite for the callback row.
    public int ExportCallbackPollMilliseconds { get; set; } = 500;
}
