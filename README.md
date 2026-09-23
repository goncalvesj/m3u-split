# m3u-split

Split and manage M3U playlists with a React + Vite frontend and a .NET 6 Azure Functions API.

## Highlights

- Parse and split M3U playlists in the browser
- Save favourite categories and reuse them with your next playlist
- React 19 + TypeScript UI powered by Vite
- (Optional) Azure Functions v4 API (net6.0) with Azure Blob Storage

## Repo layout

- `client/` – React + TypeScript app (Vite)
- `api/` – .NET 6 Azure Functions API

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
- `npm test` – run favourites storage and UI regression tests
- `npm run preview` – preview production build