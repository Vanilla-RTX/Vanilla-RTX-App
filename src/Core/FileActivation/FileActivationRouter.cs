using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using WinUIEx;   // Restore/SetForegroundWindow are WinUIEx extensions on Window

namespace Vanilla_RTX_App.Core.FileActivation;

/// <summary>
/// Everything about being opened <i>from outside the app</i> - a file from Explorer, or a
/// <c>vanillartx://</c> link: reading what an activation
/// launched us with, handing it to the instance that is already running when this one loses
/// the mutex, dropping the duplicate deliveries Windows produces on its own, and routing what
/// survives to whichever import owns that extension, or to MainWindow's link commands
/// (MainWindow.LinkCommands.cs) for a link.
///
/// <para><b>Everything up to the moment a list of paths becomes an import lives here</b>, so
/// that "what happens when someone double-clicks a .rtpack?" has one answer in one file, and
/// adding a file type is one entry rather than a hunt. The imports themselves stay on
/// MainWindow, since what they do is drive its log, progress bar and buttons - see
/// MainWindow.ImportRouters.cs.</para>
///
/// <para><see cref="Routes"/> is the whole of the app's answer to which files it can be
/// opened with, and is meant to be read alongside the FileTypeAssociation entries in
/// Package.appxmanifest - the manifest is what makes Explorer offer us, this is what happens
/// afterwards, and the two have to agree.</para>
///
/// <para><b>The ways in</b>, all from App.OnLaunched:
/// <see cref="HandOffToRunningInstance"/> when this process lost the single-instance mutex,
/// <see cref="HandleWakeAsync"/> when it won and is being woken by one that lost,
/// <see cref="TryRunLaunchLinkWithoutWindowAsync"/> when it won with a link that can run
/// without a window (<see cref="SilentLinks"/>), and <see cref="RouteLaunchAsync"/> when it won
/// and was itself launched with files or a link.</para>
/// </summary>
internal static class FileActivationRouter
{
    /// <summary>
    /// One entry per file type the app can be opened with. Order matters only in that the
    /// first matching route wins, and <see cref="Routes"/>' last entry deliberately matches
    /// everything left over - see <see cref="RouteAsync"/>.
    /// </summary>
    /// <param name="OwnerModule">
    /// The module that owns this file type. When it is open it takes the import
    /// itself (see <see cref="IFileActivationTarget"/>) and is what gets raised; otherwise the
    /// files go to MainWindow via <paramref name="Handler"/>. Must implement
    /// <see cref="IFileActivationTarget"/> or it is ignored.
    /// </param>
    private readonly record struct Route(
        string Label,
        string[] Extensions,
        Func<MainWindow, IReadOnlyList<string>, Task> Handler,
        Type OwnerModule);

    /// <summary>
    /// <list type="bullet">
    /// <item><c>.rtpack</c> - a shader mod preset (BetterRTX or Supervanilla RTX), into this app's own RTX_Cache.</item>
    /// <item>everything else - a Minecraft pack, into Minecraft's resource_packs.</item>
    /// </list>
    ///
    /// <para><b>The pack route is deliberately the catch-all rather than a list of
    /// extensions.</b> Only .mcpack and .rtpack are declared in the manifest, but Explorer's
    /// "Open with" will hand us anything a user points at, and ExpImpDel already accepts
    /// .zip and .mcaddon besides - and, more to the point, answers an unusable file with a
    /// real explanation of its own ("not identified as a resource pack") that is worth far
    /// more to the user than this silently dropping the file would be.</para>
    /// </summary>
    private static readonly Route[] Routes =
    [
        new("Shader mod preset", [".rtpack"], (window, files) => window.ImportShaderModPresetFilesAsync(files),
            typeof(Modules.ShaderMods.ShaderModManagerOverlay)),
        new("Minecraft pack",   [],          (window, files) => window.ImportPackFilesAsync(files),
            typeof(Modules.PackBrowser.PackBrowserOverlay)),
    ];

    // =========================================================================
    // Entry points - App.OnLaunched calls these and nothing else
    // =========================================================================

    /// <summary>
    /// For a process that lost the single-instance mutex and is about to Exit: leaves the
    /// files it was launched with where the running instance will find them.
    ///
    /// <para><b>Call before signalling the wake event, never after.</b> The running instance
    /// reads the hand-off file when it wakes, so writing afterwards is a race it can lose -
    /// and losing it means the user's double-clicked pack is silently dropped.</para>
    /// </summary>
    public static void HandOffToRunningInstance()
    {
        var incoming = GetActivationFilePaths();
        if (GetActivationLink() is { } link)
            incoming.Add(link.OriginalString);
        if (incoming.Count == 0) return;

        WriteHandoffFile(incoming);
    }

    /// <summary>
    /// Everything the wake event means: a second launch either just wants the app in front,
    /// or brought files or a link with it.
    ///
    /// <para><b>Which window is raised is decided here, not by the caller.</b> A plain wake
    /// raises MainWindow. A wake carrying files raises whichever window ends up importing
    /// them, which may not be MainWindow - see <see cref="RouteAsync"/>. Raising MainWindow
    /// unconditionally first is what made a double-clicked file yank the app away from the
    /// module the user had open for exactly that file.</para>
    /// </summary>
    public static async Task HandleWakeAsync()
    {
        var pending = ConsumeHandoffFile();
        var links = pending.Select(AsAppLink).OfType<Uri>().ToList();
        var paths = pending.Where(p => AsAppLink(p) is null).ToList();

        if (paths.Count == 0 && links.Count == 0)
        {
            BringToFront(MainWindow.Instance);
            return;
        }

        await RouteAsync(paths);
        foreach (var link in links)
            await RouteLinkAsync(link);
    }

    /// <summary>
    /// For a cold launch that won the mutex, before MainWindow exists: runs the link this
    /// process was started with, if it is one of <see cref="SilentLinks"/>, with no window at
    /// all. True means it is done and App can exit without ever showing one. False - no link, a
    /// link that navigates, or a silent command that failed - means start up as usual, and
    /// <see cref="RouteLaunchAsync"/> then routes the link like any other.
    /// </summary>
    public static async Task<bool> TryRunLaunchLinkWithoutWindowAsync()
    {
        if (GetActivationLink() is not { } link || SilentLinks.Find(link) is not { } command)
            return false;

        _launchLinkHandled = await SilentLinks.TryRunWithoutWindowAsync(command);
        return _launchLinkHandled;
    }

    /// <summary>
    /// Set when <see cref="TryRunLaunchLinkWithoutWindowAsync"/> already did what this
    /// process's link asked, and App started a window anyway (a second launch arrived while it
    /// ran). <see cref="RouteLaunchAsync"/> must not run it a second time.
    /// </summary>
    private static bool _launchLinkHandled;

    /// <summary>
    /// For a cold launch that won the mutex: imports the files, or follows the link, this
    /// process was itself started with. No hand-off file involved - the activation args carry
    /// them directly. No-ops for an ordinary launch from the Start menu, the taskbar or the
    /// <c>vanillartx</c> command alias.
    /// </summary>
    public static async Task RouteLaunchAsync()
    {
        var launched = GetActivationFilePaths();
        if (launched.Count > 0)
            await RouteAsync(launched);

        if (!_launchLinkHandled && GetActivationLink() is { } link)
            await RouteLinkAsync(link);
    }

    /// <summary>
    /// Hands a <c>vanillartx://</c> link to MainWindow. A link that navigates raises the window
    /// first - see MainWindow.LinkCommands.cs. A <see cref="SilentLinks"/> command runs where
    /// the window already is, and raises it only if it failed, so its log is in front of the
    /// user. Links skip <see cref="FilterRecentlyHandledFiles"/>: a navigation lands somewhere
    /// the user already is, and a silent command is idempotent by that class's own rule.
    /// </summary>
    private static async Task RouteLinkAsync(Uri link)
    {
        if (MainWindow.Instance == null) return;

        if (SilentLinks.Find(link) is { } command)
        {
            if (!await MainWindow.Instance.RunSilentLinkAsync(command))
                BringToFront(MainWindow.Instance);
            return;
        }

        BringToFront(MainWindow.Instance);
        await MainWindow.Instance.OpenLinkAsync(link);
    }

    /// <summary>
    /// The command and optional section a link names. <c>vanillartx://help/rtx-reactor</c> is
    /// command "help", section "rtx-reactor"; so is <c>vanillartx://help#rtx-reactor</c>. The
    /// form without slashes (<c>vanillartx:help</c>) has no host, so the path stands in for it.
    /// A leading "open" is dropped, so <c>OpenPackBrowser</c> and <c>packbrowser</c> agree.
    /// </summary>
    internal static (string Command, string? Section) ParseLink(Uri uri)
    {
        var segments = (uri.Host + "/" + uri.AbsolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();

        var command = segments.Count > 0 ? segments[0].ToLowerInvariant() : string.Empty;
        if (command.Length > 4 && command.StartsWith("open", StringComparison.Ordinal))
            command = command[4..];

        var section = segments.Count > 1 ? segments[1]
            : uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..])
            : null;

        return (command, section);
    }

    /// <summary>
    /// <paramref name="value"/> as one of this app's links, or null if it isn't one. A hand-off
    /// line is either a file path or a link, and no Windows path can start with
    /// "vanillartx:" - a drive letter is one character - so the scheme alone tells them apart.
    /// </summary>
    private static Uri? AsAppLink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(MainWindow.LinkScheme, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    // =========================================================================
    // Routing
    // =========================================================================

    /// <summary>
    /// Splits incoming activation paths by extension and hands each group to the import that
    /// owns it. Every activation path funnels through here, whether the launch was cold or
    /// handed off from a losing second instance (see <see cref="ConsumeHandoffFile"/>), so a
    /// mixed selection - unlikely, but Explorer permits it - still routes correctly instead
    /// of one type winning outright.
    ///
    /// <para><see cref="FilterRecentlyHandledFiles"/> runs first, and is what handles Windows
    /// activating the FTA handler more than once for a single "Open with" - two or three
    /// separate deliveries of an identical file list within seconds. MainWindow's per-type
    /// import locks stop those interleaving, but both still reach an import and the second
    /// surfaces as duplicate/already-installed warnings for files that were never duplicates.
    /// Dropping a path already seen means the second delivery never reaches an import at
    /// all.</para>
    /// </summary>
    public static async Task RouteAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || MainWindow.Instance == null) return;

        var remaining = FilterRecentlyHandledFiles(paths);
        if (remaining.Count == 0) return;

        foreach (var route in Routes)
        {
            List<string> matched;

            if (route.Extensions.Length == 0)
            {
                matched = remaining;
                remaining = new List<string>();
            }
            else
            {
                matched = remaining
                    .Where(p => route.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                remaining = remaining.Except(matched, StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (matched.Count == 0) continue;

            // A module that owns this file type and is already open takes it - the user
            // opened it for this, so that is where they are looking and where the dialogs
            // belong. It is an overlay over MainWindow, so raising MainWindow raises it.
            if (FindOpenOwner(route) is { } owner)
            {
                Trace.WriteLine($"[FileActivation] Routing {matched.Count} file(s) to the open {route.Label} module.");
                BringToFront(MainWindow.Instance);
                await owner.ImportActivatedFilesAsync(matched);
                continue;
            }

            Trace.WriteLine($"[FileActivation] Routing {matched.Count} file(s) to {route.Label} import.");
            BringToFront(MainWindow.Instance);
            await route.Handler(MainWindow.Instance, matched);
        }
    }

    /// <summary>
    /// The open module that owns this route, or null when none is. Null is the ordinary case
    /// - the user double-clicked a file without the matching module open - and means the
    /// import goes through MainWindow.
    /// </summary>
    private static IFileActivationTarget? FindOpenOwner(Route route) =>
        MainWindow.Instance?.FindOpenModule(route.OwnerModule) as IFileActivationTarget;

    /// <summary>
    /// Un-minimises and raises a window. Both halves are needed: Restore alone leaves a
    /// minimised window restored but behind, and SetForegroundWindow alone does nothing to a
    /// minimised one.
    /// </summary>
    private static void BringToFront(Window? window)
    {
        if (window == null) return;

        try
        {
            window.Restore();
            window.SetForegroundWindow();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Could not raise window: {ex.Message}");
        }
    }

    // How close together two deliveries of the same path have to be to be treated as the
    // same underlying user action rather than a deliberate later re-import. Windows'
    // double-activation quirk delivers the duplicate within a couple of seconds at most
    // (direct cold-launch args vs. the wake-event hand-off both firing off one Explorer
    // action); this is generous well past that without being long enough to ever swallow a
    // genuine second click.
    private static readonly TimeSpan RecentFileActivationWindow = TimeSpan.FromSeconds(15);

    private static readonly Dictionary<string, DateTime> RecentlyHandledFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RecentlyHandledFilesLock = new();

    /// <summary>
    /// Drops any path this process has already accepted for import within
    /// <see cref="RecentFileActivationWindow"/>, and records the rest as freshly accepted.
    /// Keyed on the raw path as Windows/Explorer hand it over - every delivery of "the same
    /// file" for one user action carries an identical path string, so no normalization is
    /// needed.
    /// </summary>
    private static List<string> FilterRecentlyHandledFiles(IReadOnlyList<string> paths)
    {
        var now = DateTime.UtcNow;
        var result = new List<string>(paths.Count);

        lock (RecentlyHandledFilesLock)
        {
            // Occasional sweep so a long-running instance doesn't accumulate entries forever.
            if (RecentlyHandledFiles.Count > 200)
            {
                foreach (var stale in RecentlyHandledFiles
                    .Where(kv => now - kv.Value > RecentFileActivationWindow)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    RecentlyHandledFiles.Remove(stale);
                }
            }

            foreach (var path in paths)
            {
                if (RecentlyHandledFiles.TryGetValue(path, out var lastSeen)
                    && now - lastSeen < RecentFileActivationWindow)
                {
                    Trace.WriteLine($"[FileActivation] Dropping duplicate delivery of '{path}' - accepted for import {(now - lastSeen).TotalSeconds:F1}s ago.");
                    continue;
                }

                RecentlyHandledFiles[path] = now;
                result.Add(path);
            }
        }

        return result;
    }

    // =========================================================================
    // Where the paths come from
    // =========================================================================

    /// <summary>
    /// Reads the paths this process was actually launched with, if it was a file activation
    /// ("Open with", double-click on a .mcpack or .rtpack) - empty otherwise, including for
    /// the ordinary icon-launch case. The classic LaunchActivatedEventArgs OnLaunched
    /// receives doesn't carry file activation data for a full-trust packaged app; that lives
    /// on AppInstance.GetCurrent's own activation args regardless of which OnLaunched
    /// overload fired.
    /// </summary>
    internal static List<string> GetActivationFilePaths()
    {
        try
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs?.Kind != ExtendedActivationKind.File) return new List<string>();
            if (activationArgs.Data is not FileActivatedEventArgs fileArgs) return new List<string>();

            return fileArgs.Files
                .OfType<IStorageFile>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Failed to read activation args: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// The <c>vanillartx://</c> link this process was launched with, or null for any other kind
    /// of launch. Read from the same activation args as <see cref="GetActivationFilePaths"/>,
    /// for the same reason.
    /// </summary>
    internal static Uri? GetActivationLink()
    {
        try
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs?.Kind != ExtendedActivationKind.Protocol) return null;
            return (activationArgs.Data as IProtocolActivatedEventArgs)?.Uri;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Failed to read protocol activation args: {ex.Message}");
            return null;
        }
    }

    private static string HandoffFilePath =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "pending_pack_import.txt");

    /// <summary>
    /// How a second launch that lost the single-instance mutex hands its files to the
    /// instance that won, which picks them up on the wake signal.
    ///
    /// <para>One file path or link per line, every kind mixed together freely - the wake handler
    /// sorts them back out on the reading side (see <see cref="AsAppLink"/>). Plain text rather than JSON is a
    /// deliberate choice here, not laziness: Release publishes trimmed, and JsonSerializer's
    /// generic overloads need a source-generated context to survive that (see
    /// AlchitexJsonContext for the pattern and what happens without it). A flat file of paths
    /// needs none of that, and a Windows path can never itself contain a newline.</para>
    /// </summary>
    internal static void WriteHandoffFile(IReadOnlyList<string> paths)
    {
        try { File.WriteAllLines(HandoffFilePath, paths); }
        catch (Exception ex) { Trace.WriteLine($"[FileActivation] Failed to write hand-off file: {ex.Message}"); }
    }

    /// <summary>Reads and deletes the hand-off file in one go - it is only ever read once.</summary>
    internal static List<string> ConsumeHandoffFile()
    {
        try
        {
            if (!File.Exists(HandoffFilePath)) return new List<string>();

            var paths = File.ReadAllLines(HandoffFilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            File.Delete(HandoffFilePath);
            return paths;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileActivation] Failed to read hand-off file: {ex.Message}");
            return new List<string>();
        }
    }
}
