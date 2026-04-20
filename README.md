# Kuali Build → OnBase DIP Integration API

A .NET 8 Minimal API that replaces CSUB's manual Confidential Document Upload step. A Kuali Build workflow calls this API at approval time; the API pulls the form PDF and attachments from Kuali, writes them into an OnBase DIP drop folder with a sanitized index file, takes a dated backup, and (optionally) cleans up the source in Kuali.

Ships with:

- SQLite-backed **retry queue** with exponential backoff for transient failures
- **Event log** per job (every stage — Kuali fetch, signed URL, download, rename, backup, copy, index write, cleanup)
- Built-in **dashboard** at `/` for watching live imports and inspecting what Kuali sent
- **Backup retention** + automatic purge of old dated folders

---

## Quick start — run locally

```bash
git clone https://github.com/aburt1/kuali2OB
cd kuali2OB
dotnet run --project src/KualiOnBase.Api
```

Open <http://localhost:5050>, enter your API key, click **Fire test import**. The dashboard will show job progress with a timeline of every event.

To run tests: `dotnet test`.

> Note: running locally, Kuali cannot reach your machine for the PDF export callback. For a real end-to-end test, deploy behind a public URL (see Deployment) or use a tunnel (`ngrok http 5050`) and set `Kuali:PublicBaseUrl` to the public URL.

---

## How to call the API

```
POST https://<your-host>/api/kuali-onbase-import
     ?documentId=68fa2b19c0f15c00281b3e42
     &onbaseDocType=IT - Access
     &targetFolderPath=\\onbase-prod\DIP\incoming
     &downloadMode=all
     &deleteAttachments=true
     &deleteDocument=false
     &KeywordKey1=Department
     &KeywordValue1=ITS
     &KeywordKey2=EmployeeID
     &KeywordValue2=900123456

Headers:
  X-Api-Key: <your-auth-apikey>
```

### Parameters

| Name | Required | Type | Notes |
|---|---|---|---|
| `documentId` | yes | string | Kuali Build document id |
| `onbaseDocType` | yes | string | Value for the `ONBASE_DOC_TYPE` index line |
| `targetFolderPath` | yes | string | UNC or mapped path; must exist and be writable by the API process |
| `downloadMode` | yes | `pdf` \| `attachments` \| `all` | Which files to pull |
| `deleteAttachments` | yes | bool | If true, clears attachment fields on the Kuali document after a successful copy |
| `deleteDocument` | no | bool (default false) | If true, deletes the Kuali document after a successful copy |
| `KeywordKey1..20` / `KeywordValue1..20` | no | string pairs | Extra `KEY: VALUE` lines in the DIP index; incomplete pairs are ignored |

### Response codes

| Code | Meaning | Body |
|---|---|---|
| `200 OK` | Import completed | `{ jobId, status: "Succeeded", files: [...], backupFolder }` |
| `202 Accepted` | Transient failure — queued for retry | `{ jobId, status: "Retrying", attempt, nextAttemptAt }` |
| `400 Bad Request` | Validation error | error message |
| `401 Unauthorized` | Missing/bad `X-Api-Key` | |
| `500 Internal Server Error` | Unrecoverable error | error message |

### Example — curl

```bash
curl -X POST "https://your-host/api/kuali-onbase-import?\
documentId=68fa2b19c0f15c00281b3e42\
&onbaseDocType=IT%20-%20Access\
&targetFolderPath=%5C%5Conbase-prod%5CDIP%5Cincoming\
&downloadMode=all\
&deleteAttachments=true\
&deleteDocument=false\
&KeywordKey1=Department\
&KeywordValue1=ITS" \
  -H "X-Api-Key: $AUTH_APIKEY"
```

Spaces and backslashes must be URL-encoded (`%20`, `%5C`).

---

## Wiring to Kuali Build

In the Kuali Build form/workflow editor, add an **HTTP Action** step to the approval flow:

| Field | Value |
|---|---|
| Method | `POST` |
| URL | `https://your-host/api/kuali-onbase-import?documentId={{document.id}}&onbaseDocType=IT - Access&targetFolderPath=\\onbase-prod\DIP\incoming&downloadMode=all&deleteAttachments=true&deleteDocument=false&KeywordKey1=Department&KeywordValue1={{data.department}}` |
| Headers | `X-Api-Key: <your-auth-apikey>` |

`{{document.id}}` is Kuali's template token — it gets substituted at runtime. Form-field values follow the same `{{data.<fieldName>}}` pattern. Static params (doc type, target folder, mode) are hardcoded per workflow.

### How the callback works

For PDF export, Kuali renders asynchronously and calls *back* to this API when the signed URL is ready. You must:

1. Set `Kuali:PublicBaseUrl` to a URL Kuali can reach (e.g. your Coolify hostname).
2. The API exposes `POST /kuali-export-callback/{correlationId}?sig=...` — signed with HMAC-SHA256 of `Kuali:CallbackSecret`. No API key required (Kuali doesn't know it); the HMAC signature is the auth.

When Kuali POSTs back with the signed S3 URL, the API picks it up, downloads the PDF, and continues.

---

## Configuration

All settings bind from either `appsettings.json` or environment variables. **In production, only set via env vars — never commit real secrets.**

| Env var | `appsettings` path | Default | Purpose |
|---|---|---|---|
| `Auth__ApiKey` | `Auth:ApiKey` | `CHANGEME` | Required header for every `/api/*` request |
| `Kuali__BaseUrl` | `Kuali:BaseUrl` | `https://csub.kualibuild.com` | Kuali tenant root |
| `Kuali__ApiToken` | `Kuali:ApiToken` | `CHANGEME` | Kuali API token (Bearer) for GraphQL |
| `Kuali__PublicBaseUrl` | `Kuali:PublicBaseUrl` | *empty* | Public URL Kuali callbacks reach (e.g. `https://kuali2ob.your-coolify-host`) |
| `Kuali__CallbackSecret` | `Kuali:CallbackSecret` | `CHANGEME` | HMAC-SHA256 secret for signing callback URLs |
| `Kuali__ExportTimeZone` | `Kuali:ExportTimeZone` | `America/Los_Angeles` | Passed to Kuali `exportDocument` |
| `Kuali__ExportCallbackTimeoutSeconds` | `Kuali:ExportCallbackTimeoutSeconds` | `180` | How long to wait for Kuali's callback |
| `Kuali__ExportCallbackPollMilliseconds` | `Kuali:ExportCallbackPollMilliseconds` | `500` | Polling interval while waiting |
| `Backup__RootPath` | `Backup:RootPath` | `./backup` | Where dated backup folders go |
| `Backup__RetentionDays` | `Backup:RetentionDays` | `30` | Auto-purge older dated folders |
| `Retry__MaxAttempts` | `Retry:MaxAttempts` | `5` | |
| `Retry__BaseDelaySeconds` | `Retry:BaseDelaySeconds` | `60` | Exponential backoff base |
| `Retry__PollIntervalSeconds` | `Retry:PollIntervalSeconds` | `30` | Retry worker loop interval |
| `Database__Path` | `Database:Path` | `./data/kuali-onbase.db` | SQLite file path |
| `Ui__Enabled` | `Ui:Enabled` | `true` | Serve the dashboard at `/`. Set to `false` to hide it in production if you only want API access. |

---

## Deployment — Docker / Coolify

Multi-stage `Dockerfile` is at the repo root. Build & run:

```bash
docker build -t kuali2ob .
docker run -d \
  -p 8080:8080 \
  -v /srv/kuali2ob/data:/data \
  -v /srv/kuali2ob/backup:/backup \
  -v /mnt/onbase-drop:/target \
  -e Auth__ApiKey="..." \
  -e Kuali__ApiToken="..." \
  -e Kuali__PublicBaseUrl="https://kuali2ob.your-host" \
  -e Kuali__CallbackSecret="..." \
  -e Backup__RootPath=/backup \
  kuali2ob
```

On Coolify:

1. Add a new **Docker** service pointing at this repo.
2. Set the three volumes: `/data` (SQLite), `/backup` (dated PDF backups), `/target` (your OnBase DIP mount).
3. Fill the env vars above in the Coolify secrets panel.
4. Expose port `8080` behind a public hostname.
5. Use that hostname as `Kuali__PublicBaseUrl`.

---

## Dashboard

Open the root URL (default `/`). On first visit, click **API key** and paste your `Auth__ApiKey`. The dashboard:

- Lists the most recent 100 jobs (polls every 3s)
- Click a row to expand: left pane = metadata, right pane = **event timeline** showing every stage (Kuali fetch, signed URL, downloads, rename, copy, index write, success/fail)
- Each event has a `payload` toggle that reveals the exact JSON — including the signed PDF URL Kuali returned, attachment metadata, file byte counts, and the full DIP index file content

Useful once deployed for verifying what Kuali actually sends against real documents.

To disable the dashboard entirely in production: `Ui__Enabled=false`.

---

## Project layout

```
src/KualiOnBase.Api/
  Auth/                    ApiKeyMiddleware
  BackgroundServices/      RetryWorker, BackupCleanupWorker
  Data/                    Db + SQL migrations (embedded resources)
  Endpoints/               ImportEndpoint, JobsEndpoint, KualiExportCallbackEndpoint
  Models/                  Request/response/domain types
  Options/                 Strongly-typed config bindings
  Services/
    Kuali/                 KualiClient (GraphQL), ExportCallbackStore
    Import/                ImportOrchestrator, BackupService, FileNameSanitizer, IndexFileBuilder
    RetryQueue.cs          Dapper CRUD over ImportJobs
    JobEventLog.cs         Per-job event log with payloads
  wwwroot/index.html       Dashboard (vanilla HTML/CSS/JS)
tests/KualiOnBase.Tests/   xUnit + FluentAssertions, WebApplicationFactory
tools/probe-kuali-schema.sh  One-shot GraphQL introspection against your tenant
```

---

## Troubleshooting

**Job stuck in `Retrying`** — check `lastError` on the job row. Common causes: `targetFolderPath` not writable, Kuali token expired, or the export callback never arrived (Kuali can't reach your public URL).

**401 from Kuali in the event log** — rotate `Kuali__ApiToken`.

**401 from your own API in the dashboard** — the `X-Api-Key` the browser stored no longer matches `Auth__ApiKey`. Click **API key** and re-enter it.

**Callback never arrives** — confirm `Kuali__PublicBaseUrl` is actually reachable from Kuali's network (they're in AWS us-west-2). Coolify + a public hostname works; localhost does not.

**Dashboard shows the wrong files** — check the event log for the job. The `FilesRenamed` event shows staged → final mapping; `IndexFileWritten` shows the exact text written to disk.

---

## License

Internal CSUB project; not for public distribution.
