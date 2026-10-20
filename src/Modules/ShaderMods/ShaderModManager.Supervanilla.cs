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

/// <summary>How a Supervanilla RTX folder came to be in the cache, which decides what may retire it.</summary>
internal enum SupervanillaKind
{
    /// <summary>The Get latest button's download - one per edition, replaced in place.</summary>
    Latest,

    /// <summary>A specific build listed in that edition's index.json.</summary>
    Index,

    /// <summary>An .rtpack brought in by hand, or from the downloads page in the in-app browser.</summary>
    Import
}

/// <summary>
/// What the sidecar beside a Supervanilla RTX preset records about it. <see cref="IsPreview"/> is
/// null for an import: nothing says which game an .rtpack from disk was built for, so it is shown
/// to both, the way every BetterRTX preset is.
/// </summary>
internal sealed record SupervanillaOrigin(
    SupervanillaKind Kind, bool? IsPreview, string? Version, string? FileName, long? Size, string? Date);

/// <summary>One build in an index.json. <see cref="Url"/> is as the server wrote it - site-relative today.</summary>
internal sealed record SupervanillaIndexEntry(string Name, string Url, string? Version, long? Size, string? Date)
{
    /// <summary>The build's version: the index's own field, else whatever the file name carries.</summary>
    public string? EffectiveVersion => Version ?? ShaderModManager.VersionFromFileName(Name);
}

internal sealed record SupervanillaIndex(SupervanillaIndexEntry? Latest, IReadOnlyList<SupervanillaIndexEntry> Files);

/// <summary>
/// The Supervanilla RTX provider: vanillartx.net's per-edition index.json, the Get latest download,
/// and the rules for when either retires something already in the cache.
///
/// <para><b>Why it is shaped differently from BetterRTX.</b> bedrock.graphics hosts many distinct
/// presets and versions them on its own schedule; Supervanilla RTX is one preset that moves forward
/// with the game, published per edition. So identity here is a <i>build</i> (a file, a version)
/// rather than a UUID, the Release and Preview channels are separate files rather than one shared
/// set, and "the API changed, wipe everything" becomes "a newer build exists, retire the one held".</para>
///
/// <para><b>Ownership is the folder name</b> (<see cref="SupervanillaFolderPrefix"/>), so every wipe
/// in the cache can tell whose a folder is without opening it. Each folder also carries a sidecar
/// (<see cref="OriginFileName"/>) saying which kind it is, which edition it is for and which build -
/// the facts the reconcile and the list both need.</para>
/// </summary>
internal sealed partial class ShaderModManager
{
    #region Switches and addresses

    /// <summary>
    /// Whether the builds listed in index.json are drawn in the list. The index is fetched, cached
    /// and reconciled either way, and the download path for an entry is complete - this gates only
    /// the rows. Off while Supervanilla RTX publishes one build per game version, which the Get
    /// latest row already covers; flip it the day the index carries variants worth choosing between.
    /// </summary>
    public const bool ShowSupervanillaIndexPresets = false;

    /// <summary>
    /// Where the site lives, in order of preference. Both are the same Cloudflare deployment, both
    /// edge-cached (<c>Cache-Control: max-age=300</c>, identical ETags - measured), so the second is
    /// purely the way through when the domain itself stops resolving. Only the first is ever asked
    /// conditionally; see <see cref="FetchSupervanillaIndexAsync"/> for why.
    /// </summary>
    private static readonly string[] SupervanillaOrigins =
    [
        "https://vanillartx.net",
        "https://vanilla-rtx-website.cubeir.workers.dev"
    ];

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
    /// BetterRTX folder is a UUID, a Default backup starts with two underscores. Every wipe keys off
    /// it, so it must never change without migrating what is already on users' disks.
    /// </summary>
    public const string SupervanillaFolderPrefix = "SVRTX_";

    /// <summary>The sidecar written into every Supervanilla RTX folder. Not a manifest and not a .bin, so preset parsing never sees it.</summary>
    private const string OriginFileName = "svrtx_origin.json";

    private const string ImportInfix = "Import_";

    public static bool IsSupervanillaFolderName(string folderName) =>
        folderName.StartsWith(SupervanillaFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The prefix of every folder that is one edition's own download - Get latest and index builds alike.</summary>
    private static string EditionFolderPrefix(bool isPreview) => $"{SupervanillaFolderPrefix}{EditionLabel(isPreview)}_";

    private static string LatestFolderName(bool isPreview) => $"{EditionFolderPrefix(isPreview)}Latest";

    private static string IndexFolderName(bool isPreview, string fileName) =>
        $"{EditionFolderPrefix(isPreview)}Index_{SanitizePresetName(Path.GetFileNameWithoutExtension(fileName))}";

    /// <summary>
    /// Named after the file rather than the manifest UUID: successive builds may share a UUID, and a
    /// UUID-named folder would let importing one build silently replace another. Importing the same
    /// file twice still replaces it in place.
    /// </summary>
    private static string SupervanillaImportFolderName(string fileName) =>
        $"{SupervanillaFolderPrefix}{ImportInfix}{SanitizePresetName(Path.GetFileNameWithoutExtension(fileName))}";

    /// <summary>
    /// Whether a name says Supervanilla RTX. Covers every spelling the user asked to be recognised -
    /// "Supervanilla", "SupervanillaRTX", "Supervanilla RTX", "SVRTX" - case-insensitively, anywhere
    /// in the name, since a file name and a manifest name both put it in different places.
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

    /// <summary>Whose a folder already in the cache is - decided entirely by its name.</summary>
    public static PresetSource ClassifyFolder(string folderName) =>
        IsSupervanillaFolderName(folderName) ? PresetSource.Supervanilla : PresetSource.BetterRTX;

    /// <summary>
    /// Moves a Supervanilla RTX import that predates ownership-by-folder-name - a UUID-named folder
    /// whose manifest name says Supervanilla RTX - under the prefix it would be imported with today.
    /// Without this every wipe would take it for BetterRTX's. Idempotent: once moved there is
    /// nothing left for it to match.
    /// </summary>
    private static void AdoptLegacySupervanillaImports(string cacheFolder)
    {
        try
        {
            foreach (var folder in Directory.GetDirectories(cacheFolder))
            {
                var name = Path.GetFileName(folder);
                if (IsDefaultFolderName(name) || IsSupervanillaFolderName(name) ||
                    name.StartsWith("__staging_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var manifestPath = Directory.EnumerateFiles(folder, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
                var manifestName = manifestPath is null ? null : PackManifest.FromFile(manifestPath)?.HeaderName;
                if (!LooksLikeSupervanilla(manifestName)) continue;

                var destination = Path.Combine(cacheFolder, $"{SupervanillaFolderPrefix}{ImportInfix}{name}");
                if (Directory.Exists(destination)) continue;

                Directory.Move(folder, destination);
                WriteSupervanillaOrigin(destination, new SupervanillaOrigin(
                    SupervanillaKind.Import, IsPreview: null, ManifestVersion(destination), FileName: null, Size: null, Date: null));
                Trace.WriteLine($"[ShaderMods] [Supervanilla] Adopted legacy import \"{manifestName}\" as {Path.GetFileName(destination)}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Legacy import adoption failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes one edition's Supervanilla RTX downloads (Get latest and index builds), and every
    /// import as well when <paramref name="includeImports"/> is set. Imports have no edition, so
    /// only a wipe meant to clear the shared cache may take them.
    /// </summary>
    public void WipeSupervanillaPresets(bool isPreview, bool includeImports)
    {
        var editionPrefix = EditionFolderPrefix(isPreview);
        var importPrefix = SupervanillaFolderPrefix + ImportInfix;

        int deleted = DeleteCacheFolders(name =>
            name.StartsWith(editionPrefix, StringComparison.OrdinalIgnoreCase) ||
            (includeImports && name.StartsWith(importPrefix, StringComparison.OrdinalIgnoreCase)),
            "[Supervanilla]");

        Trace.WriteLine($"[ShaderMods] [Supervanilla] Wiped {deleted} folder(s) - {EditionLabel(isPreview)} downloads{(includeImports ? " and every import" : "")}");
    }

    #endregion

    #region The sidecar

    public static SupervanillaOrigin? ReadSupervanillaOrigin(string folder)
    {
        try
        {
            var path = Path.Combine(folder, OriginFileName);
            if (!File.Exists(path)) return null;

            var root = MinecraftJson.ParseObjectFile(path);
            if (root is null) return null;

            // Spelled out rather than Enum.TryParse: this is a file format, and the names in it
            // must not move just because an enum member is renamed.
            SupervanillaKind? kind = MinecraftJson.GetString(root["kind"])?.ToLowerInvariant() switch
            {
                "latest" => SupervanillaKind.Latest,
                "index" => SupervanillaKind.Index,
                "import" => SupervanillaKind.Import,
                _ => null
            };
            if (kind is null) return null;

            string? edition = MinecraftJson.GetString(root["edition"]);
            bool? isPreview = edition?.ToLowerInvariant() switch { "preview" => true, "release" => false, _ => null };

            return new SupervanillaOrigin(
                kind.Value, isPreview,
                MinecraftJson.GetString(root["version"]),
                MinecraftJson.GetString(root["file"]),
                MinecraftJson.GetInt(root["size"]),
                MinecraftJson.GetString(root["date"]));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Unreadable sidecar in {folder}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// What a Supervanilla RTX folder's name alone says about it, for one whose sidecar is missing
    /// or unreadable - a write that failed, or a folder from a crash between the move and the
    /// write. Kind and edition are recoverable from the layout; the build's version is not.
    /// </summary>
    public static SupervanillaOrigin InferSupervanillaOrigin(string folderName)
    {
        foreach (var isPreview in new[] { false, true })
        {
            var prefix = EditionFolderPrefix(isPreview);
            if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var kind = folderName.Equals(LatestFolderName(isPreview), StringComparison.OrdinalIgnoreCase)
                ? SupervanillaKind.Latest
                : SupervanillaKind.Index;
            return new SupervanillaOrigin(kind, isPreview, null, null, null, null);
        }

        return new SupervanillaOrigin(SupervanillaKind.Import, null, null, null, null, null);
    }

    private static void WriteSupervanillaOrigin(string folder, SupervanillaOrigin origin)
    {
        try
        {
            // Non-generic JsonValue.Create overloads throughout - the generic one reflects, which a
            // trimmed Release build strips (CLAUDE.md §6).
            var root = new JsonObject
            {
                ["kind"] = JsonValue.Create(origin.Kind switch
                {
                    SupervanillaKind.Latest => "latest",
                    SupervanillaKind.Index => "index",
                    _ => "import"
                }),
                ["edition"] = origin.IsPreview is bool p ? JsonValue.Create(p ? "preview" : "release") : null,
                ["version"] = JsonValue.Create(origin.Version),
                ["file"] = JsonValue.Create(origin.FileName),
                ["size"] = origin.Size is long size ? JsonValue.Create(size) : null,
                ["date"] = JsonValue.Create(origin.Date)
            };
            MinecraftJson.WriteIndented(Path.Combine(folder, OriginFileName), root);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] Couldn't write sidecar in {folder}: {ex.Message}");
        }
    }

    #endregion

    #region Versions

    // "Supervanilla-RTX-v1.26.40.56.rtpack" and "Supervanilla-RTX-1.26.30.rtpack" alike: the last
    // dotted run of two or more numbers in the name, with or without a leading v.
    private static readonly Regex FileVersionPattern = new(@"v?(\d+(?:\.\d+)+)(?!.*\d+\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? VersionFromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var match = FileVersionPattern.Match(Path.GetFileNameWithoutExtension(fileName));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The manifest's own header version, dotted - the fallback when no file name carries one.</summary>
    private static string? ManifestVersion(string presetFolder)
    {
        try
        {
            var manifestPath = Directory.EnumerateFiles(presetFolder, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
            return manifestPath is null ? null : PackManifest.FromFile(manifestPath)?.VersionDisplay;
        }
        catch { return null; }
    }

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
    /// Whether the index's latest build is newer than the one held. Strictly newer only: the site
    /// moves forward with the game, so an index whose latest is <i>older</i> than what was downloaded
    /// is the index lagging behind /latest (which this cache can be up to an hour behind), never a
    /// reason to take the newer build away. When either version can't be read, a different file
    /// name is the only evidence left, and is taken as a new build.
    /// </summary>
    private static bool IsNewerBuild(SupervanillaIndexEntry latest, SupervanillaOrigin held) =>
        CompareVersions(latest.EffectiveVersion, held.Version) switch
        {
            int c => c > 0,
            null => !string.Equals(latest.Name, held.FileName, StringComparison.OrdinalIgnoreCase)
        };

    #endregion

    #region State

    /// <summary>Every Supervanilla RTX folder in the cache, both editions' - see <see cref="LoadLocalPresetsAsync"/>.</summary>
    public List<LocalPresetData>? SupervanillaPresets { get; private set; }

    /// <summary>The attached edition's index, as last cached. Null until one has ever been fetched.</summary>
    public SupervanillaIndex? CurrentSupervanillaIndex { get; private set; }

    /// <summary>This edition's cached index.json. One per edition: they are two different files on the server.</summary>
    public string SupervanillaIndexCachePath { get; private set; } = string.Empty;

    private static string SupervanillaIndexCacheFileName(bool isPreview) =>
        $"svrtx_index_{EditionLabel(isPreview).ToLowerInvariant()}.json";

    /// <summary>The Get latest download held for the attached edition, if any.</summary>
    public LocalPresetData? HeldSupervanillaLatest =>
        SupervanillaPresets?.FirstOrDefault(p =>
            p.Supervanilla is { Kind: SupervanillaKind.Latest } o && o.IsPreview == IsPreview);

    /// <summary>The attached edition's download of one index build, if held.</summary>
    public LocalPresetData? HeldSupervanillaIndexBuild(SupervanillaIndexEntry entry) =>
        SupervanillaPresets?.FirstOrDefault(p =>
            p.Supervanilla is { Kind: SupervanillaKind.Index } o && o.IsPreview == IsPreview &&
            string.Equals(o.FileName, entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Download-queue keys. The <c>svrtx:</c> prefix is what keeps them clear of BetterRTX's UUIDs.</summary>
    public static string LatestDownloadKey(bool isPreview) => $"svrtx:latest:{EditionLabel(isPreview)}";

    public static string IndexDownloadKey(bool isPreview, string fileName) => $"svrtx:index:{EditionLabel(isPreview)}:{fileName}";

    #endregion

    #region The index

    /// <summary>
    /// Brings this edition's index up to date and retires whatever it shows to be superseded.
    /// Fetches when <paramref name="force"/> is set or an hour has passed since the last answer;
    /// otherwise works from the cached copy. Returns whether the server answered this time - a 304
    /// counts, since it confirms the cached copy is current.
    ///
    /// <para><b>The reconcile runs every time, cached index or fresh.</b> It is cheap, idempotent,
    /// and it is the only thing that notices a Get latest download the index has since moved past
    /// - which can happen across opens without the index itself having been re-fetched.</para>
    ///
    /// <para><b>Nothing is wiped when the server can't be reached.</b> The cached index, however
    /// old, still describes builds that exist; an error card is worse than a stale answer.</para>
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
        ReconcileSupervanillaDownloads();
        return answered;
    }

    /// <summary>
    /// What the refresh button asks of this provider: a fresh look at the index, and only if the
    /// server answered, clearing this edition's downloads and every Supervanilla RTX import so the
    /// next ones come straight from the site. The same fetch-first order as
    /// <see cref="RebuildFromApiAsync"/>, for the same reason.
    /// </summary>
    public async Task<bool> RebuildSupervanillaAsync()
    {
        if (!await RefreshSupervanillaIndexAsync(force: true))
        {
            Trace.WriteLine("[ShaderMods] [Supervanilla] [Refresh] Site unreachable - nothing cleared");
            return false;
        }

        WipeSupervanillaPresets(IsPreview, includeImports: true);
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

    /// <summary>
    /// Retires this edition's downloads that the index shows to be superseded:
    ///
    /// <list type="bullet">
    /// <item>the Get latest download, when the index's latest is a newer build
    /// (<see cref="IsNewerBuild"/>) - so the row goes back to offering the newest one;</item>
    /// <item>an index build that the index no longer lists, or now lists differently - same name
    /// but another size, date or version is a re-upload, and the held copy is not it.</item>
    /// </list>
    ///
    /// A folder whose sidecar can't be read can't be checked, and is retired: it will be downloaded
    /// again, which is the outcome a check would have reached if it had found anything wrong.
    /// </summary>
    private void ReconcileSupervanillaDownloads()
    {
        var index = CurrentSupervanillaIndex;
        if (index is null || !Directory.Exists(CacheFolder)) return;

        var latestFolder = Path.Combine(CacheFolder, LatestFolderName(IsPreview));
        if (Directory.Exists(latestFolder) && index.Latest is { } latest)
        {
            var held = ReadSupervanillaOrigin(latestFolder);
            if (held is null || IsNewerBuild(latest, held))
            {
                Trace.WriteLine($"[ShaderMods] [Supervanilla] Newer {EditionLabel(IsPreview)} build in the index ({latest.EffectiveVersion}, held {held?.Version ?? "unknown"}) - retiring the held one");
                TryDeleteFolder(latestFolder);
            }
        }

        var indexPrefix = EditionFolderPrefix(IsPreview) + "Index_";
        foreach (var folder in Directory.GetDirectories(CacheFolder, indexPrefix + "*"))
        {
            var held = ReadSupervanillaOrigin(folder);
            var listed = held is null ? null : index.Files.FirstOrDefault(f =>
                string.Equals(f.Name, held.FileName, StringComparison.OrdinalIgnoreCase));

            bool current = listed is not null &&
                listed.Size == held!.Size &&
                string.Equals(listed.Date, held.Date, StringComparison.Ordinal) &&
                string.Equals(listed.EffectiveVersion, held.Version, StringComparison.Ordinal);

            if (!current)
            {
                Trace.WriteLine($"[ShaderMods] [Supervanilla] {Path.GetFileName(folder)} is no longer what the index lists - retiring it");
                TryDeleteFolder(folder);
            }
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (Exception ex) { Trace.WriteLine($"[ShaderMods] [Supervanilla] Couldn't delete {folder}: {ex.Message}"); }
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
    /// The Get latest button: downloads whatever this edition's <c>/latest</c> points at into the
    /// edition's one Latest folder, replacing what was there. Independent of the index - it works
    /// with the index unreachable, never fetched, or switched off.
    /// </summary>
    public async Task<bool> DownloadSupervanillaLatestAsync(CancellationToken cancellationToken)
    {
        foreach (var origin in SupervanillaOrigins)
        {
            var fileUrl = await ResolveLatestAsync(origin, cancellationToken);
            if (fileUrl is null) continue;

            var fileName = FileNameFromUrl(fileUrl);

            // The index may know this build's version and size better than its file name does.
            var listed = CurrentSupervanillaIndex?.Files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
                         ?? (string.Equals(CurrentSupervanillaIndex?.Latest?.Name, fileName, StringComparison.OrdinalIgnoreCase) ? CurrentSupervanillaIndex!.Latest : null);

            var ok = await DownloadSupervanillaFileAsync(fileUrl, LatestFolderName(IsPreview), folder =>
                new SupervanillaOrigin(SupervanillaKind.Latest, IsPreview,
                    listed?.EffectiveVersion ?? VersionFromFileName(fileName) ?? ManifestVersion(folder),
                    fileName, listed?.Size, listed?.Date),
                cancellationToken);

            if (ok) return true;
            if (cancellationToken.IsCancellationRequested) return false;
        }

        return false;
    }

    /// <summary>
    /// Downloads one build listed in the index into its own folder. The whole path is live even
    /// while <see cref="ShowSupervanillaIndexPresets"/> keeps the rows that would call it hidden.
    /// </summary>
    public async Task<bool> DownloadSupervanillaIndexEntryAsync(SupervanillaIndexEntry entry, CancellationToken cancellationToken)
    {
        foreach (var origin in SupervanillaOrigins)
        {
            if (!Uri.TryCreate(new Uri(origin), entry.Url, out var fileUri)) continue;

            var ok = await DownloadSupervanillaFileAsync(fileUri.AbsoluteUri, IndexFolderName(IsPreview, entry.Name), _ =>
                new SupervanillaOrigin(SupervanillaKind.Index, IsPreview, entry.EffectiveVersion, entry.Name, entry.Size, entry.Date),
                cancellationToken);

            if (ok) return true;
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
    /// Downloads one .rtpack and puts it in place under <paramref name="folderName"/>, through the
    /// same staging every import uses: the existing folder is only replaced once the new one has
    /// been unpacked and found to be a preset, so a bad download never costs the good copy.
    /// <paramref name="origin"/> builds the sidecar once the folder is in place, since a version
    /// may only be readable from the manifest inside it.
    /// </summary>
    private async Task<bool> DownloadSupervanillaFileAsync(
        string url, string folderName, Func<string, SupervanillaOrigin> origin, CancellationToken cancellationToken)
    {
        string? downloadedPath = null;
        string? stagingFolder = null;
        try
        {
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

            var destination = Path.Combine(CacheFolder, folderName);
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);

            Directory.Move(stagingFolder, destination);
            stagingFolder = null;

            WriteSupervanillaOrigin(destination, origin(destination));
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ✓ \"{parsed.Name}\" in place at {destination}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Supervanilla] ✗ Error downloading {url}: {ex.Message}");
            return false;
        }
        finally
        {
            if (stagingFolder != null) TryDeleteFolder(stagingFolder);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                try { File.Delete(downloadedPath); } catch { }
            }
        }
    }

    private static string FileNameFromUrl(string url) =>
        Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));

    #endregion
}
