# Online playlists: Microsoft sign-in, standalone Functions, URL import and saved profiles

## What is ready

Local M3U upload/read, category search/selection, opt-in favourites and filtered
download remain available without an account or network connection once the app
has loaded. Authentication lives in a separate **Online playlists** section and
does not own or reset local file state. Favourites keep the existing
`m3u-split:favourites` key and exact category-name matching.

Phase 1 added Microsoft sign-in/sign-out and `GET /api/online/session`. The
endpoint returns `{ "tenantId": "...", "objectId": "...", "expiresAt": "..." }`
after verifying authorization.

Phase 2 adds **authenticated URL import** (see
[Phase 2: authenticated URL import](#phase-2-authenticated-url-import)). After
the API verifies access, the user pastes a provider playlist URL. The Function
App, not the browser, downloads the playlist into private storage and lists its
categories with channel counts. The user picks categories (search, select
all/none, favourites) and downloads a **server-generated** filtered playlist.
The full provider file is never sent to the browser.

Phase 3 adds **saved profiles with one-click refresh** (see
[Phase 3: saved profiles and one-click refresh](#phase-3-saved-profiles-and-one-click-refresh)).
A profile is a name, a provider URL and a set of chosen categories, stored on
the server. **Refresh** downloads the provider playlist again and regenerates
the filtered playlist on the server; **Download latest** saves the most recent
successful result at any time. A failed refresh keeps the last good playlist.
Stable TV feed URLs belong to a later phase and are not available yet.

The frontend stays at **https://goncalvesj.github.io/m3u-split/**, with Vite's
existing `/m3u-split/` base. Nothing installs or replaces a service worker or PWA
manifest. An already loaded app can continue processing local files offline;
this change does not add a new cold-start offline cache or change the browser's
existing installation/caching behavior. Microsoft sign-in and API verification
require a network connection.

No resources, app registrations, permissions or deployments are created by
checking out or building this code. Obtain approval before creating paid Azure
resources, changing a directory, granting consent or deploying. Supply actual
IDs from the chosen directory; there are no usable default identities.

## One-time Entra setup

Use **two separate application registrations in one explicitly chosen Entra
tenant**: the SPA is a public client and the API is the protected resource.
Both should use **Accounts in this organizational directory
only**. Other work/school or personal Microsoft accounts must first be invited
as guests into that tenant and then explicitly allowed by their **guest object
ID in this tenant**. This implementation deliberately does not use `common`,
`organizations`, email-based access rules or unrestricted multi-tenant access.

1. Register the **API** application. Record its Application (client) ID and the
   Directory (tenant) ID. Set its API access-token version to **2**
   (`api.requestedAccessTokenVersion: 2` in the manifest). Under **Expose an API**,
   use `api://<API-APPLICATION-CLIENT-ID>` as the application ID URI and add the
   enabled delegated scope **`access_as_user`**. Prefer administrator consent
   for this private application. The API registration needs no redirect URI,
   client secret or certificate for token validation.
2. Register the **SPA** application, separately from the API. Record its
   Application (client) ID. Under **Authentication**, add a **Single-page
   application (SPA)** platform, not a Web platform. Register these exact URIs:

   | Use | SPA redirect and post-logout URI |
   | --- | --- |
   | Existing GitHub Pages site | `https://goncalvesj.github.io/m3u-split/auth.html` |
   | Local Vite development | `http://localhost:5173/m3u-split/auth.html` |

   The same callback page handles popup sign-in and sign-out. No separate
   front-channel logout page is configured. Do not enable implicit access-token
   or ID-token grants, create a secret, or enable the legacy public-client
   password/device flows. MSAL Browser uses authorization code + PKCE.
3. Under the SPA's **API permissions**, add **My APIs -> your API -> Delegated
   permissions -> access_as_user**. Grant the required tenant administrator
   consent. No Microsoft Graph permission is needed. Optionally preauthorize
   this SPA for that scope on the API registration to avoid repeated consent
   prompts. This does **not** replace the API's account allowlist.
4. For each allowed person, obtain their **Object ID** from **Entra ID -> Users**
   in the selected tenant. Configure those IDs on the server. Do not use an
   email address, display name, app/service-principal object ID, ID-token `sub`,
   or a guest's home-directory object ID.

The SPA authority is fixed to the supplied tenant. The API checks the exact
v2 issuer `https://login.microsoftonline.com/<TENANT-ID>/v2.0`, the API
application GUID audience, RS256 signature using Microsoft's HTTPS discovery
and signing keys, `exp`/`nbf` with zero clock skew, `ver=2.0`, matching `tid`,
the required delegated `scp`, the SPA `azp`, and the user's allowed `oid`.
Keep the Function host's clock synchronized. ID tokens, Graph tokens,
application-only tokens and function keys are not API access tokens.

## Public Pages build settings

All `VITE_*` values are visible in the deployed JavaScript. They are public
configuration, **not secrets**. Set these four **repository Actions variables**
for `.github/workflows/deploy-pages.yml`:

| Variable | Required value when enabling online sign-in |
| --- | --- |
| `VITE_ENTRA_TENANT_ID` | The chosen tenant's Directory ID GUID |
| `VITE_ENTRA_SPA_CLIENT_ID` | The SPA's Application (client) ID GUID |
| `VITE_ENTRA_API_SCOPE` | `api://<API-APPLICATION-CLIENT-ID>/access_as_user` |
| `VITE_ONLINE_API_BASE_URL` | `https://<YOUR-FUNCTION-APP-HOST>/api/` |

Leave **all four unset** for a local-file-only build. Pages still builds and
deploys, and the online section explains that sign-in is not configured.
Partial or invalid configuration disables online sign-in with an explicit
message, not a permissive authentication fallback. Rebuild/redeploy Pages after
changing a public variable.

The callback is derived from the actual page origin and Vite base, not from a
relative `/api` proxy or an arbitrary environment redirect. The built artifact
contains a real `auth.html` entry at `/m3u-split/auth.html`; no SPA rewrite or
Pages 404 routing trick is required. Register another **exact** callback if you
intentionally use a different development port/origin. For Vite preview on
port 4173, that means `http://localhost:4173/m3u-split/auth.html` and an
additional API CORS origin `http://localhost:4173`.

Never put client secrets, Function keys, storage credentials, provider playlist
URLs/passwords or deployment credentials in any `VITE_*` variable. The frontend
sends an API **access token** in `Authorization: Bearer ...` to the explicit
backend base URL. It omits cookies, forbids HTTP redirect following for this
request, and does not send an email/identity parameter for authorization.

## Standalone Function App settings

Use an approved, separately provisioned **Azure Functions v4, .NET 9,
isolated-worker** Function App. The existing `net9.0` target remains unchanged.
Use a hosting plan/OS that supports this runtime and keep the supported .NET 9
runtime patched; reassess its support lifecycle before a later runtime upgrade.
This is not an Azure Static Web Apps managed API.

Set the following **Function App application settings**, never frontend
variables. Double underscores are the .NET configuration section separator:

| Server setting | Value |
| --- | --- |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | `~4` |
| `OnlineAuth__TenantId` | The same tenant GUID as the SPA authority |
| `OnlineAuth__ApiClientId` | The API Application (client) ID GUID, **not** the `api://...` URI |
| `OnlineAuth__SpaClientId` | The SPA Application (client) ID GUID; must differ from the API ID |
| `OnlineAuth__RequiredScope` | `access_as_user` (short delegated scope name, not full URI) |
| `OnlineAuth__AllowedObjectIds` | One or more user Object ID GUIDs in that tenant, comma-separated |
| `OnlinePlaylistStorage__blobServiceUri` | URL import: `https://<APP-DATA-ACCOUNT>.blob.core.windows.net` (see Phase 2) |
| `OnlinePlaylistStorage__queueServiceUri` | URL import: `https://<APP-DATA-ACCOUNT>.queue.core.windows.net` (see Phase 2) |
| `AzureWebJobs.Upload.Disabled` | `true` |
| `AzureWebJobs.Download.Disabled` | `true` |

All authentication/account values must be valid. An empty allowlist or any
invalid entry disables online access; valid entries are not silently accepted
from an otherwise invalid list. Configuration is read by the worker; restart it
after changing settings (Azure app-setting changes normally trigger restart).

URL import also reads the optional `OnlineImport__*` limits and accepts a
connection string instead of the two identity-based storage settings; see
[Import settings](#import-settings). Missing or invalid import settings make
the import endpoints answer `503 import_not_configured` and log the reason
without secrets. The session endpoint does not depend on them. The import queue
triggers need the same storage connection, so the Functions host reports errors
for those functions until it is configured.

Saved profiles use the same storage connection and `OnlineImport__*` download
limits, plus the optional `OnlineProfiles__MaxProfilesPerUser`; see
[Profile settings](#profile-settings). When any of them is missing or invalid,
the profile endpoints answer `503 profiles_not_configured` instead.

The Functions host also needs its normal **host storage** configuration
(`AzureWebJobsStorage`, or the hosting plan's supported identity-based
`AzureWebJobsStorage__...` configuration with the required storage roles).
Prefer managed identity where supported; if a connection string is necessary,
keep it only in server settings/Key Vault. Follow the selected plan's normal
content/deployment-storage requirements. Host storage is infrastructure, not
playlist storage: URL imports use the separate `OnlinePlaylistStorage`
connection described in Phase 2, never `AzureWebJobsStorage`. The hourly import
and profile cleanup timers use host storage only for the host's normal timer lock. Existing
`UseBlobStorage`, `StorageConnectionString` and `FilePath` settings belong to
disabled legacy functions and are not needed by the online functions.

The session function uses `AuthorizationLevel.Anonymous` **at the Function-key
layer only** so a SPA need not embed a shared Function key. `OnlineSession.Run`
unconditionally calls `OnlineAccess.AuthorizeAsync` before producing a
successful response. The Phase 2 import endpoints and Phase 3 profile endpoints
follow the same pattern: each calls the guard before any other work, maps denials identically and derives
data ownership only from the validated `tid`/`oid`. Future online functions
must do the same; copying the anonymous trigger without the guard would
be unsafe. This implementation does not rely on unsupported `[Authorize]`
attributes or ASP.NET middleware routing in the isolated worker.

The legacy `Upload`/`Download` triggers retain `AuthorizationLevel.Function`.
Keep the disable flags above set in local and deployed environments; they are
not user-owned authenticated playlist endpoints. Core Tools normally bypasses Function-key authentication and can execute even
disabled HTTP functions locally. Start the host with the explicit
`--functions` list shown below so legacy routes are not registered at all. In Azure, disabled endpoints are inaccessible without the privileged
host master key; never give that key to the SPA.

App Service Authentication/Easy Auth is not required: the application validates
the bearer token itself. If a platform authentication layer is added later,
configure it deliberately; it must not replace or bypass the application guard
or unexpectedly redirect API calls to an HTML login page.

## CORS: exact origins, not authentication

In the Function App's **API -> CORS** settings, remove wildcard origins and add
only origins that are actually needed:

- Production: **`https://goncalvesj.github.io`** (no `/m3u-split/` path).
- Local development against that API, if desired: **`http://localhost:5173`**.

Leave **Access-Control-Allow-Credentials disabled**. The Functions host handles
OPTIONS preflight for the `Authorization` and `Content-Type` request headers and
for the `POST` and `DELETE` methods used by URL import and saved profiles; no
extra CORS setting is needed for them. Saved profiles use only `GET`, `POST` and
`DELETE` with these same two request headers, so the CORS configuration does not
change. Do not add a second conflicting ASP.NET CORS implementation or
a wildcard header inside the function. Platform CORS cannot expose response
headers such as `Content-Disposition` to the page, so the browser chooses the
downloaded file name itself.
For local Core Tools, `local.settings.example.json` sets that one local origin.
CORS is not an account restriction: direct HTTP clients still need a valid
access token and an allowed identity.

## Phase 2: authenticated URL import

URL import runs in the same standalone Function App. The browser sends the
provider URL once. The server downloads, stores and filters the playlist and
returns only progress, category names with channel counts and the filtered
result. The full provider file never reaches the browser or the TV.

1. `POST /api/online/imports` validates the URL, stores a private import
   record, queues a message that holds **only IDs** (never the URL) and returns
   `202 Accepted` with the import status.
2. The `OnlineImportWorker` queue trigger downloads the playlist with the
   SSRF-safe fetcher and streams it into a private blob without buffering the
   whole file. It fails with `not_m3u` unless the first non-empty line is
   `#EXTM3U`, then stream-parses the stored copy into a category index. It
   saves progress (bytes, entries) every few seconds and always ends `ready` or
   `failed`.
3. The browser polls the status with backoff, then lists the categories with
   search, select all/none and the same favourites as the local workflow.
4. `POST .../playlist` streams the filtered M3U straight from blob storage. The
   browser saves it as `channels.m3u`, like the local workflow.

Category names are the exact `group-title="..."` values of `#EXTINF` lines:
untrimmed, case-sensitive, in first-seen order, with empty values excluded.
They match what the local parser shows, so favourites (exact names under
`m3u-split:favourites`) work in both workflows. The filtered playlist starts
with the source's `#EXTM3U` line, keeping attributes such as `url-tvg` or
`x-tvg-url`. It then contains every complete entry in a selected category, in
source order: the `#EXTINF` line (any duration), any directive lines before the
URL (for example `#EXTVLCOPT`, `#KODIPROP` or `#EXTGRP`) and the URL line. LF
and CRLF line endings, a UTF-8 BOM, blank lines and a missing final newline are
handled. Output is UTF-8 without a BOM, with `\n` line endings. An entry with a
line longer than 64 KiB is skipped.

### Import endpoints

Routes are relative to the API base URL (`.../api/`). Every import function
uses `AuthorizationLevel.Anonymous` at the Function-key layer only, calls
`OnlineAccess.AuthorizeAsync` before any other work, maps denials exactly like
the session endpoint (401 adds `WWW-Authenticate: Bearer`) and only touches data
stored under the caller's validated tenant and object IDs. Every response sends
`Cache-Control: no-store`. Request bodies must be JSON
(`Content-Type: application/json`). Errors are
`{ "error": "<code>", "message": "<text>" }`.

| Function | Method and route | Request body | Success |
| --- | --- | --- | --- |
| `OnlineImportStart` | `POST online/imports` | `{ "url": "..." }`, at most 16 KiB | `202` with the import status |
| `OnlineImportStatus` | `GET online/imports/{importId}` | None | `200` with the import status |
| `OnlineImportCategories` | `GET online/imports/{importId}/categories` | None | `200` with `{ "categories": [{ "name", "count" }], "entries" }` |
| `OnlineImportPlaylist` | `POST online/imports/{importId}/playlist` | `{ "categories": ["..."] }`: 1-10,000 exact names, at most 4 MiB | `200`, streamed `audio/x-mpegurl; charset=utf-8` |
| `OnlineImportDiscard` | `DELETE online/imports/{importId}` | None | `204` |

The import status has `id`, `status` (`queued`, `running`, `ready` or
`failed`), `stage` (`downloading` or `indexing` while running), `source` (only
`scheme://host[:port]`), `createdAt`, `updatedAt`, `expiresAt`,
`bytesDownloaded`, `totalBytes` (null when unknown, including compressed
responses), `maxBytes`, `entries`, `categoryCount` and, when failed,
`error: { "code", "httpStatus" }`. Import IDs are random GUIDs created by the
server. Only the canonical lowercase form returned by the API is accepted.

| Status | `error` | Meaning |
| --- | --- | --- |
| 400 | `invalid_request` | Start: missing, malformed, non-JSON or oversized request body |
| 400 | `invalid_url` | Not an absolute `http`/`https` URL, has user info or a fragment, or longer than 2,048 characters |
| 400 | `blocked_destination` | `localhost` or a literal non-public IP address; host names are resolved and checked by the worker |
| 400 | `invalid_selection` | Playlist: missing, malformed or oversized body, no or too many categories, an empty name, or none of the selected categories has channels |
| 401, 403, 503 | Guard codes | Exactly as for `GET /api/online/session` |
| 404 | `import_not_found` | Unknown, malformed, discarded or **another user's** import ID |
| 409 | `import_not_ready` | Categories or playlist requested before the import is `ready` |
| 410 | `import_expired` | The retention time has passed; the import is deleted |
| 503 | `import_not_configured` | Storage connection or `OnlineImport__*` settings missing or invalid |
| 503 | `import_unavailable` | Storage temporarily unavailable; retry |

A `failed` import reports one of these codes:

| Code | Meaning |
| --- | --- |
| `invalid_url` | The URL or its host name is not acceptable |
| `blocked_destination` | The host, or a redirect target, resolves to any non-public address |
| `invalid_redirect` | A redirect has no usable `Location`, or it is not an acceptable `http`/`https` URL |
| `too_many_redirects` | More than 5 redirects |
| `source_unreachable` | DNS, connection or TLS failure |
| `source_http_error` | The provider answered with a non-2xx status, reported in `httpStatus` |
| `source_timeout` | The overall or idle download timeout was reached |
| `source_too_large` | Larger than `OnlineImport__MaxDownloadMiB` (declared or counted) |
| `not_m3u` | The first non-empty line is not `#EXTM3U` |
| `no_categories` | No entry with a URL has a `group-title` category |
| `too_many_categories` | More than 10,000 categories, or more than 1,048,576 characters of names |
| `import_interrupted` | Processing stopped (host restart, stuck or poison message); import again |
| `internal_error` | Unexpected server error; logs contain no URL |

### Import settings

All settings are Function App application settings, never `VITE_*` variables.
Empty values use the default. Any invalid value disables URL import with
`503 import_not_configured`; the logged reason never contains secrets.

| Setting | Default | Allowed values and notes |
| --- | --- | --- |
| `OnlinePlaylistStorage__blobServiceUri` | None | Identity-based connection: `https://<APP-DATA-ACCOUNT>.blob.core.windows.net` |
| `OnlinePlaylistStorage__queueServiceUri` | None | Identity-based connection: `https://<APP-DATA-ACCOUNT>.queue.core.windows.net` |
| `OnlinePlaylistStorage__credential`, `OnlinePlaylistStorage__clientId` | System-assigned identity | For a user-assigned identity: `managedidentity` and that identity's client ID |
| `OnlinePlaylistStorage` | None | Alternative: a storage connection string, for example `UseDevelopmentStorage=true` for Azurite. It takes precedence over the identity settings |
| `OnlineImport__MaxDownloadMiB` | `300` | 1-2048. Counted after decompression |
| `OnlineImport__DownloadTimeoutSeconds` | `300` | 10-3600. Overall limit for the download, including redirects |
| `OnlineImport__IdleTimeoutSeconds` | `60` | 5-300, not above the download timeout. Limits each wait for a connection (at most 15 seconds), response headers or more data |
| `OnlineImport__RetentionHours` | `24` | 1-168. Time from the start of an import until it expires |
| `OnlineImport__UserAgent` | `m3u-split/1.0` | 1-256 printable ASCII characters. Some IPTV panels reject unknown agents |

The identity-based storage settings follow the Functions connection
conventions, so the queue trigger and the API's storage clients use the same
identity. Prefer them in Azure: no account key is needed. Keep a connection
string, if you must use one, only in server settings or Key Vault.

### App-data storage and roles

Use a **dedicated general-purpose v2 storage account** for playlist data,
separate from the Functions host storage. The code uses these resources:

| Resource | Name | Contents |
| --- | --- | --- |
| Private blob container | `online-imports` | `{tenantId}/{objectId}/{importId}/` with `import.json` (record), `source.m3u` (download) and `categories.json` (index) |
| Queue | `online-imports` | Work items `{ "v", "tenantId", "objectId", "importId" }`, never the URL |
| Queue | `online-imports-poison` | Messages that failed twice; `OnlineImportPoison` marks their imports failed |

The API creates the container (without public access) and both queues on first
use. If creation is not permitted, for example by a policy or network rule,
create them in advance with exactly these names. Disable anonymous blob access
on the account, require HTTPS and TLS 1.2 or later, and disable shared key
access when only identities use the account. Blob soft delete or versioning
would keep deleted imports, including provider URLs, for their own retention
period; leave them off for this account or keep their retention short.

Grant the **Function App's managed identity** these roles, scoped to the
app-data storage account only:

- **Storage Blob Data Contributor**: read, write and delete import blobs, and
  create the container.
- **Storage Queue Data Contributor**: create the queues, and send, receive and
  delete messages.

Role assignments can take several minutes to apply. Host storage
(`AzureWebJobsStorage`) needs its own roles; follow the hosting plan's
documentation for those.

### Lifecycle, limits and retention

- **One import per user.** Starting a new import discards that user's older
  imports once the new one is queued.
- **Expiry.** An import expires `OnlineImport__RetentionHours` (default 24)
  after it starts. Expired imports answer `410 import_expired` and are deleted
  when accessed. The `OnlineImportCleanup` timer runs hourly, at minute 15, and
  deletes every blob whose `expiresat` metadata has passed. A blob without that
  metadata is deleted one hour after its retention time, counted from its last
  modification. Queue messages use the same time-to-live.
- **Discard.** `DELETE` removes the import's record, downloaded source and
  category index at once. Signing out does not delete an import; it expires or
  is replaced by the next import.
- **Failures.** A failed import keeps only its status record, without the URL,
  until it expires; its downloaded data is deleted.
- **Stuck work.** A status request ends an import as `import_interrupted` and
  deletes its data when it stays `queued` longer than the download timeout plus
  10 minutes, or `running` without a progress update for longer than the idle
  timeout plus 3 minutes. While running, progress is saved at least every few
  seconds while data flows and before each redirect.
- **Retries.** `host.json` processes one import message at a time per instance
  (`batchSize: 1`, `newBatchThreshold: 0`). It moves a message to
  `online-imports-poison` after two attempts (`maxDequeueCount: 2`, with a
  30-second `visibilityTimeout` between them). Only storage errors before the
  download starts are retried. Download, content and unexpected errors end the
  import as `failed` at once. A redelivered message for an import that had
  already started ends it as `import_interrupted` instead of downloading it
  again.

As an optional backstop that works even while the Function App is stopped, add
a Blob lifecycle management rule to the app-data account. Keep
`daysAfterModificationGreaterThan` above the retention time in days (2 suits the
default 24 hours):

```json
{
  "rules": [
    {
      "enabled": true,
      "name": "delete-expired-online-imports",
      "type": "Lifecycle",
      "definition": {
        "filters": {
          "blobTypes": [ "blockBlob", "appendBlob" ],
          "prefixMatch": [ "online-imports/" ]
        },
        "actions": {
          "baseBlob": { "delete": { "daysAfterModificationGreaterThan": 2 } }
        }
      }
    }
  ]
}
```

### Timeouts and hosting plan

`host.json` sets `functionTimeout` to `00:10:00`, the maximum on the Consumption
plan. The default download timeout (300 seconds) leaves the rest of that time
for indexing. Flex Consumption, Premium and Dedicated plans allow longer function
timeouts. If you raise `OnlineImport__DownloadTimeoutSeconds`, keep it well
below `functionTimeout`, and raise that only where the plan allows. Azure's
front end allows an HTTP-triggered function about 230 seconds to respond,
whatever the function timeout, which may cut off very slow filtered playlist
downloads. The worker appends 4 MiB blocks to an append blob, and the playlist
endpoint streams from storage, so memory use does not grow with the playlist
size.

### Outbound request policy (SSRF)

The provider URL is untrusted. The reusable `SafeHttpFetcher` service enforces
the following for every download:

- Only `http` and `https` URLs, on any port, with no user info or fragment, at
  most 2,048 characters (8,192 for redirect targets). Plain `http` and ports
  such as 8080 are allowed because many IPTV panels use them.
- The host is resolved, and the request is refused if **any** resolved address
  is not public. Blocked IPv4 ranges: `0.0.0.0/8`, `10.0.0.0/8`,
  `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16` (including the
  `169.254.169.254` metadata endpoint), `172.16.0.0/12`, `192.0.0.0/24`,
  `192.0.2.0/24`, `192.88.99.0/24`, `192.168.0.0/16`, `198.18.0.0/15`,
  `198.51.100.0/24`, `203.0.113.0/24`, `224.0.0.0` and above (multicast,
  reserved and broadcast) and Azure WireServer `168.63.129.16`. Blocked IPv6:
  everything outside `2000::/3` (loopback, unspecified, ULA `fc00::/7`,
  link-local `fe80::/10`, site-local, multicast), documentation, benchmarking
  and ORCHID ranges, plus IPv4-mapped, IPv4-compatible, NAT64 (`64:ff9b::/96`),
  6to4 (`2002::/16`) and Teredo addresses that embed a blocked IPv4 address.
  Private endpoints and virtual-network hosts can therefore never be reached
  through an import.
- The TCP connection is pinned to the validated address
  (`SocketsHttpHandler.ConnectCallback`), which defeats DNS rebinding. HTTPS
  still uses the URL's host name for SNI and certificate validation.
- Redirects are not followed automatically. Up to 5 are followed manually, and
  each hop is validated again.
- No proxy, cookies, credentials or trace-context headers are used, and
  requests use HTTP/1.1.
- Size is limited by a `Content-Length` check and by counting bytes after
  gzip, deflate or Brotli decompression. Overall and idle timeouts apply.
- The first non-empty line must be `#EXTM3U` (a UTF-8 BOM and leading
  whitespace are allowed); otherwise the import fails with `not_m3u`.

### Privacy

Provider URLs usually contain the account user name and password.

- The full URL is stored only in the private `import.json` record of its import,
  and only until the download finishes or fails: the worker clears it before
  indexing and when an import fails. It is never put in queue messages, logs,
  exception messages, error responses or telemetry. The API returns only
  `scheme://host[:port]`.
- The `OutboundUrlRedaction` Application Insights telemetry processor reduces
  every outgoing HTTP dependency to `scheme://host[:port]`, and blanks it when
  it cannot be parsed, so automatic dependency tracking cannot record playlist
  paths or queries.
- Logs contain only import IDs, failure codes, HTTP status codes, exception
  type names and storage status codes.
- Discarding, replacing or expiring an import deletes its data.
- The browser does not save the URL in local or session storage. The input is
  cleared once the import starts.

## Phase 3: saved profiles and one-click refresh

A saved profile is a name, a provider URL and a set of chosen categories that
the server keeps for its owner. **Refresh** downloads the provider playlist
again and regenerates the filtered playlist on the server, so the browser
never handles the full provider file. **Download latest** saves the most
recent successful result whenever needed. Profiles run in the same standalone
Function App and storage account as URL import, with the same SSRF policy and
download limits.

1. `POST /api/online/profiles` validates the name and URL, saves the profile in
   private storage, queues its first refresh (the message holds **only IDs**,
   never the URL) and returns `201 Created`. Optional `categories`, such as the
   user's saved favourites, let that first refresh produce a playlist at once.
2. The `OnlineProfileRefreshWorker` queue trigger downloads the playlist into a
   temporary blob (`not_m3u` unless it starts with `#EXTM3U`), indexes its
   categories, saves the index and writes the filtered playlist as a new
   immutable output version. It saves progress every few seconds.
3. One ETag-guarded update of the profile record makes the new version
   current. Until then, and whenever a refresh fails, the previous output stays
   current and downloadable.
4. The browser polls only while a refresh is queued or running, with backoff
   from 1 to 5 seconds. `GET .../output` streams the current output.

Filtering, category names and the output format are exactly those of
[URL import](#phase-2-authenticated-url-import): exact `group-title` names, the
source's `#EXTM3U` line first, complete entries in source order, UTF-8 without
a BOM and `\n` line endings.

In the app, **Saved profiles** appears under URL import while access is
verified. It lists each profile with its host, refresh progress, last
successful refresh, output size and channel count, the latest error and any
missing categories, and shows how many of the allowed profiles are used. Each
profile has **Refresh now**, **Download latest**, **Edit categories** (search,
select all/none, apply favourites), **Rename**, **Change URL** (never prefilled)
and **Delete** (with confirmation). A new profile can start with the
favourites saved in the browser.

### Profile endpoints

Routes are relative to the API base URL (`.../api/`). Like the import
endpoints, every profile function uses `AuthorizationLevel.Anonymous` at the
Function-key layer only, calls `OnlineAccess.AuthorizeAsync` before any other
work, maps denials exactly like the session endpoint and only touches profiles
stored under the caller's validated tenant and object IDs. Every response sends
`Cache-Control: no-store`. Request bodies must be JSON
(`Content-Type: application/json`). Errors are
`{ "error": "<code>", "message": "<text>" }`.

| Function | Method and route | Request body | Success |
| --- | --- | --- | --- |
| `OnlineProfileList` | `GET online/profiles` | None | `200` with `{ "profiles": [...], "maxProfiles", "maxDownloadBytes" }`, oldest first, without `categories` |
| `OnlineProfileCreate` | `POST online/profiles` | `{ "name", "url", "categories"? }`: up to 10,000 names, at most 4 MiB | `201` with the profile; its first refresh is queued |
| `OnlineProfileGet` | `GET online/profiles/{profileId}` | None | `200` with the profile |
| `OnlineProfileCategories` | `GET online/profiles/{profileId}/categories` | None | `200` with `{ "categories": [{ "name", "count" }], "entries", "indexedAt", "selected" }` |
| `OnlineProfileSelect` | `POST online/profiles/{profileId}/categories` | `{ "categories": ["..."] }`: 1-10,000 exact names, at most 4 MiB | `202` with the profile; a refresh is queued |
| `OnlineProfileRename` | `POST online/profiles/{profileId}/rename` | `{ "name" }`, at most 16 KiB | `200` with the profile |
| `OnlineProfileSource` | `POST online/profiles/{profileId}/source` | `{ "url" }`, at most 16 KiB | `202` with the profile; a refresh is queued |
| `OnlineProfileRefresh` | `POST online/profiles/{profileId}/refresh` | None | `202` with the profile. While a refresh is active it is returned as is and nothing new is queued |
| `OnlineProfileOutput` | `GET online/profiles/{profileId}/output` | None | `200`, streamed `audio/x-mpegurl; charset=utf-8` with `Content-Length` |
| `OnlineProfileDelete` | `DELETE online/profiles/{profileId}` | None | `204` |

A profile has `id`, `name`, `sourceDisplay` (only `scheme://host[:port]`; the
URL itself is never returned), `createdAt`, `updatedAt` (name, URL or
selection changes), `selectedCount`, `categories` (the selected names, in
single-profile responses only), and:

- `refresh`, the latest refresh: `refreshId`, `status` (`queued`, `running`,
  `succeeded` or `failed`), `stage` (`downloading`, `indexing` or `filtering`
  while running), `queuedAt`, `startedAt`, `heartbeatAt`, `finishedAt`,
  `bytesDownloaded`, `totalBytes`, `entries` and, when failed,
  `error: { "code", "httpStatus" }`.
- `index`, the latest successful indexing (null before the first one):
  `indexedAt`, `categoryCount` and `entries`.
- `output`, the current playlist (null until the first one): `outputId`,
  `refreshedAt`, `sizeBytes`, `channels`, `includedCategoryCount`,
  `missingCategoryCount` and `missingCategories` (at most 100 names).

Profile IDs are random GUIDs created by the server. Only the canonical
lowercase form returned by the API is accepted.

| Status | `error` | Meaning |
| --- | --- | --- |
| 400 | `invalid_request` | Missing, malformed, non-JSON or oversized request body |
| 400 | `invalid_name` | The trimmed name is empty, longer than 80 characters or contains control or invisible formatting characters |
| 400 | `invalid_url`, `blocked_destination` | As for URL import |
| 400 | `invalid_selection` | More than 10,000 names or an empty name. Saving categories also needs at least one name, and each must be in the latest index or already selected |
| 401, 403, 503 | Guard codes | Exactly as for `GET /api/online/session` |
| 404 | `profile_not_found` | Unknown, malformed, deleted or **another user's** profile ID |
| 404 | `output_not_found` | The profile has no output yet |
| 409 | `profile_limit_reached` | Create: the user already has `OnlineProfiles__MaxProfilesPerUser` profiles |
| 409 | `profile_not_indexed` | Categories read or saved before the first successful indexing |
| 409 | `refresh_in_progress` | Categories or URL changed while a refresh is queued or running |
| 503 | `profiles_not_configured` | Storage connection, `OnlineImport__*` or `OnlineProfiles__*` settings missing or invalid |
| 503 | `profiles_unavailable` | Storage temporarily unavailable; retry |

A `failed` refresh reports the same download and playlist codes as a failed
import (from `invalid_url` to `too_many_categories`; see
[Import endpoints](#import-endpoints)), or one of these:

| Code | Meaning |
| --- | --- |
| `no_matching_channels` | None of the selected categories has channels in the new playlist |
| `refresh_interrupted` | Processing stopped (host restart, stuck or poison message, or the refresh could not be queued); refresh again |
| `internal_error` | Unexpected server error; logs contain no URL |

### Profile settings

| Setting | Default | Allowed values and notes |
| --- | --- | --- |
| `OnlineProfiles__MaxProfilesPerUser` | `10` | 1-50. Profiles each user can save; checked when a profile is created |

Like the import settings, this is a Function App application setting, and an
empty value uses the default. An invalid value disables saved profiles with
`503 profiles_not_configured` and logs the reason once; URL import keeps
working. Profiles also need the `OnlinePlaylistStorage` connection and use the
shared `OnlineImport__MaxDownloadMiB`, `OnlineImport__DownloadTimeoutSeconds`,
`OnlineImport__IdleTimeoutSeconds` and `OnlineImport__UserAgent` limits from
[Import settings](#import-settings). `OnlineImport__RetentionHours` does not
apply to profiles.

### Profile storage

Profiles use the same app-data storage account as URL import, in their own
resources, so the import cleanup never touches them:

| Resource | Name | Contents |
| --- | --- | --- |
| Private blob container | `online-profiles` | `{tenantId}/{objectId}/{profileId}/` with `profile.json` (the record, including the provider URL), `categories.json` (the latest index), `outputs/{outputId}.m3u` (current and previous output versions) and `work/{refreshId}.m3u` (the temporary download of one refresh) |
| Queue | `online-profile-refreshes` | Work items `{ "v", "tenantId", "objectId", "profileId", "refreshId" }`, never the URL |
| Queue | `online-profile-refreshes-poison` | Messages that failed twice; `OnlineProfileRefreshPoison` marks their refreshes failed |

The API creates the container (without public access) and both queues on first
use. If creation is not permitted, create them in advance with exactly these
names. The roles from [App-data storage and roles](#app-data-storage-and-roles)
(**Storage Blob Data Contributor** and **Storage Queue Data Contributor** on the
app-data account) already cover these resources; no other role is needed.
`profile.json` is only ever replaced with an ETag condition, so concurrent
changes never overwrite each other.

### Profile lifecycle and limits

- **No expiry.** Profiles stay until their owner deletes them. Signing out
  does not delete them.
- **Profile limit.** `OnlineProfiles__MaxProfilesPerUser` (default 10) is
  checked when a profile is created.
- **One refresh at a time.** Refresh while a refresh is queued or running
  returns that refresh. Saving categories or a new URL is refused with
  `409 refresh_in_progress` until it ends. Renaming works at any time.
- **Time limit.** Each refresh downloads the whole playlist again, within the
  shared download limits. Download, indexing and filtering together must fit in
  the 10-minute `functionTimeout` (see
  [Timeouts and hosting plan](#timeouts-and-hosting-plan)).
- **Stuck work.** Reading a profile or the list ends a refresh as
  `refresh_interrupted` when it stays `queued` longer than the download timeout
  plus 10 minutes, or `running` without a heartbeat for longer than the idle
  timeout plus 3 minutes, the same allowances as URL import. The worker saves a
  heartbeat at least every few seconds while working and before each redirect.
- **Retries.** The `host.json` queue settings shared with URL import apply: one
  message at a time per instance, two attempts, then
  `online-profile-refreshes-poison`. Only storage errors before the refresh
  starts are retried. A redelivered message for a refresh that had already
  started ends it as `refresh_interrupted` instead of starting again. Queue
  messages expire after 2 hours.
- **Missing categories.** Selected categories that are not in the new playlist
  are reported with the output and stay selected, so they return once the
  provider lists them again. The output uses the selected categories that do
  have channels. With no categories selected, a refresh only updates the
  category list.
- **Delete.** `DELETE` removes the record first, so a refresh in flight stops
  and removes what it writes, then every blob under the profile prefix.
- **Cleanup.** The `OnlineProfileCleanup` timer runs hourly, at minute 45. Once
  they are more than 2 hours old, it deletes left-over `work/` downloads,
  output versions that are neither the current nor the previous one, and all
  blobs of a profile whose `profile.json` no longer exists. It never deletes
  records, category indexes, current or previous outputs, or other blobs of
  existing profiles.

Do not add an age-based Blob lifecycle rule for `online-profiles/`: profiles
never expire, and a current output can be older than any fixed age. The
lifecycle example above covers only `online-imports/`.

### Atomic output and the last good playlist

- A successful refresh writes a new output version, `outputs/{refreshId}.m3u`.
  It is committed in one step with `If-None-Match: *`, so it is never visible
  half-written and is never overwritten.
- `categories.json` is replaced after every successful indexing, even when
  filtering then fails.
- One ETag-guarded update of `profile.json` makes the new version current and
  keeps the replaced version as the previous one. The version before that is
  then deleted. Exactly the current and previous versions are kept, so a
  download that started just before a refresh finishes is not cut off.
- A download reads the record and then streams that exact version. If the
  version was removed in between, the record is read once more. A download is
  therefore never partial or a mix of two versions.
- A failed refresh, including `no_matching_channels`, never changes or removes
  the current output: the last good playlist stays downloadable, and the
  failure is shown with the profile.
- If the profile was deleted, or another refresh replaced this one, the worker
  deletes the blobs it wrote and never recreates the record.

### Profile privacy

Provider URLs usually contain the account user name and password. A profile
must keep its URL to refresh it, unlike an import.

- The full URL is stored only in the private `profile.json` record of its
  profile, until the profile is deleted or its URL is changed. It is never put
  in queue messages, blob metadata, logs, exception messages, error responses
  or telemetry. The API returns only `scheme://host[:port]` (`sourceDisplay`).
- Logs contain only profile and refresh IDs, `scheme://host[:port]`, failure
  codes, HTTP status codes, exception type names and storage status codes.
- Outgoing HTTP dependency telemetry is reduced to `scheme://host[:port]` by
  the same `OutboundUrlRedaction` processor as URL import.
- The browser never receives the stored URL, and **Change URL** is never
  prefilled. The browser does not save the URL in local or session storage.
- Blob soft delete or versioning would keep deleted or replaced records,
  including provider URLs, for their own retention period. Deleting a profile
  or changing its URL cannot remove those copies. Leave both off for this
  account or keep their retention short.

### Profile limitations

- Refresh is manual (one click); there is no scheduled refresh.
- A refresh must finish within the Functions timeout, so very large or slow
  provider playlists may fail with `source_timeout` on the Consumption plan.
- The profile limit is checked best effort: two simultaneous creates can both
  pass it.
- Category names match exactly. A category the provider renames is reported
  as missing until the new name is selected.
- Profiles belong to one user and cannot be shared.
- The browser chooses the downloaded file name from the profile name, because
  CORS cannot expose `Content-Disposition`. Azure's HTTP response limit of
  about 230 seconds also applies to very slow downloads.
- Stable TV feed links are not available yet.

## Local development

Prerequisites: Node.js 22 or a compatible LTS, .NET 9 SDK/runtime, Azure Functions
Core Tools v4 and, for the example storage settings, an already running Azurite
emulator with its Blob and Queue services. Install/start only the tools and
services you have approved.

From the repository root in PowerShell:

```powershell
npm --prefix .\client ci
Copy-Item .\client\.env.example .\client\.env.local
Copy-Item .\api\local.settings.example.json .\api\local.settings.json
```

Both local configuration files are ignored; `local.settings.json` is excluded
from publish output. Populate them with the real public registration IDs,
scope and allowed account IDs. In `.env.local`, use
`VITE_ONLINE_API_BASE_URL=http://localhost:7071/api/`. Leave online values blank
if you only want the local-file workflow.

Run the following in separate terminals:

```powershell
npm.cmd --prefix .\client run dev -- --host=localhost --port=5173 --strictPort
```

```powershell
Set-Location .\api
func start --port 7071 --functions OnlineSession OnlineImportStart OnlineImportStatus OnlineImportCategories OnlineImportPlaylist OnlineImportDiscard OnlineImportWorker OnlineImportPoison OnlineImportCleanup OnlineProfileList OnlineProfileCreate OnlineProfileGet OnlineProfileCategories OnlineProfileSelect OnlineProfileRename OnlineProfileSource OnlineProfileRefresh OnlineProfileOutput OnlineProfileDelete OnlineProfileRefreshWorker OnlineProfileRefreshPoison OnlineProfileCleanup
```

This list contains every online function. It intentionally leaves out the
legacy `Upload` and `Download` functions, so their routes are not registered.
The example `local.settings.json` points both `AzureWebJobsStorage` and
`OnlinePlaylistStorage` at Azurite (`UseDevelopmentStorage=true`); the API
creates the `online-imports` and `online-profiles` containers and their queues
there on first use. The outbound request policy also applies locally: imports
and profile refreshes from `localhost`, private-network or emulator addresses
fail with `blocked_destination`, so use a publicly reachable playlist URL.

Open **http://localhost:5173/m3u-split/**. The existing Vite `/api` proxy remains
untouched for compatibility, but the new online client never uses it. It calls
the separately configured Functions origin, exercising the same cross-origin
authentication boundary as production.

## Independent deployment

`.github/workflows/deploy-pages.yml` continues to deploy only `client/dist` to
Pages, with optional public online configuration. The old
`azure-static-web-apps-purple-forest-061720b03.yml` file is retained unchanged;
it is already disabled in GitHub and should stay disabled.

`.github/workflows/deploy-api.yml` builds the API on relevant pushes and
pull requests. **Only manual `workflow_dispatch` deploys** an API package; it
does not create resources or update server settings. It checks for explicit
deployment IDs, populated server auth settings, a URL-import storage connection
(`OnlinePlaylistStorage` or both identity-based service URIs) and disabled
legacy functions before deploying. The package includes the generated hidden
`.azurefunctions` directory and `host.json`, but never local configuration.

After approval and provisioning, create the **`azure-functions` GitHub
environment**, restrict it to the intended branch (normally `master`), and add
required reviewers if available. Configure a separate deployment identity with
GitHub OIDC federation for:

`repo:goncalvesj/m3u-split:environment:azure-functions`

Use issuer `https://token.actions.githubusercontent.com` and audience
`api://AzureADTokenExchange`. Grant only the permissions required to read
Function App settings and deploy to that existing app, at the narrowest
resource scope. Do not use the SPA as the deployment identity. Configure these
**environment variables** (public IDs/names, no client secret):

| Variable | Value |
| --- | --- |
| `AZURE_DEPLOY_CLIENT_ID` | The OIDC deployment identity's client ID |
| `AZURE_TENANT_ID` | Its selected directory ID |
| `AZURE_SUBSCRIPTION_ID` | The explicitly approved subscription ID |
| `AZURE_RESOURCE_GROUP` | The existing Function App's resource group |
| `AZURE_FUNCTION_APP_NAME` | The existing Function App's name |

After setting server configuration/CORS, manually run **Standalone Functions
API** on the intended branch. Deploy Pages separately after setting its public
variables. There is no automatic cross-host deployment or paid provisioning.
No workflow has been run as part of the local code implementation.

## Authentication behavior and verification

MSAL Browser v5 uses a dedicated redirect bridge page for sign-in and sign-out.
Do not add `Cross-Origin-Opener-Policy` to that bridge page; it needs to return
the response to the original app. It must load its bundled script. Authentication
uses popup APIs only, with a direct provider popup in standalone PWA display
mode. If a browser/PWA blocks it, the UI reports the failure and asks the user
to allow popups or use a regular browser window. **There is no automatic
full-page redirect fallback that discards a loaded local file.**

Tokens are held by MSAL in session storage, not the favourites key or application
profile storage. The app does not clear unrelated local/session storage.
Sign-out is scoped to the selected MSAL account. Microsoft server sign-out is
best-effort; if interrupted, the UI says the Microsoft session may still be
active and allows retry. Signing out does not revoke an already issued bearer
token; it remains subject to server checks and token expiry. Do not treat the
session response as an authorization cookie or reuse it to bypass later guards.

Cached accounts must reverify with the API; a cached login never implies
authorization. Silent token renewal never opens an unsolicited popup. Verified
status expires at the earlier of the access-token and API-reported expiry and
is removed when the browser reports going offline. An expired or failed check
can be retried without touching local file state.

Expected API responses, all with `Cache-Control: no-store`:

| Status | Meaning |
| --- | --- |
| 200 | Valid API token, delegated permission, allowed SPA and allowed tenant/user identity |
| 401 | Missing, malformed, invalid or expired API token; includes `WWW-Authenticate: Bearer` |
| 403 | Valid token but missing delegated scope, wrong SPA, or disallowed account |
| 503 | Missing/invalid server configuration or signing-key discovery unavailable |

The import endpoints return these guard responses before anything else, and
then the codes listed in [Import endpoints](#import-endpoints). The saved
profile endpoints do the same, followed by the codes listed in
[Profile endpoints](#profile-endpoints).

Local regression checks:

```powershell
npm --prefix .\client test
npm --prefix .\client run build
npm --prefix .\client run lint
dotnet test .\api.tests\api.tests.csproj
dotnet build .\api\api.csproj
```

Frontend tests simulate MSAL interactions and HTTP responses while exercising
the local workflow; backend tests sign tokens with local test keys and validate
them through the production guard. They do **not** prove a live tenant is
configured or that a hosted deployment works.

After the real setup, verify live sign-in/callback and sign-out in both a normal
browser and the installed PWA with a local file already selected. Confirm the
file, search, selection and favourites survive. Check an allowed account (200),
a signed-in unlisted account (403), no/invalid token (401), exact-origin CORS,
legacy endpoints disabled, and offline/backend failure behavior. Microsoft
consent, deployed callback headers, PWA popup behavior, live signing-key
discovery and deployed API access still require that real environment.

For URL import, also verify in that environment, with a real provider URL:

- An allowed account can import, watch progress, choose categories and
  download a filtered playlist that Smart IPTV My List accepts.
- Another allowed account gets 404 for that import ID.
- Discarding, or starting a newer import, removes the old blobs.
- Private, `localhost` and metadata-endpoint URLs fail with
  `blocked_destination`.
- Application Insights dependencies and logs show at most
  `scheme://host[:port]`, never the playlist path or query, and queue messages
  contain only IDs.
- Imports near the size and time limits finish or fail cleanly on the chosen
  hosting plan.

For saved profiles, also verify in that environment:

- A new profile with saved favourites refreshes once and its **Download
  latest** playlist is accepted by Smart IPTV My List.
- Refresh, category changes and URL changes produce a new output, and a
  failed refresh (for example a wrong URL) keeps the previous output
  downloadable.
- Another allowed account gets 404 for that profile ID, and responses and logs
  never contain the stored URL, only `scheme://host[:port]`.
- Deleting a profile, including during a refresh, removes every blob under its
  prefix and the profile does not reappear.
- The profile limit, and refreshes near the size and time limits, behave as
  documented on the chosen hosting plan.
