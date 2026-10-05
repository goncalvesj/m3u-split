# Phase 1: Microsoft sign-in and standalone Functions

## What is ready

Local M3U upload/read, category search/selection, opt-in favourites and filtered
download remain available without an account or network connection once the app
has loaded. Authentication lives in a separate **Online playlists** section and
does not own or reset local file state. Favourites keep the existing
`m3u-split:favourites` key and exact category-name matching.

Phase 1 adds Microsoft sign-in/sign-out and **only** `GET /api/online/session`.
The endpoint returns `{ "tenantId": "...", "objectId": "...", "expiresAt": "..." }`
after verifying authorization. It does not import, upload, store, filter or
publish online playlists. Those operations, saved online profiles, refresh jobs
and stable TV feed URLs belong to later phases.

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
| `AzureWebJobs.Upload.Disabled` | `true` |
| `AzureWebJobs.Download.Disabled` | `true` |

All authentication/account values must be valid. An empty allowlist or any
invalid entry disables online access; valid entries are not silently accepted
from an otherwise invalid list. Configuration is read by the worker; restart it
after changing settings (Azure app-setting changes normally trigger restart).

The Functions host also needs its normal **host storage** configuration
(`AzureWebJobsStorage`, or the hosting plan's supported identity-based
`AzureWebJobsStorage__...` configuration with the required storage roles).
Prefer managed identity where supported; if a connection string is necessary,
keep it only in server settings/Key Vault. Follow the selected plan's normal
content/deployment-storage requirements. Host storage is infrastructure, not
playlist storage: Phase 1 creates no playlist containers, queues, profiles,
workers or provider credentials. Existing `UseBlobStorage`,
`StorageConnectionString` and `FilePath` settings belong to disabled legacy
functions and are not needed for the session endpoint.

The session function uses `AuthorizationLevel.Anonymous` **at the Function-key
layer only** so a SPA need not embed a shared Function key. `OnlineSession.Run`
unconditionally calls `OnlineAccess.AuthorizeAsync` before producing a
successful response. Future online functions must call the same guard and use
its validated identity; copying the anonymous trigger without the guard would
be unsafe. This implementation does not rely on unsupported `[Authorize]`
attributes or ASP.NET middleware routing in the isolated worker.

The legacy `Upload`/`Download` triggers retain `AuthorizationLevel.Function`.
Keep the disable flags above set in local and deployed environments; they are
not user-owned authenticated playlist endpoints. Core Tools normally bypasses Function-key authentication and can execute even
disabled HTTP functions locally. Start the Phase 1 host with
`--functions OnlineSession` as shown below so legacy routes are not registered
at all. In Azure, disabled endpoints are inaccessible without the privileged
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
OPTIONS preflight for the `Authorization` header. Do not add a second conflicting
ASP.NET CORS implementation or a wildcard header inside the function.
For local Core Tools, `local.settings.example.json` sets that one local origin.
CORS is not an account restriction: direct HTTP clients still need a valid
access token and an allowed identity.

## Local development

Prerequisites: Node.js 22 or a compatible LTS, .NET 9 SDK/runtime, Azure Functions
Core Tools v4 and, for the example host-storage setting, an already running
Azurite emulator. Install/start only the tools and services you have approved.

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
func start --port 7071 --functions OnlineSession
```

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
deployment IDs, populated server auth settings and disabled legacy functions
before deploying. The package includes the generated hidden `.azurefunctions`
directory and `host.json`, but never local configuration.

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
