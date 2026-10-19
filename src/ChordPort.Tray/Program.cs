using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace ChordPort.Tray;

internal static class Program
{
    private const string Url = "http://127.0.0.1:5098";
    private static readonly string UserSuffix = Environment.UserName.Replace('\\', '_');

    [STAThread]
    private static void Main()
    {
        using var openSignal = TrayInstanceCoordinator.CreateOpenSignal(UserSuffix);
        using var mutex = new Mutex(true, $"Local\\ChordPort.Tray.{UserSuffix}", out var ownsMutex);
        if (!ownsMutex)
        {
            openSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var controller = new TrayController(Url, openSignal);
        Application.Run();
    }
}

internal sealed class TrayController : IDisposable
{
    private readonly HttpClient _client;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private readonly EventWaitHandle _openSignal;
    private readonly RegisteredWaitHandle _openRegistration;
    private readonly ConsecutiveHealthFailureTracker _healthFailures = new(threshold: 3);
    private bool _disposed;

    public TrayController(string url, EventWaitHandle openSignal)
    {
        _client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(2) };
        _icon = new NotifyIcon
        {
            Icon = CreateChordPortIcon(),
            Text = "ChordPort — running locally",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };
        _icon.MouseClick += (_, eventArgs) => { if (eventArgs.Button == MouseButtons.Left) OpenChordPort(); };
        _healthTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _healthTimer.Tick += async (_, _) => await RemoveIfApiStoppedAsync();
        _healthTimer.Start();
        _openSignal = openSignal;
        _openRegistration = ThreadPool.RegisterWaitForSingleObject(_openSignal, (_, _) => OpenChordPort(), null, -1, false);
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ChordPort", null, (_, _) => OpenChordPort());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit ChordPort", null, async (_, _) => await ExitChordPortAsync());
        return menu;
    }

    private static Icon CreateChordPortIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(25, 42, 54));
            using var brush = new SolidBrush(Color.FromArgb(155, 225, 93));
            graphics.FillEllipse(brush, 3, 3, 26, 26);
            using var font = new Font(FontFamily.GenericSansSerif, 17, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("C", font, Brushes.Black, new PointF(8, 6));
        }
        var handle = bitmap.GetHicon();
        return IconHandleLifetime.CloneAndDestroy(handle,
            original => (Icon)Icon.FromHandle(original).Clone(),
            original => NativeMethods.DestroyIcon(original));
    }

    private static void OpenChordPort()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:5098") { UseShellExecute = true }); }
        catch { }
    }

    private async Task ExitChordPortAsync()
    {
        try
        {
            if (await IsChordPortHealthyAsync())
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/shutdown");
                await _client.SendAsync(request);
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        finally { Application.Exit(); }
    }

    private async Task RemoveIfApiStoppedAsync()
    {
        if (_healthFailures.ShouldExitAfterHealthCheck(await IsChordPortHealthyAsync())) Application.Exit();
    }

    private async Task<bool> IsChordPortHealthyAsync()
    {
        try
        {
            using var response = await _client.GetAsync("/api/health");
            if (!response.IsSuccessStatusCode) return false;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.TryGetProperty("service", out var service)
                && string.Equals(service.GetString(), "ChordPort", StringComparison.Ordinal);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (JsonException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Stop();
        _healthTimer.Dispose();
        _openRegistration.Unregister(null);
        _icon.Visible = false;
        _icon.Dispose();
        _client.Dispose();
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
}
