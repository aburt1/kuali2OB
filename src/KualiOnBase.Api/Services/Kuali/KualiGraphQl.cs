namespace KualiOnBase.Api.Services.Kuali;

// Queries match the schema shape returned by the Kuali tenant's introspection.
// The field paths under `data` (schoolId, firstName, lastName) and the attachment
// shape are deployment-specific and must be confirmed against the target form.
internal static class KualiGraphQl
{
    public const string GetDocument = """
        query GetDocument($id: ID!) {
          document(id: $id) {
            id
            meta
            data
          }
        }
        """;

    // exportDocument is callback-based: Kuali POSTs the signed PDF URL to callbackUrl
    // when rendering completes. Returns a job id string (not used further by us).
    public const string ExportDocument = """
        mutation ExportDocument(
          $id: ID!,
          $callbackUrl: String!,
          $sendAsPost: Boolean!,
          $timeZone: String
        ) {
          exportDocument(
            id: $id,
            callbackUrl: $callbackUrl,
            sendAsPost: $sendAsPost,
            timeZone: $timeZone
          )
        }
        """;

    // UpdateDocumentInput = { id: ID, data: JSON, comment: String }
    public const string UpdateDocument = """
        mutation UpdateDocument($args: UpdateDocumentInput!) {
          updateDocument(args: $args) { id }
        }
        """;

    public const string DeleteDocument = """
        mutation DeleteDocument($id: ID!) {
          deleteDocument(id: $id)
        }
        """;
}
