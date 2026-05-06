namespace KualiOnBase.Api.Features.Kuali;

public sealed record KualiDocument(
    string Id,
    string SerialNumber,
    string FirstName,
    string LastName,
    string SchoolId,
    IReadOnlyList<KualiAttachment> Attachments,
    string? RawDataJson = null);

public sealed record KualiAttachment(
    string Id,
    string FieldPath,
    string FileName,
    string Url);
