namespace KualiOnBase.Api.Options;

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    public int MaxAttempts { get; set; } = 5;
    public int BaseDelaySeconds { get; set; } = 60;
    public int PollIntervalSeconds { get; set; } = 30;
    public int SucceededJobRetentionDays { get; set; } = 30;
}
