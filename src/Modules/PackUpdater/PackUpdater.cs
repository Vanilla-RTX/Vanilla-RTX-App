using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;
using static Vanilla_RTX_App.Modules.PackLocator; // For static UUIDs, they are stored there for locating packs
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules.PackUpdater;

/// =====================================================================================================================
/// Only deals with cache, we don't care if user has Vanilla RTX installed or not, we compare versions of cache to remote
/// No cache? download latest, cache outdated? download latest, if there's a cache and the rest fails for whatever the reason, fallback to cache
/// Deployment deletes any pack that matches UUIDs as defined at the begenning of PackLocator class
/// =====================================================================================================================

public enum PackType { VanillaRTX, VanillaRTXNormals, VanillaRTXOpus }

public enum VersionSource
{
    Remote,           // Fresh from GitHub
    CachedRemote,     // From few-min cache of remote versions
    ZipballFallback   // Read from cached zipball when remote unavailable
}

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

public class PackUpdater
{
    private const string VANILLA_RTX_MANIFEST_URL = "https://raw.githubusercontent.com/vanilla-rtx/Vanilla-RTX/master/Vanilla-RTX/manifest.json";
    private const string VANILLA_RTX_NORMALS_MANIFEST_URL = "https://raw.githubusercontent.com/vanilla-rtx/Vanilla-RTX/master/Vanilla-RTX-Normals/manifest.json";
    private const string VANILLA_RTX_OPUS_MANIFEST_URL = "https://raw.githubusercontent.com/vanilla-rtx/Vanilla-RTX/master/Vanilla-RTX-Opus/manifest.json";
    private const string VANILLA_RTX_REPO_ZIPBALL_URL = "https://github.com/vanilla-rtx/Vanilla-RTX/archive/refs/heads/master.zip";

    /// <summary>
    /// The changelog of the packs this module installs, shown in the document overlay. A blob
    /// page rather than a raw URL, like Help and Bugs: the overlay fetches it from
    /// raw.githubusercontent.com regardless, and the blob form is what its relative links resolve
    /// against. Not a setting - it describes this repository and no other (§2h's rule).
    /// </summary>
    public const string ChangelogUrl = "https://github.com/vanilla-rtx/Vanilla-RTX/blob/master/CHANGELOG.md";

    // Remote version cache // how frequently to check the remote again for manifest's versions
    //
    // Not split by edition, and that is the point: everything this class fetches - the three
    // manifests above, the zipball - is the same file on the same branch whichever edition the
    // app is targeting. Only where a pack gets INSTALLED differs, and PackLocator handles that
    // separately. These keys used to be per-edition, which bought nothing and meant a user who
    // toggles Preview re-asked GitHub for bytes it had already cached, doubling the request rate
    // for identical data.
    private const string RemoteVersionsCacheKey = "RemoteVersionsCache";
    private const string RemoteVersionsCacheTimeKey = "RemoteVersionsCacheTime";
    private const string RemoteVersionsFailedUntilKey = "RemoteVersionsFailedUntil";

    /// <summary>
    /// How old the remote versions may be for a caller that doesn't say otherwise - which is
    /// MainWindow's status pass, running off every pack re-locate. Vanilla RTX releases are weeks
    /// apart, so asking GitHub more often than this buys nothing a user would notice.
    /// </summary>
    private static readonly TimeSpan RemoteVersionCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// What the updater window asks for when it opens. Someone looking at the version numbers is
    /// owed something newer than a background glyph is, without making every open a request.
    /// </summary>
    public static readonly TimeSpan OverlayVersionMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long to leave the remote alone after a failed version check. Without it, an offline
    /// session re-attempts three requests on every pack re-locate, each waiting out its own
    /// timeout, and nine call sites can trigger that.
    /// </summary>
    private static readonly TimeSpan RemoteVersionsFailureBackoff = TimeSpan.FromMinutes(10);

    // Cache validation check cooldown (Zip re-check versus remote before trying to install from it)
    private const string LastCacheCheckKey = "LastCacheValidationCheck";
    private static readonly TimeSpan CacheCheckCooldown = TimeSpan.FromMinutes(50);

    private bool _installationInProgress = false;
    private PackType? _currentInstallingPack = null;

    public string EnhancementFolderName { get; set; } = "__enhancements";
    public bool InstallToDevelopmentFolder { get; set; } = false;
    public bool CleanUpTheOtherFolder { get; set; } = true;

    // ======================= Installation State Management =======================

    /// <summary>
    /// Whether a deploy is running. The gate that keeps two installs out of the resource
    /// packs folder at once - see <see cref="UpdateSinglePackAsync"/>.
    /// </summary>
    public bool IsInstallationInProgress()
    {
        return _installationInProgress;
    }

    /// <summary>Which pack is installing, for the window's status text. Null when idle.</summary>
    public PackType? GetCurrentlyInstallingPack()
    {
        return _currentInstallingPack;
    }

    private void SetInstallationState(bool isInstalling, PackType? pack = null)
    {
        _installationInProgress = isInstalling;
        _currentInstallingPack = pack;
    }

    private void ClearInstallationState()
    {
        SetInstallationState(false, null);
    }

    // ======================= Cache Invalidation (Core) =======================

    /// <summary>
    /// Drops the cached zipball and forgets its path, so the next install downloads afresh.
    /// </summary>
    public void InvalidateCache()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var cachedPath = localSettings.Values["CachedZipballPath"] as string;

        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
        {
            try
            {
                File.Delete(cachedPath);
                Trace.WriteLine("🗑️ Deleted outdated cache file");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to delete cache file: {ex.Message}");
            }
        }

        localSettings.Values["CachedZipballPath"] = null;
        Trace.WriteLine("❌ Cache invalidated - will download fresh on next install");

        RefreshDeployableCacheState();
    }

    // ======================= Deployable Cache State (broadcast) =======================

    /// <summary>
    /// Raised when the answer to "is there a deployable cache?" changes, so anything drawing that
    /// state (MainWindow's Get-latest-packs glyph) can follow along instead of re-asking at every
    /// place that might have changed it - which is how it was done before, and meant every new
    /// cache-touching code path silently owed a refresh call it was easy to forget.
    ///
    /// Static because the cache is one process-wide thing - a single LocalSettings key plus one
    /// file on disk - not a per-instance one. That matters in practice: PackUpdaterOverlay falls
    /// back to `new PackUpdater()` when it can't borrow MainWindow's, and an invalidation from
    /// that second instance still has to reach whoever is drawing the glyph.
    ///
    /// Raised on whatever thread made the change - installs run under the caller's Task.Run - so
    /// a subscriber touching UI has to marshal. Nothing here does that for you on purpose: this
    /// class has no business knowing what a DispatcherQueue is.
    /// </summary>
    public static event Action<bool>? DeployableCacheChanged;

    private static bool? _lastBroadcastCacheState;

    /// <summary>
    /// Re-probes the cache and raises <see cref="DeployableCacheChanged"/> only when the answer
    /// actually moved since the last broadcast - so this is safe to call as often as a caller
    /// likes, and a subscriber never sees a redundant event.
    ///
    /// Callers only need this for changes made behind the class's back (a Wipe, a temp-folder
    /// sweep, a file deleted by hand); every change this class makes itself already broadcasts.
    /// Returns the freshly probed state for callers that want it inline.
    /// </summary>
    public bool RefreshDeployableCacheState()
    {
        var state = HasDeployableCache();

        if (_lastBroadcastCacheState == state)
            return state;

        _lastBroadcastCacheState = state;

        try
        {
            DeployableCacheChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            // A subscriber blowing up is a UI problem, never a reason to fail a cache operation.
            Trace.WriteLine($"[PackUpdater] DeployableCacheChanged subscriber threw: {ex.Message}");
        }

        return state;
    }

    // ======================= Cache Validation Check =======================

    /// <summary>
    /// Asks whether the cached zipball has fallen behind GitHub, and invalidates it if so.
    /// </summary>
    /// <returns>
    /// True only when the cache was actually invalidated. Every other outcome - no cache, no
    /// network, still on cooldown, GitHub unreachable, cache current - returns false, so a
    /// false answer means "nothing to do", never "the cache is fine".
    /// </returns>
    /// <remarks>
    /// <para>Three gates before any request goes out: a cache has to exist, the machine has
    /// to report a network, and the last check has to be older than <c>CacheCheckCooldown</c>.
    /// The cooldown stamp is only written after a <i>successful</i> fetch, so a failed attempt
    /// doesn't burn the window.</para>
    /// <para><b>It asks the remote itself rather than reading the stored versions</b>, and that
    /// is the point of it: this is the last moment before ~11MB is deployed, and a release that
    /// appeared after the window's own numbers were read is exactly what it exists to catch. What
    /// it learns is stored for everyone else (<see cref="StoreRemoteVersions"/>), so the cost is
    /// paid once.</para>
    /// <para>Every failure path keeps the existing cache rather than discarding it. A cache
    /// that might be one version old is worth far more to an offline user than no cache at
    /// all, and being unable to reach GitHub says nothing about whether the cache is
    /// stale.</para>
    /// </remarks>
    public async Task<bool> ValidateCacheAgainstRemote()
    {
        var cacheInfo = GetCacheInfo();

        if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
        {
            Trace.WriteLine("📦 No cache exists - will download on first pack installation");
            return false;
        }

        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            Trace.WriteLine("🛜 No network available - will use existing cache");
            return false;
        }

        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var checkKey = LastCacheCheckKey;

        if (localSettings.Values[checkKey] is string lastCheckStr &&
            DateTimeOffset.TryParse(lastCheckStr, out var lastCheck))
        {
            if (now < lastCheck + CacheCheckCooldown)
            {
                var minutesLeft = (int)Math.Ceiling((lastCheck + CacheCheckCooldown - now).TotalMinutes);
                Trace.WriteLine($"⏳ Cache check on cooldown - {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} left");
                return false;
            }
        }

        (PackManifest? rtx, PackManifest? normals, PackManifest? opus)? remote = null;

        try
        {
            remote = await FetchRemoteManifests();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"⚠️ Failed to contact GitHub: {ex.Message}");
        }

        if (remote != null)
        {
            localSettings.Values[checkKey] = now.ToString("o");

            // This is a real answer from the remote, and the only place that pays for one at
            // install time - so everything else that shows a version reads it rather than asking
            // again. It is also the freshest reading the app has: this path deliberately skips
            // the stored copy, which is what lets an install pick up a release that appeared
            // after the window's own numbers were fetched.
            StoreRemoteVersions(
                (ExtractVersionFromManifest(remote.Value.rtx), VersionSource.Remote),
                (ExtractVersionFromManifest(remote.Value.normals), VersionSource.Remote),
                (ExtractVersionFromManifest(remote.Value.opus), VersionSource.Remote));
        }
        else
        {
            Trace.WriteLine("⚠️ Could not validate cache - will use existing cache");
            RecordRemoteVersionFailure();
            return false;
        }

        bool needsInvalidation = await DoesCacheNeedUpdate(cacheInfo.path!, remote.Value);

        if (needsInvalidation)
        {
            Trace.WriteLine("📦 Cache is outdated - invalidating now");
            InvalidateCache();
            return true;
        }

        Trace.WriteLine("✅ Cache is up-to-date");
        return false;
    }

    /// <summary>
    /// Compares the three packs inside the cached zipball against the remote manifests,
    /// per pack. True if <i>any</i> of them is behind, missing from the cache, or present in
    /// the cache but gone remotely - the zipball is one file, so one stale pack invalidates
    /// all of it.
    ///
    /// <para><b>The question is whether the CACHE is behind the remote, not whether the
    /// user's installed pack is.</b> Those are different: someone running an older pack on
    /// purpose has a perfectly current cache, and invalidating it there throws away an ~11MB
    /// download and hits GitHub to replace a file that was already correct.</para>
    ///
    /// <para>A remote pack the cache doesn't have counts as outdated; a cached pack the
    /// remote no longer publishes does too, since the cache no longer describes what upstream
    /// ships. Any exception reading the zip means it can't be trusted, which also counts.</para>
    /// </summary>
    private async Task<bool> DoesCacheNeedUpdate(string cachedPath, (PackManifest? rtx, PackManifest? normals, PackManifest? opus) remoteManifests)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachedPath);
            var cachedPacks = await FindPacksInZip(archive);

            var rtxManifest = cachedPacks.TryGetValue(PackType.VanillaRTX, out var rtxFound) ? rtxFound.Manifest : null;
            var normalsManifest = cachedPacks.TryGetValue(PackType.VanillaRTXNormals, out var normalsFound) ? normalsFound.Manifest : null;
            var opusManifest = cachedPacks.TryGetValue(PackType.VanillaRTXOpus, out var opusFound) ? opusFound.Manifest : null;

            bool anyOutdated = false;

            if (remoteManifests.rtx != null)
            {
                if (rtxManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(rtxManifest, remoteManifests.rtx))
                {
                    var cacheVer = ExtractVersionFromManifest(rtxManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.rtx);
                    Trace.WriteLine($"📦 Vanilla RTX: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (rtxManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (remoteManifests.normals != null)
            {
                if (normalsManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX Normals is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(normalsManifest, remoteManifests.normals))
                {
                    var cacheVer = ExtractVersionFromManifest(normalsManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.normals);
                    Trace.WriteLine($"📦 Vanilla RTX Normals: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (normalsManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX Normals exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (remoteManifests.opus != null)
            {
                if (opusManifest == null)
                {
                    Trace.WriteLine("📦 Vanilla RTX Opus is available remotely but missing from cache");
                    anyOutdated = true;
                }
                else if (IsRemoteVersionNewer(opusManifest, remoteManifests.opus))
                {
                    var cacheVer = ExtractVersionFromManifest(opusManifest);
                    var remoteVer = ExtractVersionFromManifest(remoteManifests.opus);
                    Trace.WriteLine($"📦 Vanilla RTX Opus: {cacheVer} → {remoteVer} (update available)");
                    anyOutdated = true;
                }
            }
            else if (opusManifest != null)
            {
                Trace.WriteLine("📦 Vanilla RTX Opus exists in cache but not remotely - invalidating");
                anyOutdated = true;
            }

            if (!anyOutdated)
            {
                Trace.WriteLine("✅ All packs in cache are up-to-date");
            }

            return anyOutdated;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"⚠️ Error reading cached zipball: {ex.Message} - invalidating cache");
            return true;
        }
    }

    // ======================= Individual Pack Installation =======================

    /// <summary>
    /// Installs or updates one pack, end to end: validate the cache, download the zipball if
    /// there isn't one, deploy just that pack out of it.
    /// </summary>
    /// <returns>
    /// True if the pack deployed. False for a download failure, an unusable cache, an
    /// unexpected error, or <b>another installation already running</b> - only one runs at a
    /// time, since two deploys would be extracting into and deleting from the same resource
    /// packs folder at once.
    /// </returns>
    /// <remarks>
    /// The zipball holds all three packs, so an update of one still costs one download and
    /// then serves any later pack from cache. The installation flag is always cleared in a
    /// finally - losing it would leave the feature refusing every install for the rest of the
    /// session.
    /// </remarks>
    public async Task<bool> UpdateSinglePackAsync(PackType packType, bool enableEnhancements)
    {
        // Check if another installation is already running
        if (IsInstallationInProgress())
        {
            Trace.WriteLine("⚠️ Another installation is already in progress");
            return false;
        }

        try
        {
            // Mark installation as in progress
            SetInstallationState(true, packType);

            var packName = GetPackDisplayName(packType);
            Trace.WriteLine($"🔄 Starting installation for {packName}...");

            await ValidateCacheAgainstRemote();

            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
            {
                Trace.WriteLine("📦 No cache available - downloading now...");

                var (downloadSuccess, downloadPath) = await DownloadLatestPackage();
                if (!downloadSuccess || string.IsNullOrEmpty(downloadPath))
                {
                    Trace.WriteLine("❌ Download failed");
                    return false;
                }

                SaveCachedZipballPath(downloadPath);
                cacheInfo = (true, downloadPath);
            }

            Trace.WriteLine("✅ Using cached zipball for deployment");
            return await DeployPackage(cacheInfo.path!, packType, enableEnhancements);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Unexpected error: {ex.Message}");
            return false;
        }
        finally
        {
            // Always clear installation state when done
            ClearInstallationState();
        }
    }

    // ======================= Remote Version Fetching (For UI Display) =======================

    /// <summary>
    /// The three remote pack versions, from the stored copy while it is young enough and from
    /// GitHub when it isn't.
    ///
    /// <para><paramref name="maxAge"/> is how old the stored copy may be for this particular
    /// caller: the background status pass takes the default, the updater window asks for
    /// something fresher when it opens, and <paramref name="force"/> ignores the stored copy
    /// outright, which is what the refresh button asks for.</para>
    ///
    /// <para>A failed check is remembered too, so an offline session does not sit through three
    /// timeouts on every pack re-locate. <paramref name="force"/> ignores that as well - someone
    /// pressing a button is entitled to the attempt.</para>
    /// </summary>
    public async Task<(
        (string? version, VersionSource source) rtx,
        (string? version, VersionSource source) normals,
        (string? version, VersionSource source) opus
    )> GetRemoteVersionsAsync(TimeSpan? maxAge = null, bool force = false)
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var now = DateTimeOffset.UtcNow;
        var cacheKey = RemoteVersionsCacheKey;
        var timeKey = RemoteVersionsCacheTimeKey;
        var age = maxAge ?? RemoteVersionCacheDuration;

        if (!force &&
            localSettings.Values[timeKey] is string cacheTimeStr &&
            DateTimeOffset.TryParse(cacheTimeStr, out var cacheTime) &&
            now < cacheTime + age)
        {
            if (localSettings.Values[cacheKey] is string cachedJson)
            {
                try
                {
                    var cached = ParseJsonObject(cachedJson)
                        ?? throw new JsonException("Cached remote versions payload was not a JSON object.");

                    var rtxCached = cached["rtx"];
                    var normalsCached = cached["normals"];
                    var opusCached = cached["opus"];

                    return (
                        (MinecraftJson.GetString(rtxCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(rtxCached?["source"])))),
                        (MinecraftJson.GetString(normalsCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(normalsCached?["source"])))),
                        (MinecraftJson.GetString(opusCached?["version"]),
                         AsCached(ParseVersionSource(MinecraftJson.GetString(opusCached?["source"]))))
                    );
                }
                catch { /* Fall through */ }
            }
        }

        string? rtxVersion = null, normalsVersion = null, opusVersion = null;
        VersionSource rtxSource = VersionSource.Remote;
        VersionSource normalsSource = VersionSource.Remote;
        VersionSource opusSource = VersionSource.Remote;
        bool anyRemoteSuccess = false;
        bool attemptedNetwork = false;

        if (!force && IsRemoteVersionCheckBackedOff())
        {
            Trace.WriteLine("⏳ Remote version check backed off after a recent failure - using what is on disk");
        }
        else if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            attemptedNetwork = true;
            try
            {
                var remoteManifests = await FetchRemoteManifests();
                if (remoteManifests.HasValue)
                {
                    var (rtxManifest, normalsManifest, opusManifest) = remoteManifests.Value;

                    if (rtxManifest != null)
                    {
                        rtxVersion = ExtractVersionFromManifest(rtxManifest);
                        rtxSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }

                    if (normalsManifest != null)
                    {
                        normalsVersion = ExtractVersionFromManifest(normalsManifest);
                        normalsSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }

                    if (opusManifest != null)
                    {
                        opusVersion = ExtractVersionFromManifest(opusManifest);
                        opusSource = VersionSource.Remote;
                        anyRemoteSuccess = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to fetch remote versions: {ex.Message}");
            }
        }

        var cacheInfo = GetCacheInfo();
        if (cacheInfo.exists && File.Exists(cacheInfo.path))
        {
            try
            {
                var zipballVersions = await GetVersionsFromCachedZipball(cacheInfo.path!);
                if (zipballVersions.HasValue)
                {
                    if (rtxVersion == null && zipballVersions.Value.rtx != null)
                    {
                        rtxVersion = zipballVersions.Value.rtx;
                        rtxSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX version");
                    }

                    if (normalsVersion == null && zipballVersions.Value.normals != null)
                    {
                        normalsVersion = zipballVersions.Value.normals;
                        normalsSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Normals version");
                    }

                    if (opusVersion == null && zipballVersions.Value.opus != null)
                    {
                        opusVersion = zipballVersions.Value.opus;
                        opusSource = VersionSource.ZipballFallback;
                        Trace.WriteLine("Using zipball fallback for Vanilla RTX Opus version");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to read zipball versions: {ex.Message}");
            }
        }

        if (anyRemoteSuccess)
        {
            StoreRemoteVersions(
                (rtxVersion, rtxSource),
                (normalsVersion, normalsSource),
                (opusVersion, opusSource));
        }
        else if (attemptedNetwork)
        {
            RecordRemoteVersionFailure();
        }

        return (
            (rtxVersion, rtxSource),
            (normalsVersion, normalsSource),
            (opusVersion, opusSource)
        );
    }

    /// <summary>
    /// Stores the three versions and where each came from, and stamps the store as of now.
    ///
    /// <para><b>Every path that talks to the remote writes through here</b>, including the
    /// install-time validation, which asks the same three URLs for its own reasons. Whoever pays
    /// for a request, everyone else gets to read the answer - and the glyph on MainWindow, the
    /// updater window's numbers and the install-time check can't end up describing different
    /// moments in time.</para>
    /// </summary>
    private void StoreRemoteVersions(
        (string? version, VersionSource source) rtx,
        (string? version, VersionSource source) normals,
        (string? version, VersionSource source) opus)
    {
        try
        {
            var cacheObj = new JsonObject();

            // A pack whose manifest didn't come back keeps whatever was stored for it. One of the
            // three failing while the others answer is a normal enough outcome, and letting it
            // blank an entry would show "Failed to check" for that pack until the next check -
            // for a version the app knew perfectly well a moment ago.
            var existing = ApplicationData.Current.LocalSettings.Values[RemoteVersionsCacheKey] is string previousJson
                ? ParseJsonObject(previousJson)
                : null;

            void Add(string key, (string? version, VersionSource source) pack)
            {
                if (pack.version == null)
                {
                    if (existing?[key] is JsonNode kept) cacheObj[key] = kept.DeepClone();
                    return;
                }

                cacheObj[key] = new JsonObject
                {
                    ["version"] = pack.version,
                    ["source"] = pack.source.ToString()
                };
            }

            Add("rtx", rtx);
            Add("normals", normals);
            Add("opus", opus);

            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[RemoteVersionsCacheKey] = cacheObj.ToJsonString();
            localSettings.Values[RemoteVersionsCacheTimeKey] = DateTimeOffset.UtcNow.ToString("o");
            localSettings.Values[RemoteVersionsFailedUntilKey] = null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdater] Couldn't store remote versions: {ex.Message}");
        }
    }

    /// <summary>Whether a recent failed check says not to bother the remote yet.</summary>
    private bool IsRemoteVersionCheckBackedOff()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[RemoteVersionsFailedUntilKey] is not string stamp)
                return false;

            if (!DateTimeOffset.TryParse(stamp, out var until))
                return false;

            var wait = until - DateTimeOffset.UtcNow;

            // Further out than the backoff itself means a clock that moved, or a corrupt value;
            // either way it would otherwise lock checks out indefinitely.
            return wait > TimeSpan.Zero && wait <= RemoteVersionsFailureBackoff;
        }
        catch
        {
            return false;
        }
    }

    private void RecordRemoteVersionFailure()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[RemoteVersionsFailedUntilKey] =
                DateTimeOffset.UtcNow.Add(RemoteVersionsFailureBackoff).ToString("o");
        }
        catch { }
    }

    /// <summary>
    /// Turns the persisted version-source setting back into its enum, falling back to the
    /// default for anything unrecognised - including a value written by a newer build of the
    /// app, which a downgrade would otherwise choke on.
    /// </summary>
    private VersionSource ParseVersionSource(string? sourceString)
    {
        if (string.IsNullOrEmpty(sourceString))
            return VersionSource.Remote;

        return Enum.TryParse<VersionSource>(sourceString, out var source)
            ? source
            : VersionSource.Remote;
    }

    /// <summary>
    /// Restates a stored reading's provenance for the fact that it is now being served from the
    /// few-minute LocalSettings cache rather than freshly fetched.
    ///
    /// Without this the cache branch echoed back whatever it stored - always <c>Remote</c> - so
    /// <see cref="VersionSource.CachedRemote"/> was never produced anywhere in the app and the
    /// "(You seem up-to-date)" wording that hangs off it in PackUpdaterOverlay was unreachable.
    ///
    /// <see cref="VersionSource.ZipballFallback"/> deliberately keeps its own provenance: that a
    /// reading came off the offline zipball is the more useful thing to tell the user, and it
    /// stays true no matter how many times it is re-served from cache.
    /// </summary>
    private static VersionSource AsCached(VersionSource stored) =>
        stored == VersionSource.Remote ? VersionSource.CachedRemote : stored;

    /// <summary>
    /// Drops the cached zipball, but only when it is genuinely behind the remote versions handed
    /// in. Returns whether it dropped anything.
    ///
    /// This closes a gap that <see cref="ValidateCacheAgainstRemote"/> alone cannot: that one is
    /// behind its own cooldown, so a cache that goes stale inside that window would deploy
    /// stale. This runs whenever the updater window refreshes, with no cooldown of its own,
    /// because it costs nothing to run - the remote numbers are the caller's already-fetched
    /// ones (so no request), and the cache's own numbers are read off the zipball already on
    /// disk. Nothing here touches the network.
    ///
    /// The thing to keep straight - and what the previous version of this got wrong - is that
    /// the question is cache-versus-remote and nothing else. The caller used to trigger on
    /// installed-versus-remote and then invalidate unconditionally, which threw away a perfectly
    /// current zipball whenever the user merely happened to be running an older pack, costing an
    /// ~11MB re-download and a GitHub hit to replace a file that was already correct.
    /// </summary>
    public async Task<bool> InvalidateCacheIfStaleAsync(string? remoteVanillaRTX, string? remoteNormals, string? remoteOpus)
    {
        try
        {
            var cacheInfo = GetCacheInfo();
            if (!cacheInfo.exists || string.IsNullOrEmpty(cacheInfo.path))
                return false; // Nothing cached, nothing to drop.

            var cached = await GetVersionsFromCachedZipball(cacheInfo.path!);

            if (cached == null)
            {
                // A cache we can't read versions out of can't be trusted to install from either.
                Trace.WriteLine("📦 Cached zipball unreadable - invalidating");
                InvalidateCache();
                return true;
            }

            var packs = new[]
            {
                (cached: cached.Value.rtx,     remote: remoteVanillaRTX),
                (cached: cached.Value.normals, remote: remoteNormals),
                (cached: cached.Value.opus,    remote: remoteOpus)
            };

            // A pack the remote has and the cache doesn't counts as stale, same as one the cache
            // has an older copy of. A remote we couldn't read says nothing either way, so skip it.
            var stale = packs.Any(p =>
                !string.IsNullOrEmpty(p.remote) &&
                (string.IsNullOrEmpty(p.cached) || IsRemoteVersionNewerThanInstalled(p.cached, p.remote)));

            if (!stale)
                return false;

            Trace.WriteLine("📦 Cached zipball is behind the remote - invalidating");
            InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            // Failing to verify is not a reason to throw away a cache that may well be fine.
            Trace.WriteLine($"[PackUpdater] Staleness check failed, keeping cache: {ex.Message}");
            return false;
        }
    }

    // ======================= Update Availability (for callers outside the updater window) =======================

    /// <summary>
    /// Compares what is installed against the remote versions and returns whether that is worth
    /// telling the user about. Built for MainWindow, which has no updater UI of its own to show
    /// this in and just wants to know whether to mention it once.
    ///
    /// Reads remote versions through <see cref="GetRemoteVersionsAsync"/>, so it shares the same
    /// few-minute cache the updater window already fills - calling this on every pack re-locate
    /// costs a GitHub request at most once per cache window, not once per call.
    ///
    /// Silence is the default: a remote that can't be reached, a version that can't be read, or
    /// an install that is simply current all return <see cref="PackUpdateNotice.None"/>. Nothing
    /// about failing to check is worth interrupting someone over.
    ///
    /// Installed versions are passed in rather than located here - the caller has just done that
    /// work, and repeating a filesystem sweep to re-learn what it already knows would be waste.
    /// </summary>
    /// <returns>
    /// The verdict, plus how many of the three packs are behind - which the caller needs in order
    /// to get "update" versus "updates" right.
    /// </returns>
    public async Task<(PackUpdateNotice Notice, int OutdatedCount)> GetUpdateNoticeAsync(
        string? installedVanillaRTX,
        string? installedNormals,
        string? installedOpus)
    {
        try
        {
            var remote = await GetRemoteVersionsAsync();

            var packs = new[]
            {
                (installed: installedVanillaRTX, available: remote.rtx.version),
                (installed: installedNormals,    available: remote.normals.version),
                (installed: installedOpus,       available: remote.opus.version)
            };

            // Nothing readable came back for any of the three - offline, rate-limited, malformed
            // manifest, doesn't matter. We know nothing, so we say nothing.
            if (packs.All(p => string.IsNullOrEmpty(p.available)))
                return (PackUpdateNotice.None, 0);

            if (packs.All(p => string.IsNullOrEmpty(p.installed)))
                return (PackUpdateNotice.NothingInstalled, 0);

            var outdated = packs.Count(p =>
                !string.IsNullOrEmpty(p.installed) &&
                !string.IsNullOrEmpty(p.available) &&
                IsRemoteVersionNewerThanInstalled(p.installed, p.available));

            return outdated > 0
                ? (PackUpdateNotice.UpdatesAvailable, outdated)
                : (PackUpdateNotice.None, 0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdater] Update-notice check failed: {ex.Message}");
            return (PackUpdateNotice.None, 0);
        }
    }

    private async Task<(string? rtx, string? normals, string? opus)?> GetVersionsFromCachedZipball(string cachePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cachePath);
            var cachedPacks = await FindPacksInZip(archive);

            string? VersionOf(PackType packType) =>
                cachedPacks.TryGetValue(packType, out var found) ? found.Manifest.VersionDisplay : null;

            return (VersionOf(PackType.VanillaRTX), VersionOf(PackType.VanillaRTXNormals), VersionOf(PackType.VanillaRTXOpus));
        }
        catch
        {
            return null;
        }
    }

    // ======================= Helper Methods =======================

    // Every manifest this class reads now goes through PackManifest.cs - one tolerant parser
    // (comments, trailing commas, duplicate keys, raw control characters in strings) and one
    // definition of "header UUID", "module UUID" and "version", shared with PackLocator,
    // PackBrowser, ExpImpDel, ShaderModManager and Alchitex. What this class vets is unchanged
    // and is now stated in one place: header.uuid + modules[0].uuid for identity,
    // header.version for freshness.
    //
    // ParseJsonObject survives only for the remote-versions cache below, which is a payload
    // this class writes to LocalSettings itself - not a manifest.
    private static JsonObject? ParseJsonObject(string json) => MinecraftJson.ParseObject(json);

    /// <summary>
    /// Cache-vs-remote comparison, and the only thing this class compares: header.version.
    /// A version either side can't be read as an integer array counts as "newer", so an
    /// unreadable manifest re-downloads rather than pinning the user to a stale cache.
    /// </summary>
    private bool IsRemoteVersionNewer(PackManifest cachedManifest, PackManifest remoteManifest)
    {
        var cachedVersion = cachedManifest.VersionArray;
        var remoteVersion = remoteManifest.VersionArray;

        if (cachedVersion == null || remoteVersion == null) return true;

        return CompareVersionArrays(remoteVersion, cachedVersion) > 0;
    }

    /// <summary>
    /// Scans a cached zipball for pack manifests, identifying each by its header + module UUID
    /// pair - never by folder name. A GitHub codeload zipball wraps the whole repo in a
    /// branch/commit-named folder, so manifests sit one level inside that (depth 2 from the
    /// archive root); depth 1 is included too in case a future zipball ever drops the wrapper.
    /// This is the single place that turns "what's in the zip" into "which of our three packs
    /// is this", and every other method in this class that needs that answer goes through it.
    /// </summary>
    private async Task<Dictionary<PackType, (ZipArchiveEntry Entry, PackManifest Manifest)>> FindPacksInZip(ZipArchive archive)
    {
        var found = new Dictionary<PackType, (ZipArchiveEntry, PackManifest)>();

        foreach (var entry in Helpers.FindZipEntriesAtDepth(archive, PackManifest.ModernFileName, minDepth: 1, maxDepth: 2))
        {
            var manifest = await ReadManifestFromZipEntry(entry);
            if (manifest?.HeaderUuid == null || manifest.FirstModuleUuid == null) continue;

            var packType = IdentifyPackType(manifest.HeaderUuid, manifest.FirstModuleUuid);
            if (packType.HasValue)
            {
                found[packType.Value] = (entry, manifest);
            }
        }

        return found;
    }

    /// <summary>
    /// Which of the three Vanilla RTX packs a manifest belongs to, by matching both its header
    /// and module UUIDs against the known constants. Null for anything else, which is how a
    /// third-party pack in the same folder is left alone.
    /// </summary>
    private static PackType? IdentifyPackType(string headerUUID, string moduleUUID)
    {
        if (headerUUID == VANILLA_RTX_HEADER_UUID && moduleUUID == VANILLA_RTX_MODULE_UUID)
            return PackType.VanillaRTX;
        if (headerUUID == VANILLA_RTX_NORMALS_HEADER_UUID && moduleUUID == VANILLA_RTX_NORMALS_MODULE_UUID)
            return PackType.VanillaRTXNormals;
        if (headerUUID == VANILLA_RTX_OPUS_HEADER_UUID && moduleUUID == VANILLA_RTX_OPUS_MODULE_UUID)
            return PackType.VanillaRTXOpus;
        return null;
    }

    /// <summary>
    /// Parses a manifest straight out of a zip entry, without extracting it - which is what
    /// lets the cache be inspected for what it contains and at what versions before deciding
    /// whether to deploy or re-download. Null for an entry that isn't readable as a manifest.
    /// </summary>
    private static async Task<PackManifest?> ReadManifestFromZipEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return PackManifest.Parse(json, sourcePath: entry.FullName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Short slug ("vrtx", "vrtxn", "vrtxo") used in the <c>__rtxapp_*</c> staging folder
    /// name. Not user-facing - <see cref="GetPackDisplayName"/> is.
    /// </summary>
    private string GetPackFolderShortName(PackType packType) => packType switch
    {
        PackType.VanillaRTX => "vrtx",
        PackType.VanillaRTXNormals => "vrtxn",
        PackType.VanillaRTXOpus => "vrtxo",
        _ => "pack"
    };

    private async Task<(PackManifest? rtx, PackManifest? normals, PackManifest? opus)?> FetchRemoteManifests()
    {
        async Task<PackManifest?> TryFetchManifest(string url)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = await Helpers.UpdaterHttpClient.GetStringAsync(url, cts.Token);
                return PackManifest.Parse(response, sourcePath: url);
            }
            catch
            {
                return null;
            }
        }

        var rtxTask = TryFetchManifest(VANILLA_RTX_MANIFEST_URL);
        var normalsTask = TryFetchManifest(VANILLA_RTX_NORMALS_MANIFEST_URL);
        var opusTask = TryFetchManifest(VANILLA_RTX_OPUS_MANIFEST_URL);

        await Task.WhenAll(rtxTask, normalsTask, opusTask);

        var rtx = await rtxTask;
        var normals = await normalsTask;
        var opus = await opusTask;

        if (rtx == null && normals == null && opus == null)
        {
            return null;
        }

        return (rtx, normals, opus);
    }

    private async Task<(bool Success, string? Path)> DownloadLatestPackage()
    {
        try
        {
            Trace.WriteLine("📦 Downloading latest zipball from GitHub...");
            return await Helpers.Download(VANILLA_RTX_REPO_ZIPBALL_URL);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Download error: {ex.Message}");
            return (false, null);
        }
    }

    // ======================= Deploy Package =======================

    /// <summary>
    /// Installs packs out of a downloaded zipball into Minecraft's resource packs folder.
    /// </summary>
    /// <param name="targetPack">
    /// One pack, or null for every pack found in the archive. The zipball always carries all
    /// three, so this is what makes "update just Normals" possible without re-downloading.
    /// </param>
    /// <param name="enableEnhancements">
    /// True hoists each pack's enhancement folder over its shipped files
    /// (<see cref="ProcessEnhancementFolders"/>); false deletes those folders outright
    /// (<see cref="RemoveEnhancementsFolder"/>). The packs ship them either way.
    /// </param>
    /// <returns>True if at least one pack deployed. A per-pack failure is logged and skipped.</returns>
    /// <remarks>
    /// <para><b>Extract to a temporary folder, then move into place.</b> Each pack lands in a
    /// <c>__rtxapp_*</c> staging directory and is only moved to its real name once its files
    /// are all written, so a failure or crash mid-extract leaves a prefixed folder Minecraft
    /// ignores rather than a half-written pack it would try to load.
    /// <see cref="CleanupOrphanedDirectories"/> sweeps those later.</para>
    ///
    /// <para><b>The old copy is removed by UUID, not by folder name</b> (see
    /// <see cref="DeleteExistingPackByUUID"/>) - a user can rename a pack folder freely, and
    /// matching on the name would leave the old one installed alongside the new one, giving
    /// Minecraft two packs with the same UUID.</para>
    ///
    /// <para>Deploying while Minecraft is running is not blocked - the game holds no lock
    /// that stops it - but the user is told once per session, since the game will not see the
    /// change until it restarts.</para>
    /// </remarks>
    private async Task<bool> DeployPackage(string packagePath, PackType? targetPack = null, bool enableEnhancements = true)
    {
        if (Helpers.IsMinecraftRunning() && Helpers.RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Trace.WriteLine("⚠️ Minecraft is running. Please close the game while using the app.");
        }

        bool anyPackDeployed = false;
        string? tempExtractionDir = null;
        string? resourcePackPath = null;

        try
        {
            var versionName = MinecraftUserDataLocator.GetVersionDisplayName(EnvironmentVariables.Persistent.IsTargetingPreview);

            if (!MinecraftUserDataLocator.IsDataValid(EnvironmentVariables.Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"❌ {versionName} data root not found. Please make sure the game is installed or has been launched at least once.");
                return false;
            }

            resourcePackPath = MinecraftUserDataLocator.GetResourcePacksPath(
                EnvironmentVariables.Persistent.IsTargetingPreview,
                development: InstallToDevelopmentFolder,
                createIfMissing: true);

            if (string.IsNullOrEmpty(resourcePackPath))
            {
                Trace.WriteLine("❌ Could not access or create the resource packs directory.");
                return false;
            }

            Trace.WriteLine("📁 Resource pack directory ready.");

            tempExtractionDir = Path.Combine(resourcePackPath, "__rtxapp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractionDir);

            var packsToProcess = new List<(string uuid, string moduleUuid, string finalName, string displayName, PackType packType)>();

            // Targeted extraction: identify pack folders inside the zip by manifest UUID (never
            // by name, see FindPacksInZip), then pull only the matched folder(s) out to disk -
            // the other pack(s) and the rest of the repo (README, .github, etc.) are never
            // extracted at all, rather than extracting the whole zipball just to move one folder.
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var foundPacks = await FindPacksInZip(archive);

                foreach (var (packType, found) in foundPacks)
                {
                    if (targetPack.HasValue && packType != targetPack.Value) continue;
                    packsToProcess.Add((found.Manifest.HeaderUuid!, found.Manifest.FirstModuleUuid!, GetPackFolderShortName(packType), GetPackDisplayName(packType), packType));
                }

                if (packsToProcess.Count == 0)
                {
                    Trace.WriteLine(targetPack.HasValue
                        ? $"❌ {GetPackDisplayName(targetPack.Value)} not found in the cached package."
                        : "❌ No recognized Vanilla RTX packs found in the cached package.");
                    return false;
                }

                Trace.WriteLine($"📦 Found {packsToProcess.Count} pack(s) to install: {string.Join(", ", packsToProcess.Select(p => p.displayName))}");

                foreach (var pack in packsToProcess)
                {
                    var found = foundPacks[pack.packType];
                    var zipFolderPrefix = found.Entry.FullName.Substring(0, found.Entry.FullName.Length - found.Entry.Name.Length);
                    var destFolder = Path.Combine(tempExtractionDir, pack.finalName);
                    Directory.CreateDirectory(destFolder);

                    // Same entry-to-path resolution ExpImpDel's import uses (Helpers.EnumerateZipFolderExtraction) -
                    // only the write strategy differs, since this method already runs off the UI
                    // thread via its caller's own Task.Run and doesn't need ExpImpDel's per-file one.
                    foreach (var (entry, targetPath, isDirectory) in Helpers.EnumerateZipFolderExtraction(archive, zipFolderPrefix, destFolder))
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
            }

            Trace.WriteLine("📦 Extracted targeted pack folder(s) from cached zipball");

            foreach (var pack in packsToProcess)
            {
                try
                {
                    Trace.WriteLine($"🔄 Processing {pack.displayName}...");

                    await DeleteExistingPackByUUID(resourcePackPath, pack.uuid, pack.moduleUuid, pack.displayName);

                    var finalDestination = GetSafeDirectoryName(resourcePackPath, pack.finalName);
                    var extractedPackPath = Path.Combine(tempExtractionDir, pack.finalName);
                    Directory.Move(extractedPackPath, finalDestination);

                    if (enableEnhancements)
                    {
                        ProcessEnhancementFolders(finalDestination);
                    }
                    else
                    {
                        RemoveEnhancementsFolder(finalDestination);
                    }

                    Helpers.GenerateBookkeepingFiles(finalDestination);

                    Trace.WriteLine($"✅ {pack.displayName} deployed successfully");
                    anyPackDeployed = true;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"❌ Failed to deploy {pack.displayName}: {ex.Message}");
                }
            }

            return anyPackDeployed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"❌ Deployment error: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempExtractionDir != null && Directory.Exists(tempExtractionDir))
            {
                try
                {
                    ForceWritable(tempExtractionDir);
                    Directory.Delete(tempExtractionDir, true);
                    Trace.WriteLine(anyPackDeployed ? "🧹 Cleaned up" : "🧹 Cleaned up after fail");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠️ Failed to clean up temp directory: {ex.Message}");
                }
            }

            if (resourcePackPath != null)
            {
                CleanupOrphanedDirectories(resourcePackPath);
            }
        }
    }

    /// <summary>
    /// Deletes leftover <c>__rtxapp_*</c> staging directories - the temporary folders a deploy
    /// extracts into before promoting them to their real names.
    ///
    /// <para><b>Only ones older than a minute</b>, because a deploy running right now owns a
    /// folder matching that pattern and deleting it would destroy the install in progress.
    /// The prefix is the invariant: nothing outside a deploy ever creates one, so an old one
    /// is by construction abandoned and always safe to remove.</para>
    ///
    /// <para>With <c>CleanUpTheOtherFolder</c> set it also sweeps the opposite of
    /// resource_packs / development_resource_packs, since a previous run with the other
    /// destination setting can have left debris there.</para>
    ///
    /// <para>Every failure is swallowed. This is housekeeping - a locked folder is retried on
    /// the next deploy, and failing the install over it would be absurd.</para>
    /// </summary>
    private void CleanupOrphanedDirectories(string resourcePackPath)
    {
        var pathsToCleanOrphans = new List<string> { resourcePackPath };

        if (CleanUpTheOtherFolder)
        {
            var dirInfo = new DirectoryInfo(resourcePackPath);
            string opposingPath = InstallToDevelopmentFolder
                ? dirInfo.Name.Equals("development_resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "development_resource_packs")
                    : resourcePackPath;

            if (Directory.Exists(opposingPath))
            {
                pathsToCleanOrphans.Add(opposingPath);
            }
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-1);

        foreach (var pathToClean in pathsToCleanOrphans)
        {
            try
            {
                var orphanedDirs = Directory.GetDirectories(pathToClean, "__rtxapp_*", SearchOption.TopDirectoryOnly)
                    .Where(d => Directory.GetCreationTimeUtc(d) < cutoff);

                foreach (var dir in orphanedDirs)
                {
                    try
                    {
                        ForceWritable(dir);
                        Directory.Delete(dir, true);
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    // ======================= Enhancement Methods =======================

    /// <summary>
    /// Deletes every enhancement folder in the tree, for a deploy with the toggle off. The
    /// packs ship these folders unconditionally, so "off" means removing what was shipped
    /// rather than skipping a step.
    ///
    /// <para>Searched recursively because subpacks carry their own. Failures are logged and
    /// skipped per folder - a leftover enhancement folder is inert, so it is not worth
    /// failing a deploy over.</para>
    /// </summary>
    private void RemoveEnhancementsFolder(string rootDirectory)
    {
        if (string.IsNullOrEmpty(EnhancementFolderName)) return;

        try
        {
            var enhancementFolders = Directory.GetDirectories(rootDirectory, EnhancementFolderName, SearchOption.AllDirectories);

            foreach (var enhancementPath in enhancementFolders)
            {
                try
                {
                    ForceWritable(enhancementPath);
                    Directory.Delete(enhancementPath, true);
                    Trace.WriteLine("🗑️ Removed enhancements folder (toggle was OFF)");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to remove enhancement folder ({enhancementPath}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error during enhancement folder removal: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies enhancements by hoisting each enhancement folder's contents into its parent -
    /// overwriting the shipped files with the enhanced versions - then removing the now-empty
    /// folder.
    ///
    /// <para>That overwrite is the entire mechanism: the packs ship the enhanced files
    /// alongside the plain ones in a subfolder, and enabling them is a move, not a download.
    /// Doing it in place also means a pack deployed with the toggle on carries no trace of
    /// the folder afterwards, which is what makes the off path a plain delete.</para>
    ///
    /// <para>Recursive, since subpacks have their own. Per-folder failures are counted and
    /// logged rather than thrown: a partially enhanced pack still renders.</para>
    /// </summary>
    private void ProcessEnhancementFolders(string rootDirectory)
    {
        if (string.IsNullOrEmpty(EnhancementFolderName)) return;

        var enhancementFolders = Directory.GetDirectories(rootDirectory, EnhancementFolderName, SearchOption.AllDirectories);
        int processed = 0, failed = 0, deleteIssues = 0;

        foreach (var enhancementPath in enhancementFolders)
        {
            try
            {
                var parentDirectory = Directory.GetParent(enhancementPath)!.FullName;

                deleteIssues += MoveDirectoryContents(enhancementPath, parentDirectory);

                try
                {
                    Directory.Delete(enhancementPath, false);
                }
                catch
                {
                    deleteIssues++;
                }

                processed++;
            }
            catch (Exception ex)
            {
                failed++;
                Trace.WriteLine($"Enhancement folder error ({enhancementPath}): {ex.Message}");
            }
        }

        if (processed + failed > 0)
        {
            var msg = failed == 0
                ? $"✨ Enabled Enhancements"
                : $"⚠️ Processing {processed} failed {failed}. Delete failures: {deleteIssues}";

            Trace.WriteLine(msg);
        }
    }

    /// <summary>
    /// Moves everything from <paramref name="sourceDir"/> into
    /// <paramref name="targetDir"/>, overwriting files that already exist there and merging
    /// directories that do - recursing into a colliding subdirectory rather than replacing
    /// it, so an enhancement folder only has to contain the files it actually changes.
    /// </summary>
    /// <returns>
    /// A count of failed deletes and moves, not a success flag. Callers aggregate it for the
    /// log; a non-zero count means the pack is partially enhanced, which is degraded but
    /// still usable, so nothing here throws.
    /// </returns>
    private int MoveDirectoryContents(string sourceDir, string targetDir)
    {
        int deleteFailures = 0;

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));

            if (File.Exists(destFile))
            {
                try { File.Delete(destFile); }
                catch { deleteFailures++; continue; }
            }

            try { File.Move(file, destFile); }
            catch { deleteFailures++; }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));

            if (Directory.Exists(destSubDir))
            {
                deleteFailures += MoveDirectoryContents(subDir, destSubDir);

                try { Directory.Delete(subDir, false); }
                catch { deleteFailures++; }
            }
            else
            {
                try { Directory.Move(subDir, destSubDir); }
                catch { deleteFailures++; }
            }
        }

        return deleteFailures;
    }

    // ======================= Cache & Utility Methods =======================

    /// <summary>
    /// Whether the cached zipball contains this particular pack. Opens the archive and looks;
    /// false if there is no cache, it can't be read, or that pack isn't in it.
    /// </summary>
    public async Task<bool> DoesPackExistInCache(PackType packType)
    {
        var cacheInfo = GetCacheInfo();
        if (!cacheInfo.exists || !File.Exists(cacheInfo.path))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(cacheInfo.path!);
            var cachedPacks = await FindPacksInZip(archive);
            return cachedPacks.ContainsKey(packType);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The pack's proper name for logs and UI ("Vanilla RTX Normals"). Never used as a path
    /// component - see <see cref="GetPackFolderShortName"/> for that.
    /// </summary>
    private string GetPackDisplayName(PackType packType)
    {
        return packType switch
        {
            PackType.VanillaRTX => "Vanilla RTX",
            PackType.VanillaRTXNormals => "Vanilla RTX Normals",
            PackType.VanillaRTXOpus => "Vanilla RTX Opus",
            _ => "Unknown Pack"
        };
    }

    private string? ExtractVersionFromManifest(PackManifest? manifest) => manifest?.VersionDisplay;

    /// <summary>
    /// Whether <paramref name="remoteVersionString"/> is strictly newer than
    /// <paramref name="installedVersionString"/>. Both are dotted version strings, optionally
    /// <c>v</c>-prefixed.
    ///
    /// <para><b>Anything it cannot parse answers false</b> - a missing version, a
    /// non-numeric component, either side empty. False means "do not offer an update", so an
    /// unreadable version is reported as "nothing to do" rather than prompting the user to
    /// install over something this cannot reason about.</para>
    /// </summary>
    public bool IsRemoteVersionNewerThanInstalled(string? installedVersionString, string? remoteVersionString)
    {
        try
        {
            if (string.IsNullOrEmpty(installedVersionString) || string.IsNullOrEmpty(remoteVersionString))
                return false;

            var installedVersion = ParseVersionString(installedVersionString);
            var remoteVersion = ParseVersionString(remoteVersionString);

            if (installedVersion == null || remoteVersion == null)
                return false;

            return CompareVersionArrays(remoteVersion, installedVersion) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Splits a dotted version into components, tolerating a leading <c>v</c>/<c>V</c>.
    /// Null when any component isn't a plain integer - partial parses are not returned,
    /// because a half-understood version compares wrong and comparing wrong is worse than
    /// declining to compare. Length is not fixed; <see cref="CompareVersionArrays"/> handles
    /// mismatches.
    /// </summary>
    private int[]? ParseVersionString(string versionString)
    {
        try
        {
            if (string.IsNullOrEmpty(versionString))
                return null;

            versionString = versionString.TrimStart('v', 'V');

            var parts = versionString.Split('.');
            return parts.Select(int.Parse).ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Component-wise comparison, negative/zero/positive for A before/equal/after B. Missing
    /// trailing components count as 0, so <c>1.2</c> and <c>1.2.0</c> compare equal rather
    /// than the shorter one losing.
    /// </summary>
    private static int CompareVersionArrays(int[] versionA, int[] versionB)
    {
        for (int i = 0; i < Math.Max(versionA.Length, versionB.Length); i++)
        {
            int a = i < versionA.Length ? versionA[i] : 0;
            int b = i < versionB.Length ? versionB[i] : 0;

            if (a > b) return 1;
            if (a < b) return -1;
        }
        return 0;
    }

    /// <summary>
    /// A directory path that is free to write to: the desired name if it is unused,
    /// otherwise the same name with a numeric suffix.
    ///
    /// <para>An existing but <i>empty</i> directory is deleted and its name reused, so
    /// repeated deploys don't accumulate <c>name1</c>, <c>name2</c>… behind abandoned empty
    /// folders. A non-empty one belongs to somebody and is never touched.</para>
    /// </summary>
    private string GetSafeDirectoryName(string parentPath, string desiredName)
    {
        var fullPath = Path.Combine(parentPath, desiredName);

        if (!Directory.Exists(fullPath))
            return fullPath;

        if (Directory.GetFileSystemEntries(fullPath).Length == 0)
        {
            Directory.Delete(fullPath);
            return fullPath;
        }

        int suffix = 1;
        string safeName;
        do
        {
            safeName = Path.Combine(parentPath, $"{desiredName}{suffix}");
            suffix++;
        } while (Directory.Exists(safeName) && Directory.GetFileSystemEntries(safeName).Length > 0);

        if (Directory.Exists(safeName))
            Directory.Delete(safeName);

        return safeName;
    }

    /// <summary>
    /// Removes any already-installed copy of a pack, identified by its manifest UUIDs rather
    /// than its folder name or display name - both of which the user is free to change, and
    /// neither of which Minecraft uses to tell packs apart.
    ///
    /// <para>Scans every manifest under the resource packs folder, resolves each match back to
    /// the pack's top-level folder (<see cref="GetTopLevelFolderForManifest"/>, since a match
    /// can be a nested subpack manifest) and deletes that whole folder. More than one match is
    /// possible and all are removed: duplicates are exactly the state this prevents.</para>
    /// </summary>
    private async Task DeleteExistingPackByUUID(string resourcePackPath, string targetHeaderUUID, string targetModuleUUID, string packName)
    {
        var pathsToClean = new List<string> { resourcePackPath };

        if (CleanUpTheOtherFolder)
        {
            var dirInfo = new DirectoryInfo(resourcePackPath);

            string opposingPath = InstallToDevelopmentFolder
                ? dirInfo.Name.Equals("development_resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "resource_packs")
                    : resourcePackPath
                : dirInfo.Name.Equals("resource_packs", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dirInfo.Parent!.FullName, "development_resource_packs")
                    : resourcePackPath;

            if (Directory.Exists(opposingPath))
            {
                pathsToClean.Add(opposingPath);
            }
        }

        foreach (var pathToClean in pathsToClean)
        {
            var currentManifests = Directory.GetFiles(pathToClean, PackManifest.ModernFileName, SearchOption.AllDirectories)
                .Where(m => !Path.GetDirectoryName(m)!.Contains("__rtxapp_"));

            foreach (var manifestPath in currentManifests)
            {
                var uuids = await ReadManifestUUIDs(manifestPath);
                if (uuids == null) continue;

                var (headerUUID, moduleUUID) = uuids.Value;
                if (headerUUID.Equals(targetHeaderUUID, StringComparison.OrdinalIgnoreCase) &&
                    moduleUUID.Equals(targetModuleUUID, StringComparison.OrdinalIgnoreCase))
                {
                    var topLevelFolder = GetTopLevelFolderForManifest(manifestPath, pathToClean);
                    if (topLevelFolder != null && Directory.Exists(topLevelFolder))
                    {
                        ForceWritable(topLevelFolder);
                        Directory.Delete(topLevelFolder, true);
                        Trace.WriteLine($"🗑️ Removed previous installation of: {packName}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clears the read-only attribute on a directory and everything under it.
    ///
    /// <para>Needed before deleting a tree: <see cref="Directory.Delete(string, bool)"/>
    /// throws on a read-only file, and pack archives in the wild do carry them - extraction
    /// preserves the flag. Called ahead of every recursive delete here for that reason. Silent
    /// no-op for a path that doesn't exist.</para>
    /// </summary>
    private void ForceWritable(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;

        if ((di.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
            di.Attributes &= ~System.IO.FileAttributes.ReadOnly;

        foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
        {
            if ((file.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                file.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }

        foreach (var dir in di.GetDirectories("*", SearchOption.AllDirectories))
        {
            if ((dir.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                dir.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }
    }

    private async Task<(string headerUUID, string moduleUUID)?> ReadManifestUUIDs(string manifestPath)
    {
        try
        {
            var manifest = await PackManifest.FromFileAsync(manifestPath);
            if (manifest == null) return null;

            string? headerUUID = manifest.HeaderUuid;
            string? moduleUUID = manifest.FirstModuleUuid;

            if (headerUUID == null || moduleUUID == null)
                return null;

            return (headerUUID, moduleUUID);
        }
        catch
        {
            return null;
        }
    }

    private (bool exists, string? path) GetCacheInfo()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var cachedPath = localSettings.Values["CachedZipballPath"] as string;
        bool exists = !string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath);
        if (exists)
        {
            try
            {
                using (ZipFile.OpenRead(cachedPath!)) { }
            }
            catch
            {
                Trace.WriteLine("⚠️ Cached package is corrupted, proceeding as if no cache was available.");
                exists = false;
                cachedPath = null;
            }
        }
        return (exists, cachedPath);
    }

    private void SaveCachedZipballPath(string path)
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values["CachedZipballPath"] = path;

        RefreshDeployableCacheState();
    }

    /// <summary>
    /// Whether a cached zipball is on disk and readable - i.e. whether an install can proceed
    /// without the network. Does not consider whether it is current;
    /// <see cref="ValidateCacheAgainstRemote"/> is that question.
    /// </summary>
    public bool HasDeployableCache()
    {
        var (exists, _) = GetCacheInfo();
        return exists;
    }

    /// <summary>
    /// Walks up from a manifest to the folder that sits directly inside
    /// <paramref name="resourcePackPath"/> - the pack's own root as Minecraft sees it, which
    /// is what has to be deleted or replaced to remove the pack.
    ///
    /// <para>Necessary because a manifest can be nested: a pack may keep subpacks with their
    /// own manifests, so the folder holding the manifest is not always the pack's root. Null
    /// when the manifest is not under that path at all.</para>
    /// </summary>
    private string? GetTopLevelFolderForManifest(string manifestPath, string resourcePackPath)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath);
        var resourcePackDir = new DirectoryInfo(resourcePackPath);
        var currentDir = manifestDir != null ? new DirectoryInfo(manifestDir) : null;

        while (currentDir != null && currentDir.Parent != null)
        {
            if (currentDir.Parent.FullName.Equals(resourcePackDir.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return currentDir.FullName;
            }
            currentDir = currentDir.Parent;
        }

        return null;
    }
}
