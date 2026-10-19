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
npm install
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

The published application hosts the built React UI and API together. V0.1 persistence covers queue requests and basic library preferences only; the catalog is deliberately mock/local adapter data awaiting later authorized MusicBrainz, Spotify, or AcoustID integrations.

## API

- `GET /api/health`
- `GET /api/search?mode=Songs|Artists|Albums&q=...`
- `GET /api/queue`, `POST /api/queue`, `DELETE /api/queue/{id}`
- `GET /api/settings`, `PUT /api/settings`

Invalid inputs return small client-readable JSON error bodies and no internal details.
