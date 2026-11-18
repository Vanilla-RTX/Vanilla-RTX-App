using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules.Json;
using static Vanilla_RTX_App.Modules.PackLocator;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// One pack on the activation list, by header UUID. <paramref name="Subpack"/> is the subpack's
/// folder name, <see cref="PackActivation.PackDefault"/> to leave the game's own choice, or
/// <see cref="PackActivation.MatchBasePack"/> to pick whichever subpack is named after the
/// Vanilla RTX pack being activated alongside it.
/// </summary>
public readonly record struct ActivationEntry(string Uuid, string Subpack);

/// <summary>A resource pack found installed for one edition.</summary>
public sealed record InstalledPack(string Uuid, string Name, int[] Version, IReadOnlyList<PackSubpack> Subpacks, string Folder)
{
    public string VersionDisplay => string.Join('.', Version);
}

/// <summary>What one launch actually activates, top of the list first.</summary>
public readonly record struct ResolvedActivation(InstalledPack Pack, string? Subpack, string? SubpackName);

/// <summary>
/// Activates a chosen, ordered set of resource packs just before the Launch button starts the
/// game, by writing them at the top of <c>global_resource_packs.json</c> - the game's own list of
/// globally active packs, top entry highest priority - in every <c>minecraftpe</c> folder
/// <see cref="MinecraftUserDataLocator.FindAllOptionsFiles"/> finds: Shared, which the game reads
/// signed out, and one per signed-in account. The same folders the options.txt edits go to,
/// because the two files sit side by side.
///
/// <para><b>The three Vanilla RTX packs are mutually exclusive.</b> All three can be on the list,
/// but only the highest one that is installed is activated; the others are skipped, and any of
/// them already active in the file is removed, so the game never stacks two base packs.</para>
///
/// <para><b>"Match the base pack" subpacks</b> are a name match, not a table: an add-on that ships
/// subpacks named "Vanilla RTX", "Vanilla RTX Normals" and "Vanilla RTX Opus" (Glowing Ores and
/// Glowing Glazed Terracotta today) gets the one named after the base pack this launch activates,
/// whatever their folder letters are. With no base pack activated it is left to the game.</para>
///
/// <para><b>Everything else in the file is kept</b>, in its own order, below ours - or above them
/// with <see cref="EnvironmentVariables.Persistent.ActivatePacksAboveOthers"/> off: packs the user
/// activated in game stay active. Entries for packs on our list, and the other base packs when one
/// is activated, are always removed first and rewritten at our position, so a listed pack that was
/// already active is moved rather than duplicated. Each entry carries the installed version, since
/// the game matches packs by UUID and version together.</para>
///
/// <para>Stored as one <c>uuid:subpack;uuid:subpack</c> string for the same reason launch options
/// are (<see cref="MinecraftLauncher.ParseOptions"/>), with the same "none" marker for a list the
/// user emptied on purpose. Off by default: it changes what the game loads, so it is opt-in.</para>
/// </summary>
public static class PackActivation
{
    public const string PackDefault = "";
    public const string MatchBasePack = "auto";

    private const string FileName = "global_resource_packs.json";
    private const char EntrySeparator = ';';
    private const char FieldSeparator = ':';

    /// <summary>The three base packs in their default priority, with the names their matching subpacks carry.</summary>
    public static readonly (string Uuid, string Name)[] BasePacks =
    {
        (VANILLA_RTX_HEADER_UUID, "Vanilla RTX"),
        (VANILLA_RTX_NORMALS_HEADER_UUID, "Vanilla RTX Normals"),
        (VANILLA_RTX_OPUS_HEADER_UUID, "Vanilla RTX Opus"),
    };

    public static readonly ActivationEntry[] DefaultEntries =
        BasePacks.Select(b => new ActivationEntry(b.Uuid, PackDefault)).ToArray();

    public static bool IsBasePack(string uuid) => BasePacks.Any(b => b.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical name of a base pack, or null for any other pack.</summary>
    public static string? BasePackName(string uuid)
        => BasePacks.FirstOrDefault(b => b.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>True when a pack's subpacks are named after the base packs, so "Match the base pack" means something for it.</summary>
    public static bool HasBasePackSubpacks(InstalledPack pack)
        => pack.Subpacks.Any(s => BasePacks.Any(b => b.Name.Equals(s.Name.Trim(), StringComparison.OrdinalIgnoreCase)));

    // ======================= Stored form =======================

    public static ActivationEntry[] Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultEntries;
        if (stored.Trim() == MinecraftLauncher.EmptyMarker) return Array.Empty<ActivationEntry>();

        var entries = new List<ActivationEntry>();
        foreach (var part in stored.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = part.Split(FieldSeparator, 2);
            var uuid = fields[0].Trim();
            if (!Guid.TryParse(uuid, out _)) continue;
            if (entries.Any(e => e.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(new ActivationEntry(uuid, fields.Length > 1 ? fields[1].Trim() : PackDefault));
        }
        return entries.ToArray();
    }

    public static string Serialize(IEnumerable<ActivationEntry> entries)
    {
        var parts = entries.Select(e => $"{e.Uuid}{FieldSeparator}{e.Subpack}").ToList();
        return parts.Count == 0 ? MinecraftLauncher.EmptyMarker : string.Join(EntrySeparator, parts);
    }

    // ======================= What is installed =======================

    /// <summary>
    /// Every resource pack installed for <paramref name="isPreview"/>'s edition, one per UUID
    /// (the highest version when a pack is installed twice), read from each pack folder's own
    /// manifest in both resource pack folders.
    /// </summary>
    public static List<InstalledPack> FindInstalledPacks(bool isPreview)
    {
        var found = new Dictionary<string, InstalledPack>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(isPreview))
        {
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var folder in folders)
            {
                if (Path.GetFileName(folder).StartsWith("__", StringComparison.Ordinal)) continue;

                var manifest = PackManifest.FromFile(Path.Combine(folder, PackManifest.ModernFileName));
                if (manifest?.HeaderUuid is not { } uuid || manifest.VersionArray is not { } version) continue;
                if (!manifest.HasResourceModule) continue;

                if (found.TryGetValue(uuid, out var existing) && CompareVersions(version, existing.Version) <= 0) continue;

                var name = BasePackName(uuid) ?? manifest.DisplayName ?? Path.GetFileName(folder);
                found[uuid] = new InstalledPack(uuid, name, version, manifest.Subpacks, folder);
            }
        }

        return found.Values.ToList();
    }

    private static int CompareVersions(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // ======================= Resolution =======================

    /// <summary>
    /// What <paramref name="entries"/> activate against what is installed: packs not installed are
    /// skipped, only the first installed base pack is taken, and each subpack choice is resolved -
    /// a chosen folder the pack no longer declares falls back to the game's own choice.
    /// </summary>
    public static List<ResolvedActivation> Resolve(IEnumerable<ActivationEntry> entries, IReadOnlyCollection<InstalledPack> installed)
    {
        var byUuid = installed.ToDictionary(p => p.Uuid, StringComparer.OrdinalIgnoreCase);
        var list = entries.ToList();

        var basePack = list.Select(e => byUuid.GetValueOrDefault(e.Uuid)).FirstOrDefault(p => p is not null && IsBasePack(p.Uuid));
        var baseName = basePack is null ? null : BasePackName(basePack.Uuid);

        var resolved = new List<ResolvedActivation>();
        foreach (var entry in list)
        {
            if (!byUuid.TryGetValue(entry.Uuid, out var pack)) continue;
            if (IsBasePack(pack.Uuid) && pack != basePack) continue;

            PackSubpack? subpack = entry.Subpack switch
            {
                PackDefault => null,
                MatchBasePack => baseName is null ? null
                    : pack.Subpacks.FirstOrDefault(s => s.Name.Trim().Equals(baseName, StringComparison.OrdinalIgnoreCase)),
                var folder => pack.Subpacks.FirstOrDefault(s => s.FolderName == folder),
            };

            resolved.Add(new ResolvedActivation(pack, subpack?.FolderName, subpack?.Name));
        }
        return resolved;
    }

    // ======================= Writing =======================

    /// <summary>
    /// Applies the configured list for <paramref name="isPreview"/>'s edition if the feature is on,
    /// and returns what is worth telling the user. Never throws; a launch goes ahead whatever
    /// happens here.
    /// </summary>
    public static async Task<List<string>> ApplyConfiguredAsync(bool isPreview)
    {
        var messages = new List<string>();
        if (!EnvironmentVariables.Persistent.ActivatePacksOnLaunch) return messages;

        try
        {
            var entries = Parse(EnvironmentVariables.Persistent.PacksToActivate);
            if (entries.Length == 0) return messages;

            var installed = await Task.Run(() => FindInstalledPacks(isPreview));
            var resolved = Resolve(entries, installed);
            if (resolved.Count == 0)
            {
                messages.Add("None of the packs set to activate on launch are installed, so nothing was activated.");
                return messages;
            }

            // Replaced: every pack on our list, and every base pack once one is being activated.
            var managed = new HashSet<string>(entries.Select(e => e.Uuid), StringComparer.OrdinalIgnoreCase);
            if (resolved.Any(r => IsBasePack(r.Pack.Uuid)))
                foreach (var (uuid, _) in BasePacks) managed.Add(uuid);

            var folders = MinecraftUserDataLocator.FindAllOptionsFiles(isPreview)
                .Select(Path.GetDirectoryName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var written = 0;
            foreach (var folder in folders)
            {
                if (await WriteAsync(Path.Combine(folder, FileName), resolved, managed, EnvironmentVariables.Persistent.ActivatePacksAboveOthers))
                    written++;
                else
                    messages.Add($"⚠️ Couldn't update the active packs for {MinecraftUserDataLocator.GetOwningFolderLabel(isPreview, folder)}.");
            }

            if (written > 0)
            {
                var names = resolved.Select(r => r.SubpackName is null ? r.Pack.Name : $"{r.Pack.Name} ({r.SubpackName})");
                messages.Add($"Activated {string.Join(", ", names)}.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackActivation] {ex}");
            messages.Add("⚠️ Couldn't activate the packs set to activate on launch.");
        }

        return messages;
    }

    /// <summary>
    /// Rewrites one file: our entries in order, then whatever else was there that isn't
    /// <paramref name="managed"/> - or the other way round when <paramref name="above"/> is false. A file the game hasn't created yet, or one that doesn't parse,
    /// starts from an empty list. Written to a temporary file and moved over the old one, so a
    /// failure part-way never leaves the game a half-written list.
    /// </summary>
    private static async Task<bool> WriteAsync(string path, List<ResolvedActivation> resolved, HashSet<string> managed, bool above)
    {
        try
        {
            var existing = File.Exists(path)
                ? MinecraftJson.ParseArray(await File.ReadAllTextAsync(path)) ?? new JsonArray()
                : new JsonArray();

            var ours = new List<JsonNode>();
            foreach (var r in resolved)
            {
                var entry = new JsonObject { ["pack_id"] = (JsonNode?)JsonValue.Create(r.Pack.Uuid) };
                if (r.Subpack is not null) entry["subpack"] = (JsonNode?)JsonValue.Create(r.Subpack);
                var version = new JsonArray();
                foreach (var part in r.Pack.Version) version.Add((JsonNode?)JsonValue.Create(part));
                entry["version"] = version;
                ours.Add(entry);
            }

            var theirs = new List<JsonNode>();
            foreach (var node in existing.ToList())
            {
                if (node is null) continue;
                var id = MinecraftJson.GetString(node["pack_id"]);
                if (id is not null && managed.Contains(id)) continue;
                existing.Remove(node);
                theirs.Add(node);
            }

            var output = new JsonArray();
            foreach (var node in above ? ours.Concat(theirs) : theirs.Concat(ours)) output.Add(node);

            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, MinecraftJson.ToIndentedString(output));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackActivation] Writing {path} failed: {ex.Message}");
            return false;
        }
    }
}
