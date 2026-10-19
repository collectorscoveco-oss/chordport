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
    public void Windows_launcher_refuses_any_existing_loopback_listener_before_starting_the_app_or_opening_a_browser()
    {
        var launcher = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-ChordPort.cmd"));
        var portAvailable = launcher.IndexOf(":port_available", StringComparison.OrdinalIgnoreCase);
        var portInUse = launcher.IndexOf(":port_in_use", StringComparison.OrdinalIgnoreCase);
        var appStart = launcher.IndexOf("start \"ChordPort\" /b", StringComparison.OrdinalIgnoreCase);
        var browserStart = launcher.IndexOf("start \"\" \"%URL%\"", StringComparison.OrdinalIgnoreCase);

        Assert.Contains("System.Net.Sockets.TcpClient", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect('127.0.0.1', 5098)", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connect('0.0.0.0'", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if errorlevel 1 goto :port_available", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goto :port_in_use", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A local service is already listening at %URL%", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close it, then retry", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.True(portAvailable >= 0, "The launcher must have a port-available path.");
        Assert.True(portInUse >= 0, "The launcher must have a port-in-use refusal path.");
        Assert.True(portAvailable < appStart, "The launcher must probe the port before starting the app.");
        Assert.True(portInUse < appStart, "A port conflict must branch before the app can start.");
        Assert.True(portInUse < browserStart, "A port conflict must branch before the browser can open.");
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChordPort.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the ChordPort repository root.");
    }
}
