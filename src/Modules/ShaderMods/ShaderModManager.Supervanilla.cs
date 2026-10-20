using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.ShaderMods;

/// <summary>
/// Which Supervanilla RTX build a preset is. <see cref="IsPreview"/> comes from the folder the
/// preset sits in; the rest is read off its build name - the .rtpack's file name, recorded at
/// import, else the manifest name - because nothing inside the pack carries it.
/// </summary>
internal sealed record SupervanillaBuild(bool IsPreview, string? FileName, int? Release, string? Version)
{
    /// <summary>"R2 - 1.26.7020.0", or whichever half is known. Null when neither is.</summary>
    public string? Label => (Release, Version) switch
    {
        (int r, string v) => $"R{r} - {v}",
        (null, string v) => v,
        (int r, null) => $"R{r}",
        _ => null
    };
}

/// <summary>One build in an index.json. <see cref="Url"/> is as the server wrote it - site-relative today.</summary>
internal sealed record SupervanillaIndexEntry(string Name, string Url, string? Version, long? Size, string? Date)
{
    public SupervanillaBuild ToBuild(bool isPreview) =>
        new(isPreview, Name, ShaderModManager.ReleaseFromName(Name), Version ?? ShaderModManager.VersionFromFileName(Name));
}

internal sealed record SupervanillaIndex(SupervanillaIndexEntry? Latest, IReadOnlyList<SupervanillaIndexEntry> Files);

/// <summary>
/// The Supervanilla RTX provider: vanillartx.net's per-edition index.json, the Get latest download,
/// and the one distinction this provider needs that BetterRTX's doesn't - which edition a build is for.
///
/// <para><b>Why the edition matters here and not for BetterRTX.</b> bedrock.graphics publishes one
/// pool, built against Release. Supervanilla RTX publishes a Release channel and a Preview channel,
/// each moving with its own game, so a build belongs to one edition and each edition's update
/// retires only its own. That is the whole reason Supervanilla RTX presets sit in a folder per
/// edition (<see cref="SupervanillaFolderName"/>) - and the only reason: where a preset came from
/// (Get latest, the downloads page, a file from disk) is deliberately not recorded anywhere.</para>
/// </summary>
internal sealed partial class ShaderModManager
{
    #region Switches and addresses

    /// <summary>
    /// Whether the builds listed in index.json are drawn in the list. The index is fetched and cached
    /// either way, and the download path for an entry is complete - this gates only the rows. Off
    /// while Supervanilla RTX publishes one build per game version, which the Get latest row already
    /// covers; flip it the day the index carries variants worth choosing between.
    /// </summary>
    public const bool ShowSupervanillaIndexPresets = false;

    /// <summary>
    /// The site's two origins (<see cref="Helpers.VanillaRtxSiteOrigins"/>). Only the first is ever
    /// asked conditionally; see <see cref="FetchSupervanillaIndexAsync"/> for why.
    /// </summary>
    private static readonly string[] SupervanillaOrigins = Helpers.VanillaRtxSiteOrigins;

    /// <summary>
    /// The downloads page, in the minimal layout <c>?app</c> asks for, opened in the in-app
    /// browser. Not a setting, unlike BetterRTX's creator page: there is no other provider of
    /// Supervanilla RTX builds to point it at (CLAUDE.md §2h).
    /// </summary>
    public const string SupervanillaDownloadsPageUrl = "https://vanillartx.net/supervanilla-rtx-downloads?app";

    private static string ChannelPath(bool isPreview) =>
        $"/downloadables/supervanillartx/{(isPreview ? "preview" : "release")}";

    public static string EditionLabel(bool isPreview) => isPreview ? "Preview" : "Release";

    private static readonly TimeSpan SupervanillaRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SupervanillaIndexInterval = TimeSpan.FromHours(1);
    private const string SV_INDEX_LAST_FETCH_KEY = "ShaderModManager_SvrtxIndexLastFetch_";

    #endregion

    #region Cache layout and ownership

    /// <summary>
    /// Every Supervanilla RTX folder in the cache starts with this, and nothing else does - a
    /// BetterRTX folder is a bare UUID, a Default backup starts with two underscores. Every wipe keys
    /// off it, so it must never change without migrating what is already on users' disks.
    ///
    /// <para><b>It is a contract with a second repository.</b> The Supervanilla RTX factory
    /// (<c>scripts/vrs.py</c>, <c>app_folder</c>) builds dev presets straight into this cache as
    /// <c>SVRTX_&lt;Edition&gt;_svrtx-&lt;id&gt;</c>. Only the prefix and edition are read; what follows
    /// is any unique name, a UUID when the app writes it.</para>
    /// </summary>
    public const string SupervanillaFolderPrefix = "SVRTX_";

    /// <summary>
    /// Holds the one fact about a build that can't be recovered from the pack afterwards: the file
    /// name it arrived as, which is where its release number and version live. Not a manifest and
    /// not a .bin, so preset parsing never sees it.
    /// </summary>
    private const string BuildFileName = "svrtx_build.json";

    public static bool IsSupervanillaFolderName(string folderName) =>
        folderName.StartsWith(SupervanillaFolderPrefix, StringComparison.OrdinalIgnoreCase);

    private static string EditionFolderPrefix(bool isPreview) => $"{SupervanillaFolderPrefix}{EditionLabel(isPreview)}_";

    /// <summary>
    /// <c>SVRTX_Release_&lt;uuid&gt;</c> / <c>SVRTX_Preview_&lt;uuid&gt;</c>. Keyed by UUID like
    /// BetterRTX's folders - every build has its own, and bringing in one that shares a UUID replaces
    /// it in place, which is what BetterRTX does too.
    /// </summary>
    private static string SupervanillaFolderName(bool isPreview, string uuid) =>
        EditionFolderPrefix(isPreview) + SanitizePresetName(uuid);

    /// <summary>The edition a Supervanilla RTX folder belongs to, from its name; null for anything else.</summary>
    private static bool? EditionOfFolder(string folderName) =>
        folderName.StartsWith(EditionFolderPrefix(true), StringComparison.OrdinalIgnoreCase) ? true
        : folderName.StartsWith(EditionFolderPrefix(false), StringComparison.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>
    /// Whether a name says Supervanilla RTX - "Supervanilla", "SupervanillaRTX", "Supervanilla RTX"
    /// or "SVRTX", case-insensitively, anywhere in it.
    /// </summary>
    public static bool LooksLikeSupervanilla(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.Contains("supervanilla", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("svrtx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whose an .rtpack of unknown origin is. A Supervanilla RTX pack carries nothing a BetterRTX
    /// one does not - same files, same manifest shape - so its name is the only evidence there is.
    /// Either the file's name or the manifest's counts.
    /// </summary>
    public static PresetSource ClassifyImport(string? fileName, string? manifestName) =>
        LooksLikeSupervanilla(fileName) || LooksLikeSupervanilla(manifestName)
            ? PresetSource.Supervanilla
            : PresetSource.BetterRTX;

    /// <summary>
    /// Which edition an imported Supervanilla RTX build is for. Builds are named
    /// <c>Supervanilla-RTX-Preview-R1-…</c> for Preview and <c>Supervanilla-RTX-R1-…</c> for
    /// Release, and that convention is the only evidence there is, so "preview" anywhere in the file
    /// or manifest name decides it. Fragile by nature, and accepted: the naming is ours and fixed.
    /// </summary>
    public static bool IsPreviewBuildName(string? fileName, string? manifestName) =>
        (fileName?.Contains("preview", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (manifestName?.Contains("preview", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Whose a folder already in the cache is - decided entirely by its name.</summary>
    public static PresetSource ClassifyFolder(string folderName) =>
        IsSupervanillaFolderName(folderName) ? PresetSource.Supervanilla : PresetSource.BetterRTX;

    /// <summary>
    /// Deletes every Supervanilla RTX build held for one edition - however it arrived. The other
    /// edition's builds are a different game's and are never collateral.
    /// </summary>
    public void WipeSupervanillaPresets(bool isPreview)
    {
        var prefix = EditionFolderPrefix(isPreview);
        int deleted = DeleteCacheFolders(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "[Supervanilla]");
        Trace.WriteLine($"[ShaderMods] [Supervanilla] Wiped {deleted} {EditionLabel(isPreview)} build(s)");
    }

    #endregion

    #region Build names

    // "Supervanilla-RTX-R1-1.26.7020.0" and "Supervanilla-RTX-v1.26.40.56" alike: the last dotted run
    // of two or more numbers in the name, with or without a leading v.
    private static readonly Regex FileVersionPattern = new(@"v?(\d+(?:\.\d+)+)(?!.*\d+\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The R in "Supervanilla-RTX-Preview-R2-1.26.7020.0": an R and digits standing on their own
    // between separators, so the R of "RTX" never matches.
    private static readonly Regex ReleasePattern = new(@"(?:^|[-_ ])R(\d+)(?=$|[-_ ])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? VersionFromFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var match = FileVersionPattern.Match(StripRtpack(name));
        return match.Success ? match.Groups[1].Value : null;
    }

    public static int? ReleaseFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var match = ReleasePattern.Match(StripRtpack(name));
        return match.Success && int.TryParse(match.Groups[1].Value, out var r) ? r : null;
    }

    // Only a known extension is stripped: Path.GetFileNameWithoutExtension on a manifest name such
    // as "Supervanilla RTX 1.26.7020.0" would take ".0" off the version.
    private static string StripRtpack(string name) =>
        name.EndsWith(".rtpack", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

    /// <summary>
    /// Compares two dotted versions part by part, a missing trailing part counting as 0, so
    /// 1.26.50.25.0 and 1.26.50.25 are equal and 1.26.40.56 is above 1.26.30. Null when either side
    /// isn't a version at all - "can't tell" must stay distinguishable from "equal".
    /// </summary>
    public static int? CompareVersions(string? a, string? b)
    {
        static long[]? Parse(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var parts = v.Trim().TrimStart('v', 'V').Split('.');
            var result = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], out result[i])) return null;
            return result;
        }

        var x = Parse(a);
        var y = Parse(b);
        if (x is null || y is null) return null;

        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>
    /// Orders builds: game version first, release number within it - so R2 of a version sits above
    /// R1 of it, and anything of a newer version above both. A missing release number counts as 0.
    /// Null when the versions can't be compared.
    /// </summary>
    public static int? CompareBuilds(SupervanillaBuild a, SupervanillaBuild b) =>
        CompareVersions(a.Version, b.Version) switch
        {
            0 => (a.Release ?? 0).CompareTo(b.Release ?? 0),
            var c => c
        };

    #endregion

    #region The sidecar

    /// <summary>
    /// What is known about the build in one Supervanilla RTX folder: its edition from the folder
    /// name, its release and version from the recorded file name, else from the manifest name.
    /// </summary>
    private static SupervanillaBuild ReadSupervanillaBuild(string folder, string? manifestName)
    {
        string? fileName = null;
        try
        {
            var path = Path.Combine(folder, BuildFileName);
            if (File.Exists(path))
                fileName = MinecraftJson.GetString(MinecraftJson.ParseObjectFile(path)?["file"]);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Unreadable sidecar in {folder}: {ex.Message}");
        }

        var source = fileName ?? manifestName;
        return new SupervanillaBuild(
            EditionOfFolder(Path.GetFileName(folder)) ?? false,
            fileName,
            ReleaseFromName(source) ?? ReleaseFromName(manifestName),
            VersionFromFileName(source) ?? VersionFromFileName(manifestName));
    }

    private static void WriteSupervanillaBuild(string folder, string fileName)
    {
        try
        {
            // The non-generic JsonValue.Create - the generic one reflects, which a trimmed Release
            // build strips (CLAUDE.md §6).
            MinecraftJson.WriteIndented(Path.Combine(folder, BuildFileName), new JsonObject { ["file"] = JsonValue.Create(fileName) });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Couldn't record the build name in {folder}: {ex.Message}");
        }
    }

    #endregion

    #region State

    /// <summary>Every Supervanilla RTX preset in the cache, both editions' - see <see cref="LoadLocalPresetsAsync"/>.</summary>
    public List<LocalPresetData>? SupervanillaPresets { get; private set; }

    /// <summary>The attached edition's index, as last cached. Null until one has ever been fetched.</summary>
    public SupervanillaIndex? CurrentSupervanillaIndex { get; private set; }

    /// <summary>This edition's cached index.json. One per edition: they are two different files on the server.</summary>
    public string SupervanillaIndexCachePath { get; private set; } = string.Empty;

    private static string SupervanillaIndexCacheFileName(bool isPreview) =>
        $"svrtx_index_{EditionLabel(isPreview).ToLowerInvariant()}.json";

    /// <summary>The attached edition's builds, newest first (<see cref="CompareBuilds"/>).</summary>
    public List<LocalPresetData> HeldSupervanillaBuilds()
    {
        var held = (SupervanillaPresets ?? new List<LocalPresetData>())
            .Where(p => p.Supervanilla?.IsPreview == IsPreview)
            .ToList();

        // Builds whose version can't be read sort below those whose can, then by name - a stable
        // order rather than whatever the comparer happens to do with an incomparable pair.
        var readable = held.Where(p => CompareVersions(p.Supervanilla!.Version, "0") is not null).ToList();
        readable.Sort((a, b) => -(CompareBuilds(a.Supervanilla!, b.Supervanilla!) ?? 0));
        var rest = SmartPresetSorter.SafeOrderByName(held.Except(readable), p => p.Name);
        return readable.Concat(rest).ToList();
    }

    /// <summary>
    /// Whether this edition already holds the index's latest build, or something at least as new.
    /// False when there is no index to ask, so the Get latest row keeps offering itself - it asks
    /// /latest directly and never needed the index to work.
    /// </summary>
    public bool HoldsSupervanillaLatest()
    {
        if (CurrentSupervanillaIndex?.Latest is not { } latest) return false;
        var latestBuild = latest.ToBuild(IsPreview);

        return HeldSupervanillaBuilds().Any(p =>
            string.Equals(p.Supervanilla!.FileName, latest.Name, StringComparison.OrdinalIgnoreCase) ||
            CompareBuilds(p.Supervanilla!, latestBuild) >= 0);
    }

    /// <summary>Whether this edition holds one particular index build.</summary>
    public bool HoldsSupervanillaBuild(SupervanillaIndexEntry entry) =>
        HeldSupervanillaBuilds().Any(p =>
            string.Equals(p.Supervanilla!.FileName, entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Download-queue keys. The <c>svrtx:</c> prefix is what keeps them clear of BetterRTX's UUIDs.</summary>
    public static string LatestDownloadKey(bool isPreview) => $"svrtx:latest:{EditionLabel(isPreview)}";

    public static string IndexDownloadKey(bool isPreview, string fileName) => $"svrtx:index:{EditionLabel(isPreview)}:{fileName}";

    #endregion

    #region The index

    /// <summary>
    /// Brings this edition's index up to date. Fetches when <paramref name="force"/> is set, when an
    /// hour has passed since the last answer, or when nothing is cached; otherwise uses the cached
    /// copy. Returns whether the server answered this time - a 304 counts.
    ///
    /// <para><b>A new build appearing deletes nothing.</b> All it changes is
    /// <see cref="HoldsSupervanillaLatest"/>, which brings the Get latest row back, offering it. The
    /// builds already held stay installable until this edition's game updates or Refresh is pressed
    /// - the two events that do mean "these are stale". Nothing is wiped when the server can't be
    /// reached either; a stale index still describes builds that exist.</para>
    /// </summary>
    public async Task<bool> RefreshSupervanillaIndexAsync(bool force)
    {
        var stampKey = SV_INDEX_LAST_FETCH_KEY + EditionLabel(IsPreview);
        bool answered = false;

        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            bool due = force ||
                !(settings.Values.TryGetValue(stampKey, out var raw) && raw is long ticks &&
                  DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < SupervanillaIndexInterval) ||
                !File.Exists(SupervanillaIndexCachePath);

            if (due)
            {
                Trace.WriteLine($"[ShaderMods] [Supervanilla] Checking the {EditionLabel(IsPreview)} index{(force ? " (forced)" : "")}...");
                answered = await FetchSupervanillaIndexAsync();
                if (answered) settings.Values[stampKey] = DateTime.UtcNow.Ticks;
            }
            else
            {
                Trace.WriteLine("[ShaderMods] [Supervanilla] Index checked within the hour - using the cached copy");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Index check failed: {ex.Message}");
        }

        CurrentSupervanillaIndex = LoadCachedSupervanillaIndex();
        return answered;
    }

    /// <summary>
    /// What the refresh button asks of this provider: a fresh look at the index, and only if the
    /// server answered, clearing this edition's builds so the next ones come straight from the site.
    /// The same fetch-first order as <see cref="RebuildFromApiAsync"/>, for the same reason.
    /// </summary>
    public async Task<bool> RebuildSupervanillaAsync()
    {
        if (!await RefreshSupervanillaIndexAsync(force: true))
        {
            Trace.WriteLine("[ShaderMods] [Supervanilla] [Refresh] Site unreachable - nothing cleared");
            return false;
        }

        WipeSupervanillaPresets(IsPreview);
        return true;
    }

    /// <summary>
    /// Fetches this edition's index.json into the cache. True when the server answered with
    /// something usable - a new index, or a 304 for the one held.
    ///
    /// <para><b>Only the first origin is asked conditionally.</b> A validator describes the bytes in
    /// the cache, and those may have come from either origin; a fallback asked with its own stale
    /// validator could answer 304 for a copy the cache no longer holds. So the fallback is always
    /// asked for the full body, and when it supplies one the first origin's validator is dropped
    /// too, since the cache has just moved on without it.</para>
    /// </summary>
    private async Task<bool> FetchSupervanillaIndexAsync()
    {
        bool cacheExists = File.Exists(SupervanillaIndexCachePath);

        for (int i = 0; i < SupervanillaOrigins.Length; i++)
        {
            var url = SupervanillaOrigins[i] + ChannelPath(IsPreview) + "/index.json";
            bool conditional = i == 0 && cacheExists;

            // A validator with no cache behind it would make the server answer 304 for nothing.
            if (!conditional) Helpers.SetValidator(url, null);

            var result = await Helpers.GetStringConditionalAsync(url, SupervanillaRequestTimeout);

            if (result.Status == FetchStatus.NotModified)
            {
                Trace.WriteLine($"[ShaderMods] [Supervanilla] ✓ Index unchanged (304) from {SupervanillaOrigins[i]}");
                return true;
            }

            if (result.Status == FetchStatus.Modified && ParseSupervanillaIndex(result.Body) is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(SupervanillaIndexCachePath, result.Body);

                    // Stored only once the body it describes is on disk (CLAUDE.md §2l).
                    if (i == 0) Helpers.SetValidator(url, result.ETag);
                    else Helpers.SetValidator(SupervanillaOrigins[0] + ChannelPath(IsPreview) + "/index.json", null);

                    Trace.WriteLine($"[ShaderMods] [Supervanilla] ✓ Index updated from {SupervanillaOrigins[i]}");
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ShaderMods] [Supervanilla] Couldn't cache the index: {ex.Message}");
                    return false;
                }
            }

            // A body that isn't an index is what a lapsed domain's parking page looks like, so it
            // gets the fallback the same as a connection that never opened.
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ⚠ No usable index from {SupervanillaOrigins[i]} ({result.Status})");
        }

        return false;
    }

    private SupervanillaIndex? LoadCachedSupervanillaIndex()
    {
        try
        {
            return File.Exists(SupervanillaIndexCachePath)
                ? ParseSupervanillaIndex(File.ReadAllText(SupervanillaIndexCachePath))
                : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Couldn't read the cached index: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads an index.json as nodes - the site is ours, but it is still a file from the network, and
    /// an entry of an unexpected shape should cost that entry rather than the whole index. Null when
    /// the document isn't an index at all.
    /// </summary>
    private static SupervanillaIndex? ParseSupervanillaIndex(string? json)
    {
        var root = MinecraftJson.ParseObject(json);
        if (root is null) return null;

        static SupervanillaIndexEntry? Entry(JsonNode? node)
        {
            if (node is not JsonObject o) return null;
            var name = MinecraftJson.GetString(o["name"]);
            var url = MinecraftJson.GetString(o["url"]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return null;

            return new SupervanillaIndexEntry(name, url,
                MinecraftJson.GetString(o["version"]),
                MinecraftJson.GetInt(o["size"]),
                MinecraftJson.GetString(o["date"]));
        }

        var files = (root["files"] as JsonArray)?.Select(Entry).OfType<SupervanillaIndexEntry>().ToList() ?? [];
        var latest = Entry(root["latest"]);

        return latest is null && files.Count == 0 ? null : new SupervanillaIndex(latest, files);
    }

    #endregion

    #region Downloads

    /// <summary>
    /// A client that does not follow redirects, for reading where <c>/latest</c> points without
    /// downloading it. The 302 is what names the build - Helpers.Download follows it silently and
    /// would leave the file called "latest" - and it is a header-only response the edge caches.
    /// </summary>
    private static readonly HttpClient RedirectProbeClient = CreateRedirectProbeClient();

    private static HttpClient CreateRedirectProbeClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("User-Agent", Helpers.BuildUserAgent());
        return client;
    }

    /// <summary>
    /// The Get latest button: downloads whatever this edition's <c>/latest</c> points at. Independent
    /// of the index - it works with the index unreachable, never fetched, or switched off. The build
    /// is filed under the edition being targeted, whatever its name says: this edition's channel is
    /// the authority on what this edition's build is.
    /// </summary>
    public async Task<bool> DownloadSupervanillaLatestAsync(CancellationToken cancellationToken)
    {
        foreach (var origin in SupervanillaOrigins)
        {
            var fileUrl = await ResolveLatestAsync(origin, cancellationToken);
            if (fileUrl is null) continue;

            if (await DownloadSupervanillaFileAsync(fileUrl, cancellationToken)) return true;
            if (cancellationToken.IsCancellationRequested) return false;
        }

        return false;
    }

    /// <summary>
    /// Downloads one build listed in the index. The whole path is live even while
    /// <see cref="ShowSupervanillaIndexPresets"/> keeps the rows that would call it hidden.
    /// </summary>
    public async Task<bool> DownloadSupervanillaIndexEntryAsync(SupervanillaIndexEntry entry, CancellationToken cancellationToken)
    {
        foreach (var origin in SupervanillaOrigins)
        {
            if (!Uri.TryCreate(new Uri(origin), entry.Url, out var fileUri)) continue;

            if (await DownloadSupervanillaFileAsync(fileUri.AbsoluteUri, cancellationToken)) return true;
            if (cancellationToken.IsCancellationRequested) return false;
        }

        return false;
    }

    /// <summary>
    /// Where <c>/latest</c> points on one origin, as an absolute file URL, or null when that origin
    /// gave no usable answer. A 200 instead of a redirect is accepted as the file itself, so the site
    /// can stop redirecting without breaking this.
    /// </summary>
    private async Task<string?> ResolveLatestAsync(string origin, CancellationToken cancellationToken)
    {
        var latestUrl = origin + ChannelPath(IsPreview) + "/latest";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SupervanillaRequestTimeout);

            using var response = await RedirectProbeClient.GetAsync(latestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var resolved = location.IsAbsoluteUri ? location : new Uri(new Uri(latestUrl), location);
                Trace.WriteLine($"[ShaderMods] [Supervanilla] /latest ({EditionLabel(IsPreview)}) -> {resolved.AbsoluteUri}");
                return resolved.AbsoluteUri;
            }

            if (response.IsSuccessStatusCode) return latestUrl;

            Trace.WriteLine($"[ShaderMods] [Supervanilla] ⚠ /latest answered {status} on {origin}");
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ⚠ /latest unreachable on {origin}: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads one .rtpack into this edition's builds, through the same staging every import uses:
    /// a folder already holding that UUID is only replaced once the new one has been unpacked and
    /// found to be a preset, so a bad download never costs the good copy.
    /// </summary>
    private async Task<bool> DownloadSupervanillaFileAsync(string url, CancellationToken cancellationToken)
    {
        string? downloadedPath = null;
        string? stagingFolder = null;
        try
        {
            var fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));

            Trace.WriteLine($"[ShaderMods] [Supervanilla] Downloading: {url}");
            var (success, path) = await Helpers.Download(url, cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(3));
            downloadedPath = path;

            if (!success || string.IsNullOrEmpty(path))
            {
                Trace.WriteLine("[ShaderMods] [Supervanilla] ✗ Download failed");
                return false;
            }

            stagingFolder = await ExtractToStagingAsync(path, "[Supervanilla]");

            var parsed = await ParseLocalPresetAsync(stagingFolder);
            if (parsed == null || string.IsNullOrEmpty(parsed.Uuid))
            {
                Trace.WriteLine($"[ShaderMods] [Supervanilla] ✗ Downloaded file is not a valid preset: {url}");
                return false;
            }

            var destination = Path.Combine(CacheFolder, SupervanillaFolderName(IsPreview, parsed.Uuid));
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);

            Directory.Move(stagingFolder, destination);
            stagingFolder = null;

            WriteSupervanillaBuild(destination, fileName);
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ✓ \"{parsed.Name}\" ({fileName}) in place at {destination}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ✗ Error downloading {url}: {ex.Message}");
            return false;
        }
        finally
        {
            if (stagingFolder != null)
            {
                try { Directory.Delete(stagingFolder, true); } catch { }
            }
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                try { File.Delete(downloadedPath); } catch { }
            }
        }
    }

    #endregion
}
