using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.ShaderMods;
using Vanilla_RTX_App.Modules.LUT;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Outcome of a pre-wipe "is a custom preset currently installed?" check.
/// </summary>
public enum RTXDefaultsGuard
{
    /// <summary>No Default backup exists, or the game already matches it - nothing to do.</summary>
    NoActionNeeded,

    /// <summary>A non-default preset was detected and successfully reverted to Default.</summary>
    Restored,

    /// <summary>A non-default preset was detected but reverting failed (elevation declined / IO error).</summary>
    RestoreFailed,

    /// <summary>A Default backup exists but its state relative to the game couldn't be safely verified, so nothing was touched.</summary>
    Skipped
}

/// <summary>
/// QoL safety net for the "hard wipe" button. The shader mod manager and RTX LUT both keep a
/// Default preset backup used to restore the game's original files - but that backup
/// lives in local storage, which the reset button wipes. If the game currently has a
/// non-default preset installed when that happens, the user loses their only path back
/// to vanilla files. This guard checks each feature's state right before the wipe and,
/// if needed, silently reverts to Default first (one UAC prompt per dirty backup - each
/// feature keeps one per Minecraft edition).
/// </summary>
public static class DefaultsGuard
{
    /// <summary>
    /// Runs the shader mod check for <b>both</b> editions, since each keeps its own Default
    /// backup and the wipe is about to take both with it. The LUT guard is called once per
    /// edition from the call site instead; this one answers for the feature as a whole
    /// because its two backups live in one cache folder and are cleared by one wipe.
    ///
    /// <para>The combined result keeps the worst news: RestoreFailed beats Restored beats
    /// Skipped, and only "neither edition needed anything" reports NoActionNeeded. Each
    /// edition logs under its own label, so the combined verdict never hides which one it
    /// came from. Each dirty edition costs one UAC prompt.</para>
    /// </summary>
    public static async Task<RTXDefaultsGuard> RestoreShaderModDefaultIfNeededAsync(Action<string>? log = null)
    {
        var release = await RestoreShaderModDefaultForEditionAsync(targetPreview: false, log);
        var preview = await RestoreShaderModDefaultForEditionAsync(targetPreview: true, log);

        if (release == RTXDefaultsGuard.RestoreFailed || preview == RTXDefaultsGuard.RestoreFailed)
            return RTXDefaultsGuard.RestoreFailed;

        if (release == RTXDefaultsGuard.Restored || preview == RTXDefaultsGuard.Restored)
            return RTXDefaultsGuard.Restored;

        if (release == RTXDefaultsGuard.Skipped || preview == RTXDefaultsGuard.Skipped)
            return RTXDefaultsGuard.Skipped;

        return RTXDefaultsGuard.NoActionNeeded;
    }

    // Folder layout comes from ShaderModManager rather than being spelled again here: this
    // guard protects that feature's backup, so it has to resolve the same folder permanently.
    // A local copy of "RTX_Cache" / "__DEFAULT" silently stops matching the moment the
    // manager's layout changes - a new edition, a renamed folder - and guards nothing.
    private static async Task<RTXDefaultsGuard> RestoreShaderModDefaultForEditionAsync(bool targetPreview, Action<string>? log)
    {
        var tag = $"[ShaderMods Guard{(targetPreview ? " Preview" : "")}]";

        try
        {
            var defaultFolder = ShaderModManager.GetDefaultFolderPath(targetPreview);
            var folderName = ShaderModManager.GetDefaultFolderName(targetPreview);

            if (defaultFolder == null || !Directory.Exists(defaultFolder))
            {
                log?.Invoke($"{tag} No {folderName} backup exists - nothing to protect.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var defaultBinFiles = Directory.GetFiles(defaultFolder, "*.bin", SearchOption.TopDirectoryOnly).ToList();
            if (defaultBinFiles.Count == 0)
            {
                log?.Invoke($"{tag} {folderName} folder exists but has no .bin files, nothing to restore.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var cachedPath = targetPreview ? Persistent.MinecraftPreviewInstallPath : Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, targetPreview))
            {
                log?.Invoke($"{tag} Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return RTXDefaultsGuard.Skipped;
            }

            var gameMaterialsPath = Path.Combine(cachedPath!, "data", "renderer", "materials");
            if (!Directory.Exists(gameMaterialsPath))
            {
                log?.Invoke($"{tag} Materials folder not found in game install - can't verify current preset state.");
                return RTXDefaultsGuard.Skipped;
            }

            var defaultHashes = ShaderModManager.GetPresetHashes(defaultBinFiles);
            var currentHashes = ShaderModManager.GetCurrentlyInstalledHashes(gameMaterialsPath);

            if (currentHashes.Count == 0)
            {
                log?.Invoke($"{tag} Could not read any RTX files from the game - skipping to avoid acting on incomplete info.");
                return RTXDefaultsGuard.Skipped;
            }

            if (ShaderModManager.AreHashesMatching(currentHashes, defaultHashes))
            {
                log?.Invoke($"{tag} Game already matches Default - nothing to do.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            log?.Invoke($"{tag} Non-default preset detected - restoring Default before wipe...");

            var filesToApply = defaultBinFiles
                .Select(src => (src, Path.Combine(gameMaterialsPath, Path.GetFileName(src))))
                .ToList();

            var success = await Helpers.ReplaceFilesWithElevation(filesToApply, tag, "betterrtx_predelete_restore");

            log?.Invoke(success ? $"{tag} Default restored successfully." : $"{tag} Failed to restore Default.");
            return success ? RTXDefaultsGuard.Restored : RTXDefaultsGuard.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{tag} Exception: {ex.Message}");
            return RTXDefaultsGuard.Skipped;
        }
    }

    // File names, folder layout and the hash check all come from LUTManager rather than being
    // spelled again here: this guard protects that feature's backup, so it has to resolve the
    // same files in the same place permanently - including *which* folder, since each edition
    // keeps its own.
    public static async Task<RTXDefaultsGuard> RestoreLutDefaultIfNeededAsync(bool targetPreview, Action<string>? log = null)
    {
        var tag = $"[LUT Guard{(targetPreview ? " Preview" : "")}]";

        try
        {
            var defaultsFolder = LUTManager.GetDefaultsFolderPath(targetPreview);

            if (defaultsFolder == null || !LUTManager.AllFiles.Any(f => File.Exists(Path.Combine(defaultsFolder, f))))
            {
                log?.Invoke($"{tag} No usable Default backup exists - nothing to protect.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            var cachedPath = targetPreview ? Persistent.MinecraftPreviewInstallPath : Persistent.MinecraftInstallPath;
            if (!MinecraftGDKLocator.RevalidateCachedPath(cachedPath, targetPreview))
            {
                log?.Invoke($"{tag} Default backup exists but no valid Minecraft path is known - can't verify or restore.");
                return RTXDefaultsGuard.Skipped;
            }

            // Only the files the backup actually holds - however many the game had when it
            // was taken. Same set LUTManager.InstallAsync writes for the Default preset, for
            // the same reason: a file that was never backed up cannot be restored.
            var pairs = new List<(string, string)>();
            foreach (var fileName in LUTManager.AllFiles)
            {
                var backup = Path.Combine(defaultsFolder, fileName);
                if (File.Exists(backup))
                    pairs.Add((backup, LUTManager.GameFilePath(cachedPath!, fileName)));
            }

            if (pairs.Count == 0)
            {
                log?.Invoke($"{tag} Backup holds no ray_tracing files - nothing to restore.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            bool alreadyDefault = pairs.All(pair => File.Exists(pair.Item2) && LUTManager.HashesMatch(pair.Item2, pair.Item1));

            if (alreadyDefault)
            {
                log?.Invoke($"{tag} Game already matches Default - nothing to do.");
                return RTXDefaultsGuard.NoActionNeeded;
            }

            log?.Invoke($"{tag} Non-default preset detected - restoring Default before wipe...");

            var success = await Helpers.ReplaceFilesWithElevation(pairs, tag, "rtx_defaults_predelete_restore");

            log?.Invoke(success ? $"{tag} Default restored successfully." : $"{tag} Failed to restore Default.");
            return success ? RTXDefaultsGuard.Restored : RTXDefaultsGuard.RestoreFailed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{tag} Exception: {ex.Message}");
            return RTXDefaultsGuard.Skipped;
        }
    }
}
