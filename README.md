# m3u-split

Split and manage M3U playlists with a React + Vite frontend on GitHub Pages and an optional, standalone .NET 9 Azure Functions API.

## Highlights

- Parse and split M3U playlists in the browser
- Save favourite categories and reuse them with your next playlist
- React 19 + TypeScript UI powered by Vite
- Local file processing, selection, search, favourites and download never require an account
- Optional Microsoft sign-in with server-verified account access; online playlist processing is not available yet

## Repo layout

- `client/` – React + TypeScript app (Vite)
- `api/` – .NET 9 isolated-worker Azure Functions v4 API
- `api.tests/` – API token-validation and account-authorization regression tests

## Optional online sign-in (Phase 1)

The default workflow remains entirely browser-local at
[goncalvesj.github.io/m3u-split](https://goncalvesj.github.io/m3u-split/).
The separate **Online playlists** section uses Microsoft sign-in in a popup,
then verifies an API access token against `GET /api/online/session` on a
separately configured Function App. Sign-in and sign-out do not reset the loaded
local file, search, selection or favourites.

An account is authorized only when the API validates its token and its configured
tenant/user object ID allowlist. A successful Microsoft login alone is not online
authorization. Without configuration, or when authentication/the backend fails,
local processing remains available.

**Only the sign-in boundary is implemented.** URL import, saved online profiles,
refresh, publishing and TV feed links are not exposed. The legacy upload/download
functions are not the online feature; keep them disabled in standalone deployments.

See [Online sign-in and standalone API setup](docs/online-setup.md) for the exact
Entra registrations, callbacks, public build variables, server settings, CORS
and the API workflow, which builds on relevant pushes/PRs and deploys only by
manual dispatch. No live resources or identities are configured in this repository.
Pages remains the frontend host; the already-disabled SWA workflow is retained
unchanged.

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