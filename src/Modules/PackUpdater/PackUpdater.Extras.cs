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

namespace Vanilla_RTX_App.Modules.PackUpdater;

/// <summary>
/// One pack the Vanilla RTX Extras repository publishes, as its release index describes it.
/// Everything needed to decide what to offer is here, so nothing is downloaded until the user
/// asks for this pack specifically.
/// </summary>
public sealed record ExtraPack(
    string Id,
    string File,
    long Size,
    string? Sha256,
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
public sealed record ExtrasIndex(string Tag, DateTime? BuiltUtc, IReadOnlyList<ExtraPack> Packs);

/// <summary>An installed copy of an extra: its version, and the pack folder it lives in.</summary>
public readonly record struct InstalledExtra(int[] Version, string Folder)
{
    public string VersionDisplay => string.Join('.', Version);
}

/// <summary>
/// The Vanilla RTX Extras - add-ons and extensions from github.com/Vanilla-RTX/Vanilla-RTX-Extras -
/// tracked and installed the same way as the three Vanilla RTX packs: matched to what is
/// installed by manifest UUID, replaced by UUID, staged under a <c>__rtxapp_</c> folder and moved
/// into place.
///
/// <para><b>What is available comes from one file</b>, <c>extras-index.json</c>, which that
/// repository's release workflow (<c>pack_builder.py</c>) writes beside the .mcpacks of every
/// release. It is read through <see cref="AssetUpdater"/> from
/// <c>releases/latest/download/</c>, which redirects to the newest release's asset without
/// touching the GitHub API, so the API's unauthenticated rate limit never applies; the final hop
/// answers conditional requests, so an unchanged index costs a 304. The alternatives were the
/// repository zipball (the whole source tree, media folders and all, for one pack) or one raw
/// manifest per pack (thirteen requests to learn what one release already knows).</para>
///
/// <para><b>Each pack downloads on its own</b>, from the release the index names. The bundle
/// .mcaddon exists too, but Creative RTX alone is most of its ~75 MB, and nobody installing
/// Clear Water should pay for it. Release packs are built with their enhancements already
/// merged, so there is no enhancements toggle here.</para>
///
/// <para>A file the index names can vanish: a same-day rebuild deletes and recreates its
/// release. A failed download therefore re-reads the index, ignoring its cooldown, and retries
/// once if the pack now points somewhere else.</para>
/// </summary>
public partial class PackUpdater
{
    private const string ExtrasRepository = "https://github.com/Vanilla-RTX/Vanilla-RTX-Extras";

    public static readonly ManagedAsset ExtrasIndexAsset = new(
        ExtrasRepository + "/releases/latest/download/extras-index.json",
        TimeSpan.FromHours(1),
        CacheFolderName: "PackUpdater",
        FileName: "extras-index.json",
        FailureBackoff: TimeSpan.FromMinutes(10));

    // The index layout this build understands. A future layout that isn't backward-compatible
    // bumps the number in pack_builder.py, and this build then reads nothing rather than misread.
    private const int ExtrasIndexFormat = 1;

    private string? _currentInstallingExtraId;

    /// <summary>The extra being installed right now, by index id, or null.</summary>
    public string? GetCurrentlyInstallingExtra() => _currentInstallingExtraId;

    // ======================= Index =======================

    /// <summary>
    /// The extras index: fresh when its cooldown has passed or <paramref name="force"/> asks,
    /// the cached copy otherwise or when the fetch fails. Null only when there is no copy at all.
    /// <c>CheckedRemote</c> is AssetUpdater's: true when the repository answered just now,
    /// including with a 304.
    /// </summary>
    public static async Task<(ExtrasIndex? Index, bool CheckedRemote)> GetExtrasIndexAsync(
        bool force = false, CancellationToken cancellationToken = default)
    {
        var read = await AssetUpdater.ResolveFreshOrCachedAsync(ExtrasIndexAsset, force, FetchPriority.Foreground, cancellationToken);
        return (ParseExtrasIndex(read.Path), read.CheckedRemote);
    }

    /// <summary>The cached index as it stands, without any request - for readers that must not wait.</summary>
    public static ExtrasIndex? ReadCachedExtrasIndex() => ParseExtrasIndex(AssetUpdater.Resolve(ExtrasIndexAsset));

    private static ExtrasIndex? ParseExtrasIndex(string? path)
    {
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var root = MinecraftJson.ParseObjectFile(path);
            if (root is null) return null;

            if (MinecraftJson.GetInt(root["format"]) != ExtrasIndexFormat)
            {
                Trace.WriteLine($"[Extras] Index format {MinecraftJson.GetString(root["format"])} isn't one this build reads.");
                return null;
            }

            var tag = MinecraftJson.GetString(root["tag"]);
            if (string.IsNullOrWhiteSpace(tag)) return null;

            DateTime? built = DateTime.TryParse(MinecraftJson.GetString(root["built_utc"]), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var b)
                ? b : null;

            var packs = new List<ExtraPack>();
            foreach (var node in root["packs"] as JsonArray ?? new JsonArray())
            {
                var id = MinecraftJson.GetString(node?["id"]);
                var file = MinecraftJson.GetString(node?["file"]);
                var uuid = MinecraftJson.GetString(node?["uuid"]);
                var version = MinecraftJson.GetIntArray(node?["version"]);

                // An entry missing any of these can be neither matched nor installed.
                if (id is null || file is null || uuid is null || version is null || version.Length == 0) continue;
                if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                var subpacks = new List<PackSubpack>();
                foreach (var sp in node?["subpacks"] as JsonArray ?? new JsonArray())
                {
                    var folder = MinecraftJson.GetString(sp?["folder_name"]);
                    if (string.IsNullOrEmpty(folder)) continue;
                    subpacks.Add(new PackSubpack(folder, MinecraftJson.GetString(sp?["name"]) ?? folder));
                }

                packs.Add(new ExtraPack(
                    id,
                    file,
                    MinecraftJson.TryGetDouble(node?["size"], out var size) ? (long)size : 0,
                    MinecraftJson.GetString(node?["sha256"]),
                    uuid,
                    MinecraftJson.GetString(node?["module_uuid"]),
                    version,
                    Helpers.StripMinecraftFormatting(MinecraftJson.GetString(node?["name"]) ?? id),
                    Helpers.StripMinecraftFormatting(MinecraftJson.GetString(node?["description"]) ?? ""),
                    MinecraftJson.GetString(node?["group"]),
                    subpacks));
            }

            return new ExtrasIndex(tag, built, packs);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Extras] Couldn't read the extras index: {ex.Message}");
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
    public static Dictionary<string, InstalledExtra> FindInstalledExtras(IEnumerable<ExtraPack> packs)
    {
        var wanted = new HashSet<string>(packs.Select(p => p.Uuid), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, InstalledExtra>(StringComparer.OrdinalIgnoreCase);

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
                    found[uuid] = new InstalledExtra(version, folder);
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="pack"/> is newer than what is installed. False when it isn't installed at all.</summary>
    public static bool IsExtraOutdated(ExtraPack pack, InstalledExtra? installed)
        => installed is { } i && CompareVersionArrays(pack.Version, i.Version) > 0;

    // ======================= Installing =======================

    /// <summary>
    /// Installs each of <paramref name="packs"/> in turn, under the same one-install-at-a-time
    /// rule as the Vanilla RTX packs (<see cref="IsInstallationInProgress"/>), and calls
    /// <paramref name="onStarting"/> as each one begins. One pack failing doesn't stop the rest.
    /// </summary>
    /// <returns>The packs that installed and the ones that didn't. Both empty when another install was already running.</returns>
    public async Task<(List<ExtraPack> Installed, List<ExtraPack> Failed)> InstallExtrasAsync(
        ExtrasIndex index, IReadOnlyList<ExtraPack> packs, Action<ExtraPack>? onStarting = null,
        CancellationToken cancellationToken = default)
    {
        var installed = new List<ExtraPack>();
        var failed = new List<ExtraPack>();

        if (IsInstallationInProgress())
        {
            Trace.WriteLine("⚠️ Another installation is already in progress");
            return (installed, failed);
        }

        try
        {
            SetInstallationState(true);

            foreach (var pack in packs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                _currentInstallingExtraId = pack.Id;
                onStarting?.Invoke(pack);

                if (await InstallOneExtraAsync(index, pack, cancellationToken))
                    installed.Add(pack);
                else
                    failed.Add(pack);
            }
        }
        finally
        {
            _currentInstallingExtraId = null;
            ClearInstallationState();
        }

        return (installed, failed);
    }

    private async Task<bool> InstallOneExtraAsync(ExtrasIndex index, ExtraPack pack, CancellationToken cancellationToken)
    {
        string? downloaded = null;
        try
        {
            Trace.WriteLine($"🔄 Installing {pack.Name} {pack.VersionDisplay}...");

            var download = await DownloadExtraAsync(index.Tag, pack, cancellationToken);

            // The release may have been rebuilt since the index was cached - see the class remarks.
            if (!download.Success)
            {
                var (fresh, _) = await GetExtrasIndexAsync(force: true, cancellationToken);
                var current = fresh?.Packs.FirstOrDefault(p => p.Id == pack.Id);
                if (fresh is not null && current is not null && (fresh.Tag != index.Tag || current.File != pack.File))
                {
                    Trace.WriteLine($"[Extras] {pack.Name} moved to {fresh.Tag}/{current.File}, retrying.");
                    pack = current;
                    download = await DownloadExtraAsync(fresh.Tag, pack, cancellationToken);
                }
            }

            downloaded = download.Path;
            if (!download.Success || downloaded is null)
            {
                Trace.WriteLine($"❌ Couldn't download {pack.Name}.");
                return false;
            }

            if (!string.IsNullOrEmpty(pack.Sha256) && !await HashMatchesAsync(downloaded, pack.Sha256, cancellationToken))
            {
                Trace.WriteLine($"❌ {pack.Name} didn't match the hash the release lists; nothing was installed.");
                return false;
            }

            return await Task.Run(() => DeployExtra(downloaded, pack), cancellationToken);
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
        finally
        {
            if (downloaded is not null)
            {
                try { File.Delete(downloaded); } catch { }
            }
        }
    }

    private static Task<DownloadResult> DownloadExtraAsync(string tag, ExtraPack pack, CancellationToken cancellationToken)
        => Helpers.Download(
            $"{ExtrasRepository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(pack.File)}",
            cancellationToken,
            Helpers.UpdaterHttpClient,
            TimeSpan.FromMinutes(15));

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts one downloaded .mcpack (manifest at the archive root) into a staging folder,
    /// removes every installed copy by UUID, and moves the new one into place - the same steps
    /// <see cref="DeployPackage"/> takes for a Vanilla RTX pack, against the same folders. The
    /// archive's own manifest must carry the UUID the index promised, or nothing is touched.
    /// </summary>
    private bool DeployExtra(string mcpackPath, ExtraPack pack)
    {
        string? staging = null;
        string? resourcePackPath = null;
        try
        {
            if (!MinecraftUserDataLocator.IsDataValid(EnvironmentVariables.Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"❌ {MinecraftUserDataLocator.GetVersionDisplayName(EnvironmentVariables.Persistent.IsTargetingPreview)} data root not found.");
                return false;
            }

            resourcePackPath = MinecraftUserDataLocator.GetResourcePacksPath(
                EnvironmentVariables.Persistent.IsTargetingPreview, development: InstallToDevelopmentFolder, createIfMissing: true);
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

            DeleteExistingPackByUUID(resourcePackPath, uuid, manifest.FirstModuleUuid ?? pack.ModuleUuid ?? "", pack.Name)
                .GetAwaiter().GetResult();

            var destination = GetSafeDirectoryName(resourcePackPath, ExtraFolderName(pack.Id));
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
    /// The folder an extra is installed under. Shortened from the release id because a subpack's
    /// deepest texture path is long already - Glowing Glazed Terracotta's runs past 200 characters
    /// from a typical user data root - and Windows' path limit is what that runs into first.
    /// RTX Reactor caps its output folder names for the same reason.
    /// </summary>
    private static string ExtraFolderName(string id)
    {
        var name = id.Replace("-Add-On", "", StringComparison.OrdinalIgnoreCase);
        foreach (var suffix in new[] { "-Any", "-All" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        }

        name = new string(name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim('-', ' ', '.');
        if (name.Length > 24) name = name[..24].TrimEnd('-', ' ', '.');
        return string.IsNullOrEmpty(name) ? "VanillaRTXExtra" : name;
    }
}
