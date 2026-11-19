using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.DLSS;

/// <summary>
/// One DLSS runtime sitting in the local cache. <see cref="Version"/> is whatever the file
/// itself reports, verbatim - some builds report it comma-separated ("3,7,0,0"), which is
/// why <see cref="DisplayVersion"/> exists and why nothing compares raw version strings
/// without going through <see cref="DLSSSwapper.IsSupportedVersion"/> first.
/// </summary>
internal sealed class DllData
{
    public string Version { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    public string DisplayVersion => Version.Replace(",", ".");
}

/// <summary>
/// Everything the DLSS swapper does to files: where the cache lives, what's in it, how a
/// .dll or .zip gets imported into it, and how one of them ends up in the game folder.
///
/// <para><b>Why it's separate from the window.</b> None of this needs a UI - it is
/// FileVersionInfo, File.Copy and one elevated replace. The window's job is to render the
/// list this produces and to decide *when* these run; this class is the only thing that
/// knows the cache layout. Split out so that reading either half doesn't mean reading
/// both, exactly as Alchitex separates its pipeline from AlchitexOverlay.</para>
///
/// <para>The instance is bound to one Minecraft install by <see cref="TryAttach"/>; every
/// method below is inert until that has succeeded.</para>
/// </summary>
internal sealed class DLSSSwapper
{
    /// <summary>The game's own DLSS runtime - the file every swap overwrites.</summary>
    public const string GameDllFileName = "nvngx_dlss.dll";

    private const string CacheFolderName = "DLSS_Cache";

    /// <summary>
    /// Anything below this is a DLSS 1.x runtime, which Minecraft's renderer can't use.
    /// Cached copies that fail this are deleted on load rather than offered and refused.
    /// </summary>
    private static readonly Version MinimumSupportedVersion = new(2, 0, 0, 0);

    public string GameDllPath { get; private set; } = string.Empty;
    public string CacheFolder { get; private set; } = string.Empty;

    /// <summary>
    /// Version string of the DLL currently sitting in the game folder, as last read by
    /// <see cref="CacheInstalledDllAsync"/>. Null until that has run at least once, and
    /// "Unknown" if the file was there but unreadable.
    /// </summary>
    public string? InstalledVersion { get; private set; }

    public bool GameDllExists => !string.IsNullOrEmpty(GameDllPath) && File.Exists(GameDllPath);

    /// <summary>
    /// Points this instance at a Minecraft install and makes sure the cache folder exists.
    /// False means the cache couldn't be created, which is fatal for the whole feature -
    /// there is nowhere to put anything.
    /// </summary>
    public bool TryAttach(string minecraftPath)
    {
        GameDllPath = Path.Combine(minecraftPath, GameDllFileName);

        var cacheFolder = EstablishCacheFolder();
        if (cacheFolder == null)
            return false;

        CacheFolder = cacheFolder;
        return true;
    }

    private static string? EstablishCacheFolder()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            var cacheLocation = Path.Combine(localFolder, CacheFolderName);

            Trace.WriteLine($"[DLSS] Creating DLSS cache at: {cacheLocation}");
            Directory.CreateDirectory(cacheLocation);
            Trace.WriteLine($"[DLSS] ✓ DLSS cache established at: {cacheLocation}");

            return cacheLocation;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] ✗ Failed to create DLSS cache: {ex.Message}");
            return null;
        }
    }

    // ======================= Cache contents =======================

    /// <summary>
    /// Every cached .dll, newest file first. Used for the repair path, which just takes
    /// the most recent one it can find.
    /// </summary>
    public List<string> CachedDllPathsByNewest() =>
        Directory.GetFiles(CacheFolder, "*.dll")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();

    /// <summary>
    /// The cache as the window lists it: newest file first, one entry per distinct version
    /// string. Files that can't be read at all are dropped.
    /// </summary>
    public List<DllData> ListCachedVersions()
    {
        var results = new List<DllData>();
        var seenVersions = new HashSet<string>();

        foreach (var dllPath in CachedDllPathsByNewest())
        {
            var dllData = ParseDll(dllPath);
            if (dllData != null && seenVersions.Add(dllData.Version))
                results.Add(dllData);
        }

        return results;
    }

    private static DllData? ParseDll(string dllPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            return new DllData { Version = version, FilePath = dllPath };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error parsing DLL {dllPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The single answer to "can Minecraft actually use this one?", shared by the cleanup
    /// pass and by the list (which greys out anything this rejects). Covers unreadable,
    /// unparseable and DLSS 1.x alike - a version string that isn't a real version is no
    /// more usable than one that's simply too old.
    /// </summary>
    public static bool IsSupportedVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return false;

        return Version.TryParse(rawVersion.Replace(",", "."), out var parsed)
            && parsed >= MinimumSupportedVersion;
    }

    /// <summary>
    /// Drops every cached runtime the game couldn't use anyway. The currently installed
    /// version is spared even when it fails the check - it's already in the game folder,
    /// so deleting our copy of it would only remove the way back.
    /// </summary>
    public async Task RemoveUnsupportedFromCacheAsync()
    {
        await Task.Run(() =>
        {
            foreach (var dllPath in Directory.GetFiles(CacheFolder, "*.dll"))
            {
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                    var raw = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "";

                    if (IsSupportedVersion(raw))
                        continue;

                    if (!string.IsNullOrEmpty(InstalledVersion) &&
                        Path.GetFileNameWithoutExtension(dllPath) == InstalledVersion)
                    {
                        Trace.WriteLine($"[DLSS] Skipping cleanup of current installed version: {dllPath}");
                        continue;
                    }

                    File.Delete(dllPath);
                    Trace.WriteLine($"[DLSS] Cleaned up incompatible/unversioned DLSS from cache: {dllPath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[DLSS] Error during cleanup of {dllPath}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Removes one cached runtime. Refuses the installed one for the same reason the
    /// cleanup pass spares it.
    /// </summary>
    public bool DeleteCachedVersion(DllData dll)
    {
        try
        {
            if (dll.Version == InstalledVersion)
            {
                Trace.WriteLine("[DLSS] Cannot delete currently installed DLSS version");
                return false;
            }

            if (!File.Exists(dll.FilePath))
                return false;

            File.Delete(dll.FilePath);
            Trace.WriteLine($"[DLSS] Deleted DLSS version {dll.Version} from cache");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error deleting DLL: {ex.Message}");
            return false;
        }
    }

    // ======================= Import =======================

    /// <summary>
    /// Copies the game's current runtime into the cache under its own version number, and
    /// records that version as installed. Runs on every open so the user always has a way
    /// back to whatever they started with.
    /// </summary>
    public async Task CacheInstalledDllAsync()
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(GameDllPath);

            InstalledVersion = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";

            var cachePath = Path.Combine(CacheFolder, $"{InstalledVersion}.dll");

            await Task.Run(() => File.Copy(GameDllPath, cachePath, true));

            Trace.WriteLine($"[DLSS] Copied current DLSS {InstalledVersion} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error copying current DLL to cache: {ex.Message}");
            InstalledVersion = "Unknown";
        }
    }

    /// <summary>
    /// Cache name is the version, not the source file name, so importing the same runtime
    /// twice from two different downloads lands on one entry rather than two.
    /// </summary>
    public async Task ImportDllAsync(string dllPath)
    {
        try
        {
            if (!Path.GetFileName(dllPath).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[DLSS] Skipped non-DLL file: {dllPath}");
                return;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
            var cachePath = Path.Combine(CacheFolder, $"{version}.dll");

            await Task.Run(() => File.Copy(dllPath, cachePath, true));
            Trace.WriteLine($"[DLSS] Added DLSS {version} to cache");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing DLL {dllPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Most places hand out DLSS runtimes as a zip. Every .dll inside is taken on its own
    /// terms - a bad entry costs that entry, not the archive.
    /// </summary>
    public async Task ImportZipAsync(string zipPath)
    {
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.WriteLine($"[DLSS] Skipped non-DLL file from ZIP");
                        continue;
                    }

                    try
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), entry.Name);
                        entry.ExtractToFile(tempPath, true);

                        var versionInfo = FileVersionInfo.GetVersionInfo(tempPath);
                        var version = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "Unknown";
                        var cachePath = Path.Combine(CacheFolder, $"{version}.dll");

                        File.Copy(tempPath, cachePath, true);
                        File.Delete(tempPath);

                        Trace.WriteLine($"[DLSS] Extracted and added DLSS {version} from ZIP");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[DLSS] Error processing {entry.FullName} from ZIP: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing ZIP file: {ex.Message}");
        }
    }

    // ======================= Install =======================

    /// <summary>
    /// The swap itself. The game folder is under Program Files, so this is the one step
    /// that needs elevation - one UAC prompt, one file.
    /// </summary>
    public Task<bool> InstallAsync(string sourceDllPath) =>
        Helpers.ReplaceFilesWithElevation(
            new List<(string, string)> { (sourceDllPath, GameDllPath) },
            "[DLSS]",
            "dlss_dll");

    // ======================= NVIDIA's update server =======================
    //
    // The driver keeps DLSS current for games through NGX's over-the-air updates, served from a
    // public bucket. Each build there is the plain nvngx_dlss.dll, published by NVIDIA itself, so
    // offering it means the app never redistributes NVIDIA's binaries - the user downloads them
    // from NVIDIA. It is undocumented, which is why it is the secondary source and every failure
    // here leaves the rest of the manager working exactly as it does without it.
    //
    // Layout: <namespace>/org/nvidia/team/ngx/models/dlss/versions/<id>/files/160_E658700.bin,
    // with id = major << 16 | minor << 8 | patch, and a .sha256 beside it in newer namespaces.
    // E658700 is the generic application id, the build any game gets; the other ids are per-game
    // builds of 1.x and 2.x. Measured 2026-11: two namespaces, 310.2.1-310.6.0 in one and
    // 310.9.0/310.9.16 (with checksums) in the other.
    //
    // The bucket is behind a cache that ignores the query string, so it can't be paged or
    // filtered: the root listing is its first 1,000 keys, which today hold both namespaces' DLSS
    // folders. Each namespace's server config names the version the driver itself would hand
    // out, which catches the newest build even if the listing ever stops reaching it.

    private const string NgxOrigin = "https://ngx.download.nvidia.com/";
    private const string NgxDlssFile = "160_E658700.bin";
    private const string NgxApplication = "app_E658700";
    private static readonly string[] NgxNamespaces = ["257a0e72-47ec-4829-b73e-6edb796c1a71", "d6e9b45e-d4f6-4a84-a460-bf61decae3e8"];
    private static readonly TimeSpan NgxRequestDeadline = TimeSpan.FromSeconds(20);

    /// <summary>The listing changes when NVIDIA ships a DLSS release; an hour is plenty, and a session's reopens cost nothing.</summary>
    private static readonly TimeSpan NgxListingFreshFor = TimeSpan.FromHours(1);
    private static (DateTime At, IReadOnlyList<NvidiaDll> Dlls)? _ngxListing;

    private static string NgxKey(string ns, int id, string file) =>
        $"{ns}/org/nvidia/team/ngx/models/dlss/versions/{id}/files/{file}";

    private static int NgxId(Version v) => (v.Major << 16) | (v.Minor << 8) | Math.Max(0, v.Build);

    /// <summary>
    /// Every DLSS build NVIDIA's update server has, newest first, one per version. Null when the
    /// server couldn't be reached at all; an empty list is a real answer.
    /// </summary>
    public static async Task<IReadOnlyList<NvidiaDll>?> ListNvidiaVersionsAsync(CancellationToken cancellationToken)
    {
        if (_ngxListing is { } cached && DateTime.UtcNow - cached.At < NgxListingFreshFor)
            return cached.Dlls;

        var found = new Dictionary<Version, NvidiaDll>();
        var answered = false;

        try
        {
            var xml = await GetNgxTextAsync(NgxOrigin, cancellationToken);
            answered = true;

            XNamespace s3 = "http://s3.amazonaws.com/doc/2006-03-01/";
            var objects = XDocument.Parse(xml).Root?.Elements(s3 + "Contents")
                .Select(c => (Key: (string?)c.Element(s3 + "Key") ?? "", Size: (long?)c.Element(s3 + "Size") ?? 0))
                .ToList() ?? [];
            var keys = objects.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var (key, size) in objects)
            {
                var match = NgxDllKey.Match(key);
                if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var id)) continue;

                var version = new Version(id >> 16, (id >> 8) & 0xFF, id & 0xFF);
                var dll = new NvidiaDll(version, NgxOrigin + key, size, keys.Contains(key + ".sha256"));
                if (!found.TryGetValue(version, out var other) || (dll.HasChecksum && !other.HasChecksum))
                    found[version] = dll;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine($"[DLSS] [NVIDIA] Listing failed: {ex.Message}");
        }

        foreach (var ns in NgxNamespaces)
        {
            try
            {
                var config = await GetNgxTextAsync($"{NgxOrigin}{ns}/org/nvidia/team/ngx/models/config/versions/2/files/nvngx_server_config.txt", cancellationToken);
                answered = true;
                if (ReadPinnedVersion(config) is not { } pinned || found.ContainsKey(pinned)) continue;

                var url = NgxOrigin + NgxKey(ns, NgxId(pinned), NgxDlssFile);
                if (await HeadSizeAsync(url, cancellationToken) is not { } size) continue;
                found[pinned] = new NvidiaDll(pinned, url, size, await HeadSizeAsync(url + ".sha256", cancellationToken) is not null);
                Trace.WriteLine($"[DLSS] [NVIDIA] {pinned} found through {ns}'s server config, not the listing");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Trace.WriteLine($"[DLSS] [NVIDIA] Server config of {ns} failed: {ex.Message}");
            }
        }

        if (!answered) return null;

        var list = found.Values.Where(d => d.Version >= MinimumSupportedVersion).OrderByDescending(d => d.Version).ToList();
        _ngxListing = (DateTime.UtcNow, list);
        Trace.WriteLine($"[DLSS] [NVIDIA] {list.Count} build(s): {string.Join(", ", list.Select(d => d.DisplayVersion))}");
        return list;
    }

    private static readonly Regex NgxDllKey = new(
        @"^[0-9a-f-]+/org/nvidia/team/ngx/models/dlss/versions/(?<id>\d+)/files/" + Regex.Escape(NgxDlssFile) + "$",
        RegexOptions.CultureInvariant);

    /// <summary>The <c>[dlss]</c> section's version for the generic application, e.g. <c>app_E658700 = 310.9.16</c>.</summary>
    private static Version? ReadPinnedVersion(string config)
    {
        var inDlss = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inDlss = line.Equals("[dlss]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inDlss) continue;

            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Equals(NgxApplication, StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(parts[1], out var version))
                return version;
        }
        return null;
    }

    /// <summary>
    /// A small text file from the update server. Asks for gzip and inflates it itself - the shared
    /// client doesn't - because the root listing is 390 KB of XML and 34 KB compressed.
    /// </summary>
    private static async Task<string> GetNgxTextAsync(string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(NgxRequestDeadline);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var response = await Helpers.SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        Stream text = response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
            ? new GZipStream(body, CompressionMode.Decompress)
            : body;
        await using (text)
        {
            using var reader = new StreamReader(text, Encoding.UTF8);
            return await reader.ReadToEndAsync(deadline.Token);
        }
    }

    /// <summary>The size of a file on the update server, or null when it isn't there.</summary>
    private static async Task<long?> HeadSizeAsync(string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(NgxRequestDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await Helpers.SharedHttpClient.SendAsync(request, deadline.Token);
        return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : null;
    }

    /// <summary>
    /// The builds from <paramref name="available"/> the cache doesn't already hold, compared on
    /// the first three version parts - a cached runtime reports four ("310.9.16.0"), the server
    /// names three.
    /// </summary>
    public IReadOnlyList<NvidiaDll> NotYetCached(IReadOnlyList<NvidiaDll> available)
    {
        var held = ListCachedVersions()
            .Select(d => Version.TryParse(d.DisplayVersion, out var v) ? (v.Major, v.Minor, v.Build) : (-1, -1, -1))
            .ToHashSet();
        return available.Where(d => !held.Contains((d.Version.Major, d.Version.Minor, d.Version.Build))).ToList();
    }

    /// <summary>
    /// Downloads one build from NVIDIA into the cache, under its own version like every import.
    /// Nothing reaches the cache unless it is the file NVIDIA published: the server's checksum
    /// matches where it has one, the file carries a valid Authenticode signature from NVIDIA
    /// Corporation, and the version it reports is the one it was listed as. Returns a reason when
    /// it fails.
    /// </summary>
    public async Task<string?> DownloadNvidiaVersionAsync(NvidiaDll dll, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = await Helpers.Download(dll.Url, cancellationToken, quiet: true, progress: progress);
        if (!result.Success || result.Path is not { } path)
            return cancellationToken.IsCancellationRequested ? "cancelled" : "the download didn't finish";

        try
        {
            if (dll.HasChecksum)
            {
                var expected = (await GetNgxTextAsync(dll.Url + ".sha256", cancellationToken)).Trim().Split(' ', '\t')[0];
                await using var file = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"[DLSS] [NVIDIA] Checksum mismatch for {dll.DisplayVersion}: {actual} against {expected}");
                    return "the file didn't match NVIDIA's checksum";
                }
            }

            if (!Helpers.IsAuthenticodeSignedBy(path, "NVIDIA Corporation"))
                return "the file isn't signed by NVIDIA";

            var reported = FileVersionInfo.GetVersionInfo(path).FileVersion?.Replace(",", ".");
            if (!Version.TryParse(reported, out var version)
                || (version.Major, version.Minor, version.Build) != (dll.Version.Major, dll.Version.Minor, dll.Version.Build))
            {
                Trace.WriteLine($"[DLSS] [NVIDIA] {dll.DisplayVersion} reports itself as {reported}");
                return "the file reports a different version";
            }

            File.Copy(path, Path.Combine(CacheFolder, $"{FileVersionInfo.GetVersionInfo(path).FileVersion}.dll"), true);
            Trace.WriteLine($"[DLSS] [NVIDIA] Added DLSS {dll.DisplayVersion} to cache");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] [NVIDIA] Importing {dll.DisplayVersion} failed: {ex.Message}");
            return cancellationToken.IsCancellationRequested ? "cancelled" : "it couldn't be added to the cache";
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>A DLSS build on NVIDIA's update server, not downloaded yet.</summary>
internal sealed record NvidiaDll(Version Version, string Url, long Size, bool HasChecksum)
{
    public string DisplayVersion => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}
