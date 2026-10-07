# m3u-split

Split and manage M3U playlists with a React + Vite UI on Azure App Service or GitHub Pages.
The optional API uses .NET 10 Azure Functions.

## Highlights

- Parse and split M3U playlists in the browser
- Save favourite categories and reuse them with your next playlist
- React 19 + TypeScript UI powered by Vite
- Local file processing, selection, search, favourites and download never require an account
- Optional Microsoft sign-in with server-verified account access, plus URL import and saved profiles for allowed accounts: the API downloads a provider playlist and returns only the filtered channels you pick, and a saved profile regenerates them with one click and can serve the latest result to Smart IPTV through a secret TV link

## Repo layout

- `client/` – React + TypeScript app (Vite)
- `api/` – .NET 10 isolated-worker Azure Functions v4 API
- `api.tests/` – API token-validation and account-authorization regression tests

## Optional online sign-in, URL import and saved profiles

The local file workflow stays entirely in the browser.
The Azure UI is at
[m3u-split.azurewebsites.net](https://m3u-split.azurewebsites.net/).
GitHub Pages stays at
[goncalvesj.github.io/m3u-split](https://goncalvesj.github.io/m3u-split/).
The separate **Online playlists** section uses Microsoft sign-in in a popup,
then verifies an API access token against `GET /api/online/session` on a
separately configured Function App. Sign-in and sign-out do not reset the loaded
local file, search, selection or favourites.

An account is authorized only when the API validates its token and its configured
tenant/user object ID allowlist. A successful Microsoft login alone is not online
authorization. Without configuration, or when authentication/the backend fails,
local processing remains available.

**URL import is available to verified accounts.** Paste a provider playlist URL
in **Online playlists**: the Function App downloads it into private storage with
SSRF protection and lists its categories with channel counts. Pick categories
(with search and the same favourites) and download a server-generated filtered
`channels.m3u`; the full provider file never reaches the browser. Imports are
private to each account, one at a time, and expire after 24 hours by default.

**Saved profiles** keep a name, a provider URL and your chosen categories on the
server, up to 10 per account by default. One click on **Refresh now** downloads
the playlist again and regenerates the filtered playlist on the server, and
**Download latest** saves the newest result at any time. A failed refresh keeps
the last good playlist. The stored URL stays in private storage until you delete
the profile or change its URL, and is never shown, returned or logged; only its
host is displayed. The API does not have the legacy upload and download
functions.

**TV links** give a saved profile a secret, read-only playlist URL for Smart
IPTV **My List**. Open **TV link** on the profile, create the link and paste it
once as an external playlist link: from then on the TV loads the profile's
latest successful refresh without sign-in. Anyone with the link can download
that playlist, including the provider's stream credentials, so keep it private.
**Regenerate link** replaces it and **Revoke link** turns it off immediately.
The API limits the requests of each client and the downloads of each TV link.
The API sends HTTP 429 for each request above a limit.

See [Online sign-in, URL import, saved profiles and standalone API setup](docs/online-setup.md)
for the exact Entra registrations, callbacks, public build variables, server
settings, rate limits, playlist storage and roles, CORS, the hosting plan and
cost guards, and the API workflow. The workflow builds the API for pushes and
pull requests to the `azure` branch. It deploys a push to `azure` after a
reviewer approves the deployment.
Before the first push of `azure`, add the `azure-functions` environment, its `azure` branch rule and a required reviewer.
The UI uses App Service.
The API now uses a Dedicated Linux plan with a system-assigned identity.
Refer to [API on a Dedicated plan](docs/online-setup.md#api-on-a-dedicated-plan) for the template and local deployment commands.
The API denies online access until it has valid Entra settings.
Entra registrations and API CORS are future tasks.
GitHub Pages keeps `master` as its source.
The `azure` branch does not deploy to the old Static Web App.
Refer to [Independent deployment](docs/online-setup.md#independent-deployment).

## Category favourites

1. Load an M3U playlist and check the categories you want.
2. Choose **Save selection as favourites** to remember all checked categories,
   including those hidden by the search filter.
3. On a later visit, load your playlist and choose **Use favourites** to replace
   the current selection with the saved categories present in that file.
4. Download as usual. Favourites are never applied or updated automatically.

**Update favourites** replaces the saved set with all currently checked
categories; an empty selection cannot be saved. **Forget favourites** removes
the saved set without changing the current selection. Changing files, clearing
checkboxes, and downloading do not alter favourites.

Matching uses exact `group-title` category names, including case and whitespace,
not the filename or category order. Missing categories are reported and kept in
favourites for future playlists; if none match, the current selection is left
unchanged. Search only filters the visible list, not which favourites are saved
or applied. An explicit update replaces the entire saved set, including any
previously saved categories missing from the current playlist.

Only category names are saved in a versioned `localStorage` entry named
`m3u-split:favourites`. Playlist contents, stream URLs, and files are not saved by
this feature. There is one favourites set shared across playlists on the same
site and browser profile, with no account, server storage, or cross-device sync.
Clearing site data removes favourites; private browsing may discard them when
the private session ends. You still choose your M3U file each time.

If browser storage is unavailable, full, or contains invalid favourites, the app
shows a warning and ordinary category selection and downloading remain usable.
Invalid saved data is not overwritten unless you explicitly save a new selection.

## Client scripts

- `npm run dev` – start Vite dev server
- `npm run build` – typecheck + build
- `npm run lint` – run ESLint
- `npm test` – run local workflow, favourites, and online authentication regression tests
- `npm run preview` – preview production build

From the repository root, run `dotnet test .\api.tests\api.tests.csproj` for the
backend boundary tests. These tests use locally generated signing keys, not live
Microsoft accounts.