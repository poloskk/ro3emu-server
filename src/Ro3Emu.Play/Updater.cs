using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ro3Emu.Play;

/// <summary>
/// The launcher's own update, from the newest GitHub release of poloskk/ro3emu-server: its Windows archive (the
/// player's RO3Emu-Offline.zip, or tools/release/pack.ps1's) replaces the program files in the launcher's folder. The
/// data and logs are not in the archive, and the player's own settings, README and server development settings are
/// kept. A file in use (this launcher) is moved aside to *.old, which Windows allows for a running exe, and deleted on
/// the next start. A launcher feature, not the original game's.
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string DefaultRepository = "poloskk/ro3emu-server";
    /// <summary>The Windows archive: the player's own package, or pack.ps1's.</summary>
    private static readonly string[] Assets = ["RO3Emu-Offline.zip", "RO3Emu-win-x64.zip"];
    private const string Launcher = "RO3EmuPlay.exe";
    private const string Aside = ".old";
    /// <summary>Kept when the folder has them: the player's own copies.</summary>
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        "RO3Emu.settings.json", "README.txt", "server/appsettings.Development.json",
    };
    /// <summary>The folders the archive writes to, where the files moved aside are looked for.</summary>
    private static readonly string[] ProgramDirs = ["", "server", "population", "client"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    // Development: RO3EMU_UPDATE_REPO=<owner/name> checks another repository's releases.
    private readonly string _repository = Environment.GetEnvironmentVariable("RO3EMU_UPDATE_REPO") is { Length: > 0 } repo ? repo : DefaultRepository;

    public Updater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"RO3EmuPlay/{Current.ToString(3)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>This launcher's version (pack.ps1 -Version).</summary>
    public static Version Current => Normal(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    public sealed record Release(Version Version, Uri Download, long Size, string? Sha256);

    /// <summary>The newest release when it is newer than this launcher and carries the Windows archive, else null.</summary>
    public async Task<Release?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        string json = await _http.GetStringAsync($"https://api.github.com/repos/{_repository}/releases/latest", timeout.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normal(version) <= Current) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!Assets.Contains(asset.GetProperty("name").GetString())
                || !Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com") continue;
            // GitHub gives each asset's SHA-256 ("sha256:<hex>"); older releases may not have it.
            string? digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } s
                && s.StartsWith("sha256:", StringComparison.Ordinal) ? s["sha256:".Length..] : null;
            return new Release(Normal(version), url, asset.GetProperty("size").GetInt64(), digest);
        }
        return null;
    }

    /// <summary>The release's archive in the temp folder, checked against its size and checksum.</summary>
    public async Task<string> DownloadAsync(Release release, Action<double> progress, CancellationToken ct)
    {
        string file = Path.Combine(Path.GetTempPath(), $"RO3Emu-{release.Version.ToString(3)}.zip");
        using (var response = await _http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using var to = File.Create(file);
            var buffer = new byte[1 << 16];
            long done = 0;
            for (int n; (n = await from.ReadAsync(buffer, ct)) > 0;)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (release.Size > 0) progress(Math.Min(1, (double)done / release.Size));
            }
        }
        if (new FileInfo(file).Length != release.Size) throw new InvalidDataException("The download is incomplete. Try again.");
        if (release.Sha256 is { } expected)
        {
            await using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download is damaged (its checksum differs). Try again.");
        }
        return file;
    }

    /// <summary>Puts the archive's program files into <paramref name="dir"/>; on any failure every file is put back.</summary>
    public static void Apply(string archive, string dir)
    {
        using var zip = ZipFile.OpenRead(archive);
        var files = Files(zip, dir);
        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var (entry, target) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    Retry(() => File.Move(target, target + Aside, overwrite: true));
                    replaced.Add(target);
                }
                else added.Add(target);
                entry.ExtractToFile(target);
            }
        }
        catch
        {
            foreach (var target in added)
                if (File.Exists(target)) File.Delete(target);
            foreach (var target in replaced) File.Move(target + Aside, target, overwrite: true);
            throw;
        }
    }

    /// <summary>
    /// The archive's files and where each goes: the folder that holds its launcher ("RO3Emu/", or the player's own
    /// "RO3Emu-Offline/RO3Emu/") is the launcher's folder. Refused without a launcher in it.
    /// </summary>
    private static List<(ZipArchiveEntry Entry, string Target)> Files(ZipArchive zip, string dir)
    {
        var names = zip.Entries.Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/'))).ToList();
        string? launcher = names.Select(n => n.Name).Where(n => n == Launcher || n.EndsWith("/" + Launcher, StringComparison.Ordinal))
            .OrderBy(n => n.Length).FirstOrDefault();
        if (launcher == null) throw new InvalidDataException("The update has no launcher in it.");
        string prefix = launcher[..^Launcher.Length];
        string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var files = new List<(ZipArchiveEntry, string)>();
        foreach (var (entry, name) in names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            string rel = name[prefix.Length..];
            string target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The update has a file outside its folder: {name}");
            if (Kept.Contains(rel) && File.Exists(target)) continue;
            files.Add((entry, target));
        }
        return files;
    }

    /// <summary>The files a previous update moved aside, deleted (best effort: one still in use waits for the next start).</summary>
    public static void CleanUp(string dir)
    {
        foreach (string sub in ProgramDirs)
        {
            string folder = Path.Combine(dir, sub);
            if (!Directory.Exists(folder)) continue;
            foreach (string old in Directory.EnumerateFiles(folder, "*" + Aside))
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A server or bots process that has just been stopped can hold its exe for a moment.</summary>
    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 30) { Thread.Sleep(500); }
        }
    }

    private static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public void Dispose() => _http.Dispose();
}
