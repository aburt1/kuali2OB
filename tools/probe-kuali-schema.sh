#!/usr/bin/env bash
# Probes the Kuali Build GraphQL schema for the operations used by this API.
#
# Usage:
#   KUALI_BASE_URL=https://csub.kualibuild.com \
#   KUALI_API_TOKEN=xxxxxxx \
#   ./tools/probe-kuali-schema.sh
#
# Produces:
#   - tools/kuali-schema-dump.json   full introspection response
#   - stdout summary comparing assumed ops to what your tenant actually exposes
#
# Requires: curl, jq
set -euo pipefail

: "${KUALI_BASE_URL:?set KUALI_BASE_URL, e.g. https://csub.kualibuild.com}"
: "${KUALI_API_TOKEN:?set KUALI_API_TOKEN (Bearer token with admin scope)}"

command -v jq >/dev/null || { echo "jq is required (brew install jq)" >&2; exit 2; }

endpoint="${KUALI_BASE_URL%/}/app/api/v0/graphql"
outdir="$(cd "$(dirname "$0")" && pwd)"
dump="$outdir/kuali-schema-dump.json"

read -r -d '' INTROSPECTION <<'GQL' || true
query Introspect {
  __schema {
    queryType { name }
    mutationType { name }
    types {
      name
      kind
      fields(includeDeprecated: true) {
        name
        args {
          name
          type { name kind ofType { name kind ofType { name kind ofType { name kind } } } }
        }
        type { name kind ofType { name kind ofType { name kind ofType { name kind } } } }
      }
      inputFields {
        name
        type { name kind ofType { name kind ofType { name kind ofType { name kind } } } }
      }
      enumValues(includeDeprecated: true) { name }
    }
  }
}
GQL

payload="$(jq -cn --arg q "$INTROSPECTION" '{query: $q}')"

echo ">> POST $endpoint"
curl -fsS "$endpoint" \
    -H "Authorization: Bearer $KUALI_API_TOKEN" \
    -H "Content-Type: application/json" \
    -d "$payload" > "$dump"

if jq -e '.errors' "$dump" >/dev/null; then
    echo "!! GraphQL returned errors:"
    jq '.errors' "$dump"
    exit 1
fi

query_type=$(jq -r '.data.__schema.queryType.name'    "$dump")
mut_type=$(  jq -r '.data.__schema.mutationType.name' "$dump")
echo "Query root:    $query_type"
echo "Mutation root: $mut_type"
echo

list_fields() {
    local type_name=$1
    jq -r --arg t "$type_name" '
        .data.__schema.types[]
        | select(.name == $t)
        | .fields[]?
        | "  \(.name)(\([.args[]? | "\(.name): \(.type.name // .type.ofType.name // "?")"] | join(", "))) -> \(.type.name // .type.ofType.name // .type.ofType.ofType.name // "?")"
    ' "$dump"
}

echo "-- $query_type fields --"
list_fields "$query_type"
echo
echo "-- $mut_type fields --"
list_fields "$mut_type"
echo

expect_op() {
    local root=$1 op=$2
    if jq -e --arg r "$root" --arg o "$op" \
        '.data.__schema.types[] | select(.name == $r) | .fields[]? | select(.name == $o)' \
        "$dump" >/dev/null; then
        echo "  [OK]      $root.$op"
    else
        echo "  [MISSING] $root.$op  <-- KualiGraphQl.cs expects this; rename or adjust"
    fi
}

echo "== Comparing to assumed operations in KualiGraphQl.cs =="
expect_op "$query_type"  document
expect_op "$query_type"  exportPDFStatus
expect_op "$mut_type"    exportPDF
expect_op "$mut_type"    updateDocument
expect_op "$mut_type"    deleteDocument
echo

probe_type() {
    local name=$1
    local rows
    rows=$(jq -r --arg t "$name" '
        def typename: (.name // .ofType.name // .ofType.ofType.name // .ofType.ofType.ofType.name // "?");
        .data.__schema.types[]
        | select(.name == $t)
        | ((.fields // []) + (.inputFields // []))[]
        | "  \(.name): \(.type | typename)"' "$dump")
    if [ -n "$rows" ]; then
        echo "-- type $name --"
        echo "$rows"
        echo
    fi
}

echo "== Document-shaped types (so you can confirm data.firstName / meta.serialNumber / attachments) =="
for t in Document Form BuildDocument Submission FormDocument DocumentMeta Meta Attachment File Upload FileUpload; do
    probe_type "$t"
done

echo "== Input types for the mutations we need to call =="
for t in UpdateDocumentInput DeleteDocumentInput ExportDocumentInput ExportDocumentOptions DocumentInput; do
    probe_type "$t"
done

echo "== Full mutation signatures we care about =="
for op in document exportDocument updateDocument deleteDocument; do
    jq -r --arg o "$op" '
        def typename: (.name // .ofType.name // .ofType.ofType.name // .ofType.ofType.ofType.name // "?");
        .data.__schema.types[]
        | select(.name == "Query" or .name == "Mutation")
        | .fields[]?
        | select(.name == $o)
        | "  \(.name)(\([.args[]? | "\(.name): \(.type | typename)"] | join(", "))) -> \(.type | typename)"
    ' "$dump"
done
echo

echo "Full introspection saved to: $dump"
echo "Grep tips:"
echo "  jq '.data.__schema.types[] | select(.name | test(\"Document|Attach\"; \"i\"))' $dump"
echo "  jq -r '.data.__schema.types[].name' $dump | sort"
