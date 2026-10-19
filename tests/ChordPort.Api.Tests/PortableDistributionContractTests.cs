using System;
using System.IO;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class PortableDistributionContractTests
{
    [Fact]
    public void Runtime_data_directory_uses_a_configured_directory_before_platform_defaults()
    {
        var result = RuntimeDataDirectory.Resolve("D:\\Tester Data\\ChordPort", "C:\\Users\\Tester\\AppData\\Local", isWindows: true, "/opt/chordport");

        Assert.Equal("D:\\Tester Data\\ChordPort", result);
    }

    [Fact]
    public void Runtime_data_directory_uses_local_app_data_for_windows_packages()
    {
        var result = RuntimeDataDirectory.Resolve(null, "C:\\Users\\Tester\\AppData\\Local", isWindows: true, "/opt/chordport");

        Assert.Equal(Path.Combine("C:\\Users\\Tester\\AppData\\Local", "ChordPort"), result);
    }

    [Fact]
    public void Runtime_data_directory_preserves_application_directory_default_off_windows()
    {
        var result = RuntimeDataDirectory.Resolve(null, "/home/tester/.local/share", isWindows: false, "/opt/chordport");

        Assert.Equal("/opt/chordport", result);
    }

    [Fact]
    public void Windows_launcher_is_local_only_and_waits_for_health_before_opening_a_browser()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd"));

        Assert.Contains("%~dp0", launcher);
        Assert.Contains("cd /d", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1:5098", launcher);
        Assert.Contains("CHORDPORT_DATA_DIR", launcher);
        Assert.Contains("/api/health", launcher);
        Assert.Contains("start \"\" \"%URL%\"", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pause", launcher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_launcher_routes_nonzero_port_preflight_results_to_refusal_or_failure_before_starting()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd"));
        var preflight = GetBlockBeforeFirstLabel(launcher);
        var portInUse = GetLabelBlock(launcher, "port_in_use");
        var portPreflightFailed = GetLabelBlock(launcher, "port_preflight_failed");
        var portAvailable = GetLabelBlock(launcher, "port_available");

        Assert.Contains("System.Net.Sockets.TcpClient", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect('127.0.0.1', 5098)", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connect('0.0.0.0'", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"$client = $null; try { $client = [System.Net.Sockets.TcpClient]::new(); $client.Connect('127.0.0.1', 5098); exit 0 } catch [System.Net.Sockets.SocketException] { if ($_.Exception.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionRefused) { exit 1 }; exit 2 } catch { exit 2 } finally { if ($null -ne $client) { $client.Dispose() } } }\"\nif errorlevel 2 goto :port_preflight_failed\nif errorlevel 1 goto :port_in_use\ngoto :port_available",
            NormalizeLineEndings(preflight),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A local service is already listening at %URL%", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close it, then retry", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :startup_failed", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Could not verify whether %URL% is available", portPreflightFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :startup_failed", portPreflightFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if not exist \"%CHORDPORT_DATA_DIR%\" mkdir", portAvailable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start \"ChordPort\" /b", portAvailable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start \"\" \"%URL%\"", portAvailable, StringComparison.OrdinalIgnoreCase);

        foreach (var nonzeroExitCode in new[] { 1, 2 })
        {
            Assert.False(PreflightCanReach(launcher, nonzeroExitCode, "start \"ChordPort\" /b"),
                $"Preflight exit {nonzeroExitCode} must not start the app.");
            Assert.False(PreflightCanReach(launcher, nonzeroExitCode, "start \"\" \"%URL%\""),
                $"Preflight exit {nonzeroExitCode} must not open a browser.");
        }
    }

    [Fact]
    public void Package_verification_documents_that_WSL_and_Linux_cannot_validate_the_Windows_binary()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));

        Assert.Contains("cannot be executed or smoke-tested from WSL or Linux", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Native Windows validation is still required", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_packaging_script_builds_a_self_contained_versioned_zip_with_the_launcher()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "package-windows.ps1"));

        Assert.Contains("-r win-x64", script);
        Assert.Contains("--self-contained true", script);
        Assert.Contains("Start-ChordPort.cmd", script);
        Assert.Contains("Compress-Archive", script);
        Assert.Contains("artifacts", script);
    }

    private static string GetBlockBeforeFirstLabel(string launcher)
    {
        var normalized = NormalizeLineEndings(launcher);
        var firstLabel = System.Text.RegularExpressions.Regex.Match(normalized, @"(?m)^:[A-Za-z0-9_]+\s*$");
        if (!firstLabel.Success) throw new InvalidDataException("The launcher has no batch labels.");

        return normalized[..firstLabel.Index];
    }

    private static string GetLabelBlock(string launcher, string label)
    {
        var normalized = NormalizeLineEndings(launcher);
        var labelMatch = System.Text.RegularExpressions.Regex.Match(
            normalized,
            $@"(?im)^:{System.Text.RegularExpressions.Regex.Escape(label)}\s*$");
        if (!labelMatch.Success) throw new InvalidDataException($"The launcher is missing :{label}.");

        var bodyStart = labelMatch.Index + labelMatch.Length;
        if (bodyStart < normalized.Length && normalized[bodyStart] == '\n') bodyStart++;
        var nextLabel = new System.Text.RegularExpressions.Regex(@"(?m)^:[A-Za-z0-9_]+\s*$").Match(normalized, bodyStart);
        return normalized[bodyStart..(nextLabel.Success ? nextLabel.Index : normalized.Length)];
    }

    private static bool PreflightCanReach(string launcher, int exitCode, string command)
    {
        var normalized = NormalizeLineEndings(launcher);
        var probe = normalized.IndexOf("System.Net.Sockets.TcpClient", StringComparison.OrdinalIgnoreCase);
        if (probe < 0) throw new InvalidDataException("The launcher is missing its TCP preflight probe.");

        var position = normalized.IndexOf('\n', probe);
        if (position < 0) throw new InvalidDataException("The TCP preflight probe is not followed by routing.");
        position++;

        for (var steps = 0; steps < 100 && position < normalized.Length; steps++)
        {
            var lineEnd = normalized.IndexOf('\n', position);
            if (lineEnd < 0) lineEnd = normalized.Length;
            var line = normalized[position..lineEnd].Trim();
            position = lineEnd == normalized.Length ? normalized.Length : lineEnd + 1;

            if (line.Contains(command, StringComparison.OrdinalIgnoreCase)) return true;

            var errorLevelJump = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^if\s+errorlevel\s+(?<threshold>\d+)\s+goto\s+:(?<label>[A-Za-z0-9_]+)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (errorLevelJump.Success)
            {
                if (exitCode >= int.Parse(errorLevelJump.Groups["threshold"].Value))
                {
                    position = GetLabelBodyStart(normalized, errorLevelJump.Groups["label"].Value);
                }

                continue;
            }

            var jump = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^goto\s+:(?<label>[A-Za-z0-9_]+)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (jump.Success)
            {
                position = GetLabelBodyStart(normalized, jump.Groups["label"].Value);
                continue;
            }

            if (line.StartsWith("exit /b", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return false;
    }

    private static int GetLabelBodyStart(string launcher, string label)
    {
        var labelMatch = System.Text.RegularExpressions.Regex.Match(
            launcher,
            $@"(?im)^:{System.Text.RegularExpressions.Regex.Escape(label)}\s*$");
        if (!labelMatch.Success) throw new InvalidDataException($"The launcher is missing :{label}.");

        var bodyStart = labelMatch.Index + labelMatch.Length;
        return bodyStart < launcher.Length && launcher[bodyStart] == '\n' ? bodyStart + 1 : bodyStart;
    }

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChordPort.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the ChordPort repository root.");
    }
}
