using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;
using static Vanilla_RTX_App.Modules.PackLocator;

namespace Vanilla_RTX_App.Modules.PackUpdater;

public enum PackType { VanillaRTX, VanillaRTXNormals, VanillaRTXOpus }

/// <summary>
/// The verdict of <see cref="PackUpdater.GetUpdateNoticeAsync"/> - what, if anything, is worth
/// telling the user about Vanilla RTX from outside the updater window.
///
/// Deliberately a verdict and not a sentence: this class decides the facts, the caller owns the
/// wording, because the wording names a button that lives in the caller's own XAML.
/// </summary>
public enum PackUpdateNotice
{
    /// <summary>Everything installed is current, or nothing could be verified against the remote.
    /// Both mean the same thing to a caller: say nothing.</summary>
    None,

    /// <summary>The remote answered, and the user has none of the three packs installed.</summary>
    NothingInstalled,

    /// <summary>At least one installed pack is behind the remote. See the accompanying count.</summary>
    UpdatesAvailable
}

/// <summary>One release asset: the file's name in the release, its size and its sha256.</summary>
public sealed record ReleaseAsset(string File, long Size, string? Sha256);

/// <summary>
/// One pack a repository's release index describes. Everything needed to decide what to offer is
/// here, so nothing is downloaded until the user asks for this pack specifically.
/// </summary>
public sealed record ReleasePack(
    string Id,
    ReleaseAsset Asset,
    string Uuid,
    string? ModuleUuid,
    int[] Version,
    string Name,
    string Description,
    string? Group,
    IReadOnlyList<PackSubpack> Subpacks)
{
    public string VersionDisplay => string.Join('.', Version);
}

/// <param name="Tag">The release the index belongs to. Download URLs are built against it, never against "latest", so a cached index keeps pointing at files that exist.</param>
public sealed record ReleaseIndex(string Tag, DateTime? BuiltUtc, IReadOnlyList<ReleasePack> Packs)
{
    public ReleasePack? Find(string uuid) => Packs.FirstOrDefault(p => p.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An installed copy of a pack: its version, and the pack folder it lives in.</summary>
public readonly record struct InstalledPackCopy(int[] Version, string Folder)
{
    public string VersionDisplay => string.Join('.', Version);
}

/// <summary>
/// One GitHub repository whose releases this class installs from. <see cref="Index"/> is its
/// <c>index.json</c>, read through <see cref="AssetUpdater"/>; <see cref="CacheKey"/> names its
/// folder of cached .mcpacks.
/// </summary>
public sealed record ReleaseRepository(string Name, string Url, ManagedAsset Index, string CacheKey);

/// <summary>
/// Installs and updates packs from GitHub releases - the three Vanilla RTX packs from
/// vanilla-rtx/Vanilla-RTX and every add-on and extension from vanilla-rtx/Vanilla-RTX-Extras -
/// and the two are handled identically, because both repositories publish the same thing.
///
/// <para><b>What is available comes from one file per repository, <c>index.json</c></b>, which
/// their shared release workflow (<c>.github/workflows/pack_builder.py</c>) writes beside the
/// .mcpacks of every release: per pack its asset name, size, sha256, header and module UUID,
/// version, name, description and subpacks. A pack developed with an <c>__enhancements</c>
/// folder is published with it merged in, so what a release holds is exactly what gets installed.
/// It is read from
/// <c>releases/latest/download/index.json</c>, which redirects to the newest release's asset
/// without the GitHub API, so the API's 60-an-hour unauthenticated limit never applies; the last
/// hop answers <c>If-None-Match</c>, so an unchanged index costs a 304.</para>
///
/// <para><b>Every pack downloads on its own</b>, from the release its index names, never from
/// "latest": a cached index then keeps pointing at files that exist. Each download is kept in a
/// per-repository cache (<see cref="CacheFolder"/>) beside a record of its sha256, so a reinstall
/// is a copy off the disk; a cached file is reused only while its size and hash match what the
/// index lists, and anything the index no longer lists is pruned whenever a fresh index arrives.</para>
///
/// <para><b>An install always asks the repository first</b>, ignoring the index's cooldown
/// (<see cref="InstallAsync"/>). The numbers on screen may be minutes old, and a release that
/// appeared since is exactly the one an install must not miss. Offline, the cached index stands,
/// so a reinstall off the cache still works. A same-day rebuild deletes and recreates its release,
/// so a failed download re-reads the index and retries once if the pack moved.</para>
///
/// <para><b>Nothing installed is touched until the new copy is proven</b>: the file's sha256
/// matches the index, and the archive's own manifest carries the UUID the index promised. Then
/// every installed copy is removed by UUID from both resource pack folders, and the new one moves
/// into place from a <c>__rtxapp_*</c> staging folder, which nothing else ever creates and so is
/// always safe to sweep.</para>
///
/// <para>One install at a time, across both repositories. The <see cref="PackUpdater"/> MainWindow
/// owns runs the work, so an install outlives the overlay that started it.</para>
/// </summary>
public class PackUpdater
{
    /// <summary>
    /// The changelog of the packs this module installs, shown in the document overlay. A blob
    /// page rather than a raw URL, like Help and Bugs: the overlay fetches it from
    /// raw.githubusercontent.com regardless, and the blob form is what its relative links resolve
    /// against. Not a setting - it describes this repository and no other.
    /// </summary>
    public const string ChangelogUrl = "https://github.com/vanilla-rtx/Vanilla-RTX/blob/master/CHANGELOG.md";

    /// <summary>
    /// How long an index is used without asking again. Releases are days or weeks apart, but an
    /// answer is a 304 of no bytes, so this is set by how fresh the updater's own numbers should be
    /// when it opens. Installs and the refresh button ignore it.
    /// </summary>
    private static readonly TimeSpan IndexCooldown = TimeSpan.FromMinutes(Tunables.PackIndexCooldownMinutes.Value);

    public static readonly ReleaseRepository VanillaRtx = MakeRepository("Vanilla RTX", "https://github.com/vanilla-rtx/Vanilla-RTX", "vanilla-rtx");
    public static readonly ReleaseRepository Extras = MakeRepository("Vanilla RTX Extras", "https://github.com/Vanilla-RTX/Vanilla-RTX-Extras", "extras");

    private static ReleaseRepository MakeRepository(string name, string url, string key) => new(
        name,
        url,
        new ManagedAsset(
            url + "/releases/latest/download/index.json",
            IndexCooldown,
            CacheFolderName: "PackUpdater",
            FileName: $"{key}-index.json",
            FailureBackoff: TimeSpan.FromMinutes(5)),
        key);

    // The index layout this build understands. A future layout that isn't backward-compatible
    // bumps the number in pack_builder.py, and this build then reads nothing rather than misread.
    private const int IndexFormat = 1;

    public bool InstallToDevelopmentFolder { get; set; } = false;
    public bool CleanUpTheOtherFolder { get; set; } = true;

    private int _installationInProgress;
    private string? _currentInstallingUuid;

    public PackUpdater() => RemoveLegacyZipballCache();

    /// <summary>Whether an install is running - the gate that keeps two out of the resource packs folder at once.</summary>
    public bool IsInstallationInProgress() => Volatile.Read(ref _installationInProgress) == 1;

    /// <summary>The header UUID of the pack being installed right now, or null.</summary>
    public string? CurrentlyInstallingUuid => _currentInstallingUuid;

    // ======================= Pack identity =======================

    public static string Uuid(PackType type) => type switch
    {
        PackType.VanillaRTX => VANILLA_RTX_HEADER_UUID,
        PackType.VanillaRTXNormals => VANILLA_RTX_NORMALS_HEADER_UUID,
        _ => VANILLA_RTX_OPUS_HEADER_UUID,
    };

    public static PackType? TypeOf(string uuid) => Enum.GetValues<PackType>().Cast<PackType?>()
        .FirstOrDefault(t => Uuid(t!.Value).Equals(uuid, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(PackType type) => type switch
    {
        PackType.VanillaRTX => "Vanilla RTX",
        PackType.VanillaRTXNormals => "Vanilla RTX Normals",
        _ => "Vanilla RTX Opus",
    };

    // ======================= Index =======================

    /// <summary>
    /// A repository's index: fresh when its cooldown has passed or <paramref name="force"/> asks,
    /// the cached copy otherwise or when the fetch fails. Null only when there is no copy at all.
    /// <c>CheckedRemote</c> is AssetUpdater's: true when the repository answered just now,
    /// including with a 304. A fresh answer also prunes that repository's cached packs.
    /// </summary>
    public static async Task<(ReleaseIndex? Index, bool CheckedRemote)> GetIndexAsync(
        ReleaseRepository repo, bool force = false, FetchPriority priority = FetchPriority.Foreground,
        CancellationToken cancellationToken = default)
    {
        var read = await AssetUpdater.ResolveFreshOrCachedAsync(repo.Index, force, priority, cancellationToken);
        var index = ParseIndex(read.Path);
        if (index is not null && read.CheckedRemote) PruneCache(repo, index);
        return (index, read.CheckedRemote);
    }

    /// <summary>The cached index as it stands, without any request - for readers that must not wait.</summary>
    public static ReleaseIndex? ReadCachedIndex(ReleaseRepository repo) => ParseIndex(AssetUpdater.Resolve(repo.Index));

    private static ReleaseAsset? ParseAsset(JsonNode? node)
    {
        var file = MinecraftJson.GetString(node?["file"]);
        if (string.IsNullOrEmpty(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return new ReleaseAsset(
            file,
            MinecraftJson.TryGetDouble(node?["size"], out var size) ? (long)size : 0,
            MinecraftJson.GetString(node?["sha256"]));
    }

    private static ReleaseIndex? ParseIndex(string? path)
    {
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var root = MinecraftJson.ParseObjectFile(path);
            if (root is null) return null;

            if (MinecraftJson.GetInt(root["format"]) != IndexFormat)
            {
                Trace.WriteLine($"[PackUpdater] Index format {MinecraftJson.GetString(root["format"])} isn't one this build reads.");
                return null;
            }

            var tag = MinecraftJson.GetString(root["tag"]);
            if (string.IsNullOrWhiteSpace(tag)) return null;

            DateTime? built = DateTime.TryParse(MinecraftJson.GetString(root["built_utc"]), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var b)
                ? b : null;

            var packs = new List<ReleasePack>();
            foreach (var node in root["packs"] as JsonArray ?? new JsonArray())
            {
                var id = MinecraftJson.GetString(node?["id"]);
                var asset = ParseAsset(node);
                var uuid = MinecraftJson.GetString(node?["uuid"]);
                var version = MinecraftJson.GetIntArray(node?["version"]);

                // An entry missing any of these can be neither matched nor installed.
                if (id is null || asset is null || uuid is null || version is null || version.Length == 0) continue;

                var subpacks = new List<PackSubpack>();
                foreach (var sp in node?["subpacks"] as JsonArray ?? new JsonArray())
                {
                    var folder = MinecraftJson.GetString(sp?["folder_name"]);
                    if (string.IsNullOrEmpty(folder)) continue;
                    subpacks.Add(new PackSubpack(folder, MinecraftJson.GetString(sp?["name"]) ?? folder));
                }

                packs.Add(new ReleasePack(
                    id,
                    asset,
                    uuid,
                    MinecraftJson.GetString(node?["module_uuid"]),
                    version,
                    Helpers.StripMinecraftFormatting(MinecraftJson.GetString(node?["name"]) ?? id),
                    Helpers.StripMinecraftFormatting(MinecraftJson.GetString(node?["description"]) ?? ""),
                    MinecraftJson.GetString(node?["group"]),
                    subpacks));
            }

            return new ReleaseIndex(tag, built, packs);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdater] Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    // ======================= What is installed =======================

    /// <summary>
    /// Which of <paramref name="packs"/> are installed for the targeted edition, keyed by header
    /// UUID - the same identity the game and <see cref="DeleteExistingPackByUUID"/> use. Reads the
    /// top-level manifest of every folder in both resource pack folders. A pack installed twice
    /// reports its highest version.
    /// </summary>
    public static Dictionary<string, InstalledPackCopy> FindInstalled(IEnumerable<ReleasePack> packs)
    {
        var wanted = new HashSet<string>(packs.Select(p => p.Uuid), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, InstalledPackCopy>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(EnvironmentVariables.Persistent.IsTargetingPreview))
        {
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var folder in folders)
            {
                if (Path.GetFileName(folder).StartsWith("__rtxapp_", StringComparison.OrdinalIgnoreCase)) continue;

                var manifest = PackManifest.FromFile(Path.Combine(folder, PackManifest.ModernFileName));
                var uuid = manifest?.HeaderUuid;
                var version = manifest?.VersionArray;
                if (uuid is null || version is null || !wanted.Contains(uuid)) continue;

                if (!found.TryGetValue(uuid, out var existing) || CompareVersionArrays(version, existing.Version) > 0)
                    found[uuid] = new InstalledPackCopy(version, folder);
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="pack"/> is newer than what is installed. False when it isn't installed at all.</summary>
    public static bool IsOutdated(ReleasePack pack, InstalledPackCopy? installed)
        => installed is { } i && CompareVersionArrays(pack.Version, i.Version) > 0;

    /// <summary>
    /// Whether <paramref name="remoteVersionString"/> is strictly newer than
    /// <paramref name="installedVersionString"/>, both dotted. Anything unparseable answers false,
    /// which means "don't offer an update" rather than prompting over something unreadable.
    /// </summary>
    public static bool IsNewer(string? installedVersionString, string? remoteVersionString)
    {
        var installed = ParseVersion(installedVersionString);
        var remote = ParseVersion(remoteVersionString);
        return installed is not null && remote is not null && CompareVersionArrays(remote, installed) > 0;
    }

    private static int[]? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().TrimStart('v', 'V').Split('.');
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out values[i])) return null;
        return values;
    }

    /// <summary>
    /// Component-wise comparison, negative/zero/positive for A before/equal/after B. Missing
    /// trailing components count as 0, so <c>1.2</c> and <c>1.2.0</c> compare equal.
    /// </summary>
    private static int CompareVersionArrays(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // ======================= Package cache =======================

    private static string? CacheFolder(ReleaseRepository repo)
        => Helpers.LocalStateFolder is { } root ? Path.Combine(root, "PackUpdater", "Packages", repo.CacheKey) : null;

    private static string HashRecordPath(string file) => file + ".sha256";

    /// <summary>
    /// The cached copy of <paramref name="asset"/>, or null. A file only counts while its size
    /// matches the index and the sha256 recorded when it was cached matches the index's - a
    /// rebuild under the same file name is a different file, and is downloaded again.
    /// </summary>
    private static string? CachedCopy(ReleaseRepository repo, ReleaseAsset asset)
    {
        if (CacheFolder(repo) is not { } folder) return null;
        var path = Path.Combine(folder, asset.File);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return null;
            if (string.IsNullOrEmpty(asset.Sha256)) return null;
            var recorded = File.Exists(HashRecordPath(path)) ? File.ReadAllText(HashRecordPath(path)).Trim() : null;
            return asset.Sha256.Equals(recorded, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether installing <paramref name="pack"/> would come off the disk rather than the network.</summary>
    public static bool IsCached(ReleaseRepository repo, ReleasePack pack)
        => CachedCopy(repo, pack.Asset) is not null;

    /// <summary>Removes cached files the index no longer lists. Never throws.</summary>
    private static void PruneCache(ReleaseRepository repo, ReleaseIndex index)
    {
        if (CacheFolder(repo) is not { } folder || !Directory.Exists(folder)) return;

        var keep = new HashSet<string>(index.Packs.Select(p => p.Asset.File), StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var asset = name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ? name[..^".sha256".Length] : name;
                if (keep.Contains(asset)) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch { }

        if (repo == VanillaRtx) _ = Task.Run(() => RefreshDeployableCacheStateStatic());
    }

    /// <summary>
    /// Earlier versions cached the whole repository zipball and the versions read off it. Both are
    /// dead weight now; removed on the first run that finds them.
    /// </summary>
    private static void RemoveLegacyZipballCache()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (settings["CachedZipballPath"] is string zipball)
            {
                try { if (File.Exists(zipball)) File.Delete(zipball); } catch { }
            }
            foreach (var key in new[] { "CachedZipballPath", "RemoteVersionsCache", "RemoteVersionsCacheTime", "RemoteVersionsFailedUntil", "LastCacheValidationCheck" })
                settings.Remove(key);
        }
        catch { }
    }

    // ======================= Deployable cache state (the main menu's glyph) =======================

    /// <summary>
    /// Raised when the answer to <see cref="HasDeployableCache"/> changes, so MainWindow's
    /// Get-latest-packs glyph can follow along. Static because the cache is process-wide. Raised
    /// on whichever thread made the change; a subscriber touching UI marshals.
    /// </summary>
    public static event Action<bool>? DeployableCacheChanged;

    private static bool? _lastBroadcastCacheState;

    /// <summary>
    /// Whether opening the updater can install off the disk: for every Vanilla RTX pack that is
    /// installed, the cache holds the build the cached index lists. False with nothing installed or
    /// no index, which is the case the cloud glyph is right for.
    /// </summary>
    public static bool HasDeployableCache()
    {
        var index = ReadCachedIndex(VanillaRtx);
        if (index is null) return false;

        var installed = new[]
        {
            (PackType.VanillaRTX, EnvironmentVariables.VanillaRTXVersion),
            (PackType.VanillaRTXNormals, EnvironmentVariables.VanillaRTXNormalsVersion),
            (PackType.VanillaRTXOpus, EnvironmentVariables.VanillaRTXOpusVersion),
        }.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => p.Item1).ToList();

        return installed.Count > 0 && installed.All(t => index.Find(Uuid(t)) is { } pack && IsCached(VanillaRtx, pack));
    }

    /// <summary>Re-probes and raises <see cref="DeployableCacheChanged"/> only when the answer moved. Returns the answer.</summary>
    public bool RefreshDeployableCacheState() => RefreshDeployableCacheStateStatic();

    private static bool RefreshDeployableCacheStateStatic()
    {
        var state = HasDeployableCache();
        if (_lastBroadcastCacheState == state) return state;
        _lastBroadcastCacheState = state;

        try { DeployableCacheChanged?.Invoke(state); }
        catch (Exception ex) { Trace.WriteLine($"[PackUpdater] DeployableCacheChanged subscriber threw: {ex.Message}"); }

        return state;
    }

    // ======================= Update notice (the main menu's log line) =======================

    /// <summary>
    /// Whether anything installed is behind the Vanilla RTX index, for MainWindow's once-a-session
    /// log line and its glyph. Reads the index through its cooldown at background priority, so the
    /// nine LocatePacksTask call sites cost one request every few minutes at most.
    /// </summary>
    public async Task<(PackUpdateNotice Notice, int OutdatedCount)> GetUpdateNoticeAsync(
        string? installedVanillaRTX, string? installedNormals, string? installedOpus)
    {
        try
        {
            var (index, _) = await GetIndexAsync(VanillaRtx, priority: FetchPriority.Background);
            if (index is null) return (PackUpdateNotice.None, 0);

            var packs = new[]
            {
                (installed: installedVanillaRTX, available: index.Find(Uuid(PackType.VanillaRTX))?.VersionDisplay),
                (installed: installedNormals, available: index.Find(Uuid(PackType.VanillaRTXNormals))?.VersionDisplay),
                (installed: installedOpus, available: index.Find(Uuid(PackType.VanillaRTXOpus))?.VersionDisplay),
            };

            if (packs.All(p => string.IsNullOrEmpty(p.available))) return (PackUpdateNotice.None, 0);
            if (packs.All(p => string.IsNullOrEmpty(p.installed))) return (PackUpdateNotice.NothingInstalled, 0);

            var outdated = packs.Count(p => IsNewer(p.installed, p.available));
            return outdated > 0 ? (PackUpdateNotice.UpdatesAvailable, outdated) : (PackUpdateNotice.None, 0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdater] Update-notice check failed: {ex.Message}");
            return (PackUpdateNotice.None, 0);
        }
    }

    // ======================= Installing =======================

    /// <summary>
    /// Installs each pack in <paramref name="uuids"/> from <paramref name="repo"/>, in turn, and
    /// calls <paramref name="onStarting"/> as each one begins. The index is re-read once, first,
    /// ignoring its cooldown, and every pack is resolved against that answer; offline, the cached
    /// index is used, so a cached pack still reinstalls. One pack failing doesn't stop the rest.
    /// </summary>
    /// <returns>The packs that installed and the ones that didn't. Both empty when another install was already running.</returns>
    public async Task<(List<ReleasePack> Installed, List<ReleasePack> Failed)> InstallAsync(
        ReleaseRepository repo,
        IReadOnlyList<string> uuids,
        Action<ReleasePack>? onStarting = null,
        CancellationToken cancellationToken = default)
    {
        var installed = new List<ReleasePack>();
        var failed = new List<ReleasePack>();

        if (Interlocked.Exchange(ref _installationInProgress, 1) == 1)
        {
            Trace.WriteLine("⚠️ Another installation is already in progress");
            return (installed, failed);
        }

        try
        {
            if (Helpers.IsMinecraftRunning() && Helpers.RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
                Trace.WriteLine("⚠️ Minecraft is running. Please close the game while using the app.");

            var (index, _) = await GetIndexAsync(repo, force: true, cancellationToken: cancellationToken);
            if (index is null)
            {
                Trace.WriteLine($"❌ Couldn't read the {repo.Name} release index.");
                return (installed, failed);
            }

            foreach (var uuid in uuids)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (index.Find(uuid) is not { } pack)
                {
                    Trace.WriteLine($"❌ {repo.Name}'s latest release doesn't list {uuid}.");
                    continue;
                }

                _currentInstallingUuid = pack.Uuid;
                onStarting?.Invoke(pack);

                if (await InstallOneAsync(repo, index, pack, cancellationToken))
                    installed.Add(pack);
                else
                    failed.Add(pack);
            }
        }
        finally
        {
            _currentInstallingUuid = null;
            Volatile.Write(ref _installationInProgress, 0);
            if (repo == VanillaRtx) RefreshDeployableCacheState();
        }

        return (installed, failed);
    }

    private async Task<bool> InstallOneAsync(ReleaseRepository repo, ReleaseIndex index, ReleasePack pack, CancellationToken cancellationToken)
    {
        try
        {
            Trace.WriteLine($"🔄 Installing {pack.Name} {pack.VersionDisplay}...");
            var asset = pack.Asset;

            var path = CachedCopy(repo, asset);
            if (path is not null)
            {
                Trace.WriteLine($"📦 {asset.File} is cached, installing it from the disk.");
            }
            else
            {
                path = await DownloadToCacheAsync(repo, index.Tag, asset, cancellationToken);

                // The release may have been rebuilt since the index was read.
                if (path is null)
                {
                    var (fresh, _) = await GetIndexAsync(repo, force: true, cancellationToken: cancellationToken);
                    if (fresh?.Find(pack.Uuid) is { } moved && (fresh.Tag != index.Tag || moved.Asset.File != asset.File))
                    {
                        Trace.WriteLine($"[PackUpdater] {pack.Name} moved to {fresh.Tag}, retrying.");
                        pack = moved;
                        asset = moved.Asset;
                        path = CachedCopy(repo, asset) ?? await DownloadToCacheAsync(repo, fresh.Tag, asset, cancellationToken);
                    }
                }
            }

            if (path is null)
            {
                Trace.WriteLine($"❌ Couldn't download {pack.Name}.");
                return false;
            }

            return await Task.Run(() => Deploy(path, pack), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Failed to install {pack.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads one asset, checks its sha256 against the index, and moves it into the cache with
    /// a record of that hash. Null when anything fails; a file that doesn't match is deleted, never
    /// cached.
    /// </summary>
    private static async Task<string?> DownloadToCacheAsync(ReleaseRepository repo, string tag, ReleaseAsset asset, CancellationToken cancellationToken)
    {
        if (CacheFolder(repo) is not { } folder) return null;

        var download = await Helpers.Download(
            $"{repo.Url}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.File)}",
            cancellationToken,
            Helpers.UpdaterHttpClient,
            TimeSpan.FromMinutes(15));

        if (!download.Success || download.Path is not { } downloaded) return null;

        string? temp = downloaded;
        try
        {
            string hash;
            await using (var stream = File.OpenRead(temp))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));

            if (!string.IsNullOrEmpty(asset.Sha256) && !hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"❌ {asset.File} didn't match the hash the release lists; nothing was installed.");
                return null;
            }

            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, asset.File);
            File.Move(temp, destination, overwrite: true);
            temp = null;
            File.WriteAllText(HashRecordPath(destination), hash);
            return destination;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>
    /// Extracts one .mcpack (manifest at the archive root) into a staging folder, removes every
    /// installed copy by UUID, and moves the new one into place. The archive's own manifest must
    /// carry the UUID the index promised, or nothing is touched.
    /// </summary>
    private bool Deploy(string mcpackPath, ReleasePack pack)
    {
        string? staging = null;
        string? resourcePackPath = null;
        try
        {
            var isPreview = EnvironmentVariables.Persistent.IsTargetingPreview;
            if (!MinecraftUserDataLocator.IsDataValid(isPreview))
            {
                Trace.WriteLine($"❌ {MinecraftUserDataLocator.GetVersionDisplayName(isPreview)} data root not found. Make sure the game is installed and has been launched at least once.");
                return false;
            }

            resourcePackPath = MinecraftUserDataLocator.GetResourcePacksPath(isPreview, development: InstallToDevelopmentFolder, createIfMissing: true);
            if (string.IsNullOrEmpty(resourcePackPath)) return false;

            staging = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            var stagedPack = Path.Combine(staging, "pack");
            Directory.CreateDirectory(stagedPack);

            using (var archive = ZipFile.OpenRead(mcpackPath))
            {
                foreach (var (entry, targetPath, isDirectory) in Helpers.EnumerateZipFolderExtraction(archive, "", stagedPack))
                {
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }

            var manifest = PackManifest.FromFile(Path.Combine(stagedPack, PackManifest.ModernFileName));
            if (manifest?.HeaderUuid is not { } uuid || !uuid.Equals(pack.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"❌ {pack.Name}'s archive isn't the pack the release index describes; nothing was installed.");
                return false;
            }

            DeleteExistingPackByUUID(resourcePackPath, uuid, pack.Name);

            var destination = GetSafeDirectoryName(resourcePackPath, FolderName(pack));
            Directory.Move(stagedPack, destination);
            Helpers.GenerateBookkeepingFiles(destination);

            Trace.WriteLine($"✅ {pack.Name} {pack.VersionDisplay} installed");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Failed to deploy {pack.Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try
                {
                    ForceWritable(staging);
                    Directory.Delete(staging, true);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠️ Failed to clean up {staging}: {ex.Message}");
                }
            }

            if (resourcePackPath is not null) CleanupOrphanedDirectories(resourcePackPath);
        }
    }

    /// <summary>
    /// The folder a pack is installed under. The three Vanilla RTX packs keep the short names they
    /// have always had. Anything else is shortened from its release id, because a subpack's
    /// deepest texture path is long already - Glowing Glazed Terracotta's runs past 200 characters
    /// from a typical user data root - and Windows' path limit is what that runs into first.
    /// </summary>
    private static string FolderName(ReleasePack pack)
    {
        switch (TypeOf(pack.Uuid))
        {
            case PackType.VanillaRTX: return "vrtx";
            case PackType.VanillaRTXNormals: return "vrtxn";
            case PackType.VanillaRTXOpus: return "vrtxo";
        }

        var name = pack.Id.Replace("-Add-On", "", StringComparison.OrdinalIgnoreCase);
        foreach (var suffix in new[] { "-Any", "-All" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        }

        name = new string(name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim('-', ' ', '.');
        if (name.Length > 24) name = name[..24].TrimEnd('-', ' ', '.');
        return string.IsNullOrEmpty(name) ? "VanillaRTXPack" : name;
    }

    /// <summary>
    /// Deletes leftover <c>__rtxapp_*</c> staging folders older than a minute - an install running
    /// right now owns a younger one. The prefix is the invariant: nothing but an install creates
    /// one. With <see cref="CleanUpTheOtherFolder"/> the opposite pack folder is swept too.
    /// </summary>
    private void CleanupOrphanedDirectories(string resourcePackPath)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        foreach (var path in FoldersToClean(resourcePackPath))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(path, "__rtxapp_*", SearchOption.TopDirectoryOnly)
                             .Where(d => Directory.GetCreationTimeUtc(d) < cutoff))
                {
                    try
                    {
                        ForceWritable(dir);
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>The resource pack folder being installed into, plus its development/non-development counterpart when <see cref="CleanUpTheOtherFolder"/> is set.</summary>
    private IEnumerable<string> FoldersToClean(string resourcePackPath)
    {
        yield return resourcePackPath;
        if (!CleanUpTheOtherFolder) yield break;

        var dir = new DirectoryInfo(resourcePackPath);
        var other = dir.Name.Equals("development_resource_packs", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(dir.Parent!.FullName, "resource_packs")
            : Path.Combine(dir.Parent!.FullName, "development_resource_packs");
        if (Directory.Exists(other)) yield return other;
    }

    /// <summary>
    /// Removes every installed copy of a pack, identified by its header UUID rather than its
    /// folder or display name - both of which the user is free to change, and neither of which
    /// Minecraft uses to tell packs apart. A match can be a nested manifest, so each is resolved
    /// to the pack's top-level folder first. More than one match is possible, and all go:
    /// duplicates are exactly what this prevents.
    /// </summary>
    private void DeleteExistingPackByUUID(string resourcePackPath, string headerUuid, string packName)
    {
        foreach (var root in FoldersToClean(resourcePackPath))
        {
            List<string> manifests;
            try
            {
                manifests = Directory.GetFiles(root, PackManifest.ModernFileName, SearchOption.AllDirectories)
                    .Where(m => !Path.GetDirectoryName(m)!.Contains("__rtxapp_"))
                    .ToList();
            }
            catch { continue; }

            foreach (var manifestPath in manifests)
            {
                if (PackManifest.FromFile(manifestPath)?.HeaderUuid is not { } uuid) continue;
                if (!uuid.Equals(headerUuid, StringComparison.OrdinalIgnoreCase)) continue;

                var top = TopLevelFolder(manifestPath, root);
                if (top is null || !Directory.Exists(top)) continue;

                ForceWritable(top);
                Directory.Delete(top, true);
                Trace.WriteLine($"🗑️ Removed previous installation of: {packName}");
            }
        }
    }

    /// <summary>The folder directly inside <paramref name="resourcePackPath"/> that holds <paramref name="manifestPath"/>, or null.</summary>
    private static string? TopLevelFolder(string manifestPath, string resourcePackPath)
    {
        var root = new DirectoryInfo(resourcePackPath).FullName.TrimEnd(Path.DirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetDirectoryName(manifestPath)!);
        while (current.Parent is not null)
        {
            if (current.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase)) return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// A free directory path: the desired name if unused, else the same name with a numeric
    /// suffix. An existing but empty directory is deleted and its name reused.
    /// </summary>
    private static string GetSafeDirectoryName(string parentPath, string desiredName)
    {
        var fullPath = Path.Combine(parentPath, desiredName);
        if (!Directory.Exists(fullPath)) return fullPath;
        if (Directory.GetFileSystemEntries(fullPath).Length == 0)
        {
            Directory.Delete(fullPath);
            return fullPath;
        }

        var suffix = 1;
        string candidate;
        do candidate = Path.Combine(parentPath, $"{desiredName}{suffix++}");
        while (Directory.Exists(candidate) && Directory.GetFileSystemEntries(candidate).Length > 0);

        if (Directory.Exists(candidate)) Directory.Delete(candidate);
        return candidate;
    }

    /// <summary>
    /// Clears the read-only attribute under a directory. <see cref="Directory.Delete(string, bool)"/>
    /// throws on a read-only file, and pack archives in the wild carry them.
    /// </summary>
    private static void ForceWritable(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;

        if ((di.Attributes & System.IO.FileAttributes.ReadOnly) != 0) di.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        foreach (var info in di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((info.Attributes & System.IO.FileAttributes.ReadOnly) != 0) info.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }
    }
}
