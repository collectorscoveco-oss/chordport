@echo off
setlocal EnableExtensions

set "DIST_DIR=%~dp0"
set "URL=http://127.0.0.1:5098"
set "CHORDPORT_DATA_DIR=%LOCALAPPDATA%\ChordPort"
set "ASPNETCORE_URLS=%URL%"

cd /d "%DIST_DIR%" || goto :startup_failed

if not exist "%DIST_DIR%ChordPort.Api.exe" (
  echo ChordPort executable was not found in "%DIST_DIR%".
  goto :startup_failed
)

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$client = $null; try { $client = [System.Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1', 5098); exit 10 } catch [System.Net.Sockets.SocketException] { if ($_.Exception.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionRefused) { exit 0 }; exit 20 } catch { exit 20 } finally { if ($null -ne $client) { $client.Dispose() } }"
if errorlevel 20 goto :port_preflight_failed
if errorlevel 10 goto :port_in_use
if errorlevel 1 goto :port_preflight_failed
goto :port_available

:port_in_use
echo A local service is already listening at %URL%.
echo Close it, then retry Start-ChordPort.cmd.
goto :startup_failed

:port_preflight_failed
echo Could not verify whether %URL% is available.
echo ChordPort was not started. Check Windows PowerShell, then retry.
goto :startup_failed

:port_available
if not exist "%CHORDPORT_DATA_DIR%" mkdir "%CHORDPORT_DATA_DIR%" || goto :startup_failed

echo Starting ChordPort at %URL%
echo Data is stored in "%CHORDPORT_DATA_DIR%".
echo Waiting for the local health check...
start "ChordPort" /b "%DIST_DIR%ChordPort.Api.exe" --urls "%URL%"

for /l %%I in (1,1,30) do (
  powershell -NoProfile -Command "try { $response = Invoke-WebRequest -UseBasicParsing -Uri '%URL%/api/health' -TimeoutSec 1; if ($response.StatusCode -eq 200) { exit 0 }; exit 1 } catch { exit 1 }"
  if not errorlevel 1 goto :ready
  timeout /t 1 /nobreak >nul
)

echo ChordPort did not become healthy within 30 seconds.
echo Check the messages above, then press a key to close this window.
goto :startup_failed

:ready
echo ChordPort is ready. Opening your browser...
start "" "%URL%"
exit /b 0

:startup_failed
echo Startup failed. Press a key to close this window.
pause >nul
exit /b 1
