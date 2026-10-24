using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// One options.txt parameter and the value the app writes into it. Minecraft Bedrock's
/// options.txt is a flat <c>name:value</c> file and every setting the app has ever needed to
/// touch is an integer, so that is the whole shape - a value the game stores as a float or a
/// string is out of scope and should not be forced through here.
/// </summary>
public readonly record struct LaunchOption(string Name, int Value);

/// <summary>
/// What a launch attempt did. <see cref="Launched"/> is only whether the game was handed its
/// protocol - a launch can still carry warnings in <see cref="Log"/> (one options.txt that
/// couldn't be written, say). A caller with nowhere to show the log uses it to decide whether
/// the attempt needs somewhere that can; see SilentLinks.
/// </summary>
public readonly record struct LaunchOutcome(bool Launched, string Log);

public class MinecraftLauncher
{
    /// <summary>
    /// What a fresh install launches with: ray tracing on (<c>graphics_mode 3</c>), the in-game
    /// graphics-mode switcher enabled, and VSync off for latency. This is the set the settings
    /// panel's Reset restores, and the one <see cref="ParseOptions"/> falls back to when nothing
    /// is stored yet.
    /// </summary>
    public static readonly LaunchOption[] DefaultOptions =
    {
        new("graphics_mode", 3),
        new("graphics_mode_switch", 1),
        new("gfx_vsync", 0)
    };

    /// <summary>
    /// Separator between entries in the persisted form. Not a valid character in an options.txt
    /// parameter name, so it can never appear inside one and split a name in half.
    /// </summary>
    private const char EntrySeparator = ';';

    // -------------------------------------------------------------------------
    // PUBLIC API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Launches the game with whatever options the user has configured in the settings panel
    /// (<see cref="EnvironmentVariables.Persistent.LaunchOptions"/>). This is what the Launch
    /// button calls.
    ///
    /// <para>With <see cref="EnvironmentVariables.Persistent.CloseGameBeforeLaunch"/> on, the
    /// edition's own running game is ended first (<see cref="CloseRunningGameAsync"/>). That runs
    /// before options.txt is written, not after: a game still open would write its own options
    /// back over ours on exit, and the write in between is what gives the closed process a moment
    /// to let go before the protocol starts a new one.</para>
    ///
    /// <para>An empty configured set is a deliberate choice, not an error: the user removed
    /// every row because they want the game launched with nothing touched. The plain protocol
    /// launch below is what that means, and it must not silently fall back to the defaults -
    /// doing so would rewrite options the user explicitly asked us to leave alone.</para>
    /// </summary>
    public static async Task<LaunchOutcome> LaunchConfiguredMinecraftRTXAsync(bool isTargetingPreview)
    {
        var closeMessages = EnvironmentVariables.Persistent.CloseGameBeforeLaunch
            ? await CloseRunningGameAsync(isTargetingPreview)
            : new List<string>();

        var options = ParseOptions(EnvironmentVariables.Persistent.LaunchOptions);

        var outcome = options.Length == 0
            ? await LaunchOnlyAsync(isTargetingPreview)
            : await LaunchWithOptionsAsync(isTargetingPreview, launchAfterUpdate: true, options);

        return closeMessages.Count == 0
            ? outcome
            : outcome with { Log = string.Join("\n", closeMessages.Append(outcome.Log)) };
    }

    /// <summary>
    /// The game's own executables, relative to an install folder <see cref="MinecraftGDKLocator"/>
    /// resolved. <c>gamelaunchhelper.exe</c> is the GDK bootstrapper that starts and outlives
    /// <c>Minecraft.Windows.exe</c>; a leftover one is as able to swallow a new launch as the game.
    /// </summary>
    private static readonly string[] GameExecutables =
    {
        Path.Combine("Content", "Minecraft.Windows.exe"),
        Path.Combine("Content", "gamelaunchhelper.exe")
    };

    /// <summary>How long a killed process gets to actually exit before the launch carries on regardless.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ends every process whose executable is one of <see cref="GameExecutables"/> under the
    /// install path the app holds for <paramref name="isPreview"/>'s edition, and returns what is
    /// worth telling the user. Never throws; the launch goes ahead whatever happens here.
    ///
    /// <para><b>Matching is by full path and nothing else, deliberately.</b> Both editions run a
    /// <c>Minecraft.Windows.exe</c>, and third-party launchers start more of them from their own
    /// folders, so killing by name would close games the user never asked about. The install path
    /// is one the locator validated and resolved to its physical location
    /// (<see cref="Helpers.ResolveToPhysicalPath"/>), which is also the form Windows reports a process's image in, so a plain
    /// case-insensitive comparison is exact. With no install path known nothing is closed.</para>
    ///
    /// <para>Reading another process's path fails for some (access denied, or exited mid-read),
    /// so each is tried on its own and an unreadable one is left running.</para>
    /// </summary>
    private static async Task<List<string>> CloseRunningGameAsync(bool isPreview)
    {
        var messages = new List<string>();
        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isPreview);

        var installPath = isPreview
            ? EnvironmentVariables.Persistent.MinecraftPreviewInstallPath
            : EnvironmentVariables.Persistent.MinecraftInstallPath;

        if (string.IsNullOrWhiteSpace(installPath))
        {
            messages.Add($"⚠️ Couldn't check whether {versionName} is already open: its install location isn't known. Set it in Settings to have it closed before launching.");
            return messages;
        }

        HashSet<string> targets;
        try
        {
            targets = new HashSet<string>(
                GameExecutables.Select(exe => Path.GetFullPath(Path.Combine(installPath, exe))),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            messages.Add($"⚠️ Couldn't check whether {versionName} is already open: {ex.Message}");
            return messages;
        }

        var closed = 0;

        foreach (var name in GameExecutables.Select(Path.GetFileNameWithoutExtension))
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MinecraftLauncher] Listing {name} processes failed: {ex.Message}");
                continue;
            }

            foreach (var process in candidates)
            {
                using (process)
                {
                    string? imagePath;
                    try { imagePath = process.MainModule?.FileName; }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[MinecraftLauncher] Couldn't read the path of {name} ({process.Id}), left running: {ex.Message}");
                        continue;
                    }

                    if (imagePath is null || !targets.Contains(imagePath))
                        continue;

                    try
                    {
                        process.Kill();
                        using var timeout = new CancellationTokenSource(CloseTimeout);
                        await process.WaitForExitAsync(timeout.Token);
                        closed++;
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited between being listed and being killed - the outcome we wanted.
                        closed++;
                    }
                    catch (OperationCanceledException)
                    {
                        messages.Add($"⚠️ {Path.GetFileName(imagePath)} ({versionName}) didn't exit within {CloseTimeout.TotalSeconds:0} seconds of being closed; launching anyway.");
                    }
                    catch (Exception ex)
                    {
                        messages.Add($"⚠️ Couldn't close {Path.GetFileName(imagePath)} ({versionName}): {ex.Message}");
                    }
                }
            }
        }

        if (closed > 0)
            messages.Add($"Closed {closed} running {versionName} process(es) before launching.");

        return messages;
    }

    /// <summary>
    /// Reads the persisted <c>name=value;name=value</c> form back into options. Anything
    /// unreadable - a missing <c>=</c>, a value that isn't an integer, an empty name - is
    /// dropped rather than failing the whole string, so one corrupt entry costs that entry and
    /// not the user's entire configuration. A null/blank string means "nothing has been saved
    /// yet" and yields <see cref="DefaultOptions"/>; an explicitly empty configuration is stored
    /// as <see cref="EmptyMarker"/> so those two cases stay distinguishable.
    /// </summary>
    public static LaunchOption[] ParseOptions(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return DefaultOptions.ToArray();

        if (stored.Trim() == EmptyMarker)
            return Array.Empty<LaunchOption>();

        var parsed = new List<LaunchOption>();

        foreach (var entry in stored.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.Split('=', 2);
            if (split.Length != 2) continue;

            var name = split[0].Trim();
            if (name.Length == 0) continue;

            // InvariantCulture, and int rather than the culture-aware parse a UI would use: this
            // string is written by the app and read back by the app, so it must not start
            // meaning something else because the user changed their regional settings.
            if (!int.TryParse(split[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            parsed.Add(new LaunchOption(name, value));
        }

        return parsed.ToArray();
    }

    /// <summary>
    /// The inverse of <see cref="ParseOptions"/>. Entries with a blank name are skipped - the
    /// settings panel lets a row exist while it is being typed into, and a half-filled row is
    /// not a configuration.
    /// </summary>
    public static string SerializeOptions(IEnumerable<LaunchOption> options)
    {
        var text = string.Join(EntrySeparator, options
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => $"{o.Name.Trim()}={o.Value.ToString(CultureInfo.InvariantCulture)}"));

        return text.Length == 0 ? EmptyMarker : text;
    }

    /// <summary>
    /// Stands in for "the user deliberately configured no options at all". A plain empty string
    /// can't say that - it is also what an unset setting looks like - and the two have to launch
    /// differently (see <see cref="LaunchConfiguredMinecraftRTXAsync"/>).
    /// </summary>
    public const string EmptyMarker = "none";

    /// <summary>
    /// Launches the game without reading or writing a single options.txt. The "no options
    /// configured" path - see <see cref="LaunchConfiguredMinecraftRTXAsync"/>.
    /// </summary>
    private static async Task<LaunchOutcome> LaunchOnlyAsync(bool isTargetingPreview)
    {
        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);
        var messages = new List<string> { $"No launch options are configured - launching {versionName} without changing any game settings." };

        await Task.Delay(250);

        var protocol = isTargetingPreview ? "minecraft-preview://" : "minecraft://";
        var launched = TryLaunchGame(protocol, versionName, anyModificationsMade: false, messages);

        return new(launched, string.Join("\n", messages));
    }

    /// <summary>
    /// General-purpose entry point: updates any number of integer options.txt
    /// parameters across every signed-in account's options.txt (and Shared, if present),
    /// optionally launching the game afterward. The settings panel is what decides which
    /// parameters those are; nothing in this file hardcodes a preset beyond
    /// <see cref="DefaultOptions"/>.
    /// </summary>
    public static async Task<LaunchOutcome> LaunchWithOptionsAsync(
        bool isTargetingPreview,
        bool launchAfterUpdate,
        params LaunchOption[] updates)
    {
        if (updates == null || updates.Length == 0)
            return new(false, "❗ No options were specified to update.");

        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);

        if (!MinecraftUserDataLocator.IsDataValid(isTargetingPreview))
        {
            return new(false, $"❗ {versionName} data folder not found.\n" +
                   "Make sure the correct version of the game is installed and has been launched at least once.");
        }

        var optionsFiles = MinecraftUserDataLocator.FindAllOptionsFiles(isTargetingPreview);
        if (optionsFiles.Length == 0)
        {
            return new(false, $"❗ No options.txt files found for {versionName}.\n" +
                   "Make sure the game has been launched at least once.");
        }

        var allStatusMessages = new List<string>();
        var filesProcessed = 0;
        var anyModificationsMade = false;

        foreach (var optionsFilePath in optionsFiles)
        {
            var ownerLabel = MinecraftUserDataLocator.GetOwningFolderLabel(isTargetingPreview, optionsFilePath);
            var (success, modified, messages) = await TryUpdateOptionsFileAsync(optionsFilePath, ownerLabel, updates);

            allStatusMessages.AddRange(messages);

            if (success)
            {
                filesProcessed++;
                if (modified)
                    anyModificationsMade = true;
            }
        }

        if (filesProcessed == 0)
            return new(false, string.Join("\n", allStatusMessages.Append("❗ No options files could be processed due to access issues.")));

        allStatusMessages.Add($"Processed {filesProcessed} options file(s).");

        if (!launchAfterUpdate)
            return new(false, string.Join("\n", allStatusMessages));

        await Task.Delay(250);

        var protocol = isTargetingPreview ? "minecraft-preview://" : "minecraft://";
        var launched = TryLaunchGame(protocol, versionName, anyModificationsMade, allStatusMessages);

        return new(launched, string.Join("\n", allStatusMessages));
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies all requested updates to a single options.txt file: validates
    /// accessibility, clears read-only, applies each update line-by-line (appending any
    /// parameter not already present), then - only if something changed - replaces the file
    /// atomically, keeping the previous one as .backup. See <see cref="ReplaceAtomicallyAsync"/>.
    /// </summary>
    private static async Task<(bool success, bool modified, List<string> messages)> TryUpdateOptionsFileAsync(
        string optionsFilePath,
        string ownerLabel,
        LaunchOption[] updates)
    {
        var messages = new List<string>();

        // Accessibility check
        try
        {
            using var fileStream = File.Open(optionsFilePath, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            messages.Add($"❗ Access denied to [{ownerLabel}] options file");
            return (false, false, messages);
        }
        catch (IOException ex)
        {
            messages.Add($"❗ File inaccessible [{ownerLabel}]: {ex.Message}");
            return (false, false, messages);
        }

        // Clear read-only attribute if set
        try
        {
            var fileInfo = new FileInfo(optionsFilePath);
            if (fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;
        }
        catch (Exception ex)
        {
            messages.Add($"❗ Failed to remove readonly attribute [{ownerLabel}]: {ex.Message}");
            return (false, false, messages);
        }

        try
        {
            var lines = (await File.ReadAllLinesAsync(optionsFilePath)).ToList();
            var fileModified = false;

            foreach (var (paramName, value) in updates)
            {
                var applied = ApplyOption(lines, paramName, value, ownerLabel, messages);
                fileModified |= applied;
            }

            // Every value already right: leave the file, and with it the backup, alone. That is
            // what keeps .backup meaning "before the app's last real change" rather than being
            // overwritten with an identical copy on every launch.
            if (!fileModified)
                return (true, false, messages);

            await ReplaceAtomicallyAsync(optionsFilePath, lines);

            return (true, true, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"❗ Failed to update [{ownerLabel}] options file: {ex.Message}");
            return (false, false, messages);
        }
    }

    /// <summary>
    /// Writes <paramref name="lines"/> to a temp file beside <paramref name="optionsFilePath"/>,
    /// then swaps it in with <see cref="File.Replace(string, string, string?)"/>, which moves the
    /// previous file to <c>.backup</c> in the same step.
    ///
    /// <para><b>Atomic is the point.</b> Writing in place truncates first, so a failure mid-write
    /// (disk full, an antivirus lock) leaves the game a half-written options.txt. Here the
    /// original is untouched until the finished file replaces it whole, so a failure leaves it
    /// exactly as it was and there is nothing to recover. The temp file lives in the same folder
    /// because Replace needs source and destination on one volume.</para>
    ///
    /// <para><b>The backup is never restored automatically, deliberately.</b> It is the file as
    /// it was before the app's last change, possibly weeks ago, and options.txt is where every
    /// setting the user has changed in game since then lives - restoring it would roll all of
    /// that back. Nothing needs it anyway: the line edits cannot fail on malformed content,
    /// Bedrock rewrites an options.txt it can't read with defaults, and a missing file is never
    /// visited (and is usually a reset the user made on purpose). It is there for a person to go
    /// back to by hand.</para>
    /// </summary>
    private static async Task ReplaceAtomicallyAsync(string optionsFilePath, List<string> lines)
    {
        var tempPath = optionsFilePath + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(tempPath, lines);
            File.Replace(tempPath, optionsFilePath, optionsFilePath + ".backup");
        }
        finally
        {
            // Gone already on success - Replace consumes its source. On failure, this is the
            // only thing left behind.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Updates a single "paramName:value" line in-place if present (only touching it
    /// when the value actually differs), or appends a new line if the parameter
    /// doesn't exist yet in this options.txt. Returns true if the line list was changed.
    /// </summary>
    private static bool ApplyOption(List<string> lines, string paramName, int value, string ownerLabel, List<string> messages)
    {
        var prefix = paramName + ":";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = lines[i].Split(':');
            if (parts.Length > 1)
            {
                var oldValue = parts[1].Trim();
                var newValue = value.ToString();

                if (oldValue == newValue)
                    return false; // already correct, nothing to do

                lines[i] = $"{paramName}:{newValue}";
                messages.Add($"[{ownerLabel}] {paramName}: {oldValue} -> {newValue}");
                return true;
            }

            // Malformed line with the right prefix but no value — overwrite cleanly
            lines[i] = $"{paramName}:{value}";
            messages.Add($"[{ownerLabel}] {paramName}: (malformed) -> {value}");
            return true;
        }

        // Parameter wasn't present at all — append it
        lines.Add($"{paramName}:{value}");
        messages.Add($"[{ownerLabel}] Added {paramName}:{value}");
        return true;
    }

    /// <summary>
    /// Launches the game via protocol activation and returns whether it was handed off.
    /// Failures are reported but never thrown — if options were already updated successfully,
    /// the user is told to launch manually rather than losing that progress to an unrelated
    /// launch failure.
    /// </summary>
    private static bool TryLaunchGame(string protocol, string versionName, bool anyModificationsMade, List<string> allStatusMessages)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = protocol,
                UseShellExecute = true,
                ErrorDialog = false
            };

            Process.Start(processInfo);
            allStatusMessages.Add($"✅ Settings updated and launched {versionName} successfully.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            allStatusMessages.Add($"Failed to launch {versionName}: {ex.Message}");
            if (anyModificationsMade)
                allStatusMessages.Add("⚠️ Settings were updated successfully — you should now launch the game manually.");
            return false;
        }
        catch (Exception ex)
        {
            allStatusMessages.Add($"Unexpected error launching {versionName}: {ex.Message}");
            if (anyModificationsMade)
                allStatusMessages.Add("⚠️ Settings were updated successfully — you should now launch the game manually.");
            return false;
        }
    }
}
