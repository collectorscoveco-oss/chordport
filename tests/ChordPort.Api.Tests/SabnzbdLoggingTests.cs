using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ChordPort.Api.Tests;

public sealed class SabnzbdLoggingTests
{
    [Fact]
    public async Task Saved_sabnzbd_key_is_not_written_to_runtime_logs_when_connection_is_tested()
    {
        const string sentinel = "sab-log-leak-sentinel-4f2a9c";
        await using var server = await LocalSabnzbdServer.StartAsync();
        var logs = new CapturingLoggerProvider();
        var databasePath = Path.Combine(Path.GetTempPath(), $"chordport-{Guid.NewGuid():N}.db");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChordPort", $"Data Source={databasePath}");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            });
        });
        using var client = factory.CreateClient();

        var saved = await client.PutAsJsonAsync("/api/settings", new
        {
            libraryRoot = "Music",
            importMode = "Manual",
            sabnzbd = new { enabled = true, serverUrl = server.BaseAddress, category = "music", apiKey = sentinel, clearApiKey = false }
        });
        saved.EnsureSuccessStatusCode();

        var tested = await client.PostAsync("/api/settings/sabnzbd/test", null);
        var body = await tested.Content.ReadAsStringAsync();

        tested.EnsureSuccessStatusCode();
        Assert.Contains("Connected to SABnzbd 4.3.0.", body);
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(sentinel, entry));
        Assert.All(logs.Scopes, scope => Assert.DoesNotContain(sentinel, scope));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
        public ConcurrentQueue<string> Entries { get; } = [];
        public ConcurrentQueue<string> Scopes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
        public void Dispose() { }
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopeProvider.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<string>();
                provider._scopeProvider.ForEachScope((scope, values) => values.Add(scope?.ToString() ?? string.Empty), scopes);
                foreach (var scope in scopes) provider.Scopes.Enqueue(scope);
                provider.Entries.Enqueue($"{categoryName}|{eventId}|{formatter(state, exception)}|{exception}");
            }
        }
    }

    private sealed class LocalSabnzbdServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serveTask;

        private LocalSabnzbdServer(TcpListener listener)
        {
            _listener = listener;
            _serveTask = ServeOnceAsync();
        }

        public string BaseAddress => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public static Task<LocalSabnzbdServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LocalSabnzbdServer(listener));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serveTask;
        }

        private async Task ServeOnceAsync()
        {
            try
            {
                using var connection = await _listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 19\r\nConnection: close\r\n\r\n{\"version\":\"4.3.0\"}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch (SocketException) when (!_listener.Server.IsBound) { }
        }
    }
}
