using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ChordPort.Api.Tests;

public sealed class PublishedStaticFilesTests
{
    [Fact]
    public async Task Published_app_serves_the_react_root_when_started_from_a_different_working_directory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ChordPort.Tests", Guid.NewGuid().ToString("N"));
        var publishDirectory = Path.Combine(temporaryRoot, "publish");
        var callerDirectory = Path.Combine(temporaryRoot, "caller");
        Directory.CreateDirectory(callerDirectory);
        Process? server = null;

        try
        {
            await RunProcess(repositoryRoot, "dotnet", "publish", "src/ChordPort.Api/ChordPort.Api.csproj", "-c", "Release", "--no-restore", "-o", publishDirectory);

            var port = GetFreePort();
            server = StartPublishedApp(publishDirectory, callerDirectory, port);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            await WaitForHealthyServer(client, server);
            var response = await client.GetAsync("/");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<div id=\"root\">", body);
        }
        finally
        {
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            server?.Dispose();
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChordPort.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the ChordPort repository root.");
    }

    private static Process StartPublishedApp(string publishDirectory, string callerDirectory, int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = callerDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(publishDirectory, "ChordPort.Api.dll"));
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the published ChordPort application.");
    }

    private static async Task WaitForHealthyServer(HttpClient client, Process server)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (server.HasExited)
            {
                var error = await server.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Published application exited early: {error}");
            }

            try
            {
                if ((await client.GetAsync("/api/health")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException)
            {
                // Kestrel has not finished starting yet.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Published application did not become healthy within five seconds.");
    }

    private static async Task RunProcess(string workingDirectory, string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed: {output}{Environment.NewLine}{error}");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
