using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
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
    public void Windows_launcher_uses_unambiguous_port_preflight_codes_and_fails_closed_before_starting()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd"));
        var preflight = GetBlockBeforeFirstLabel(launcher);
        var portInUse = GetLabelBlock(launcher, "port_in_use");
        var portPreflightFailed = GetLabelBlock(launcher, "port_preflight_failed");
        var portAvailable = GetLabelBlock(launcher, "port_available");

        Assert.Contains("System.Net.Sockets.TcpClient", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect('127.0.0.1', 5098)", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connect('0.0.0.0'", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit 10", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionRefused) { exit 0 }", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catch { exit 20 }", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finally { if ($null -ne $client) { $client.Dispose() } }\"", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if errorlevel 20 goto :port_preflight_failed", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if errorlevel 10 goto :port_in_use", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if errorlevel 1 goto :port_preflight_failed", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :port_available", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A local service is already listening at %URL%", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close it, then retry", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :startup_failed", portInUse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Could not verify whether %URL% is available", portPreflightFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :startup_failed", portPreflightFailed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if not exist \"%CHORDPORT_DATA_DIR%\" mkdir", portAvailable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start \"ChordPort\" /b", portAvailable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start \"\" \"%URL%\"", portAvailable, StringComparison.OrdinalIgnoreCase);

        foreach (var blockedExitCode in new[] { 1, 10, 20 })
        {
            Assert.False(PreflightCanReach(launcher, blockedExitCode, "start \"ChordPort\" /b"),
                $"Preflight exit {blockedExitCode} must not start the app.");
            Assert.False(PreflightCanReach(launcher, blockedExitCode, "start \"\" \"%URL%\""),
                $"Preflight exit {blockedExitCode} must not open a browser.");
        }

        Assert.True(PreflightCanReach(launcher, 0, "start \"ChordPort\" /b"),
            "Only a connection-refused (available-port) result may start the app.");
    }

    [Fact]
    public void Windows_launcher_starts_the_singleton_tray_only_after_a_healthy_api()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd"));
        var ready = GetLabelBlock(launcher, "ready");
        var failed = GetLabelBlock(launcher, "startup_failed");

        Assert.Contains("%TRAY_DIR%ChordPort.Tray.exe", ready, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start \"ChordPort Tray\" /b", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start \"ChordPort Tray\" /b", GetBlockBeforeFirstLabel(launcher), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChordPort.Tray.exe", failed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_tray_contract_uses_local_identity_checked_shutdown_and_disposes_when_the_api_stops()
    {
        var tray = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ChordPort.Tray", "Program.cs"));

        Assert.Contains("Local\\\\ChordPort.Tray", tray, StringComparison.Ordinal);
        Assert.Contains("Mutex", tray, StringComparison.Ordinal);
        Assert.Contains("Open ChordPort", tray, StringComparison.Ordinal);
        Assert.Contains("Exit ChordPort", tray, StringComparison.Ordinal);
        Assert.Contains("/api/system/shutdown", tray, StringComparison.Ordinal);
        Assert.Contains("/api/health", tray, StringComparison.Ordinal);
        Assert.Contains("\"ChordPort\"", tray, StringComparison.Ordinal);
        Assert.Contains("Application.Exit", tray, StringComparison.Ordinal);
        Assert.Contains("_icon.Dispose()", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_packaging_script_includes_the_self_contained_tray_helper()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "package-windows.ps1"));

        Assert.Contains("ChordPort.Tray", script, StringComparison.Ordinal);
        Assert.Contains("ChordPort.Tray.exe", script, StringComparison.Ordinal);
        Assert.Contains("$trayDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $publishDirectory \"tray\"", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_launcher_probe_runs_in_native_Windows_PowerShell_when_available()
    {
        if (!OperatingSystem.IsWindows() || !CanRunWindowsPowerShell()) return;

        var probe = ExtractPowerShellProbe(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd")));
        using var listener = new TcpListener(IPAddress.Loopback, 5098);
        listener.Start();
        Assert.Equal(10, RunWindowsPowerShell(probe));

        listener.Stop();
        Assert.Equal(0, WaitForWindowsPowerShellExitCode(probe, expectedExitCode: 0));
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

    private static bool CanRunWindowsPowerShell()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "exit 0" }
            });
            process?.WaitForExit(5000);
            return process is { ExitCode: 0 };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string ExtractPowerShellProbe(string launcher)
    {
        var match = Regex.Match(
            launcher,
            "powershell\\.exe -NoLogo -NoProfile -NonInteractive -Command \\\"(?<probe>.*?)\\\"",
            RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("The launcher is missing its PowerShell TCP preflight probe.");

        return match.Groups["probe"].Value;
    }

    private static int WaitForWindowsPowerShellExitCode(string script, int expectedExitCode)
    {
        var exitCode = RunWindowsPowerShell(script);
        for (var attempt = 0; attempt < 10 && exitCode != expectedExitCode; attempt++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            exitCode = RunWindowsPowerShell(script);
        }

        return exitCode;
    }

    private static int RunWindowsPowerShell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }
        }) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var standardError = process.StandardError.ReadToEnd();
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        if (!process.HasExited) throw new TimeoutException("Windows PowerShell did not finish the TCP preflight probe.");
        Assert.True(string.IsNullOrWhiteSpace(standardError), $"Windows PowerShell probe wrote stderr: {standardError}");
        Assert.True(string.IsNullOrWhiteSpace(standardOutput), $"Windows PowerShell probe wrote stdout: {standardOutput}");
        return process.ExitCode;
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
