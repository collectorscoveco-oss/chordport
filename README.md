# ChordPort

ChordPort is a Windows-first, cross-platform local music request desk. V0.1.1 is a safe metadata/request manager: it ships with a small local starter catalog, a request queue, local settings, optional local SABnzbd connection testing, and system health. It does **not** acquire music, send downloads, contact indexers, manage accounts, expose remote access, or handle API secrets outside its protected local configuration.

> Only use music and metadata from sources you are authorized to use. ChordPort never alters files in V0.1.

## Architecture

- `src/ChordPort.Api` — ASP.NET Core .NET 8 API, SQLite store, and production static-file host.
- `frontend` — React + TypeScript Vite UI. Production publish builds it and copies it into the backend's `wwwroot`.
- `tests/ChordPort.Api.Tests` — xUnit coverage for catalog searching, queue behavior, settings validation, and the health endpoint.

The backend binds to `127.0.0.1:5098` by default. On Windows it stores its SQLite database under `%LOCALAPPDATA%\ChordPort\chordport.db`; other platforms retain the application-directory default. Set `CHORDPORT_DATA_DIR` to an absolute user-writable directory only when an advanced local override is needed.

## SABnzbd connection settings

In **Settings → Download Clients → SABnzbd**, enter a root server URL such as `http://127.0.0.1:8080`, an optional SABnzbd category (default `music`), and the SABnzbd API key. The server URL is deliberately restricted to `localhost`, loopback, or RFC1918/unique-local private network addresses; public hosts, URL paths, credentials, query strings, and fragments are rejected.

The key is protected with ASP.NET Core Data Protection before it reaches the local SQLite database. The UI only reports whether a key is saved; it never reads the value back. Leaving the key blank preserves the existing saved key, while **Clear saved key** explicitly removes it. ChordPort never logs the key or includes it in API responses. The protected key material is stored in the local data directory; access to that directory is equivalent to access to local ChordPort settings.

**Test connection** makes one local/private request to SABnzbd's `mode=version` API to verify the saved address and key. It reports a safe success/failure message and does not add NZBs, send downloads, query indexers, or otherwise control SABnzbd. No download or send-to-SAB workflow exists in this release.

## Update checks

The System screen can check whether a newer stable ChordPort release is available. The browser calls only ChordPort's local backend; the backend makes the GitHub `releases/latest` request, accepts only the project's HTTPS GitHub release-tag URL, compares stable semantic versions, and caches the result briefly. It never downloads, installs, applies, or restarts an update. This release reports current version `0.1.1`.

## Windows portable tester package

A Windows x64 package is self-contained: testers do **not** need a .NET installation. From the repository root in PowerShell (PowerShell 7 is supported on Windows, Linux, and macOS), build it with:

```powershell
./scripts/package-windows.ps1 -Version 0.1.1
```

The command publishes `win-x64` with `--self-contained true`, builds the React UI, and writes only ignored output beneath `artifacts/`:

- `artifacts/ChordPort-0.1.1-win-x64/` — unpacked tester distribution
- `artifacts/ChordPort-0.1.1-win-x64.zip` — versioned portable ZIP

Extract the ZIP to a user-writable folder (for example, Downloads), open the extracted `ChordPort-0.1.1-win-x64` folder, and double-click `Start-ChordPort.cmd`. The launcher first refuses to start if any service is already listening on `http://127.0.0.1:5098`; close that service and retry. Otherwise, it starts `ChordPort.Api.exe` only on `http://127.0.0.1:5098`, waits for `GET /api/health` to return successfully, and only then opens the browser. Its console shows diagnostics and remains open when startup fails.

### Package verification

WSL/Linux can build the self-contained `win-x64` package and inspect its ZIP contents, but the resulting Windows EXE and `.cmd` launcher cannot be executed or smoke-tested from WSL or Linux. Native Windows validation is still required: on a Windows x64 machine, extract the ZIP, run `Start-ChordPort.cmd`, confirm the health check succeeds at `http://127.0.0.1:5098/api/health`, and verify the browser opens only after it is healthy.

Use `taskkill /IM ChordPort.Api.exe` from Command Prompt to stop a running tester copy. To update, stop it, extract a new versioned ZIP to a new folder, and start the new launcher. Do not copy a new package over a running one.

Tester data is deterministic and survives package replacement at `%LOCALAPPDATA%\ChordPort\chordport.db`, not beside the extracted app. Back up that directory before testing a pre-release update if you need to preserve requests and settings. This V0.1 package is local-only, has no remote access, acquisition, account, indexer, or secret-management features, and it is not code-signed; Windows may show a reputation warning for unsigned software.

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
- `GET /api/settings`, `PUT /api/settings` — SABnzbd responses mask the saved key; `POST /api/settings/sabnzbd/test` only calls SABnzbd's version endpoint.
- `GET /api/system/update` — backend-only GitHub release check; reports availability but never installs an update.

Invalid inputs return small client-readable JSON error bodies and no internal details.
