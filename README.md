# Kuali Build → OnBase DIP Integration API

A .NET 8 Minimal API that replaces CSUB's manual Confidential Document Upload step. A Kuali Build workflow calls this API at approval time; the API pulls the form PDF and attachments from Kuali, writes them into an OnBase DIP drop folder with a sanitized index file, takes a dated backup, and (optionally) cleans up the source in Kuali.

Ships with:

- SQLite-backed **retry queue** with exponential backoff for transient failures
- **Event log** per job (every stage — Kuali fetch, signed URL, download, rename, backup, copy, index write, cleanup)
- Built-in **dashboard** at `/` for watching live imports and inspecting what Kuali sent
- **Backup retention** + automatic purge of old dated folders

---

## Integration flow

At a high level, the integration works like this:

1. A Kuali Build workflow sends `POST /api/kuali-onbase-import` with the Kuali document id, the target OnBase DIP folder, the OnBase document type, and any keyword pairs to write into the DIP index file.
2. This API authenticates the request with `Authorization: Bearer <key>`, validates the target path against `Import__AllowedTargetRoots`, and creates a job record plus event timeline in SQLite.
3. The API calls Kuali's GraphQL API to load the document metadata and decide what to download:
   - `downloadMode=pdf` asks Kuali to export a single PDF and waits for Kuali's callback with the signed download URL.
   - `downloadMode=attachments` downloads the raw attachment files directly from Kuali in their original formats.
4. The downloaded file or files are staged locally, renamed into OnBase-safe filenames, copied into a dated backup folder, then copied into the target OnBase DIP folder.
5. The API writes the DIP index text file that OnBase uses for indexing, using the requested `onbaseDocType` plus any `KeywordKeyN` / `KeywordValueN` pairs.
6. If requested, the API then clears the attachment fields and/or deletes the source document in Kuali after the OnBase copy succeeds.
7. The dashboard at `/` reads the stored job and event data so operators can see what happened, inspect sanitized payloads, replay jobs, and troubleshoot failures without rerunning the Kuali workflow blindly.

In short: Kuali is the source system, this API is the orchestrator and audit trail, and OnBase DIP is the destination drop folder.

---

## Quick start — run locally

### Option 1: `dotnet run` from the terminal

```bash
git clone https://github.com/aburt1/kuali2OB
cd kuali2OB
export Auth__ApiKey="replace-with-a-local-api-key"
export Kuali__ApiToken="replace-with-your-kuali-api-token"
export Kuali__PublicBaseUrl="https://your-public-host"
export Kuali__CallbackSecret="replace-with-a-long-random-secret"
export Import__AllowedTargetRoots="/absolute/path/you-will-write-into"
dotnet run --project src/KualiOnBase.Api
```

Open <http://localhost:5050>, enter your API key, and the Auditor page will list every job as it runs — Kuali Build POSTs to `/api/kuali-onbase-import`, the jobs appear here with a per-event timeline. You can also replay any past job with edited params from its row.

To fire a one-off against a real Kuali document, POST to `/api/kuali-onbase-import` directly (see the curl example below).

Notes:

- `StartupValidator` will refuse to boot if `Auth__ApiKey`, `Kuali__ApiToken`, `Kuali__PublicBaseUrl`, `Kuali__CallbackSecret`, or `Import__AllowedTargetRoots` are missing or still set to placeholders.
- If you want the local SQLite DB and backup folder somewhere explicit, also set `Database__Path` and `Backup__RootPath`.
- Running locally, Kuali cannot reach your machine for the PDF export callback unless you expose it publicly. For a real end-to-end test, publish the app behind a publicly reachable HTTPS URL and set `Kuali__PublicBaseUrl` to that public URL.

### Option 2: run from Visual Studio / IIS Express

The repo already includes [launchSettings.json](/Users/aburt1/Desktop/kuali2OB/src/KualiOnBase.Api/Properties/launchSettings.json:1) with:

- `http` profile on `http://localhost:5029`
- `IIS Express` profile on `http://localhost:5565`

Before launching from Visual Studio, set the same required configuration values as environment variables on your machine. `appsettings.Development.json` only contains logging overrides; it does not provide the required secrets or allowed target roots.

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
  Authorization: Bearer <your-auth-apikey>
```

Authentication is bearer-only. Kuali Build's built-in **"Bearer authentication"** option on the HTTP Action step wires straight through — paste the key into Kuali's bearer-token field and leave the custom-headers section alone. Every example below uses the same `Authorization: Bearer …` form; no `X-Api-Key` path exists.

### Parameters

| Name | Required | Type | Notes |
|---|---|---|---|
| `documentId` | yes | string | Kuali Build document id |
| `onbaseDocType` | yes | string | Value for the `ONBASE_DOC_TYPE` index line |
| `targetFolderPath` | yes | string | UNC or mapped path; must exist and be writable by the API process |
| `downloadMode` | yes | `pdf` \| `attachments` | `pdf` = one PDF from Kuali's `exportDocument` (tenant setting decides whether attachments are merged in — see **Kuali tenant prerequisite** below). `attachments` = raw attachment files in their original formats (.docx/.jpg/.pdf/…). |
| `deleteAttachments` | yes | bool | If true, clears attachment fields on the Kuali document after a successful copy. **Requires** the Kuali form setting "Ignore required field validation on save" — see below. |
| `deleteDocument` | no | bool (default false) | If true, deletes the Kuali document after a successful copy |
| `KeywordKey1..20` / `KeywordValue1..20` | no | string pairs | Extra `KEY: VALUE` lines in the DIP index; incomplete pairs are ignored |

### Response codes

| Code | Meaning | Body |
|---|---|---|
| `200 OK` | Import completed | `{ jobId, status: "Succeeded", files: [...], backupFolder }` |
| `202 Accepted` | Transient failure — queued for retry | `{ jobId, status: "Retrying", attempt, nextAttemptAt }` |
| `400 Bad Request` | Validation error | error message |
| `401 Unauthorized` | Missing/bad `Authorization: Bearer` header | |
| `429 Too Many Requests` | Per-key rate limit exceeded (60/min on `POST /api/kuali-onbase-import`) | |
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
  -H "Authorization: Bearer $AUTH_APIKEY"
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

---

## Kuali form prerequisite — "Ignore required field validation on save"

Required only when `deleteAttachments=true`.

The clean-up step calls Kuali's `updateDocument` mutation to null out each attachment field on the document. If any of those fields are marked **required** on the Kuali form (the usual case), Kuali rejects the update with a validation error — you'll see something like `ValidationError: field is required` in the job's `lastError`.

**Fix:** in the Kuali form editor, open **Form → Settings** and enable **"Ignore required field validation on save"**. That flag tells Kuali to allow server-side writes (including ours) to leave required fields empty. End-user form submissions are unaffected — that path runs a separate validation layer.

Without this flag, the import still delivers the file to OnBase and takes the backup — only the post-import `deleteAttachments` step fails. The API surfaces the error with a hint pointing to this setting.

Leave the flag off if you don't use `deleteAttachments` or if your attachment fields aren't required.

### Diagnostic probe

When a new tenant behaves differently, probe it empirically without running a full import:

```bash
curl -X POST "https://<host>/api/diag/kuali-probe-export" \
  -H "Authorization: Bearer $AUTH_APIKEY" \
  -H "Content-Type: application/json" \
  -d '{"documentId":"<some-doc-id>","options":["Combined"]}'
```

Returns `{ documentId, sentOptions, sizeBytes, sha256, durationMs }`. Sweep different `options` arrays and compare sizes — same size = no effect, larger size = something was merged in. (The probe intentionally does **not** return the signed URL — those are for server-internal use only.) Also available: `GET /api/diag/db-status` for persistence checks. The probe endpoint shares the same per-bearer rate limit as `/api/kuali-onbase-import` (60 req/minute) and size-caps the downloaded body at 200 MB to stop scripted abuse from exhausting container memory or blowing through your Kuali export quota.

---

## Wiring to Kuali Build

In the Kuali Build form/workflow editor, add an **HTTP Action** step to the approval flow:

| Field | Value |
|---|---|
| Method | `POST` |
| URL | `https://your-host/api/kuali-onbase-import?documentId={{document.id}}&onbaseDocType=IT - Access&targetFolderPath=\\onbase-prod\DIP\incoming&downloadMode=pdf&deleteAttachments=true&deleteDocument=false&KeywordKey1=Department&KeywordValue1={{data.department}}` |
| Authentication | **Bearer authentication** — paste `<your-auth-apikey>` into Kuali's bearer-token field. |

`{{document.id}}` is Kuali's template token — it gets substituted at runtime. Form-field values follow the same `{{data.<fieldName>}}` pattern. Static params (doc type, target folder, mode) are hardcoded per workflow.

### How the callback works

For PDF export, Kuali renders asynchronously and calls *back* to this API when the signed URL is ready. You must:

1. Set `Kuali:PublicBaseUrl` to a URL Kuali can reach (e.g. your public Docker host name).
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
| `Kuali__PublicBaseUrl` | `Kuali:PublicBaseUrl` | *empty* | Public URL Kuali callbacks reach (e.g. `https://kuali2ob.your-public-host`) |
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
| `Import__AllowedTargetRoots` | `Import:AllowedTargetRoots` | *empty — rejects every request* | Semicolon-separated list of absolute path prefixes that `targetFolderPath` is allowed to land under. Fail-secure: without at least one root configured the API refuses to start. Example: `/mnt/onbase-drop;\\\\onbase-prod\\DIP`. Prevents Kuali workflow authors from writing to arbitrary locations (another tenant's share, `/etc`, a container root mount, etc.). |
| `Ui__Enabled` | `Ui:Enabled` | `true` | Serve the dashboard at `/`. Set to `false` to hide it in production if you only want API access. |
| `Notifications__Email__Enabled` | `Notifications:Email:Enabled` | `false` | Send an email when a job hits terminal `Failed`. |
| `Notifications__Email__SmtpHost` | `Notifications:Email:SmtpHost` | *empty* | SMTP relay hostname |
| `Notifications__Email__SmtpPort` | `Notifications:Email:SmtpPort` | `25` | |
| `Notifications__Email__SmtpUsername` / `SmtpPassword` | same | *empty* | Optional SMTP auth |
| `Notifications__Email__UseSsl` | `Notifications:Email:UseSsl` | `false` | |
| `Notifications__Email__From` | `Notifications:Email:From` | *empty* | Envelope sender (required when Enabled) |
| `Notifications__Email__To` | `Notifications:Email:To` | *empty* | Comma-separated recipients (required when Enabled) |

**Boot-time validation.** The API refuses to start if any of `Auth:ApiKey`, `Kuali:BaseUrl`, `Kuali:ApiToken`, `Kuali:PublicBaseUrl`, `Kuali:CallbackSecret`, `Backup:RootPath`, or `Import:AllowedTargetRoots` is missing or still set to a known placeholder (`CHANGEME`, `PLACEHOLDER`, `TODO`, `FIXME`, …). Secrets shorter than 16 characters are also rejected. URLs must be valid `http(s)://` absolute URLs, and every `Import:AllowedTargetRoots` entry must be an absolute path. Catches the most common deploy mistakes (env vars not injected, weak placeholder left in, a single typoed path) at deploy time instead of on the first real request.

---

## Deployment — Docker

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

Run notes:

1. Mount `/data` for SQLite persistence.
2. Mount `/backup` for dated backup copies.
3. Mount your OnBase drop location and make sure it is included under `Import__AllowedTargetRoots`.
4. Provide the required env vars at container start.
5. Expose the container behind a public hostname and use that hostname as `Kuali__PublicBaseUrl`.

### Deployment — IIS (Windows Server)

This app can also run behind IIS using the ASP.NET Core Module.

1. Install prerequisites on the IIS server:
   - IIS with the `Web Server` role
   - ASP.NET Core Hosting Bundle for .NET 8
   - Access from the server to your OnBase target share and any backup/data folders

2. Publish the app:

```powershell
dotnet publish .\src\KualiOnBase.Api\KualiOnBase.Api.csproj `
  -c Release `
  -o C:\inetpub\kuali2ob\publish
```

`dotnet publish` will generate the `web.config` IIS needs in the publish folder.

3. Create writable folders on the server, for example:

```powershell
New-Item -ItemType Directory -Force C:\inetpub\kuali2ob\data
New-Item -ItemType Directory -Force C:\inetpub\kuali2ob\backup
```

4. Set required configuration as **system environment variables** for the IIS worker process, for example:

```powershell
[Environment]::SetEnvironmentVariable("Auth__ApiKey", "replace-with-api-key", "Machine")
[Environment]::SetEnvironmentVariable("Kuali__ApiToken", "replace-with-kuali-token", "Machine")
[Environment]::SetEnvironmentVariable("Kuali__PublicBaseUrl", "https://your-public-iis-hostname", "Machine")
[Environment]::SetEnvironmentVariable("Kuali__CallbackSecret", "replace-with-a-long-random-secret", "Machine")
[Environment]::SetEnvironmentVariable("Import__AllowedTargetRoots", "\\onbase-prod\DIP", "Machine")
[Environment]::SetEnvironmentVariable("Database__Path", "C:\inetpub\kuali2ob\data\kuali-onbase.db", "Machine")
[Environment]::SetEnvironmentVariable("Backup__RootPath", "C:\inetpub\kuali2ob\backup", "Machine")
```

5. In IIS Manager:
   - Create a new **Application Pool**
   - Set **.NET CLR version** to **No Managed Code**
   - Set the pool identity to an account that can read/write the target share, DB path, and backup path
   - Create a new **Site** (or Application) pointing to `C:\inetpub\kuali2ob\publish`

6. Grant filesystem/share permissions:
   - IIS app-pool identity must be able to write `Database__Path`
   - IIS app-pool identity must be able to write `Backup__RootPath`
   - IIS app-pool identity must be able to write every folder under `Import__AllowedTargetRoots`

7. Restart IIS after setting environment variables:

```powershell
iisreset
```

8. Verify:
   - browse to `https://your-public-iis-hostname/health`
   - browse to `https://your-public-iis-hostname/health/ready`
   - open the dashboard at `https://your-public-iis-hostname/`

IIS-specific notes:

- `Kuali__PublicBaseUrl` must be the externally reachable IIS URL, not `localhost`.
- If you host under HTTPS termination in IIS, keep the public URL HTTPS so Kuali's callback URL remains valid.
- If the app pool identity is a domain/service account for SMB access, make sure both NTFS and share permissions allow writes.

---

## Operations

**Health checks.** Point Docker health checks, your reverse proxy, or any orchestrator at these:

| Endpoint | Purpose | Codes |
|---|---|---|
| `GET /health` | Liveness — is the process up? | `200 {status:"ok"}` |
| `GET /health/ready` | Readiness — can the app actually serve work? Opens the DB and writes a probe file to `Backup:RootPath`. | `200 {status:"ready"}` or `503 {status:"not_ready", problems:[...]}` |

Neither requires an API key.

**Rate limit.** `POST /api/kuali-onbase-import` is capped at **60 requests per minute per API key** (fixed window, partitioned by the bearer token). Over-limit requests get `429 Too Many Requests`. Well above any real Kuali workflow cadence; only trips on accidental loops (stuck workflow, dashboard-replay spam). Auth runs before the limiter, so unauthenticated requests can't spray unique tokens to exhaust the partition dictionary.

**Failure email.** When a job reaches terminal `Failed` (either immediately or after exhausting `Retry:MaxAttempts`), the API sends an email to `Notifications:Email:To` listing the job id, document id, attempts, and last error. Configure the `Notifications__Email__*` env vars above. Send failures are logged but never bubble up — a broken SMTP relay won't obscure the underlying job failure. 4xx-class failures (validation, missing target folder) don't trigger notifications — those are caller-fixable and the HTTP response already carries the detail.

**SQLite VACUUM.** `BackupCleanupWorker` runs daily, prunes succeeded jobs older than `Retry:SucceededJobRetentionDays`, and only then runs `VACUUM` to reclaim disk. Keeps the DB file compact without rewriting it on days nothing was pruned.

---

## Auditor / Visualizer (dashboard)

Open the root URL (default `/`). On first visit, click **API key** and paste your `Auth__ApiKey`. The page is read-first — it shows what the API actually did against Kuali for every job Kuali Build fires at it:

- Lists the most recent 100 jobs (polls every 3s)
- Click a row to expand: left pane = metadata, right pane = **event timeline** showing every stage (Kuali fetch, signed URL, downloads, rename, copy, index write, success/fail)
- Each event has a `payload` toggle that reveals sanitized JSON — attachment metadata, file byte counts, and the full DIP index file content. Sensitive upstream URLs are redacted before they reach the browser.
- **Replay with changes** — every job row exposes a button that re-opens its parameters in a dialog; edit anything (target folder, keywords, delete flags, download mode) and re-submit. Useful for reissuing a failed job after fixing its inputs, or for testing a param change against the same document without touching Kuali Build's workflow config.

To disable the page entirely in production: `Ui__Enabled=false`.

---

## Project layout

```
src/KualiOnBase.Api/
  Backend/                 All C# backend code
    Configuration/Configuration.cs  Config option classes + startup validation
    Features/Import/ImportFeature.cs  Import endpoint, retry, cleanup, backup
    Features/Jobs/         Backend job dashboard/file APIs
    Features/Kuali/        Backend Kuali GraphQL/download/callback integration
    Features/Notifications/ Backend failure email notifications
    Infrastructure/        Auth middleware + Db + SQL migrations
  Frontend/index.html      Auditor / visualizer page (vanilla HTML/CSS/JS)
  Properties/launchSettings.json
tools/probe-kuali-schema.sh  One-shot GraphQL introspection against your tenant
```

---

## Design decisions & security rationale

### Three distinct secrets, one per trust boundary

| Secret | Trust boundary | Why separate |
|---|---|---|
| `Auth__ApiKey` | Kuali → this API | Authenticates inbound requests. Sent as `Authorization: Bearer <key>` on `/api/*`. |
| `Kuali__ApiToken` | This API → Kuali | Bearer token for Kuali's GraphQL. Different scope, rotated in Kuali's admin UI, should never equal the inbound key. |
| `Kuali__CallbackSecret` | This API ↔ itself | HMAC-SHA256 key to sign callback URLs. See below. |

Compromising one secret must not compromise the others. They live in separate env vars for exactly this reason.

### HMAC-signed callback URL (not an API key)

Kuali's `exportDocument` mutation is asynchronous — Kuali renders the PDF and POSTs the signed S3 URL back minutes later to a callback URL we supply. That callback endpoint has two constraints:

1. It must be publicly reachable (Kuali's servers hit it from AWS us-west-2).
2. It cannot require `Authorization: Bearer <key>` — Kuali doesn't know our API key.

Left unprotected, anyone on the internet who guesses a correlation id could POST a malicious URL and trick the API into downloading an attacker-controlled PDF into the OnBase drop folder — a document-spoofing attack against OnBase itself.

**Fix:** every callback URL we hand to Kuali is signed: `?sig = HMAC-SHA256(correlationId, CallbackSecret)`. The callback endpoint recomputes the HMAC and rejects any request whose `sig` doesn't match. Only code that knows `CallbackSecret` can forge a valid signature. Kuali just echoes the URL back verbatim — it never sees or handles the secret.

Why not just put `Auth__ApiKey` in the URL? A single static key in logs everywhere (Kuali side, proxy logs, browser history) works for every callback forever. HMAC binds each signature to one specific correlation id — a leaked URL is useless for forging a different callback.

### Callback endpoint lives outside `/api/*` on purpose

`ApiKeyMiddleware` only guards `/api/*`. The callback endpoint is at `/kuali-export-callback/{id}` so it bypasses the middleware entirely. The compensating control is the HMAC above. This is an explicit design choice: the middleware stays simple and doesn't need a carve-out for unauthenticated traffic.

### Separate client for downloading signed URLs

When downloading from the signed URL Kuali returns, `KualiClient` uses a separate named `HttpClient` rather than the typed `IKualiClient` instance that carries the Bearer token. The signed URL already contains its own auth; sending our Kuali `Authorization` header to a third-party endpoint would needlessly expose the token. Relative same-origin Kuali downloads still use the authenticated client, but absolute external URLs do not.

### Synchronous happy path, asynchronous retry fallback

The API responds `200 OK` only after the file lands in the OnBase folder. If transient failure strikes (Kuali 5xx after Polly retries, network glitch, filesystem flakiness) the job is marked `Retrying` and the API returns `202 Accepted`. The `RetryWorker` drains the queue with exponential backoff. This keeps Kuali's side simple — they don't have to poll us or handle webhooks — while still tolerating infrastructure hiccups.

Non-transient failures (`400` from Kuali, invalid target path, validation errors) are marked `Failed` and return 4xx/500 immediately. No retry.

### Two layers of retries, each for different failures

- **Polly policy** on `HttpClient` — 3 attempts with exponential backoff **inside a single API call**, for transient HTTP errors against Kuali. Caller never sees the retry.
- **RetryQueue + RetryWorker** — **across calls**, for any job that failed even after Polly gave up. Up to `MaxAttempts` with longer backoff. Survives a process restart.

### SQLite + Dapper, not EF + Postgres

One process, one file, one binary. No external dependencies, no ORM ceremony. Job history survives restarts, migrations are forward-only (embedded `*.sql` tracked in a tiny `__Migrations` table). If scale ever demands it, swapping to Postgres is a single connection-string change plus a Dapper dialect tweak — but YAGNI until then.

### Event log per job, exposed to the dashboard

Every stage (`DocumentFetched`, `ExportRequested`, `ExportCallbackReceived`, `PdfDownloaded`, `AttachmentDownloaded`, `FilesRenamed`, `BackupCreated`, `FilesCopiedToTarget`, `IndexFileWritten`, `AttachmentsCleared`, `DocumentDeleted`, `ImportSucceeded`/`ImportFailed`) records a row in `JobEvents` with structured payload data such as attachment metadata, byte counts, and the exact DIP index file content written to disk.

This is the audit trail. It lets you confirm *after the fact* what Kuali actually sent and what landed in OnBase without re-running a job. When payloads are returned to the browser, sensitive upstream URLs are redacted.

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

**401 from your own API in the dashboard** — the bearer token the browser stored no longer matches `Auth__ApiKey`. Click **API key** and re-enter it.

**Callback never arrives** — confirm `Kuali__PublicBaseUrl` is actually reachable from Kuali's network (they're in AWS us-west-2). A public hostname works; localhost does not.

**`downloadMode=pdf` is only returning the form render — attachments are missing** — the Kuali tenant setting "Include PDFs uploaded through the form" is off. See [Kuali tenant prerequisite](#kuali-tenant-prerequisite--include-pdfs-uploaded-through-the-form) above. Either turn the setting on (simplest), or switch the workflow to `downloadMode=attachments` for raw attachment files.

**`deleteAttachments=true` job fails with "required" / "validation" in `lastError`** — the Kuali form doesn't have "Ignore required field validation on save" enabled. See [Kuali form prerequisite](#kuali-form-prerequisite--ignore-required-field-validation-on-save) above. The file was still delivered to OnBase and backed up; only the clean-up call failed.

**Dashboard shows the wrong files** — check the event log for the job. The `FilesRenamed` event shows staged → final mapping; `IndexFileWritten` shows the exact text written to disk.

---

## License

Internal CSUB project; not for public distribution.
