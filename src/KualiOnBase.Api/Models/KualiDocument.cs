namespace KualiOnBase.Api.Models;

public sealed record KualiDocument(
    string Id,
    string SerialNumber,
    string FirstName,
    string LastName,
    string SchoolId,
    IReadOnlyList<KualiAttachment> Attachments);

public sealed record KualiAttachment(
    string Id,
    string FieldPath,
    string FileName,
    string Url);
