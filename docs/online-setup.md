# Online playlists: Microsoft sign-in, standalone Functions, URL import, saved profiles and TV links

## What is ready

Local M3U upload/read, category search/selection, opt-in favourites and filtered
download remain available without an account or network connection once the app
has loaded. Select **Local file**, **Import from URL**, or **Saved profiles** first.
Use **Change method** to select a different method. A change of method keeps the
loaded local file, sign-in, and import in progress. Both online methods show
Microsoft sign-in before the selected method. The API must check access first.
Online methods are available only when the online configuration is valid.
Favourites keep the existing
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

Phase 4 adds **secret TV links** (see
[Phase 4: secret TV links for Smart IPTV](#phase-4-secret-tv-links-for-smart-iptv)).
Each saved profile can have one permanent, read-only link that Smart IPTV
**My List** loads without sign-in. It always serves the profile's latest
successful playlist, and its owner can copy, regenerate or revoke it while
signed in.

The API limits the requests of each client address and of each instance. It
also limits the downloads of each TV link. Refer to
[Rate limits and cost protection](#rate-limits-and-cost-protection).

The Azure UI is at **https://m3u-split.azurewebsites.net/**.
GitHub Pages stays at **https://goncalvesj.github.io/m3u-split/**, with Vite's
default `/m3u-split/` base.
The App Service build uses `/`.

Neither host adds or replaces a service worker or PWA manifest.
After the app loads, it can process local files without a network connection.
The app does not add an offline cache for a first visit.
Microsoft sign-in and API verification use a network connection.

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
   application (SPA)** platform, not a Web platform.
   Register the exact callback for each UI host that you enable:

   | Use | SPA redirect and post-logout URI |
   | --- | --- |
   | Azure UI | `https://m3u-split.azurewebsites.net/auth.html` |
   | GitHub Pages | `https://goncalvesj.github.io/m3u-split/auth.html` |

   Do not add a `localhost` URI to this production registration. For local
   development, use a separate SPA registration. See
   [Development registrations](#development-registrations).

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

### Development registrations

Keep local development out of the production SPA registration. Entra ignores
the port when it compares `localhost` redirect URIs. If the production SPA has
a `localhost` URI, a local web page on any port can get tokens that the
production API accepts.

1. Register a second SPA application for development only. Use the settings
   of step 2, but register only this URI:

   | Use | SPA redirect and post-logout URI |
   | --- | --- |
   | Local Vite development and preview | `http://localhost:5173/m3u-split/auth.html` |

   This one URI also works for Vite preview on port 4173. Do not add a second
   `localhost` URI that has only a different port.
2. Add the delegated `access_as_user` permission to this SPA, as in step 3.
3. Put the client ID of this development SPA in `client/.env.local`
   (`VITE_ENTRA_SPA_CLIENT_ID`) and in `api/local.settings.json`
   (`OnlineAuth__SpaClientId`).

The production API accepts only tokens that the production SPA gets (the
`azp` claim). It rejects the tokens of the development SPA. For more
isolation, you can also use a separate development API registration and a
separate development Function App.

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
Pages 404 routing trick is required. Register local callbacks only on the
development SPA registration. Add local origins only to the CORS setting of
the local Core Tools host (`Host.CORS` in `local.settings.json`). Do not add
them to the production Function App. For Vite preview on port 4173, add
`http://localhost:4173` to `Host.CORS`. Put a comma between the origins.

Never put client secrets, Function keys, storage credentials, provider playlist
URLs/passwords or deployment credentials in any `VITE_*` variable. The frontend
sends an API **access token** in `Authorization: Bearer ...` to the explicit
backend base URL. It omits cookies, forbids HTTP redirect following for this
request, and does not send an email/identity parameter for authorization.

## UI on Azure App Service

The UI at **https://m3u-split.azurewebsites.net/** has Microsoft sign-in.
Select **Import from URL** or **Saved profiles** to use an online method.
The API must check access after sign-in. The UI deployment does not deploy the API.
The GitHub Pages default base and workflows stay unchanged.

### Host settings

The placeholders replace the real Azure IDs and names.
Keep the real values outside this public repository.

- Web App: `m3u-split`.
- Subscription: `<SUBSCRIPTION-ID>`.
- Resource group: `<RESOURCE-GROUP>`.
- App Service plan: `<APP-SERVICE-PLAN>`.
- Plan OS: Linux.
- Plan SKU: B1 (Basic).
- Region: France Central.
- Runtime: Node 24 LTS (`NODE|24-lts`).
- Startup command: `pm2 serve /home/site/wwwroot --no-daemon --spa`.
- HTTPS only: enabled.
- Minimum TLS: 1.2 for the app and SCM.
- FTP and FTPS: disabled.
- Basic authentication: disabled for SCM and FTP.
- HTTP/2: enabled.
- ARR affinity: disabled.
- Always On: enabled.
- Remote build: disabled (`SCM_DO_BUILD_DURING_DEPLOYMENT=false`).
- Application Insights: none.
- Managed identity: none.

The app settings contain no secrets.
The deployment uses Microsoft Entra authentication through the Azure CLI.
It does not change the plan or other apps.

### Build and deploy again

Use PowerShell 7 from the repository root.
Use `scripts\deploy-ui.ps1` to deploy to the current UI app.
Replace the placeholders with the real values.
Make sure that the Azure CLI has a signed-in account for this subscription.
The script does not make resources or change app settings.
It preserves the plan, storage, roles, CORS, and Entra setup.

```powershell
$target = @{
    SubscriptionId = '<SUBSCRIPTION-ID>'
    ResourceGroup = '<RESOURCE-GROUP>'
    AppName = '<UI-WEB-APP>'
}

.\scripts\deploy-ui.ps1 @target -WhatIf
.\scripts\deploy-ui.ps1 @target
```

`-WhatIf` checks the Azure target without a build or deployment.
Use Node.js and npm for the UI build.
The script runs `npm ci`, `tsc -b`, and `vite build --base /`.
It puts both entry pages and their assets at the zip root.
It deploys the zip with Microsoft Entra authentication.
It removes its temporary build and zip files, also after an error.

The script rejects local `.env*` files, except `.env.example`.
It ignores inherited values of the four online variables.
Without online parameters, it builds the local-only UI.
It restores the caller's variables and current directory after the build.
The default build still uses `/m3u-split/` for GitHub Pages.
Do not commit `node_modules` or `dist`.

### Later online setup

- SPA redirect and post-logout URI: `https://m3u-split.azurewebsites.net/auth.html`.
- API CORS origin: `https://m3u-split.azurewebsites.net` (no path).
- `VITE_ENTRA_TENANT_ID`: the production tenant Directory ID.
- `VITE_ENTRA_SPA_CLIENT_ID`: the production SPA Application (client) ID.
- `VITE_ENTRA_API_SCOPE`: `api://<API-APPLICATION-CLIENT-ID>/access_as_user`.
- `VITE_ONLINE_API_BASE_URL`: `https://<YOUR-FUNCTION-APP-HOST>/api/`.

Register the callback on the SPA platform, not the Web platform.
Do not add a `localhost` callback to the production SPA.
Configure the API and CORS before you supply the four public values.
Pass all four public values to the UI script.
Do not pass only some of these values.

```powershell
.\scripts\deploy-ui.ps1 @target `
    -TenantId '<TENANT-ID>' `
    -SpaClientId '<SPA-APPLICATION-CLIENT-ID>' `
    -ApiScope 'api://<API-APPLICATION-CLIENT-ID>/access_as_user' `
    -OnlineApiBaseUrl 'https://<FUNCTION-APP-HOST>/api/'
```

Rebuild and deploy the UI after the API deployment.
App settings alone cannot change the compiled client.

## API on a Dedicated plan

The template at `infra\function-api.bicep` supports an existing Linux App Service plan.
It does not change that plan or its other apps.
It adds a Function App that uses an approved, existing storage account.
The owner must approve the managed identity and its data access before deployment.
Keep actual resource names and IDs in local values, not tracked files.

### Storage and access

- Use the plan region for the API.
- Use the approved account for `AzureWebJobsStorage` and `OnlinePlaylistStorage`.
- Make sure the account disables shared keys and anonymous blob access.
- Make sure the account uses HTTPS only and TLS 1.2.
- Give the API identity **Storage Blob Data Owner** on each of its two host containers.
- Give it **Storage Blob Data Contributor** on each of its three data containers.
- Give it **Storage Queue Data Contributor** on each of its four queues.
- Scope each role to its container or queue, not the account, group, or subscription.

The Function App uses a system-assigned identity.
It does not use storage keys, SAS tokens, connection strings, or a separate identity resource.
The template adds nine role assignments of three role types.

It gives no access to legacy containers.
It does not change existing containers, account settings, or service retention settings.
This option uses separate containers, not the separate accounts that the storage section recommends.
Use it only with owner approval.

The health check for host storage can call `ListContainers` every 30 seconds.
Container-scoped roles deny this call.
Logs and metrics can show HTTP 403 results.
The functions continue to operate.
Do not widen the roles for this check.

Keep blob soft delete and version history off for playlist data.
If the account enables either feature, Azure can retain deleted playlist data.
Container soft delete affects whole containers, not the API's normal blob delete operations.
An account in another region can add network cost and delay.

| App setting | Initial value |
| --- | --- |
| `AzureWebJobsStorage__accountName` | `<STORAGE-ACCOUNT>` |
| `AzureWebJobsStorage__credential` | `managedidentity` |
| `OnlinePlaylistStorage__blobServiceUri` | `https://<STORAGE-ACCOUNT>.blob.core.windows.net/` |
| `OnlinePlaylistStorage__queueServiceUri` | `https://<STORAGE-ACCOUNT>.queue.core.windows.net/` |
| `OnlinePlaylistStorage__credential` | `managedidentity` |

Use the system-assigned identity for both connections.
Do not set `__clientId` for these connections.
Do not set the key-based `AzureWebJobsStorage` or `OnlinePlaylistStorage` values.
Do not set `WEBSITE_CONTENTAZUREFILECONNECTIONSTRING` or `WEBSITE_CONTENTSHARE` on this Dedicated app.

The template adds two private host containers, three private data containers, and four queues.
Their names match the API code.
It does not add Application Insights, a Log Analytics workspace, or an alert.
Entra registrations, the account allowlist, API CORS, and the UI build values remain separate tasks.

### First resource deployment

Use PowerShell 7 from the repository root.
Supply the approved values in place of these placeholders.
Make sure the selected plan supports Linux and Always On.
Use the storage account that the owner approved.

```powershell
$subscription = '<SUBSCRIPTION-ID>'
$group = '<RESOURCE-GROUP>'
$app = '<FUNCTION-APP-NAME>'
$parameters = @(
    "functionAppName=$app"
    'appServicePlanName=<APP-SERVICE-PLAN>'
    'location=<PLAN-REGION>'
    'storageAccountName=<STORAGE-ACCOUNT>'
)

az deployment group what-if --subscription $subscription `
    --resource-group $group --mode Incremental `
    --template-file .\infra\function-api.bicep `
    --parameters @parameters --result-format ResourceIdOnly
if ($LASTEXITCODE -ne 0) {
    throw 'The resource preview failed.'
}
```

Examine the preview before the owner approves deployment.
It must not change or delete an existing app, plan, account, or legacy container.
Run this next command only after that approval.

```powershell
az deployment group create --subscription $subscription `
    --resource-group $group --mode Incremental `
    --template-file .\infra\function-api.bicep --parameters @parameters `
    --query properties.provisioningState --output tsv
if ($LASTEXITCODE -ne 0) {
    throw 'The resource deployment failed.'
}
```

Use this template for the first resource deployment only.
It supplies the initial app settings.
A later template deployment can replace settings that you add manually.
Deploy later code updates with the package commands below.
Azure can take up to 10 minutes to apply new storage roles.

### Publish and deploy the API package

Use `scripts\deploy-api.ps1` to deploy to the current Function App.
Use PowerShell 7 from the repository root.
The script selects a stable SDK that matches the project framework.
Install that SDK before deployment if it is absent.
The script publishes for Linux x64.
It does not deploy the UI, change app settings, or run tests.

```powershell
$target = @{
    SubscriptionId = '<SUBSCRIPTION-ID>'
    ResourceGroup = '<RESOURCE-GROUP>'
    AppName = '<FUNCTION-APP-NAME>'
}

.\scripts\deploy-api.ps1 @target -WhatIf
.\scripts\deploy-api.ps1 @target
```

`-WhatIf` checks the Azure target without a publish or deployment.
The script checks the runtime, Always On, and package mode.
It rejects legacy functions, unsupported HTTP routes, and local configuration files.
It includes `host.json`, `functions.metadata`, `worker.config.json`, and the hidden `.azurefunctions` folder.
It puts the publish files at the zip root.
It deploys with Microsoft Entra authentication and removes its temporary files.

The scripts accept `properties.status=RuntimeSuccessful` or `status=4` with `complete=true`.
They stop if Azure returns a failed or unknown result.
They use the default Azure CLI time limits.

Both scripts check the subscription and app type before a build.
The target must be a Linux app with HTTPS and disabled SCM basic authentication.
They do not deploy the Bicep template or change identities, roles, or storage settings.
The Azure CLI must already have a valid session.
Keep SCM and FTP basic authentication disabled.
Do not use an account key to bypass a storage access error.
If Azure Policy denies a step, stop and report its name and effect.

Without valid `OnlineAuth__*` values, protected routes send `503 online_not_configured`.
TV links send `503 feeds_not_configured`.
Do not interpret these responses as a successful online setup.
After Entra and CORS setup, set `VITE_ONLINE_API_BASE_URL` to `https://<FUNCTION-APP-HOST>/api/`.
Supply the other three public values and rebuild the UI.

## Standalone Function App settings

Use an approved, separately provisioned **Azure Functions v4, .NET 10,
isolated-worker** Function App. The API targets `net10.0`.
Use an approved Dedicated App Service plan or Flex Consumption.
For a Dedicated plan, refer to [API on a Dedicated plan](#api-on-a-dedicated-plan).
For Flex Consumption, refer to
[Hosting plan and cost guards](#hosting-plan-and-cost-guards).
Microsoft support for .NET 10 stops on 14 November 2028.
This is not an Azure Static Web Apps managed API.

Set the following **Function App application settings**, never frontend
variables. Double underscores are the .NET configuration section separator:

| Server setting | Value |
| --- | --- |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated`. Not on Flex Consumption: see below the table |
| `FUNCTIONS_EXTENSION_VERSION` | `~4`. Not on Flex Consumption: see below the table |
| `OnlineAuth__TenantId` | The same tenant GUID as the SPA authority |
| `OnlineAuth__ApiClientId` | The API Application (client) ID GUID, **not** the `api://...` URI |
| `OnlineAuth__SpaClientId` | The SPA Application (client) ID GUID; must differ from the API ID |
| `OnlineAuth__RequiredScope` | `access_as_user` (short delegated scope name, not full URI) |
| `OnlineAuth__AllowedObjectIds` | One or more user Object ID GUIDs in that tenant, comma-separated |
| `OnlinePlaylistStorage__blobServiceUri` | URL import: `https://<APP-DATA-ACCOUNT>.blob.core.windows.net` (see Phase 2) |
| `OnlinePlaylistStorage__queueServiceUri` | URL import: `https://<APP-DATA-ACCOUNT>.queue.core.windows.net` (see Phase 2) |

On Flex Consumption, do not set `FUNCTIONS_WORKER_RUNTIME` or
`FUNCTIONS_EXTENSION_VERSION`. Set the runtime when you make the app, in
`functionAppConfig.runtime` (name `dotnet-isolated`, version `10.0`). The
platform sets the host version. The deployment workflow reads the runtime from
`functionAppConfig.runtime`, and reads `FUNCTIONS_WORKER_RUNTIME` only on other
plans. Local Core Tools still reads `FUNCTIONS_WORKER_RUNTIME` from
`local.settings.json`.

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

TV links need no settings of their own. They use the saved-profile
configuration and the `OnlineAuth__*` settings; when either is missing or
invalid, the TV link endpoints answer `503 feeds_not_configured`. Setting
`AzureWebJobs.OnlineFeed.Disabled` to `true` turns every TV link off; see
[TV link kill switches](#tv-link-kill-switches).

All HTTP endpoints read the optional `OnlineRateLimits__*` settings. Refer to
[Rate limits and cost protection](#rate-limits-and-cost-protection). If one of
these settings is not valid, all HTTP endpoints send
`503 rate_limits_not_configured`. This includes `online/session`.

The Functions host also needs its normal **host storage** configuration
(`AzureWebJobsStorage`, or the hosting plan's supported identity-based
`AzureWebJobsStorage__...` configuration with the required storage roles).
Prefer managed identity where supported; if a connection string is necessary,
keep it only in server settings/Key Vault. Follow the selected plan's normal
content/deployment-storage requirements. Host storage is infrastructure, not
playlist storage: URL imports use the separate `OnlinePlaylistStorage`
connection described in Phase 2, never `AzureWebJobsStorage`. The hourly import,
profile and TV link cleanup timers use host storage only for the host's normal timer lock. The API
does not use the `UseBlobStorage`, `StorageConnectionString` and `FilePath`
settings of the removed legacy functions. Remove these settings from the
Function App.

The session function uses `AuthorizationLevel.Anonymous` **at the Function-key
layer only** so a SPA need not embed a shared Function key. `OnlineSession.Run`
unconditionally calls `OnlineAccess.AuthorizeAsync` before producing a
successful response. The Phase 2 import endpoints, Phase 3 profile endpoints and Phase 4 TV link
management endpoints follow the same pattern: each calls the guard before any other work, maps denials identically and derives
data ownership only from the validated `tid`/`oid`. Future online functions
must do the same; copying the anonymous trigger without the guard would
be unsafe. The one deliberate exception is the Phase 4 `OnlineFeed` function,
which serves a TV link to Smart IPTV without sign-in and is authorized by the
secret token in its URL instead; see [TV link security](#tv-link-security).
This implementation does not rely on unsupported `[Authorize]`
attributes or ASP.NET middleware routing in the isolated worker.

The API does not have the legacy `Upload` and `Download` functions. The `verify`
job of `deploy-api.yml` examines the package. The job stops if the package has
a function with one of these names. The job also stops if an HTTP route is not
in `online/` or `feed/`. After you deploy this package, the Function App does
not have the legacy endpoints.

The `AzureWebJobs.Upload.Disabled` and `AzureWebJobs.Download.Disabled`
settings have no effect now. You can remove them. Never give a Function key or
the host master key to the SPA.

App Service Authentication/Easy Auth is not required: the application validates
the bearer token itself. If a platform authentication layer is added later,
configure it deliberately; it must not replace or bypass the application guard
or unexpectedly redirect API calls to an HTML login page.

## CORS: exact origins, not authentication

In the Function App's **API -> CORS** settings, remove wildcard origins.
Add only the origin of each UI host that you enable:

- Azure UI: **`https://m3u-split.azurewebsites.net`** (no path).
- GitHub Pages: **`https://goncalvesj.github.io`** (no `/m3u-split/` path).

Do not add a `localhost` origin to the production Function App. Local
development uses the local Core Tools host and its own CORS setting.

Leave **Access-Control-Allow-Credentials disabled**. The Functions host handles
OPTIONS preflight for the `Authorization` and `Content-Type` request headers and
for the `POST` and `DELETE` methods used by URL import and saved profiles; no
extra CORS setting is needed for them. Saved profiles use only `GET`, `POST` and
`DELETE` with these same two request headers, so the CORS configuration does not
change. Do not add a second conflicting ASP.NET CORS implementation or
a wildcard header inside the function. Platform CORS cannot expose response
headers such as `Content-Disposition` to the page, so the browser chooses the
downloaded file name itself.
For local Core Tools, `local.settings.example.json` sets the local origin
`http://localhost:5173` in `Host.CORS`.
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
| 429 | `rate_limited` | Too many requests from this client address, or from all clients on this instance. Refer to [Rate limits and cost protection](#rate-limits-and-cost-protection) |
| 503 | `import_not_configured` | Storage connection or `OnlineImport__*` settings missing or invalid |
| 503 | `import_unavailable` | Storage temporarily unavailable; retry |
| 503 | `rate_limits_not_configured` | An `OnlineRateLimits__*` setting is not valid |

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

Use a **dedicated general-purpose v2 storage account** for new deployments.
Keep it separate from the host account.
The owner can approve reuse of an existing account.
Refer to [Storage and access](#storage-and-access) for that option.
The code uses these resources:

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

For owner-approved account reuse, scope these roles to the API containers and queues.
Do not change existing account settings without owner approval.

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
- **Rate limits.** The API limits the requests of each client address and of
  each instance. Refer to
  [Rate limits and cost protection](#rate-limits-and-cost-protection).

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

In the app, select **Saved profiles** and sign in. After the API checks access,
the page shows saved profiles, not URL import. It lists each profile with its host, refresh progress, last
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
| 429 | `rate_limited` | As for URL import |
| 503 | `profiles_not_configured` | Storage connection, `OnlineImport__*` or `OnlineProfiles__*` settings missing or invalid |
| 503 | `profiles_unavailable` | Storage temporarily unavailable; retry |
| 503 | `rate_limits_not_configured` | As for URL import |

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
- A TV link serves the latest successful output and never starts a refresh;
  see [Phase 4](#phase-4-secret-tv-links-for-smart-iptv).

## Phase 4: secret TV links for Smart IPTV

Smart IPTV **My List** can load a playlist from a link, but a full provider
playlist is often too large for the TV app. A TV link gives a saved profile a
permanent, secret, read-only URL that always serves the profile's latest
successful filtered playlist, so the TV loads only the chosen categories.

- Its owner creates, copies, regenerates and revokes the link while signed in.
  The link itself works without sign-in, cookies or extra headers: the random
  token in its path is the credential.
- A profile has at most one active link. Links do not expire: a link works
  until it is regenerated or revoked, its profile is deleted or its owner
  leaves the allowlist.
- The link serves the output of the latest successful **Refresh**. A failed
  refresh keeps serving the previous playlist, and loading the link never
  starts a refresh.

In the app, each saved profile has a collapsed **TV link** button. Opening it
loads the profile's link (nothing is requested before that) and then offers
**Create TV link**, or shows the link in a read-only field with **Copy link**,
**Regenerate link** and **Revoke link**. Regenerating and revoking ask for
confirmation first. If the browser does not allow copying, the link is
selected in its field for a manual copy.

### Using a TV link with Smart IPTV

1. Refresh the profile successfully at least once. Until then the link
   answers `404 feed_not_ready`.
2. Open **TV link** on the profile, choose **Create TV link** and copy the
   link. It has the form `https://<FUNCTION-APP-HOST>/api/feed/<TOKEN>.m3u`.
3. On the Smart IPTV My List page, add the link once as an external playlist
   link for your TV, following that page's instructions.
4. After each successful refresh of the profile, the TV gets the new playlist
   the next time it loads the list. The link itself does not change.

If the link may have been shared or exposed, choose **Regenerate link** and
replace it in My List: the old link stops working immediately. A link to a
local Core Tools host (`http://localhost:7071/api/...`) works only on that
computer.

### TV link endpoints

The owner endpoints manage a profile's link. Like the profile endpoints, each
uses `AuthorizationLevel.Anonymous` at the Function-key layer only, calls
`OnlineAccess.AuthorizeAsync` before any other work, maps denials exactly like
the session endpoint and only touches profiles stored under the caller's
validated tenant and object IDs. Every response sends `Cache-Control: no-store`
and no request needs a body. A link is returned as
`{ "token": "<43 characters>", "createdAt": "..." }`; the app builds the URL
from the token and its API base URL.

| Function | Method and route | Success |
| --- | --- | --- |
| `OnlineFeedGet` | `GET online/profiles/{profileId}/feed` | `200` with the link |
| `OnlineFeedCreate` | `POST online/profiles/{profileId}/feed` | `201` with a new link, or `200` with the existing one. A profile never gets a second link |
| `OnlineFeedRegenerate` | `POST online/profiles/{profileId}/feed/regenerate` | `200` with a new token. The previous link stops working at once |
| `OnlineFeedRevoke` | `DELETE online/profiles/{profileId}/feed` | `204`, also when the profile has no link |

| Status | `error` | Meaning |
| --- | --- | --- |
| 401, 403, 503 | Guard codes | Exactly as for `GET /api/online/session` |
| 404 | `profile_not_found` | Unknown, malformed, deleted or **another user's** profile ID |
| 404 | `feed_not_found` | Get or regenerate: the profile has no link. Regenerate never creates one, so a stale page cannot bring back a revoked link |
| 429 | `rate_limited` | As for URL import |
| 503 | `feeds_not_configured` | Saved profiles, `OnlineAuth__*` or the storage connection missing or invalid |
| 503 | `feeds_unavailable` | Storage temporarily unavailable, or too many simultaneous changes to the same link; retry |
| 503 | `rate_limits_not_configured` | As for URL import |

The link itself is served by `OnlineFeed`:

| Function | Method and route | Success |
| --- | --- | --- |
| `OnlineFeed` | `GET` or `HEAD` `feed/{token}`, with or without a `.m3u` suffix | `200`, streamed `audio/x-mpegurl; charset=utf-8` with `Content-Length` and `Content-Disposition: attachment`. `HEAD` gets the same status and headers without a body |

| Status | `error` | Meaning |
| --- | --- | --- |
| 404 | `feed_not_found` | The link does not work. Status, body and headers are identical whatever the reason |
| 404 | `feed_not_ready` | The link is valid, but its profile has no successful refresh yet |
| 429 | `rate_limited` | Too many TV link requests from this client address in one minute, too many requests on this instance, or too many `GET` downloads of this link in one hour. Refer to [Rate limits and cost protection](#rate-limits-and-cost-protection) |
| 503 | `feeds_not_configured` | As for the owner endpoints |
| 503 | `feeds_unavailable` | Storage temporarily unavailable; retry. No details are returned |
| 503 | `rate_limits_not_configured` | As for URL import |

`OnlineFeed` uses `AuthorizationLevel.Anonymous` and deliberately does **not**
call the sign-in guard, because Smart IPTV cannot sign in. Every response,
errors included, sends `Cache-Control: no-store`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and
`X-Robots-Tag: noindex, nofollow`. The identical `404`,
`{ "error": "feed_not_found", "message": "This TV link does not exist or was revoked." }`,
answers a malformed, unknown, revoked or replaced token, a deleted profile, an
owner who is no longer allowed and a missing or mismatched `feed.json`, so a
response never reveals whether a token was ever valid. `feed_not_ready` is
returned only after the token has been fully verified. It is a `404` rather
than a `503` with `Retry-After` because waiting does not help: the link starts
working after the owner's first successful refresh. If a download fails after
streaming started, the connection is aborted, so a partial playlist is never
mistaken for a complete one.

### TV link storage

TV links use the app-data storage account and connection of saved profiles:

| Resource | Name | Contents |
| --- | --- | --- |
| Blob in `online-profiles` | `{tenantId}/{objectId}/{profileId}/feed.json` | The profile's link, and the source of truth: `{ "v", "feedId", "token", "tokenHash", "createdAt" }` |
| Private blob container | `online-feeds` | One index entry per link, `{sha256}.json`, named by the lowercase hex SHA-256 of the token: `{ "v", "tenantId", "objectId", "profileId", "feedId", "createdAt" }`, never the token |

`feed.json` is a separate blob, never part of `profile.json`, and sits under
the profile's prefix, so deleting the profile deletes it too. It is the only
place that keeps the token itself, so that the signed-in owner can copy the
link again later; the same private container already holds the provider URL.
Index entries and `feed.json` are created with `If-None-Match: *`, and
`feed.json` is replaced only with an ETag condition.

The API creates the `online-feeds` container (without public access) when a
link is first created. If creation is not permitted, create it in advance with
exactly this name. TV links need only **Storage Blob Data Contributor** on the
app-data account, already granted in
[App-data storage and roles](#app-data-storage-and-roles); they use no queue
and need no new role or setting. Do not add a Blob lifecycle rule for
`online-feeds/`: links never expire, and `OnlineFeedCleanup` removes index
entries that no longer lead anywhere. As with profiles, blob soft delete or
versioning would keep deleted `feed.json` copies, including revoked tokens;
leave both off or keep their retention short.

### TV link lifecycle

- **Create** writes a new index entry, then `feed.json`. When two creates
  race, one link wins: the other request returns it and deletes its own unused
  index entry.
- **Regenerate** writes a new index entry, replaces `feed.json` with an ETag
  condition (the old link stops working at this moment) and then deletes the
  old index entry.
- **Revoke** deletes `feed.json` first (the link stops working at this moment)
  and then its index entry. Revoke wins against a simultaneous regenerate.
- **Profile delete** removes the profile record, which ends the link at once,
  then every blob under the profile prefix, including `feed.json`. A link
  created or regenerated while its profile is being deleted is removed again.
- **Cleanup.** The `OnlineFeedCleanup` timer runs hourly, at minute 30. It
  checks up to 500 index entries older than 2 hours per run and deletes, with
  an ETag condition, those that cannot be read, whose profile record or
  `feed.json` is missing or whose `feed.json` now holds another link. It
  skips blob names that do not follow the index naming, keeps the entries of
  owners removed from the allowlist and logs only counts. Removing an index
  entry never affects a working link.

### TV link security

- **Secret link.** The link is a bearer-style secret: anyone who has it can
  download the playlist, including the provider's stream credentials in its
  channel URLs, until the link is regenerated or revoked. Keep it as private
  as the provider URL itself. Its token is 32 bytes (256 bits) from a
  cryptographic random number generator, encoded as 43 base64url characters,
  so it cannot be guessed.
- **Hash index.** Index entries are named by the SHA-256 of the token and
  never contain it, and a hash cannot be used as a link. The exact token
  format is checked before any storage access.
- **Every request is checked again.** A link works only while its index entry
  exists, its owner is still allowed by `OnlineAuth__TenantId` and
  `OnlineAuth__AllowedObjectIds`, its profile exists and its `feed.json` holds
  the same link with a token hash that matches in constant time. Revoke,
  regenerate, profile delete and allowlist removal therefore take effect on
  the next request. Adding an owner back to the allowlist restores their
  links.
- **Read-only, last good output.** A link only reads the latest successful
  output. It cannot start a refresh, change a profile or reveal the stored
  provider URL, and a failed refresh never replaces what it serves.
- **Not cached or indexed.** `no-store`, `no-referrer` and
  `noindex, nofollow` keep browsers, proxies and search engines from storing,
  passing on or listing the link and its playlist.
- **No CORS change.** Smart IPTV fetches the link directly, as it fetches
  provider playlists, so the [CORS](#cors-exact-origins-not-authentication)
  configuration stays as it is. The owner endpoints use only `GET`, `POST` and
  `DELETE` with the same request headers as saved profiles.
- **Logs.** Only the signed-in owner receives the token. The token and its
  hash are never logged; logs contain link and profile IDs, storage status
  and error codes, exception type names and counts.

### TV link telemetry and logs

A TV link carries its token in the request path, so anything that records full
request URLs can record the token. The worker prevents this where it can:

- ASP.NET Core in the worker logs every request with its URL at
  `Information` ("Request starting ..."). Through the worker's logging, such
  entries can reach the console output that the Functions host captures, the
  host's logs and Application Insights. `Program.cs` therefore raises the
  minimum level of `Microsoft.AspNetCore` logs to `Warning`; ASP.NET Core
  warnings and errors are still logged.
- The `FeedPathRedaction` telemetry processor replaces the path segment after
  `/feed/` with `redacted` in request names and URLs, trace and exception
  messages, custom properties and operation names that the worker sends to
  Application Insights. Worker telemetry for the regenerate endpoint therefore
  shows `.../feed/redacted` too. `OutboundUrlRedaction` already reduces
  outgoing dependencies, including storage calls whose blob names hold token
  hashes, to `scheme://host[:port]`.
- The API's own logs never contain a token or hash, and its error responses
  never contain storage exception messages.

The worker cannot change what the platform records itself. The Functions host
records every invocation; with Application Insights, its request telemetry
includes the full URL. Adaptive sampling in `host.json` also applies to
requests, to limit the telemetry cost of a flood. At the usual low traffic,
Application Insights keeps all requests. If many requests arrive, it keeps
only some of them. Each record that it keeps can contain the full link. App
Service HTTP and web server logs, diagnostic settings that export them, proxies
in front of the app and Smart IPTV, which keeps the link for your TV, can
record it too. Treat all of these as holding secrets:

- Restrict who can read the Function App's logs, its Application Insights
  resource and any Log Analytics workspace or storage that receives its
  diagnostics, and keep their retention short.
- Enable App Service HTTP logging, or diagnostic settings that export request
  URLs, only when needed, and restrict them the same way.
- Turn on **HTTPS Only** for the Function App, so the link never travels
  unencrypted, and give Smart IPTV only the `https://` link.
- Regenerate a link whenever it may have been exposed.

### TV link kill switches

- **One link:** **Revoke link**, or **Regenerate link** to replace it, on its
  profile. Deleting the profile also ends its link.
- **All links of one account:** remove the account from
  `OnlineAuth__AllowedObjectIds`. This also ends its online access.
- **Every link:** set the Function App application setting
  `AzureWebJobs.OnlineFeed.Disabled` to `true`. TV links stop working until
  the setting is removed; nothing is deleted, and owners can still manage
  their links.

### TV link limitations

- One link per profile, with no expiry, no per-device links and no usage
  statistics.
- A link serves the latest successful refresh; refresh the profile when the
  provider's list changes. Smart IPTV decides when the TV reloads the list.
- Azure's HTTP response limit of about 230 seconds also applies to very slow
  TV downloads.

## Rate limits and cost protection

The API does not use Azure API Management or Azure Front Door. The worker
middleware `OnlineRateLimitMiddleware` limits the HTTP requests instead. It
uses the .NET `System.Threading.RateLimiting` limiters, with fixed windows and
no queue. The middleware examines each HTTP request before the function
starts. The queue workers and the timers do not have these limits.

| Setting | Default | Range | The API counts |
| --- | --- | --- | --- |
| `OnlineRateLimits__ApiRequestsPerMinute` | `300` | 10-6000 | The requests from 1 client address to the `online/*` endpoints in 1 minute |
| `OnlineRateLimits__FeedRequestsPerMinute` | `30` | 1-600 | The `GET` and `HEAD` requests from 1 client address to all TV links in 1 minute |
| `OnlineRateLimits__FeedDownloadsPerHour` | `20` | 1-1000 | The `GET` requests to 1 TV link in 1 hour, after the API verifies the token |
| `OnlineRateLimits__InstanceRequestsPerMinute` | `1200` | 60-60000 | All HTTP requests on 1 instance in 1 minute |

An empty setting uses the default. If a value is not a whole number in its
range, all HTTP endpoints send `503 rate_limits_not_configured`. The API then
logs the error `All HTTP endpoints are disabled: ...` with the name of the
setting.

### How the API applies the limits

For each HTTP request, the middleware does these checks in this order:

1. It examines the instance limit, but it does not count the request yet.
2. It counts the request against the limit of the client address.
3. It counts the request against the instance limit.

A request above the client limit or the instance limit gets a `429` response
immediately. The function does not start. The API does not examine the token,
read a blob or download a playlist for this request. A client above its own
limit does not use the budget of the instance.

The counters are in the memory of each instance. Each instance has its own
counters, and a restart sets them to zero. In 1 minute, the API accepts a
maximum of the instance limit multiplied by the number of instances.

The `OnlineFeed` function examines the download limit of a TV link after it
verifies the token. A request with a token that is not valid does not use the
download limit of a link. A `HEAD` request does not count, but a
`404 feed_not_ready` response counts. The key of this limit is a hash of the
token. Each regenerated link gets a new limit immediately.

A person with a TV link can use all downloads of the link in 1 hour. The TV
then gets `429` until the window ends. To stop this, regenerate the link. Then
give the new link to the TV.

A `429` response has `Cache-Control: no-store`, a `Retry-After` header and this
body: `{ "error": "rate_limited", "message": "Too many requests. Try again later." }`.
`Retry-After` gives the length of the window in seconds. This value is `60`,
or `3600` for the download limit of a TV link. A TV link response also keeps
the TV link headers. The app tells the user to wait 1 minute and try again.

### Client address

The client address is the connection address that the Functions host gives to
the worker. The API does not read `X-Forwarded-For` or other request headers,
because a client can set these headers. An IPv4-mapped IPv6 address counts as
its IPv4 address. An IPv6 address counts as its /64 prefix, because 1 client
can use many addresses in its /64 prefix. A network with NAT, for example a
home network, has 1 client address for all of its devices.

On Flex Consumption, the host can give the worker an address of the Azure
infrastructure instead of the address of the client. Then all clients on an
instance share 1 client limit. A client that floods can then block the other
users. The instance limit continues to limit the cost.

To make sure that each client has its own limit, do this test after each
deployment. The test is correct only when the app has 1 instance.

1. Do not use TV links from your network for 2 minutes.
2. Send 31 requests to a TV link that does not exist. Send them from your
   network in less than 1 minute. For example:

   ```powershell
   1..31 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code}`n" "https://<FUNCTION-APP-HOST>/api/feed/link-that-does-not-exist" }
   ```

3. Make sure that the first 30 requests get `404`.
4. Make sure that the last request gets `429`.
5. Immediately send 1 request from a different network, for example a phone on
   mobile data.
6. Make sure that this request gets `404`.

If the request in step 5 gets `429`, all clients share 1 client address. Then
keep the maximum instance count at 1 or 2, because each instance has its own
counters.

### Cost controls in Azure

The limits in the worker protect each instance. They do not stop a large flood
from many addresses. A flood can use all of the instance limit. Then the other
users also get `429` until the flood stops. Each rejected request also starts a
short function execution, which has a small cost.

Use the settings and the alerts in
[Hosting plan and cost guards](#hosting-plan-and-cost-guards). They include the
maximum instance count, a budget and daily caps. Also make a log alert for
this warning:

`Rate limit {RateLimitPolicy} rejected {RejectedRequests} requests on this instance since its previous warning.`

Each instance writes this warning a maximum of 1 time each minute for each
limit. `RateLimitPolicy` is `Instance`, `Api`, `Feed` or `FeedDownload`. The
warning does not contain client addresses or tokens.

To stop an attack on the TV links, set `AzureWebJobs.OnlineFeed.Disabled` to
`true`. Refer to [TV link kill switches](#tv-link-kill-switches). To stop all
requests to the API, stop the Function App.

## Local development

Prerequisites: Node.js 22 or a compatible LTS, .NET 10 SDK/runtime, Azure Functions
Core Tools v4 and, for the example storage settings, an already running Azurite
emulator with its Blob and Queue services. Install/start only the tools and
services you have approved.

Azurite keeps the imported playlists, the saved profiles and their outputs.
These files contain the provider credentials. Keep the Azurite data outside
the repository. For example, start Azurite with
`azurite --location "$env:LOCALAPPDATA\Azurite"`. In VS Code, set
`azurite.location` to a folder outside the repository.

The root `.gitignore` also ignores the Azurite data and all `*.m3u` and
`*.m3u8` files. Before each commit, examine the output of `git status --short`.
Stage the files by name.

From the repository root in PowerShell:

```powershell
npm --prefix .\client ci
Copy-Item .\client\.env.example .\client\.env.local
Copy-Item .\api\local.settings.example.json .\api\local.settings.json
```

Both local configuration files are ignored; `local.settings.json` is excluded
from publish output. Put the public registration IDs, the scope and the
allowed account IDs in them. For `VITE_ENTRA_SPA_CLIENT_ID` and
`OnlineAuth__SpaClientId`, use the client ID of the development SPA, not the
production SPA. See [Development registrations](#development-registrations).
In `.env.local`, use
`VITE_ONLINE_API_BASE_URL=http://localhost:7071/api/`. Leave online values blank
if you only want the local-file workflow.

Run the following in separate terminals:

```powershell
npm.cmd --prefix .\client run dev -- --host=localhost --port=5173 --strictPort
```

```powershell
Set-Location .\api
func start --port 7071 --functions OnlineSession OnlineImportStart OnlineImportStatus OnlineImportCategories OnlineImportPlaylist OnlineImportDiscard OnlineImportWorker OnlineImportPoison OnlineImportCleanup OnlineProfileList OnlineProfileCreate OnlineProfileGet OnlineProfileCategories OnlineProfileSelect OnlineProfileRename OnlineProfileSource OnlineProfileRefresh OnlineProfileOutput OnlineProfileDelete OnlineProfileRefreshWorker OnlineProfileRefreshPoison OnlineProfileCleanup OnlineFeedGet OnlineFeedCreate OnlineFeedRegenerate OnlineFeedRevoke OnlineFeed OnlineFeedCleanup
```

This list contains every function of the API. The API does not have the
legacy `Upload` and `Download` functions.
The example `local.settings.json` points both `AzureWebJobsStorage` and
`OnlinePlaylistStorage` at Azurite (`UseDevelopmentStorage=true`); the API
creates the `online-imports`, `online-profiles` and `online-feeds` containers
and their queues there on first use. The outbound request policy also applies locally: imports
and profile refreshes from `localhost`, private-network or emulator addresses
fail with `blocked_destination`, so use a publicly reachable playlist URL.

Open **http://localhost:5173/m3u-split/**. The existing Vite `/api` proxy remains
untouched for compatibility, but the new online client never uses it. It calls
the separately configured Functions origin, exercising the same cross-origin
authentication boundary as production.

## Hosting plan and cost guards

The `OnlineFeed` function accepts requests without sign-in. A flood of
requests to it can increase the cost of the Function App. Use these settings
to keep the cost low and to get a warning quickly.

### Dedicated plan settings

Keep Always On enabled for the API.
Its four queue triggers and three hourly timers must operate without HTTP requests.
The timers delete expired data, which can contain provider credentials.
Do not change a shared plan to control this API's cost.
Such a change also affects its other apps.

| Setting | Value |
| --- | --- |
| Linux runtime (`linuxFxVersion`) | `DOTNET-ISOLATED\|10.0` |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | `~4` |
| Always On | `true` |
| `WEBSITE_RUN_FROM_PACKAGE` | `1` |
| `SCM_DO_BUILD_DURING_DEPLOYMENT` | `false` |
| Minimum TLS for the app and SCM | `1.2` |
| HTTPS only and HTTP/2 | Enabled |
| FTP, FTPS, and basic authentication for SCM and FTP | Disabled |
| ARR affinity | Disabled |

The existing plan supplies the compute capacity.
Storage transactions and network traffic can add cost.
Flex Consumption memory and scale limits do not apply to a Dedicated plan.
Use an App Service metric such as requests or CPU for a Dedicated plan alert.
`OnDemandFunctionExecutionCount` below applies to Flex Consumption.

### Flex Consumption settings

Use these values if the Function App uses **Flex Consumption** (Linux):

| Setting | Value | Reason |
| --- | --- | --- |
| Instance memory (`instanceMemoryMB`) | `512` | The smallest size. If a large import fails, use `2048`. |
| Maximum instance count (`maximumInstanceCount`) | `1` | The lowest value. The default is 100 and the highest value is 1,000. |
| Always ready instances | `0` | The default. An always ready instance costs money all the time. |
| HTTP concurrency for each instance | The default | For 512 MB, the default is 4 requests at the same time. |

The maximum instance count applies to each scale group, not to the full app.
All HTTP functions are one group. Each queue function and each timer function
is a separate group. If too many requests arrive, a low maximum can cause
delays or rejected requests. If the web app is too slow, set the maximum to 2.

### Cost alerts and caps

Make these items in the subscription or in the resource group of the app:

1. Make an action group that sends an email to you.
2. Make a monthly budget of 5 USD on the resource group. Add alerts at 50%,
   80% and 100% of the actual cost.
3. Add budget alerts at 100% of the forecast cost and at 400% of the actual
   cost.
4. Make a cost anomaly alert on the subscription.
5. Make a metric alert on the Function App for `OnDemandFunctionExecutionCount`.
   Use the total for 1 hour, a threshold of 500 and a check every 5 minutes.
   Send the alert to the action group.
6. Use a Log Analytics workspace with the `PerGB2018` pricing tier, a
   retention of 30 days and a daily cap of 0.15 GB.
7. Use workspace-based Application Insights with a daily cap of 0.15 GB.
8. Give read access to the telemetry only to the owner. The request URLs
   contain the TV link tokens.

> **WARNING:** Do not use the budget alerts as the only flood guard. A budget
> alert can arrive approximately 8 to 49 hours after the cost occurs. A budget
> does not stop resources. The metric alert arrives in minutes.

> **CAUTION:** When the workspace or Application Insights gets to its daily
> cap, it stops data collection until the next daily reset. Until then, it
> gets no new security records, and log alerts get no new data.

The API also has its own rate limits. For example, by default the API accepts
a maximum of 20 downloads of each TV link in 1 hour. With 1 instance and a
playlist of 2 MB, the API then sends a maximum of approximately 30 GB for each
link in 1 month. Refer to
[Rate limits and cost protection](#rate-limits-and-cost-protection).
Azure API Management and Azure Front Door can also stop a flood, but each of
them adds cost. This setup does not use them.

### If a flood occurs

1. On the profile, open **TV link**. Select **Regenerate link** or
   **Revoke link**. Then confirm the action. If you regenerate the link, put
   the new link in Smart IPTV **My List**.
2. If the flood continues, set `AzureWebJobs.OnlineFeed.Disabled` to `true`.
   This change restarts the app. See
   [TV link kill switches](#tv-link-kill-switches).
3. When the flood stops, remove the setting.

### Runtime support

The API and its test project target `net10.0`.
The API uses stable package versions. The workflow uses the .NET `10.0.x` SDK.
Microsoft support for .NET 10 LTS stops on 14 November 2028.

- For Flex Consumption, use `functionAppConfig.runtime` with name `dotnet-isolated` and version `10.0`.
- For a Dedicated Linux plan, use `DOTNET-ISOLATED|10.0` for `linuxFxVersion`.
- The template `infra\function-api.bicep` uses the .NET 10 Dedicated runtime.
- An existing .NET 9 app stays unchanged until the owner approves its runtime change and package deployment.

## Independent deployment

`.github/workflows/deploy-pages.yml` continues to deploy only `client/dist` to
Pages, with optional public online configuration.

The `azure` code cannot run as a Static Web Apps managed API. For this reason,
the `azure` branch does not have the old
`azure-static-web-apps-purple-forest-061720b03.yml` workflow. For a pull
request, GitHub uses the workflows of the merge commit. A pull request from
`azure` to `master` does not deploy a preview to the old Static Web App.

On `master`, this workflow is still active. Each push to `master` that changes
`client/`, `api/` or the workflow deploys to the old Static Web App. This
deployment includes the legacy `Upload` and `Download` functions. A pull
request to `master` can also deploy a public preview environment. After a
merge of `azure` into `master`, no workflow updates the old Static Web App.

When you no longer use the old Static Web App, do these steps:

1. Remove the workflow file from `master`, or disable the workflow in GitHub.
2. Delete the Static Web App resource. This step also deletes its preview
   environments.
3. Delete the repository secret
   `AZURE_STATIC_WEB_APPS_API_TOKEN_PURPLE_FOREST_061720B03`. Also delete the
   other `AZURE_STATIC_WEB_APPS_API_TOKEN_*` secrets that no workflow uses.
4. If the old app used blob storage, delete its `uploads` container. The
   legacy `Upload` function kept each playlist there, with the provider
   credentials.

The `azure` branch is the source of the Azure deployment. The `master` branch
stays the source of GitHub Pages. `.github/workflows/deploy-api.yml` builds the
API for each push and pull request to `azure` that changes the API or the
workflow. A push to `azure` also deploys the package, but only after a
reviewer approves the deployment in the `azure-functions` environment. A pull
request never deploys.

The workflow does not add Azure resources or change server settings. Before
it deploys, it checks these items:

- The deployment IDs
- The server auth settings
- A URL import storage connection (`OnlinePlaylistStorage` or both
  identity-based service URIs)
- The `dotnet-isolated` runtime (`functionAppConfig.runtime` on Flex
  Consumption, or `FUNCTIONS_WORKER_RUNTIME` on other plans)

The `verify` job also examines the package of each build. The job stops if the
package has the legacy `Upload` or `Download` function. It also stops if an
HTTP route is not in `online/` or `feed/`.

The package includes the generated hidden `.azurefunctions` directory and
`host.json`, but never local configuration. GitHub shows **Run workflow** only
for a workflow file on the default branch (`master`). This file is not on
`master`, so use a push to `azure` to deploy.

Before the first push of `azure`, add the **`azure-functions`** environment in
GitHub. Set its deployment branch rule to the `azure` branch only. Add
yourself as a required reviewer. Required reviewers are free for public
repositories.

When the Azure resources are ready, use a separate deployment identity, with a
GitHub OIDC federated credential for this subject:

`repo:goncalvesj/m3u-split:environment:azure-functions`

Use issuer `https://token.actions.githubusercontent.com` and audience
`api://AzureADTokenExchange`. Grant only the permissions required to read
Function App settings and deploy to that existing app, at the narrowest
resource scope. Do not use the SPA as the deployment identity.

Add these values as **environment secrets** of `azure-functions`, not as
repository secrets. They are IDs and names, not credentials. Anyone can read
the run logs of a public repository. GitHub masks `secrets` values in these
logs, but it does not mask `vars` values. Do not add a client secret.

| Secret | Value |
| --- | --- |
| `AZURE_DEPLOY_CLIENT_ID` | The OIDC deployment identity's client ID |
| `AZURE_TENANT_ID` | Its selected directory ID |
| `AZURE_SUBSCRIPTION_ID` | The explicitly approved subscription ID |
| `AZURE_RESOURCE_GROUP` | The existing Function App's resource group |
| `AZURE_FUNCTION_APP_NAME` | The existing Function App's name |

If you push `azure` before you add this environment, GitHub adds it with no
protection rules. The `deploy` job then stops at its first step, because the
environment has no secrets. Add the branch rule and the reviewer before you
add the secrets.

Each action in `deploy-api.yml` uses a full commit SHA, with its version in a
comment. A tag can move to different code, but a commit SHA cannot. To get
pull requests that move these pins to new versions, add this
`.github/dependabot.yml` file to `master`. Dependabot reads its configuration
only from the default branch.

```yaml
version: 2
updates:
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
  - package-ecosystem: github-actions
    directory: /
    target-branch: azure
    schedule:
      interval: weekly
```

Also pin the actions in the workflows on `master` to commit SHAs. Give each
workflow only the permissions that it uses.

After you set the server configuration and CORS, push the `azure` branch.
Then approve the API deployment.
Deploy the App Service UI separately with the commands in
[Build and deploy again](#build-and-deploy-again).
Deploy Pages separately if you use it.
Each host uses its own public build variables.
Rebuild the selected UI after the API deployment.

No workflow deploys to both UI hosts.
No workflow makes paid resources.
This local UI deployment runs no GitHub workflow.

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
| 429 | `rate_limited`: too many requests. Refer to [Rate limits and cost protection](#rate-limits-and-cost-protection) |
| 503 | Missing/invalid server configuration or signing-key discovery unavailable. `rate_limits_not_configured` shows that an `OnlineRateLimits__*` setting is not valid |

The rate limit checks occur first. The import endpoints then return these guard responses before anything else, and
then the codes listed in [Import endpoints](#import-endpoints). The saved
profile endpoints do the same, followed by the codes listed in
[Profile endpoints](#profile-endpoints). The TV link owner endpoints do the
same, followed by the codes listed in [TV link endpoints](#tv-link-endpoints);
the TV link itself (`OnlineFeed`) never returns guard responses.

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
`404` from the removed legacy `Upload` and `Download` routes, and offline/backend failure behavior. Microsoft
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

For TV links, also verify in that environment:

- A TV link added to Smart IPTV My List loads the profile's playlist, and the
  TV gets the new playlist after the next successful refresh.
- `GET` and `HEAD`, with and without `.m3u`, return `200` with the documented
  headers, and a link to a profile that was never refreshed returns
  `404 feed_not_ready`.
- After **Regenerate link**, **Revoke link**, deleting the profile or removing
  the owner from `OnlineAuth__AllowedObjectIds`, the old link returns the
  identical `404 feed_not_found`, as does a made-up token.
- Another allowed account gets 404 `profile_not_found` from the owner
  endpoints for that profile.
- Worker logs, traces and dependencies in Application Insights never contain
  a token. Note where the host's request telemetry and the platform's logs
  record the full link, and who can read them.
- Setting `AzureWebJobs.OnlineFeed.Disabled` to `true` stops every link, and
  removing the setting restores them.
- The client limit of TV links works, and each client has its own limit. Use
  the procedure in
  [Rate limits and cost protection](#rate-limits-and-cost-protection).
