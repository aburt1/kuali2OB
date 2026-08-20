# Developer Guide

Reference for developers modifying this codebase. For usage and deployment, see
[README.md](README.md).

## Overview

The service receives a request from a Kuali Build workflow, retrieves the document
and its attachments from Kuali, writes the files and an index file into an OnBase
DIP drop folder, stores a dated backup, and optionally clears attachments or deletes
the document in Kuali.

## Architecture

Two structural facts determine how the rest of the code is organized.

1. The import endpoint does not perform the import. It validates the query string,
   inserts a job row with status `Queued`, and returns `202 Accepted`. No download,
   copy, or delete occurs on the request thread.
2. `RetryWorker` is the only caller of `ImportService.RunAsync`. It performs all
   import work on a background timer, in a new dependency-injection scope, reading
   job rows from SQLite. It handles first attempts as well as retries.

## Project structure

| Path | Contents |
|--|--|
| `Program.cs` | Composition root. Configuration binding, service registration, HTTP clients, rate limiter, middleware order, endpoint mapping. 169 lines. |
| `CONTROLLERS/ApiController.cs` | All endpoints, as static methods mapped from `Program.cs`. No MVC controllers are used. |
| `MODELS/AppSettings.cs` | Settings tree and `StartupValidator`. |
| `MODELS/ImportModels.cs` | Job and event row types. Corresponds to the database schema. |
| `SERVICES/ImportService.cs` | Import pipeline, plus `BackupService`, `IndexFileBuilder`, `FileNameSanitizer`. |
| `SERVICES/ImportRequestValidator.cs` | Request validation, `OutboundUrl` guard, `PermanentImportException`. |
| `SERVICES/JobsService.cs` | `JobsService`, `RetryWorker`, `BackupCleanupWorker`, `JobEventLog`. |
| `SERVICES/KualiService.cs` | Kuali GraphQL client, export callback store, download resolution, outbound status notifier. |
| `SERVICES/Security.cs` | `ApiKeyMiddleware` and bearer token extraction. |
| `SERVICES/Notifications.cs` | SMTP failure notifications. |
| `SERVICES/Data/Db.cs` | Connection settings and migration runner. |
| `SERVICES/Data/Migrations/*.sql` | Schema migrations, embedded in the assembly. |
| `wwwroot/index.html` | Operator dashboard. Single self-contained file. No build step. |

Several classes share a single file. Search by type name rather than filename.

Folder names are uppercase (`CONTROLLERS`, `MODELS`, `SERVICES`). This is a naming
choice only and does not indicate an MVC project structure.

## Request lifecycle

Each step names the file it lives in, so this doubles as a path through the code.

| # | Step | Where |
|--|--|--|
| 1 | Bearer token checked for any path under `/api` | `SERVICES/Security.cs` |
| 2 | Query string validated; all violations returned together as plain-text `400` | `SERVICES/ImportRequestValidator.cs` |
| 3 | Job row inserted with status `Queued`; endpoint returns `202 Accepted`. Control leaves the HTTP pipeline here | `CONTROLLERS/ApiController.cs`, `HandleImport` |
| 4 | Background worker polls for due jobs, opens a scope, calls `ImportService.RunAsync` | `SERVICES/JobsService.cs`, `RetryWorker` |
| 5 | Target path validated, write-probe file created and deleted | `SERVICES/ImportService.cs`, `DeliverAsync` |
| 6 | Document and attachment metadata fetched from Kuali | `SERVICES/KualiService.cs`, `GetDocumentAsync` |
| 7 | Files downloaded and staged in a temp directory | `SERVICES/ImportService.cs`, `DownloadAsync` |
| 8 | Files renamed, copied to the backup folder, copied to the target folder | `SERVICES/ImportService.cs` |
| 9 | Index file written | `SERVICES/ImportService.cs`, `IndexFileBuilder.Build` |
| 10 | Optional cleanup in Kuali, or job marked `Succeeded` | `SERVICES/ImportService.cs`, `CompleteCleanupAsync` |
| 11 | Terminal status POSTed back to the caller's `X-Response-URL` | `SERVICES/KualiService.cs`, `KualiResponseUrlNotifier` |

Status transitions are written in one place: `RunJobAsync` in `SERVICES/JobsService.cs`.

## Download modes

| Mode | Behavior |
|--|--|
| `attachments` | Downloads attachment files directly from Kuali in their original formats. Synchronous. |
| `pdf` | Calls Kuali's `exportDocument` mutation, which renders asynchronously. Kuali POSTs a signed URL to `/kuali-export-callback/{correlationId}`, which the service then downloads. |

For `pdf`, the `ExportCallbacks` database row is the only channel between the
callback request and the waiting job. They run on different threads, scopes, and
connections.

## Output format

The index file is the artifact OnBase consumes. `IndexFileBuilder.Build` emits one
record block per file, separated by blank lines, with the keyword pairs repeated in
each block.

```
ONBASE_DOC_TYPE: IT - Access
FILENAME: 68fa2b19c0f15c00281b3e42.docx
EXTERNAL_SOURCE: KUALI BUILD
EXTERNAL_SOURCE_REF: 68fa2b19c0f15c00281b3e42_1
Department: ITS

ONBASE_DOC_TYPE: IT - Access
FILENAME: 68fa2b19c0f15c00281b3e42.pdf
EXTERNAL_SOURCE: KUALI BUILD
EXTERNAL_SOURCE_REF: 68fa2b19c0f15c00281b3e42_2
Department: ITS
```

## Local setup

Tests require no configuration:

```bash
dotnet test
```

Running the application requires five environment variables. `StartupValidator`
throws at startup if any is missing or set to a placeholder value.

| Variable | Purpose |
|--|--|
| `Auth__ApiKey` | Bearer token required on `/api/*` requests |
| `Kuali__ApiToken` | Credential for Kuali's GraphQL API |
| `Kuali__PublicBaseUrl` | URL Kuali uses for the export callback |
| `Kuali__CallbackSecret` | HMAC key for signing callback URLs |
| `Import__AllowedTargetRoots` | Semicolon-separated list of allowed write locations |

```bash
dotnet run --project src/KualiOnBase.Api
```

Notes:

- `appsettings.json` contains no `Import` section. A fresh clone fails to start
  until `Import__AllowedTargetRoots` is set.
- `global.json` pins the SDK to an exact version. A different 8.0 feature band fails
  to resolve.
- `launchSettings.json` opens `/swagger`, which is registered only when
  `ASPNETCORE_ENVIRONMENT=Development`.

## Where to start

The lifecycle table above is the shortest route through the system. Following it for
a `downloadMode=attachments` job covers the main path; the `pdf` export and cleanup
coordination are the two areas that need separate reading, and both are described
below.

`Program.cs` is 169 lines of wiring and is worth reading first for context on what
is registered and in what order.

## Cleanup coordination

A single Kuali approval can send two requests for the same document, one per
download mode. Both must complete before the source document is deleted. The two
requests are independent: neither knows the other exists, they can arrive
milliseconds apart, and the second row may not exist when the first job runs.
Coordination uses only rows in SQLite — there is no lock, queue, or shared state.

The implementation follows four rules:

1. Cleanup never runs on the pass that delivered the files. The first pass always
   defers for two minutes. This allows a sibling request that has not yet been
   inserted to become visible.
2. Two jobs belong to the same logical request if their `CreatedAt` values are
   within two minutes of each other.
3. The flags that execute are the union of the delete flags across all delivered
   jobs in that window, not the flags on the current job.
4. Cleanup runs at most once. If any job in the window already reached `Succeeded`
   while carrying a delete flag, cleanup has already occurred and the current job
   exits without repeating it.

Two consequences are visible in the dashboard and are expected:

- A job waiting out its grace period is stored with status `Retrying` and the
  deferral message in `LastError`. It displays as a retrying job with an error.
- `AttemptCount` increments on each deferral, so a job with `deleteDocument=true`
  typically reaches `Succeeded` on attempt 2.

`tests/KualiOnBase.Api.Tests/Import/CleanupCoordinationTests.cs` covers the three
cases: single request, paired requests, and a sibling outside the window.

## Adding a secret provider

Secrets are read from environment variables (on IIS, the `<environmentVariable>`
entries in `web.config`). `SERVICES/Secrets.cs` defines a seam so a vault can be
added later without changing `AppSettings`, `StartupValidator`, or any consumer.

The provider runs as the last configuration source, so its values override earlier
ones. `Secrets:Provider` selects it and defaults to `Environment`. An unrecognised
name throws at startup rather than falling back, so a typo cannot leave the app
reading secrets from the wrong place. The active provider is written to the startup
log.

To add one, for example Thycotic/Delinea Secret Server:

1. Implement `ISecretProvider`. `Load()` returns configuration keys in standard
   notation — the constants in `SecretKeys` are the full list of values a provider
   is expected to supply. Return `null` for a key the vault does not have; the
   earlier configuration value is kept rather than blanked.
2. Register the name in `SecretConfigurationExtensions.ResolveSecretProvider`.
3. Set `Secrets__Provider` to that name on the server.

`SecretKeys.All` contains only genuine secrets. Deployment settings — paths, URLs,
`Import:AllowedTargetRoots` — stay in `web.config` deliberately; moving them into a
vault adds a failure mode without reducing exposure.

Two constraints worth knowing before writing a provider:

- The provider needs its own credential to authenticate to the vault. Integrated
  Windows authentication as the app pool identity avoids storing one; an API key in
  `web.config` used to fetch other secrets from `web.config` does not improve much.
- `ApiKeyMiddleware` reads `IOptionsMonitor<AppSettings>.CurrentValue` per request
  rather than capturing the key at construction, so a provider that refreshes values
  can rotate the API key without an application restart.

## Implementation notes

- `InternalsVisibleTo` in the `.csproj` grants the test project access to internal
  members. Tests call `ResolveDownloadUrl`, `ParsePayload`, `IndexFileBuilder`, and
  `FileNameSanitizer` through it. Widening visibility for testing is unnecessary.
- `ProducedFiles` includes the index `.txt` file as its final element.
  `/api/jobs/{id}/files/{index}` is a positional index into that array.
- Content files are copied to the backup folder before the target folder. The index
  file is written in the opposite order — target first, then backup — because
  writing the index is what triggers the DIP sweep.
- Backup copies retain staged filenames (`export-<id>.pdf`, `attach-0.docx`) while
  the target folder receives final names (`<id>.pdf`). The index file references the
  target names. Backups preserve content but cannot be copied into a DIP folder
  without renaming.
- There is no per-chunk idle timeout on downloads. `HttpClient.Timeout` covers the
  body read even when `ResponseHeadersRead` is used: 60 seconds for same-origin
  downloads, 5 minutes for signed export URLs. A comment in `Program.cs` stating
  otherwise is inaccurate.
- `ApiKeyMiddleware` is registered before the rate limiter. The limiter partitions
  by bearer token, so unauthenticated requests must be rejected first.
- Validation failures throw `PermanentImportException` and are not retried.
- Static files are served before authentication, so the dashboard HTML loads
  anonymously and then supplies the API key on its own requests.

## Testing

xunit, with hand-written fakes. 76 tests, no external dependencies.

| File | Coverage |
|--|--|
| `Import/ImportRequestValidatorTests.cs` | Query contract: allowed roots, error aggregation, unresolved template tokens, boolean parsing, keyword slots, unknown parameters. |
| `Import/OutboundUrlAndIndexTests.cs` | Outbound URL guard, index directive injection, document id anchoring. |
| `Import/CleanupCoordinationTests.cs` | Deferred cleanup across sibling jobs. |
| `Kuali/DownloadUrlResolutionTests.cs` | Download URL resolution and credential boundary. |
| `Kuali/KualiClientTests.cs` | GraphQL response parsing. |
| `Jobs/JobsEndpointTests.cs` | Payload sanitization. Class is named `ApiControllerTests`. |
| `Configuration/ConfigurationTests.cs` | Settings binding and project layout. |

`CleanupCoordinationTests.cs` contains a `CleanupHarness` that builds a real SQLite
database on a temp file, runs the real migrations, and wires the real services with
only `IKualiClient` faked. Its `SetJobWindow` helper rewrites timestamps directly in
SQL, which is how time-dependent behavior is tested without waiting.

`RetryWorker` has no direct test coverage. The harness reimplements its persistence
logic, so changes to `RetryWorker`'s write-back are not caught by these tests.
