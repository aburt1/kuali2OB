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

Open <http://localhost:5050>, enter your API key, and the Auditor page will list every job as it runs — Kuali Build POSTs to `/api/kuali-onbase-import`, the jobs appear here with a per-event timeline. You can also replay any past job with edited params from its row.

To fire a one-off against a real Kuali document, POST to `/api/kuali-onbase-import` directly (see the curl example below). To run tests: `dotnet test`.

> Note: running locally, Kuali cannot reach your machine for the PDF export callback. For a real end-to-end test, deploy behind a public URL (see Deployment) or use a tunnel (`ngrok http 5050`) and set `Kuali:PublicBaseUrl` to the public URL.

---

## How to call the API

```
POST https://<your-host>/api/kuali-onbase-import
     ?documentId=68fa2b19c0f15c00281b3e42
     &onbaseDocType=IT - Access
     &targetFolderPath=\\onbase-prod\DIP\incoming
     &downloadMode=pdf
     &deleteAttachments=true
     &deleteDocument=false
     &KeywordKey1=Department
     &KeywordValue1=ITS
     &KeywordKey2=EmployeeID
     &KeywordValue2=900123456

Headers:
  X-Api-Key: <your-auth-apikey>
  # or equivalently:
  Authorization: Bearer <your-auth-apikey>
```

The API accepts the key in either header — whichever your HTTP client makes easier. Kuali Build's "Bearer authentication" option works out of the box; the dashboard and curl examples use `X-Api-Key`.

### Parameters

| Name | Required | Type | Notes |
|---|---|---|---|
| `documentId` | yes | string | Kuali Build document id |
| `onbaseDocType` | yes | string | Value for the `ONBASE_DOC_TYPE` index line |
| `targetFolderPath` | yes | string | UNC or mapped path; must exist and be writable by the API process |
| `downloadMode` | yes | `pdf` \| `attachments` | `pdf` = one PDF from Kuali's `exportDocument` (tenant setting decides whether attachments are merged in — see **Kuali tenant prerequisite** below). `attachments` = raw attachment files in their original formats (.docx/.jpg/.pdf/…). |
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
&downloadMode=pdf\
&deleteAttachments=true\
&deleteDocument=false\
&KeywordKey1=Department\
&KeywordValue1=ITS" \
  -H "X-Api-Key: $AUTH_APIKEY"
```

Spaces and backslashes must be URL-encoded (`%20`, `%5C`).

---

## Kuali tenant prerequisite — "Include PDFs uploaded through the form"

`downloadMode=pdf` calls Kuali's `exportDocument` mutation and writes whatever single PDF Kuali returns. What's *in* that PDF is decided by **one tenant-level toggle in Kuali's admin UI**, not by anything this API sends.

| Tenant setting | What `downloadMode=pdf` produces |
|---|---|
| **"Include PDFs uploaded through the form" = ON** | Form render + every PDF attachment merged into a single PDF file |
| **"Include PDFs uploaded through the form" = OFF** | Form render only — attachments are **not merged**, regardless of what option strings we send |

This was verified empirically against the CSUB tenant: with the setting off, sending `["Form"]`, `["Combined"]`, `["Attachments"]`, `["Form","Attachments"]`, `["Merged"]`, `["All"]`, `[]`, and `["FormAndAttachments"]` all returned the form-only render (identical byte size, ~35 KB on a document with a 158 KB PDF attachment that should have been included if merging were happening). Kuali's `options: [String!]!` array has no documented-and-working effect here — the tenant setting is the real switch. The API sends the canonical `["Combined"]` option so the GraphQL call stays consistent with Kuali's docs, but the behavior comes from the tenant configuration.

**Caveats even with the setting on:**

- Kuali can only merge **PDF attachments**. Non-PDF uploads (.docx, .jpg, .xlsx) on the source document will cause Kuali's export to fail or drop them. If a document mixes PDF and non-PDF attachments, use `downloadMode=attachments` instead — it downloads each raw file and lets OnBase index them all.
- If the toggle ever gets flipped back off by a Kuali admin, workflows that rely on the merged output will silently degrade to form-only. **Use the dashboard's event timeline (or `GET /api/jobs/{id}`) to watch `PdfDownloaded → bytes` — a sudden drop vs. historical jobs is the signal.**

Toggle location: Kuali Build admin → Settings → Documents → "Include PDFs uploaded through the form".

If you need attachment-inclusive output without relying on the tenant setting — or if attachments aren't always PDFs — use `downloadMode=attachments`. That path downloads each file from Kuali directly, preserves original formats, and produces one index-file entry per content file.

### Diagnostic probe

When a new tenant behaves differently, probe it empirically without running a full import:

```bash
curl -X POST "https://<host>/api/diag/kuali-probe-export" \
  -H "X-Api-Key: $AUTH_APIKEY" \
  -H "Content-Type: application/json" \
  -d '{"documentId":"<some-doc-id>","options":["Combined"]}'
```

Returns `{ sizeBytes, sha256, durationMs, signedUrl }`. Sweep different `options` arrays and compare sizes — same size = no effect, larger size = something was merged in. Also available: `GET /api/diag/db-status` for persistence checks.

---

## Wiring to Kuali Build

In the Kuali Build form/workflow editor, add an **HTTP Action** step to the approval flow:

| Field | Value |
|---|---|
| Method | `POST` |
| URL | `https://your-host/api/kuali-onbase-import?documentId={{document.id}}&onbaseDocType=IT - Access&targetFolderPath=\\onbase-prod\DIP\incoming&downloadMode=pdf&deleteAttachments=true&deleteDocument=false&KeywordKey1=Department&KeywordValue1={{data.department}}` |
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

## Auditor / Visualizer (dashboard)

Open the root URL (default `/`). On first visit, click **API key** and paste your `Auth__ApiKey`. The page is read-first — it shows what the API actually did against Kuali for every job Kuali Build fires at it:

- Lists the most recent 100 jobs (polls every 3s)
- Click a row to expand: left pane = metadata, right pane = **event timeline** showing every stage (Kuali fetch, signed URL, downloads, rename, copy, index write, success/fail)
- Each event has a `payload` toggle that reveals the exact JSON — including the signed PDF URL Kuali returned, attachment metadata, file byte counts, and the full DIP index file content
- **Replay with changes** — every job row exposes a button that re-opens its parameters in a dialog; edit anything (target folder, keywords, delete flags, download mode) and re-submit. Useful for reissuing a failed job after fixing its inputs, or for testing a param change against the same document without touching Kuali Build's workflow config.

To disable the page entirely in production: `Ui__Enabled=false`.

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
  wwwroot/index.html       Auditor / Visualizer page (vanilla HTML/CSS/JS)
tests/KualiOnBase.Tests/   xUnit + FluentAssertions, WebApplicationFactory
tools/probe-kuali-schema.sh  One-shot GraphQL introspection against your tenant
```

---

## Design decisions & security rationale

### Three distinct secrets, one per trust boundary

| Secret | Trust boundary | Why separate |
|---|---|---|
| `Auth__ApiKey` | Kuali → this API | Authenticates inbound requests. Used only as the `X-Api-Key` header on `/api/*`. |
| `Kuali__ApiToken` | This API → Kuali | Bearer token for Kuali's GraphQL. Different scope, rotated in Kuali's admin UI, should never equal the inbound key. |
| `Kuali__CallbackSecret` | This API ↔ itself | HMAC-SHA256 key to sign callback URLs. See below. |

Compromising one secret must not compromise the others. They live in separate env vars for exactly this reason.

### HMAC-signed callback URL (not an API key)

Kuali's `exportDocument` mutation is asynchronous — Kuali renders the PDF and POSTs the signed S3 URL back minutes later to a callback URL we supply. That callback endpoint has two constraints:

1. It must be publicly reachable (Kuali's servers hit it from AWS us-west-2).
2. It cannot require `X-Api-Key` — Kuali doesn't know our API key.

Left unprotected, anyone on the internet who guesses a correlation id could POST a malicious URL and trick the API into downloading an attacker-controlled PDF into the OnBase drop folder — a document-spoofing attack against OnBase itself.

**Fix:** every callback URL we hand to Kuali is signed: `?sig = HMAC-SHA256(correlationId, CallbackSecret)`. The callback endpoint recomputes the HMAC and rejects any request whose `sig` doesn't match. Only code that knows `CallbackSecret` can forge a valid signature. Kuali just echoes the URL back verbatim — it never sees or handles the secret.

Why not just put `Auth__ApiKey` in the URL? A single static key in logs everywhere (Kuali side, proxy logs, browser history) works for every callback forever. HMAC binds each signature to one specific correlation id — a leaked URL is useless for forging a different callback.

### Callback endpoint lives outside `/api/*` on purpose

`ApiKeyMiddleware` only guards `/api/*`. The callback endpoint is at `/kuali-export-callback/{id}` so it bypasses the middleware entirely. The compensating control is the HMAC above. This is an explicit design choice: the middleware stays simple and doesn't need a carve-out for unauthenticated traffic.

### Bare `HttpClient` for downloading signed S3 URLs

When downloading from the AWS-signed URL Kuali returns, `KualiClient` uses `new HttpClient()` rather than the typed `IKualiClient` instance that carries the Bearer token. The signed URL already contains its own auth (AWS query-string signature); sending our Kuali `Authorization` header to an AWS endpoint would needlessly expose the token to a third party.

### Synchronous happy path, asynchronous retry fallback

The API responds `200 OK` only after the file lands in the OnBase folder. If transient failure strikes (Kuali 5xx after Polly retries, network glitch, filesystem flakiness) the job is marked `Retrying` and the API returns `202 Accepted`. The `RetryWorker` drains the queue with exponential backoff. This keeps Kuali's side simple — they don't have to poll us or handle webhooks — while still tolerating infrastructure hiccups.

Non-transient failures (`400` from Kuali, invalid target path, validation errors) are marked `Failed` and return 4xx/500 immediately. No retry.

### Two layers of retries, each for different failures

- **Polly policy** on `HttpClient` — 3 attempts with exponential backoff **inside a single API call**, for transient HTTP errors against Kuali. Caller never sees the retry.
- **RetryQueue + RetryWorker** — **across calls**, for any job that failed even after Polly gave up. Up to `MaxAttempts` with longer backoff. Survives a process restart.

### SQLite + Dapper, not EF + Postgres

One process, one file, one binary. No external dependencies, no ORM ceremony. Job history survives restarts, migrations are forward-only (embedded `*.sql` tracked in a tiny `__Migrations` table). If scale ever demands it, swapping to Postgres is a single connection-string change plus a Dapper dialect tweak — but YAGNI until then.

### Event log per job, exposed to the dashboard

Every stage (`DocumentFetched`, `ExportRequested`, `ExportCallbackReceived`, `PdfDownloaded`, `AttachmentDownloaded`, `FilesRenamed`, `BackupCreated`, `FilesCopiedToTarget`, `IndexFileWritten`, `AttachmentsCleared`, `DocumentDeleted`, `ImportSucceeded`/`ImportFailed`) records a row in `JobEvents` with the raw JSON payload — including the signed S3 URL Kuali returned, attachment metadata, byte counts, and the exact DIP index file content written to disk.

This is the audit trail. It lets you confirm *after the fact* what Kuali actually sent and what landed in OnBase, which is the only way to debug discrepancies without re-running a job.

Logging is wrapped in `try/catch` that swallows errors — **the event log must never break a job**.

### Dated backup folders with retention purge

Every successful import copies its files into `<BackupRootPath>/yyyyMMdd_HHmmss_<documentId>/` *before* they land in the OnBase drop folder. If OnBase rejects the upload or an admin needs to re-process manually, the exact bytes are recoverable.

`BackupCleanupWorker` purges folders older than `Backup:RetentionDays` once per day, along with `ImportJobs` rows older than `Retry:SucceededJobRetentionDays`. This caps disk usage and DB size without a human in the loop.

### Dashboard is off by default in the threat model

`Ui__Enabled` is `true` by default for developer ergonomics, but production deployments that only want programmatic access should set it to `false`. Even with it enabled, the data API behind it (`/api/jobs`) is gated by `Auth__ApiKey` — serving the HTML is harmless, but the dashboard can't read job data without a valid key.

### Filename sanitization

OnBase DIP has strict filename rules. `FileNameSanitizer` strips `/ \ : * ? " < > |`, collapses whitespace, trims, and de-duplicates collisions by appending `_2`, `_3`, … A predictable sanitizer lets the DIP index file reference the exact on-disk name every time.

### URL parameters over JSON body

The `/api/kuali-onbase-import` endpoint takes all parameters as query-string, not a JSON body. This is because Kuali Build's HTTP Action works most reliably with GET-style query params — it avoids templating issues in their body editor and makes the call trivially replayable from curl or the dashboard.

---

## Troubleshooting

**Job stuck in `Retrying`** — check `lastError` on the job row. Common causes: `targetFolderPath` not writable, Kuali token expired, or the export callback never arrived (Kuali can't reach your public URL).

**401 from Kuali in the event log** — rotate `Kuali__ApiToken`.

**401 from your own API in the dashboard** — the `X-Api-Key` the browser stored no longer matches `Auth__ApiKey`. Click **API key** and re-enter it.

**Callback never arrives** — confirm `Kuali__PublicBaseUrl` is actually reachable from Kuali's network (they're in AWS us-west-2). Coolify + a public hostname works; localhost does not.

**`downloadMode=pdf` is only returning the form render — attachments are missing** — the Kuali tenant setting "Include PDFs uploaded through the form" is off. See [Kuali tenant prerequisite](#kuali-tenant-prerequisite--include-pdfs-uploaded-through-the-form) above. Either turn the setting on (simplest), or switch the workflow to `downloadMode=attachments` for raw attachment files.

**Dashboard shows the wrong files** — check the event log for the job. The `FilesRenamed` event shows staged → final mapping; `IndexFileWritten` shows the exact text written to disk.

---

## License

Internal CSUB project; not for public distribution.
