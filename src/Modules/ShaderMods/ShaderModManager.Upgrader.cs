using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules.ShaderMods;

/// <summary>What <see cref="ShaderModManager.PrepareInstallAsync"/> decided about one preset.</summary>
internal enum UpgradeVerdict
{
    /// <summary>Nothing to check: the Default preset, a preset of vanilla's own files, or a Supervanilla RTX build made for this exact game.</summary>
    NotNeeded,

    /// <summary>Checked: the preset already is this game's materials with its own shaders in them. Installs as shipped.</summary>
    Current,

    /// <summary>Only the packaging was out of date. An upgraded copy was made and is what installs.</summary>
    Upgraded,

    /// <summary>What the preset's shaders receive from the game has changed. No repackaging can fix it; the author has to rebuild.</summary>
    InterfaceChanged,

    /// <summary>Couldn't be checked - no converter, a preset or game file lazurite can't read, or a failed run.</summary>
    CannotCheck
}

/// <param name="Summary">One sentence for the window and the log.</param>
/// <param name="Details">What was found, one line each - the problems for <see cref="UpgradeVerdict.InterfaceChanged"/>, the changes for <see cref="UpgradeVerdict.Upgraded"/>.</param>
internal sealed record UpgradeOutcome(UpgradeVerdict Verdict, string Summary, IReadOnlyList<string> Details);

/// <summary>
/// The install-time upgrader: before a preset is installed, a preset built for an older game is rebuilt as
/// <b>this game's own materials with only the preset's shader bytecode moved in</b>, and that copy installs
/// instead. The work is lazurite's (<see cref="LazuriteToolchain"/>, <c>material_upgrade.py</c>); this
/// decides when to ask, keeps the result, and makes install and detection use it.
///
/// <para><b>A safety net for packaging, not a fix for shaders.</b> Updates that only change how materials
/// are packed - a format bump, a record layout, pass metadata, the platform list - are the ones that crash
/// the game when ray tracing is turned on, and are all rescued, because the container installed is
/// Mojang's own for this game. Updates that change what the shaders receive (buffers, uniforms, passes,
/// variants, a shader's inputs) are caught and reported as <see cref="UpgradeVerdict.InterfaceChanged"/>:
/// those need the author. Changes inside a buffer's layout are invisible to either, so an upgraded preset
/// is "probably works", and Default stays one click away.</para>
///
/// <para><b>Asked only when it can matter.</b> Never for the Default preset (it is the game's own files),
/// nor for a preset whose files are vanilla's, nor for a Supervanilla RTX build named for this exact game
/// version and edition - the common case, answered without starting Python. Anything else is classified,
/// and the answer is cached by content: <c>RTX_Cache\__UPGRADED\&lt;preset&gt;_&lt;game&gt;\</c>, keyed by
/// fingerprints of the preset's files and of this edition's Default backup, so it stops being found the
/// moment either changes.</para>
///
/// <para><b>The preset's own files are never modified</b>, and the upgraded copy is staged in the
/// toolchain's work folder and moved into place only once complete - nothing half-written is ever found by
/// <see cref="BuildInstallSet(List{string})"/>.</para>
/// </summary>
internal sealed partial class ShaderModManager
{
    public const string UpgradedFolderName = "__UPGRADED";
    private const string UpgradeRecordFileName = "upgrade.json";

    /// <summary>
    /// The game's version from its MicrosoftGame.Config, set by <see cref="TryAttachAsync"/>. A label for
    /// messages and the Supervanilla RTX fast path; never what decides whether the game changed.
    /// </summary>
    public string? GameVersion { get; private set; }

    /// <summary>
    /// Folders in the cache that are not presets and that no provider's wipe may take: both Default backups
    /// and the upgraded copies. Every sweep and every preset scan asks this.
    /// </summary>
    public static bool IsReservedFolderName(string folderName) =>
        IsDefaultFolderName(folderName) ||
        folderName.Equals(UpgradedFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Both editions' Default backups that exist - what the toolchain's gate proves itself on.</summary>
    public IReadOnlyList<string> VanillaBackupFolders() =>
        string.IsNullOrEmpty(CacheFolder)
            ? []
            : new[] { DEFAULT_PRESET_FOLDER_NAME, DEFAULT_PREVIEW_PRESET_FOLDER_NAME }
                .Select(name => Path.Combine(CacheFolder, name))
                .Where(Directory.Exists)
                .ToList();

    /// <summary>Starts the toolchain's setup or daily update in the background. Called once the backup is confirmed.</summary>
    public void WarmUpUpgrader() => LazuriteToolchain.WarmUp(VanillaBackupFolders());

    // ── Fingerprints ─────────────────────────────────────────────────────────

    /// <summary>
    /// File hashes for files under the cache, memoised against size and write time: every list load
    /// fingerprints every preset through <see cref="BuildInstallSet(List{string})"/>, and RTXStub runs to
    /// several MB. Only cache files go through this - the game's own files are rewritten by installs that
    /// preserve the source's timestamps, which is not a key to trust.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, string Hash)> _hashMemo =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? CachedHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (_hashMemo.TryGetValue(path, out var memo) && memo.Size == info.Length && memo.Written == info.LastWriteTimeUtc)
                return memo.Hash;
            var hash = ComputeFileHash(path);
            if (hash != null) _hashMemo[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 over each RTX file's name and SHA-256, in ordinal name order, first 16 hex digits - the
    /// Supervanilla RTX factory's <c>materials_fingerprint</c>, so the two name the same set the same way.
    /// Null when any file can't be read.
    /// </summary>
    private static string? Fingerprint(IEnumerable<string> files)
    {
        var parts = new List<string>();
        foreach (var file in files.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var hash = CachedHash(file);
            if (hash == null) return null;
            parts.Add(Path.GetFileName(file) + hash);
        }
        if (parts.Count == 0) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(parts))), 0, 8).ToLowerInvariant();
    }

    /// <summary>The preset files that are RTX files, by name - the only ones an install writes.</summary>
    private static List<string> RtxFilesOf(IEnumerable<string> presetFiles) =>
        presetFiles.Where(f => RTXFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();

    private string? UpgradeFolderFor(List<string> presetRtxFiles)
    {
        if (string.IsNullOrEmpty(CacheFolder) || string.IsNullOrEmpty(DefaultFolder)) return null;
        var preset = Fingerprint(presetRtxFiles);
        var game = Fingerprint(RTXFiles.Select(f => Path.Combine(DefaultFolder, f)));
        return preset == null || game == null ? null : Path.Combine(CacheFolder, UpgradedFolderName, $"{preset}_{game}");
    }

    private bool IsDefaultPresetFiles(IEnumerable<string> files) =>
        !string.IsNullOrEmpty(DefaultFolder) &&
        files.All(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), Path.GetFullPath(DefaultFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The upgraded copies of these preset files for this game, by file name, or null when there are none.
    /// What <see cref="BuildInstallSet(List{string})"/> installs in their place - so install and detection
    /// both see the upgrade with nothing else changed.
    /// </summary>
    private Dictionary<string, string>? FindUpgradedFiles(List<string> presetFiles)
    {
        try
        {
            var rtx = RtxFilesOf(presetFiles);
            if (rtx.Count == 0 || IsDefaultPresetFiles(rtx)) return null;

            var folder = UpgradeFolderFor(rtx);
            if (folder == null || !File.Exists(Path.Combine(folder, UpgradeRecordFileName))) return null;

            var upgraded = Directory.GetFiles(folder, "*.material.bin")
                .ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
            return upgraded.Count > 0 ? upgraded : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Upgrade] Could not look up an upgraded copy: {ex.Message}");
            return null;
        }
    }

    /// <summary>What an installed preset is running as, for its row: the upgrade record of its files for this game, if any.</summary>
    public string? DescribeUpgrade(List<string>? presetFiles)
    {
        if (presetFiles == null) return null;
        var rtx = RtxFilesOf(presetFiles);
        if (rtx.Count == 0 || IsDefaultPresetFiles(rtx)) return null;
        var folder = UpgradeFolderFor(rtx);
        if (folder == null) return null;
        var record = MinecraftJson.ParseObjectFile(Path.Combine(folder, UpgradeRecordFileName));
        if (MinecraftJson.GetString(record?["verdict"]) != "upgraded") return null;
        return GameVersion is { } v ? $"Auto-upgraded for Minecraft {v}" : "Auto-upgraded for this game version";
    }

    /// <summary>The loaded preset at this path, so a click on a drawn row can reach everything parsed about it.</summary>
    public LocalPresetData? FindLoadedPreset(string? presetPath)
    {
        if (presetPath == null) return null;
        bool Same(LocalPresetData p) => string.Equals(p.PresetPath, presetPath, StringComparison.OrdinalIgnoreCase);
        return LocalPresets?.Values.FirstOrDefault(Same) ?? SupervanillaPresets?.FirstOrDefault(Same);
    }

    /// <summary>
    /// Recomputes a loaded preset's expected hashes after an install, since upgrading changes what installing
    /// it writes. Without it an upgraded install wouldn't read as installed until the next load.
    /// </summary>
    public void RefreshInstallState(LocalPresetData preset)
    {
        foreach (var p in new[] { preset, FindLoadedPreset(preset.PresetPath) })
            if (p?.BinFiles != null)
                p.FileHashes = GetExpectedInstalledHashes(p.BinFiles);
    }

    /// <summary>
    /// Deletes upgraded copies made for a game neither Default backup holds any more - after a game update
    /// they can never be found again. Called once a backup is confirmed.
    /// </summary>
    public void PruneUpgradedCopies()
    {
        try
        {
            var root = Path.Combine(CacheFolder, UpgradedFolderName);
            if (!Directory.Exists(root)) return;

            var live = VanillaBackupFolders()
                .Select(folder => Fingerprint(RTXFiles.Select(f => Path.Combine(folder, f))))
                .Where(fp => fp != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // No backup to judge by is no evidence anything is stale.
            if (live.Count == 0) return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var game = name.Contains('_') ? name[(name.LastIndexOf('_') + 1)..] : name;
                if (live.Contains(game)) continue;
                LazuriteToolchain.TryDelete(dir);
                Trace.WriteLine($"[ShaderMods] [Upgrade] Pruned {name}: made for a game no backup holds any more");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Upgrade] Pruning failed: {ex.Message}");
        }
    }

    // ── The decision ─────────────────────────────────────────────────────────

    /// <summary>
    /// Decides how <paramref name="preset"/> should be installed into this game, making the upgraded copy
    /// when one is needed. Never throws; a failure is <see cref="UpgradeVerdict.CannotCheck"/>.
    /// <paramref name="displayName"/> is what messages call it; <paramref name="onActivity"/> narrates
    /// each step, from any thread.
    /// </summary>
    public async Task<UpgradeOutcome> PrepareInstallAsync(LocalPresetData preset, string displayName, Action<string> onActivity, CancellationToken ct)
    {
        string? work = null;
        try
        {
            var rtx = RtxFilesOf(preset.BinFiles ?? new List<string>());
            var gameLabel = GameVersion is { } gv ? $"Minecraft {gv}" : "this game version";

            if (rtx.Count == 0 || IsDefaultPresetFiles(rtx))
                return new(UpgradeVerdict.NotNeeded, "Nothing to upgrade.", []);

            if (rtx.All(f => CachedHash(f) is { } h && h == CachedHash(Path.Combine(DefaultFolder, Path.GetFileName(f)))))
                return new(UpgradeVerdict.NotNeeded, "Its files are this game's own.", []);

            if (preset.Supervanilla is { Version: { } built } svrtx && svrtx.IsPreview == IsPreview &&
                GameVersion != null && CompareVersions(built, GameVersion) == 0)
                return new(UpgradeVerdict.NotNeeded, $"Built for {gameLabel}.", []);

            var folder = UpgradeFolderFor(rtx);
            if (folder != null && ReadRecord(folder) is { } cached)
            {
                Trace.WriteLine($"[ShaderMods] [Upgrade] {displayName}: checked before ({cached.Verdict})");
                return cached;
            }

            onActivity($"Checking {displayName} against {gameLabel}...");
            LazuriteToolchain.Activity += onActivity;
            try
            {
                var outcome = await ClassifyAsync(displayName, rtx, gameLabel, onActivity, LazuriteToolchain.UpdateCheck.IfDue, ct);

                // A game material lazurite can't read is the one failure a newer lazurite is likely to fix:
                // Mojang moved to a format this one doesn't know yet. Ask once, now, and try again.
                if (outcome.Retry)
                {
                    var before = LazuriteToolchain.AdoptedVersion;
                    onActivity("Looking for a newer version of lazurite...");
                    await LazuriteToolchain.EnsureReadyAsync(VanillaBackupFolders(), LazuriteToolchain.UpdateCheck.Now, ct);
                    if (LazuriteToolchain.AdoptedVersion != before)
                        outcome = await ClassifyAsync(displayName, rtx, gameLabel, onActivity, LazuriteToolchain.UpdateCheck.Never, ct);
                }

                work = outcome.Work;
                var result = outcome.Outcome;

                if (folder != null && result.Verdict is UpgradeVerdict.Upgraded or UpgradeVerdict.Current)
                    Commit(folder, work, displayName, result);

                Trace.WriteLine($"[ShaderMods] [Upgrade] {displayName} on {gameLabel} ({(IsPreview ? "Preview" : "Release")}), " +
                                $"lazurite {LazuriteToolchain.AdoptedVersion ?? "-"}: {result.Verdict} - {result.Summary}");
                foreach (var line in result.Details) Trace.WriteLine($"[ShaderMods] [Upgrade]   {line}");
                return result;
            }
            finally
            {
                LazuriteToolchain.Activity -= onActivity;
            }
        }
        catch (OperationCanceledException)
        {
            return new(UpgradeVerdict.CannotCheck, "The check was cancelled.", []);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Upgrade] ✗ {displayName}: {ex}");
            return new(UpgradeVerdict.CannotCheck, $"The check failed ({ex.Message}).", []);
        }
        finally
        {
            if (work != null) LazuriteToolchain.TryDelete(work);
        }
    }

    private sealed record Classification(UpgradeOutcome Outcome, string? Work, bool Retry);

    private async Task<Classification> ClassifyAsync(
        string displayName, List<string> rtx, string gameLabel, Action<string> onActivity,
        LazuriteToolchain.UpdateCheck check, CancellationToken ct)
    {
        var setupError = await LazuriteToolchain.EnsureReadyAsync(VanillaBackupFolders(), check, ct);
        if (setupError != null)
            return new(new(UpgradeVerdict.CannotCheck, setupError, []), null, false);

        var work = LazuriteToolchain.NewWorkFolder();
        onActivity($"Checking {displayName} against {gameLabel}...");
        var (result, error) = await LazuriteToolchain.RunAsync(
            ["upgrade", DefaultFolder, work, .. rtx], TimeSpan.FromMinutes(3), ct,
            material => onActivity($"Checking {Path.GetFileName(material).Replace(".material.bin", "")} against {gameLabel}..."));

        if (result?["materials"] is not JsonArray materials)
            return new(new(UpgradeVerdict.CannotCheck, $"lazurite couldn't check this preset ({error ?? "no result"}).", []), work, false);

        var interface_ = new List<string>();
        var unreadable = new List<string>();
        var changes = new List<string>();
        int upgraded = 0;
        bool gameSide = false;

        foreach (var m in materials.OfType<JsonObject>())
        {
            var name = (MinecraftJson.GetString(m["material"]) ?? "?").Replace(".material.bin", "");
            switch (MinecraftJson.GetString(m["case"]))
            {
                case "interface_changed":
                    foreach (var problem in MinecraftJson.GetStringArray(m["problems"]))
                        interface_.Add($"{name}: {problem}");
                    if (MinecraftJson.GetInt(m["problem_count"]) is { } count && count > MinecraftJson.GetStringArray(m["problems"]).Count)
                        interface_.Add($"{name}: and {count - MinecraftJson.GetStringArray(m["problems"]).Count} more");
                    break;
                case "unreadable":
                    var side = MinecraftJson.GetString(m["side"]);
                    gameSide |= side is "game" or "merged" or "converter";
                    unreadable.Add($"{name} ({(side == "preset" ? "the preset's file" : side == "game" ? "the game's file" : "the converter")}): {MinecraftJson.GetString(m["reason"])}");
                    break;
                case "upgraded":
                    upgraded++;
                    var what = MinecraftJson.GetStringArray(m["changes"]);
                    changes.Add($"{name}: {(what.Count > 0 ? string.Join(", ", what) : "container details")}");
                    break;
                case "current":
                    break;
                default:
                    unreadable.Add($"{name}: an answer this app doesn't understand");
                    break;
            }
        }

        // A preset is one consistent set: one material it can't ship safely means none of it is upgraded.
        if (interface_.Count > 0)
            return new(new(UpgradeVerdict.InterfaceChanged,
                $"{displayName} was built for an older game, and {gameLabel} changed what its shaders are given.", interface_), work, false);

        if (unreadable.Count > 0)
            return new(new(UpgradeVerdict.CannotCheck,
                gameSide
                    ? $"Lazurite {LazuriteToolchain.AdoptedVersion} can't read {gameLabel}'s shader files yet - it likely needs an update for this game version."
                    : $"Lazurite can't read {displayName}'s shader files.", unreadable), work, gameSide);

        if (upgraded == 0)
            return new(new(UpgradeVerdict.Current, $"Already packaged for {gameLabel}.", []), work, false);

        return new(new(UpgradeVerdict.Upgraded, $"Upgraded {upgraded} shader file{(upgraded == 1 ? "" : "s")} for {gameLabel}.", changes), work, false);
    }

    /// <summary>
    /// Moves a finished result into the cache: the upgraded files and a record of what was decided. The record
    /// is written last, inside the staging folder, and the folder moved in one step - so a folder that has a
    /// record is always complete.
    /// </summary>
    private void Commit(string folder, string? work, string displayName, UpgradeOutcome outcome)
    {
        var staging = folder + ".staging";
        LazuriteToolchain.TryDelete(staging);
        Directory.CreateDirectory(staging);

        if (outcome.Verdict == UpgradeVerdict.Upgraded && work != null)
            foreach (var file in Directory.GetFiles(work, "*.material.bin"))
                File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));

        var details = new JsonArray();
        foreach (var d in outcome.Details) details.Add((JsonNode?)JsonValue.Create(d));
        var record = new JsonObject
        {
            ["verdict"] = (JsonNode?)JsonValue.Create(outcome.Verdict == UpgradeVerdict.Upgraded ? "upgraded" : "current"),
            ["summary"] = (JsonNode?)JsonValue.Create(outcome.Summary),
            ["details"] = details,
            ["preset"] = (JsonNode?)JsonValue.Create(displayName),
            ["game"] = (JsonNode?)JsonValue.Create(GameVersion),
            ["edition"] = (JsonNode?)JsonValue.Create(IsPreview ? "preview" : "release"),
            ["lazurite"] = (JsonNode?)JsonValue.Create(LazuriteToolchain.AdoptedVersion),
            ["made"] = (JsonNode?)JsonValue.Create(DateTime.UtcNow.ToString("O")),
        };
        File.WriteAllText(Path.Combine(staging, UpgradeRecordFileName), record.ToJsonString());

        LazuriteToolchain.TryDelete(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Directory.Move(staging, folder);
    }

    private static UpgradeOutcome? ReadRecord(string folder)
    {
        var record = MinecraftJson.ParseObjectFile(Path.Combine(folder, UpgradeRecordFileName));
        if (record == null) return null;
        var verdict = MinecraftJson.GetString(record["verdict"]) switch
        {
            "upgraded" => UpgradeVerdict.Upgraded,
            "current" => UpgradeVerdict.Current,
            _ => (UpgradeVerdict?)null
        };
        if (verdict == null) return null;
        if (verdict == UpgradeVerdict.Upgraded && Directory.GetFiles(folder, "*.material.bin").Length == 0) return null;
        return new(verdict.Value, MinecraftJson.GetString(record["summary"]) ?? "", MinecraftJson.GetStringArray(record["details"]));
    }
}
