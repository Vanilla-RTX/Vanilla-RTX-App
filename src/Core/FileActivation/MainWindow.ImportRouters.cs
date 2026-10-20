using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;

// Filed under Core\FileActivation with the router that calls it, rather than beside
// MainWindow, so the whole Explorer-activation path reads as one unit. The namespace stays
// Vanilla_RTX_App because this is a partial of MainWindow - it is the one place in the repo
// where namespace and folder deliberately disagree.
namespace Vanilla_RTX_App;

/// <summary>
/// What actually happens to a file opened from Explorer once
/// <see cref="Core.FileActivation.FileActivationRouter"/> has decided where it belongs.
///
/// <para><b>These live on MainWindow because what they do is drive it:</b> its log carries
/// every per-file message from the import, its progress bar runs for the duration, its
/// buttons are disabled while it runs, and any open module showing that list is refreshed so
/// it isn't left showing a list built before the import landed. All of that is private to
/// the window; a partial keeps it that way.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The open module of the given type, or null when it isn't open.
    ///
    /// <para>MainWindow is the only thing that tracks open modules - it opens them and
    /// removes them from its list on Closed - so a caller that needs to know whether one is
    /// up has to ask here. Used by <see cref="Core.FileActivation.FileActivationRouter"/> to
    /// hand a file activation to the module that owns that file type.</para>
    /// </summary>
    internal object? FindOpenModule(Type moduleType) =>
        _openModules.FirstOrDefault(m => m.GetType() == moduleType);

    /// <summary>
    /// Serializes .mcpack imports specifically - see ImportShaderModPresetFilesAsync's own
    /// RtpackImportLock for why .rtpack gets a separate one rather than sharing this: they
    /// deploy to completely unrelated folders (Minecraft's resource_packs vs. this app's own
    /// RTX_Cache) and share no state, so there's no reason a slow .rtpack import should make
    /// an .mcpack drop wait, or vice versa.
    ///
    /// What this guards against: Windows can (and, observed firsthand, does) activate the FTA
    /// handler more than once for a single multi-select "Open with" - one process wins the
    /// launch mutex and imports its files directly, but a second process can lose that race
    /// and still be carrying the very same file list, which it hands off to the first through
    /// FileActivationRouter's wake-event/hand-off mechanism. The router's own
    /// FilterRecentlyHandledFiles is the first line of defence against that - it drops a
    /// duplicate file list before either import path ever sees it - but this lock is what
    /// keeps two *different* concurrent .mcpack requests (not duplicates of each other, just
    /// two genuinely separate drops close together) from interleaving into the same Log()
    /// output and racing ExpImpDel's own duplicate-UUID check, which reads the destination
    /// folder to see if a pack is already installed and can be fooled by a read landing
    /// before an unrelated concurrent import has finished writing its own manifest.
    ///
    /// Held for the full duration of a batch, not per file - so a second request arriving
    /// mid-batch queues behind the entire first one rather than interleaving with it, which is
    /// what actually guarantees "one at a time" instead of merely "one file at a time within
    /// whichever call happens to be running".
    /// </summary>
    private static readonly SemaphoreSlim McpackImportLock = new(1, 1);

    /// <summary>
    /// Entry point for .mcpack file activation (see FileActivationRouter), used when
    /// no pack browser is open - one that is takes the import itself
    /// (<see cref="Core.FileActivation.IFileActivationTarget"/>).
    ///
    /// <para>This never <i>opens</i> one on the user's behalf: a module opening over a
    /// window nobody asked it to leave, and PackBrowserOverlay's own "no Minecraft data location yet"
    /// fallback putting a folder picker in front of them unprompted - reachable here because
    /// MainWindow_Loaded resolves that cache asynchronously and this can run first.</para>
    ///
    /// <para>Calls ExpImpDel directly instead - the same utility PackBrowserOverlay's Add-pack
    /// button and drag-and-drop both reach downstream - and reports through the same Log() the
    /// rest of the window uses, one pack at a time, no modules and no pickers either way.</para>
    ///
    /// Per-file messages come straight from ExpImpDel.ImportStatusChanged rather than a
    /// generic pass/fail here - PackBrowserOverlay already subscribes to the exact same event
    /// for its own status text, so this reuses the same wording a manual import would show
    /// ("duplicate already installed", "not identified as a resource pack", etc.) instead of
    /// inventing a second, vaguer vocabulary for the headless path.
    /// </summary>
    public async Task ImportPackFilesAsync(IReadOnlyList<string> filePaths)
    {
        string[] ToDisable = ["BrowsePacksButton"];

        var paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;

        // Waits for MainWindow_Loaded to have actually resolved the Minecraft data location
        // - see the remarks above for why guessing at a fixed delay isn't good enough here.
        await WaitUntilInitializedAsync();

        await McpackImportLock.WaitAsync();
        _progressManager.ShowProgress();
        try
        {
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

            var names = paths.Select(p => Path.GetFileNameWithoutExtension(p) ?? p).ToList();
            Log($"Starting to import:\n{string.Join(Environment.NewLine, names)}", LogLevel.Import);

            void OnStatus(string message) => Log(message, LogLevel.Import);
            ExpImpDel.ImportStatusChanged += OnStatus;

            // Same confirmation dialogs PackBrowserOverlay shows for a non-resource/duplicate
            // pack - the Confirm* hooks are static and global, so save and restore whatever
            // PackBrowserOverlay (if open) left there rather than clobbering it for the
            // duration of this import.
            var previousConfirmOverwrite = ExpImpDel.ConfirmOverwrite;
            var previousConfirmNonResourceImport = ExpImpDel.ConfirmNonResourceImport;
            var previousConfirmBehaviorImport = ExpImpDel.ConfirmBehaviorImport;
            ExpImpDel.ConfirmOverwrite = (packName, existingPath) => ImportDialogs.ShowOverwriteDialogAsync(RootElement, packName, existingPath);
            ExpImpDel.ConfirmNonResourceImport = packName => ImportDialogs.ShowNonResourceDialogAsync(RootElement, packName);
            ExpImpDel.ConfirmBehaviorImport = packName => ImportDialogs.ShowBehaviorDialogAsync(RootElement, packName);

            var succeeded = 0;
            try
            {
                foreach (var path in paths)
                {
                    if (await ExpImpDel.ImportFromPathsAsync(new[] { path }))
                        succeeded++;
                }
            }
            finally
            {
                ExpImpDel.ImportStatusChanged -= OnStatus;
                ExpImpDel.ConfirmOverwrite = previousConfirmOverwrite;
                ExpImpDel.ConfirmNonResourceImport = previousConfirmNonResourceImport;
                ExpImpDel.ConfirmBehaviorImport = previousConfirmBehaviorImport;
            }

            Log(succeeded == paths.Count
                ? $"Finished importing {succeeded} pack{(paths.Count == 1 ? "" : "s")}."
                : $"Imported {succeeded} out of {paths.Count} pack{(paths.Count == 1 ? "" : "s")} - Use '{BrowsePacksButtonText.Text}' menu to import it manually, and see what it says.",
                succeeded == paths.Count ? LogLevel.Success : LogLevel.Warning);

            // If the user already has PackBrowserOverlay open, its list was built before this
            // import landed - refresh it so it isn't left showing stale contents.
            if (succeeded > 0)
                foreach (var packBrowser in _openModules.OfType<Modules.PackBrowser.PackBrowserOverlay>())
                    await packBrowser.LoadPacksAsync();
        }
        finally
        {
            _progressManager.HideProgress();
            McpackImportLock.Release();
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);

        }
    }

    /// <summary>
    /// Serializes .rtpack imports specifically - kept separate from McpackImportLock
    /// deliberately: .mcpack and .rtpack deploy to completely different, unrelated folders
    /// (Minecraft's own resource_packs vs. this app's RTX_Cache) and share no state, so there
    /// is no correctness reason for a slow import of one type to hold up the other. The
    /// double-activation hazard McpackImportLock's remarks describe applies here just the
    /// same, and FileActivationRouter's FilterRecentlyHandledFiles is the same first line of defence -
    /// this lock is only for genuinely separate concurrent .rtpack requests.
    /// </summary>
    private static readonly SemaphoreSlim RtpackImportLock = new(1, 1);

    /// <summary>
    /// Entry point for .rtpack file activation when the shader mod manager isn't open - same
    /// idea as ImportPackFilesAsync, just for .rtpack shader mod presets. When the manager is
    /// open, <see cref="Core.FileActivation.FileActivationRouter"/> hands the files to it
    /// instead (§2g). Does not open the manager: importing into the app's preset cache is
    /// <see cref="Modules.ShaderMods.ShaderModManager.ImportPresetFilesHeadlessAsync"/>, which
    /// needs no UI at all. Installing a preset into the game still only ever happens from the
    /// manager itself - this only puts the preset in its list, ready for the next time it opens.
    /// </summary>
    public async Task ImportShaderModPresetFilesAsync(IReadOnlyList<string> filePaths)
    {
        string[] ToDisable = ["LaunchShaderModManagerButton"];

        var paths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;

        await WaitUntilInitializedAsync();

        await RtpackImportLock.WaitAsync();
        _progressManager.ShowProgress();
        try
        {
            WindowControlsManager.ToggleSpecificControls(this, false, ToDisable);

            var names = paths.Select(p => Path.GetFileNameWithoutExtension(p) ?? p).ToList();
            Log($"Importing {paths.Count} shader mod preset{(paths.Count == 1 ? "" : "s")}:\n{string.Join(Environment.NewLine, names)}", LogLevel.ShaderMods);

            var (succeeded, total) = await Modules.ShaderMods.ShaderModManager.ImportPresetFilesHeadlessAsync(
                paths, message => Log(message, LogLevel.ShaderMods));

            if (total == 0)
            {
                Log("No supported .rtpack file(s) to import.", LogLevel.Warning);
                return;
            }

            Log(succeeded == total
                ? $"Finished importing {succeeded} BetterRTX preset{(succeeded == 1 ? "" : "s")}.\nOpen BetterRTX Manager to install {(succeeded == 1 ? "it" : "one")}."
                : $"Imported {succeeded} out of {total} shader mod preset{(total == 1 ? "" : "s")} - Use Shader mod manager's own Add button to import manually instead.",
                succeeded == total ? LogLevel.Success : LogLevel.Warning);

            // If the user already has ShaderModManagerOverlay open, its list was built before
            // this import landed - refresh it so it isn't left showing stale contents.
            if (succeeded > 0)
                foreach (var manager in _openModules.OfType<Modules.ShaderMods.ShaderModManagerOverlay>())
                    await manager.RefreshLocalPresetsAsync();
        }
        finally
        {
            _progressManager.HideProgress();
            WindowControlsManager.ToggleSpecificControls(this, true, ToDisable);
            RtpackImportLock.Release();
        }
    }

}
