using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Ro3Emu.Play;

/// <summary>
/// The frameless launcher window: the page (ui/, embedded) in WebView2 talks to <see cref="PlayController"/> through web
/// messages. Closing while the server runs stops it first (the characters are saved), then the window closes.
/// </summary>
public partial class MainWindow : Window
{
    private const string Origin = "https://play.ro3emu/";
    private readonly PlayController _controller;
    private readonly string[] _args;
    // Development: RO3EMU_PLAY_UI=<folder> serves the page from disk and turns on the dev tools.
    private readonly string? _uiOverride = Environment.GetEnvironmentVariable("RO3EMU_PLAY_UI");
    private bool _closing;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _args = args;
        _controller = new PlayController(args, Post, () => Dispatcher.Invoke(Close), () => Dispatcher.Invoke(Restart));
        // A release's launcher embeds no art (pack.ps1): the player's own art\ next to it, when there is one.
        if (Art("ro3.ico") is { } icon) Icon = BitmapFrame.Create(new Uri(icon));
        SourceInitialized += (_, _) => NativeMethods.RoundCorners(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await InitializeWebAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || !_controller.Running) { _controller.Dispose(); return; }
        // The server is up: stop it cleanly first; the controller closes the window once the flow has ended.
        e.Cancel = true;
        _closing = true;
        _controller.Stop(closeAfter: true);
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RO3Emu", "PlayWebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show("This window needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.\n\n"
                + "Open the download page? (RO3Emu.exe still starts the server in a console meanwhile.)",
                "RO3Emu", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            Application.Current.Shutdown();
            return;
        }
        var core = Web.CoreWebView2;
        bool dev = _uiOverride is not null;
        core.Settings.AreDevToolsEnabled = dev;
        core.Settings.AreDefaultContextMenusEnabled = dev;
        core.Settings.AreBrowserAcceleratorKeysEnabled = dev;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.Navigate(Origin + "index.html");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
        if (path.Length == 0) path = "index.html";
        var stream = Open(path);
        var env = Web.CoreWebView2.Environment;
        e.Response = stream is null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentType(path)}\r\nCache-Control: no-store");
    }

    /// <summary>A page file: the embedded ui/ (or the development folder), or "game/cursor.cur", the game's own pointer.</summary>
    private Stream? Open(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal)) return null;
        if (path == "game/cursor.cur")
            return _controller.ClientDir is { } client && Path.Combine(client, "ro3_Data", "StreamingAssets", "CursorHandler", "mouse_normal.cur") is var cur
                && File.Exists(cur) ? new MemoryStream(File.ReadAllBytes(cur)) : null;
        if (_uiOverride is not null && !path.StartsWith("art/", StringComparison.Ordinal))
        {
            string file = Path.Combine(_uiOverride, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? new MemoryStream(File.ReadAllBytes(file)) : null;
        }
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + path)
            ?? (path.StartsWith("art/", StringComparison.Ordinal) && Art(path["art/".Length..]) is { } art ? new MemoryStream(File.ReadAllBytes(art)) : null);
    }

    /// <summary>A file of the player's own art\ folder next to the launcher (tools/release/launcher-art.ps1), or null.</summary>
    private static string? Art(string name) =>
        !name.Contains('/') && Path.Combine(Ro3Emu.Setup.SetupHome.AppDir, "art", name) is var file && File.Exists(file) ? file : null;

    /// <summary>After an update: the new launcher, with this one's arguments, in place of this one (it takes over the
    /// one-window lock).</summary>
    private void Restart()
    {
        ((App)Application.Current).ReleaseSingleInstance();
        var start = new ProcessStartInfo(Path.Combine(Ro3Emu.Setup.SetupHome.AppDir, "RO3EmuPlay.exe")) { UseShellExecute = false };
        foreach (string arg in _args) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
        Application.Current.Shutdown();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".cur" => "image/x-icon",
        _ => "application/octet-stream",
    };

    private void OnMessage(string json)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }
        string? cmd = message.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        switch (cmd)
        {
            case "minimize": WindowState = WindowState.Minimized; break;
            case "close": Close(); break;
            case "drag": NativeMethods.BeginDrag(new WindowInteropHelper(this).Handle); break;
            case "open": OpenPath(message.GetProperty("what").GetString()); break;
            case not null: _controller.Handle(cmd, message); break;
        }
    }

    private void OpenPath(string? what)
    {
        string? path = what switch
        {
            "logs" => Ro3Emu.Setup.SetupHome.LogDir,
            "folder" => Ro3Emu.Setup.SetupHome.AppDir,
            "game" => _controller.ClientDir,
            _ => null,
        };
        if (path != null && Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
    }

    private void Post(object message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Post(message));
            return;
        }
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, PlayController.Json));
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
