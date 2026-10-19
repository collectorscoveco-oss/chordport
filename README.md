# ChordPort

ChordPort is a Windows-first, cross-platform local music request desk. V0.1 is a safe metadata/request manager: it ships with a small local starter catalog, a request queue, local settings, and system health. It does **not** acquire music, contact indexers, manage accounts, expose remote access, or handle API secrets.

> Only use music and metadata from sources you are authorized to use. ChordPort never alters files in V0.1.

## Architecture

- `src/ChordPort.Api` — ASP.NET Core .NET 8 API, SQLite store, and production static-file host.
- `frontend` — React + TypeScript Vite UI. Production publish builds it and copies it into the backend's `wwwroot`.
- `tests/ChordPort.Api.Tests` — xUnit coverage for catalog searching, queue behavior, settings validation, and the health endpoint.

The backend binds to `127.0.0.1:5098` by default. SQLite uses a local `chordport.db`; no Windows-specific path is hardcoded.

## Windows development

From PowerShell, make the local .NET SDK available if it is not already installed globally:

```powershell
$env:PATH = "$HOME\.local\share\dotnet;$env:PATH"
$env:LD_LIBRARY_PATH = "$HOME\.local\share\chordport-deps\usr\lib\x86_64-linux-gnu"
```

For WSL, use the equivalent paths supplied by your environment. Then:

```powershell
cd chordport\frontend
npm ci
npm run dev
# separate terminal:
cd ..
dotnet run --project src/ChordPort.Api
```

Open the Vite URL for development. The Vite proxy sends `/api` calls to the locally bound backend.

## Test and production build

```powershell
dotnet test ChordPort.sln
cd frontend
npm run build
cd ..
dotnet publish src/ChordPort.Api -c Release -o .\artifacts\publish
```

The published application hosts the built React UI and API together. V0.1 persistence covers queue requests and library preferences. The local starter catalog remains the default offline search source.

## Metadata search (MusicBrainz)

MusicBrainz is an optional metadata-only provider. Select it in **Settings → Metadata provider**, save, then explicitly submit a search from Music. ChordPort sends the submitted term from the local backend to the configurable public MusicBrainz web-service URL (`https://musicbrainz.org/ws/2` by default); the browser never contacts MusicBrainz directly. No MusicBrainz account, token, or other secret is required.

Requests are rate-limited, time out quickly, and are cached briefly in memory. Results report their source and status rather than being silently replaced with local catalog items. Song results preserve the MusicBrainz **recording ID**; artist and album results preserve their MusicBrainz entity IDs and types for future authorized matching workflows. Remote metadata results are not queueable in this phase.

ChordPort remains local/private except for an explicit MusicBrainz search. It has no download, acquisition, indexer, account, remote-access, or music-file modification capability. Metadata results do not grant any right to obtain or use music.

Known limitations: the local catalog is still a small V0.1 starter set; MusicBrainz searches are text-only and no import, file matching, cover art, or offline metadata persistence exists yet.

## API

- `GET /api/health`
- `GET /api/search?mode=Songs|Artists|Albums&q=...&provider=Local%20catalog|MusicBrainz` — returns `{ provider, status, results, message }`; result IDs and entity types are explicit.
- `GET /api/queue`, `POST /api/queue`, `DELETE /api/queue/{id}`
- `GET /api/settings`, `PUT /api/settings`

Invalid inputs return small client-readable JSON error bodies and no internal details.
