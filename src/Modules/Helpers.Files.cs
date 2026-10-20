using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vanilla_RTX_App.Modules;

public static partial class Helpers
{
    private static string? _localStateFolder;
    private static bool _localStateResolved;

    /// <summary>
    /// The app's writable folder, and the root of every cache it keeps.
    ///
    /// <para>In the packaged app this is always <c>ApplicationData.Current.LocalFolder</c>.
    /// Reading it needs package identity, which a plain console host (a test harness driving
    /// this assembly) does not have - there it falls back to a folder under the user's temp
    /// directory, so the storage layers stay exercisable outside the app rather than silently
    /// turning themselves off. Resolved once; null only if even the fallback fails.</para>
    /// </summary>
    public static string? LocalStateFolder
    {
        get
        {
            if (_localStateResolved) return _localStateFolder;
            _localStateResolved = true;

            try
            {
                _localStateFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                try
                {
                    var fallback = Path.Combine(Path.GetTempPath(), "VanillaRTXApp_LocalState");
                    Directory.CreateDirectory(fallback);
                    _localStateFolder = fallback;
                    Trace.WriteLine($"[Helpers] No packaged app data - using '{fallback}' for local state.");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Helpers] No writable local state at all: {ex.Message}");
                    _localStateFolder = null;
                }
            }

            return _localStateFolder;
        }
    }

    /// <summary>
    /// 0 or 1 - whether an elevated replace is currently in flight, process-wide.
    /// See <see cref="ReplaceFilesWithElevation"/> for why this exists.
    /// </summary>
    private static int _elevatedReplaceInFlight;

    /// <summary>
    /// True while an elevated replace is running anywhere in the app. Callers don't need to
    /// consult this - <see cref="ReplaceFilesWithElevation"/> enforces it on its own - but a
    /// UI that wants to reflect the state can read it.
    /// </summary>
    public static bool IsElevatedReplaceInFlight => Volatile.Read(ref _elevatedReplaceInFlight) != 0;

    /// <summary>
    /// Copies a set of files using a single elevated batch script (one UAC prompt for all files).
    /// Returns true only if the elevated process exits with code 0.
    ///
    /// <para><b>One at a time, process-wide.</b> Each call writes its own .bat to the temp
    /// folder and launches an elevated cmd for it, so N overlapping calls means N scripts and
    /// N UAC prompts queued up behind each other - which is exactly what an impatient
    /// double-click on an install button used to produce. A call that arrives while another is
    /// still running is therefore <b>refused outright</b> (returns false) rather than queued:
    /// queuing is the symptom, not the fix, and a prompt the user has already stopped expecting
    /// is worse than no prompt at all.</para>
    ///
    /// <para>This is the backstop, not the user-facing story. Every window that starts one of
    /// these also refuses re-entry itself, so a second click is a clean no-op there and never
    /// reaches this check. The check exists so that the invariant survives a caller that
    /// forgets - including any added later.</para>
    ///
    /// <para>The flag is held for the whole operation, UAC prompt included, and released in a
    /// finally - so a declined prompt, a failed copy or a thrown exception all clear it.</para>
    /// </summary>
    /// <param name="filesToReplace">List of (sourcePath, destPath) pairs to copy.</param>
    /// <param name="logPrefix">Tag used in Trace output, e.g. "[ShaderMods]", "[DLSS]", "[LUTManager]".</param>
    /// <param name="tempFilePrefix">Prefix for the temp batch file name, to keep temp files identifiable per-feature.</param>
    public static async Task<bool> ReplaceFilesWithElevation(List<(string sourcePath, string destPath)> filesToReplace,
        string logPrefix = "[Helpers]", string tempFilePrefix = "file_replace")
    {
        if (filesToReplace == null || filesToReplace.Count == 0)
        {
            Trace.WriteLine($"{logPrefix} ReplaceFilesWithElevation called with no files - nothing to do");
            return false;
        }

        if (Interlocked.CompareExchange(ref _elevatedReplaceInFlight, 1, 0) != 0)
        {
            Trace.WriteLine($"{logPrefix} An elevated replace is already running - refusing this one rather than queueing a second UAC prompt");
            return false;
        }

        try
        {
            return await Task.Run(() =>
            {
                var scriptLines = new List<string> { "@echo off" };
                foreach (var (sourcePath, destPath) in filesToReplace)
                    scriptLines.Add($"copy /Y \"{sourcePath}\" \"{destPath}\" >nul 2>&1");
                scriptLines.Add("exit %ERRORLEVEL%");

                var batchScript = string.Join("\r\n", scriptLines);
                var tempBatchPath = Path.Combine(
                    Path.GetTempPath(),
                    $"{tempFilePrefix}_{Guid.NewGuid():N}.bat");

                File.WriteAllText(tempBatchPath, batchScript);

                Trace.WriteLine($"{logPrefix} Batch script: {tempBatchPath}");
                Trace.WriteLine($"{logPrefix} Contents:\n{batchScript}");

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{tempBatchPath}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        Trace.WriteLine($"{logPrefix} Exit code: {process.ExitCode}");
                        return process.ExitCode == 0;
                    }

                    Trace.WriteLine($"{logPrefix} Process.Start returned null");
                    return false;
                }
                finally
                {
                    try
                    {
                        Thread.Sleep(300);
                        if (File.Exists(tempBatchPath))
                            File.Delete(tempBatchPath);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            // Includes the user declining the UAC prompt, which Process.Start surfaces as a
            // Win32Exception rather than a null process.
            Trace.WriteLine($"{logPrefix} Error in ReplaceFilesWithElevation: {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _elevatedReplaceInFlight, 0);
        }
    }


    /// <summary>
    /// The physical location of a folder: <paramref name="path"/> with every symlink and
    /// junction along it followed to the end - at the folder itself or at any folder above it -
    /// along with a <c>subst</c>-mapped drive. Returns the path unchanged when there is nothing to
    /// resolve or resolution fails, so it is safe to call on any path.
    ///
    /// <para><b>Why it matters, and why it is only called at the roots.</b> Most file APIs
    /// follow links transparently, so a linked path works until something *compares* paths -
    /// prefix checks, relative paths, a cache keyed by path, a rename that has to stay in the same
    /// parent - and a linked and a physical spelling of one folder disagree. Every path in the app
    /// is built from four roots (each edition's install and user data folder), so the two
    /// locators resolve those once, as they are cached, and everything built from them is
    /// physical by construction.</para>
    ///
    /// <para><b>Asks Windows for the open folder's final path</b>
    /// (<c>GetFinalPathNameByHandle</c>) rather than reading link targets one at a time:
    /// <see cref="Directory.ResolveLinkTarget(string, bool)"/> only resolves the last folder in
    /// the path, so a junction on a parent - a redirected AppData, for instance - goes unnoticed.
    /// That remains the fallback for a folder that can't be opened (access-restricted locations
    /// like WindowsApps), where it at least covers the common case of the folder itself being a
    /// link.</para>
    /// </summary>
    public static string ResolveToPhysicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var resolved = TryGetFinalPath(path) ?? TryResolveLinkTarget(path);
        if (resolved is null || !Directory.Exists(resolved)
            || string.Equals(resolved.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return path;

        Trace.WriteLine($"[Paths] Resolved to physical path: {path} -> {resolved}");
        return resolved;
    }

    private static string? TryGetFinalPath(string path)
    {
        try
        {
            // No access rights are needed just to ask where a handle points; backup semantics
            // is what lets CreateFile open a directory at all.
            using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero,
                OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var buffer = new char[512];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length > buffer.Length)
            {
                // Too small: the return value is the size needed, terminator included.
                buffer = new char[length];
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0 || length > buffer.Length) return null;

            // The answer comes back in the \\?\ form; strip it to an ordinary path.
            var final = new string(buffer, 0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + final[8..];
            if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) return final[4..];
            return final;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Paths] Couldn't get the final path of {path}: {ex.Message}");
            return null;
        }
    }

    private static string? TryResolveLinkTarget(string path)
    {
        try { return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Paths] ResolveLinkTarget failed for {path}: {ex.Message}");
            return null;
        }
    }

    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath,
        uint cchFilePath, uint dwFlags);


    /// <summary>
    /// A filesystem helper that recursively finds files matching <paramref name="searchPattern"/> whose directory
    /// depth relative to <paramref name="rootDirectory"/> falls within [minDepth, maxDepth].
    /// Depth 0 = rootDirectory itself, depth 1 = its immediate subfolders, etc.
    /// Stops descending once maxDepth is reached (won't walk deeper subtrees unnecessarily).
    /// Silently skips directories it can't access.
    /// </summary>
    public static IEnumerable<string> FindFilesAtDepth(
        string rootDirectory, string searchPattern, int minDepth, int maxDepth)
    {
        if (minDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(minDepth));
        if (maxDepth < minDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));

        return Traverse(rootDirectory, 0);

        IEnumerable<string> Traverse(string dir, int depth)
        {
            if (depth >= minDepth)
            {
                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir, searchPattern); }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                foreach (var f in files)
                    yield return f;
            }

            if (depth < maxDepth)
            {
                string[] subdirs = Array.Empty<string>();
                try { subdirs = Directory.GetDirectories(dir); }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                foreach (var sub in subdirs)
                    foreach (var f in Traverse(sub, depth + 1))
                        yield return f;
            }
        }
    }


    /// <summary>
    /// The zip-entry equivalent of <see cref="FindFilesAtDepth"/>: finds every entry named
    /// <paramref name="fileName"/> (case-insensitive) whose depth within the archive falls in
    /// [minDepth, maxDepth]. Depth is the number of '/' separators in the entry's own
    /// <c>FullName</c> - 0 for an entry sitting at the archive root, 1 for one inside a single
    /// top-level folder (e.g. the branch-named folder a GitHub codeload zipball wraps everything
    /// in), 2 for one folder deeper, and so on. Zip entries always use '/' regardless of OS.
    /// Exists so callers can locate manifests (or anything else) inside a zip by shape rather
    /// than by hardcoding folder names that belong to whoever authored the archive, not to us.
    /// </summary>
    public static IEnumerable<ZipArchiveEntry> FindZipEntriesAtDepth(
        ZipArchive archive, string fileName, int minDepth, int maxDepth)
    {
        if (minDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(minDepth));
        if (maxDepth < minDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));

        foreach (var entry in archive.Entries)
        {
            // A directory-marker entry's FullName ends in '/', which makes its Name empty -
            // that can never equal a real file name, so no separate directory check is needed.
            if (!entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var depth = entry.FullName.Count(c => c == '/');
            if (depth >= minDepth && depth <= maxDepth)
                yield return entry;
        }
    }


    /// <summary>
    /// Resolves every zip entry that lives under <paramref name="folderPrefixInZip"/> (a folder's
    /// own <c>FullName</c>, trailing '/' included) to where it belongs under
    /// <paramref name="destinationDirectory"/>, with the prefix stripped and '/' converted to the
    /// platform separator. Pure path computation only - it does not touch disk or open any entry,
    /// so it carries none of a caller's I/O or threading strategy. That is deliberate: PackUpdater
    /// (already running off the UI thread via its caller) writes files inline, while ExpImpDel's
    /// import runs directly off a UI-thread button handler and wraps each file in its own
    /// <c>Task.Run</c> to keep the window responsive during a large pack - sharing this enumerator
    /// lets both use the same "which entries, what target path" logic without forcing either one
    /// onto the other's threading model.
    /// </summary>
    public static IEnumerable<(ZipArchiveEntry Entry, string TargetPath, bool IsDirectory)> EnumerateZipFolderExtraction(
        ZipArchive archive, string folderPrefixInZip, string destinationDirectory)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(folderPrefixInZip, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = entry.FullName.Substring(folderPrefixInZip.Length).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath)) continue;

            var targetPath = Path.Combine(destinationDirectory, relativePath);
            yield return (entry, targetPath, entry.FullName.EndsWith('/'));
        }
    }


    /// <summary>
    /// A custom implementation of generating a proper texture set, utilizes the custom implementation of TextureSetHelpers class in Processor.cs
    /// </summary>
    public static void GenerateTexturesLists(string rootDirectory)
    {
        static string FormatMinecraftJson(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return "[]";
            var formattedItems = paths.Select(path => $"    \"{path}\"");
            return "[\n" + string.Join(",\n", formattedItems) + "\n]";
        }

        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"Directory not found: {rootDirectory}");

        // ── Find all "textures" directories (unchanged) ───────────────────────────
        var texturesDirectories = Directory
            .GetDirectories(rootDirectory, "textures", SearchOption.AllDirectories)
            .ToList();

        if (Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("textures", StringComparison.OrdinalIgnoreCase))
            texturesDirectories.Add(rootDirectory);

        if (texturesDirectories.Count == 0)
            return;

        string[] imageExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

        foreach (string texturesDir in texturesDirectories)
        {
            // ── Collect all non-color file paths to exclude ───────────────────────
            //
            // ResolveTextureSets validates every texture set in one pass and gives us
            // structured access to each layer. We exclude any real-file path that
            // belongs to a non-color layer (MER/MERS, normal, heightmap).
            // Inline layers (RGB arrays / hex values) have no FilePath, so nothing
            // is added to the exclusion set for them.

            var pbrTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var resolvedSets = TextureSetHelper.ResolveTextureSets(texturesDir);

            foreach (var rs in resolvedSets)
            {
                // MER / MERS layer
                if (rs.Mer is { IsInline: false, FilePath: not null } mer)
                    pbrTextures.Add(mer.FilePath);

                // Normal or heightmap layer
                if (rs.NormalOrHeight is { IsInline: false, FilePath: not null } normalOrHeight)
                    pbrTextures.Add(normalOrHeight.FilePath);
            }

            // ── Collect all image files (unchanged) ───────────────────────────────
            var imageFiles = new List<string>();
            foreach (string ext in imageExtensions)
            {
                imageFiles.AddRange(Directory.GetFiles(texturesDir, $"*{ext}", SearchOption.AllDirectories));
                imageFiles.AddRange(Directory.GetFiles(texturesDir, $"*{ext.ToUpper()}", SearchOption.AllDirectories));
            }

            // ── Build relative paths, filtering out non-color PBR textures ────────
            var filteredPaths = new List<string>();
            foreach (string filePath in imageFiles.Distinct())
            {
                if (pbrTextures.Contains(filePath))
                    continue;

                string relativePath = Path.GetRelativePath(texturesDir, filePath).Replace('\\', '/');
                string pathWithoutExtension = Path.ChangeExtension(relativePath, null);
                filteredPaths.Add("textures/" + pathWithoutExtension);
            }

            // Distinct AFTER stripping extensions, not just on file paths: the same texture
            // name can legitimately exist under two extensions, and the game resolves that
            // to one texture (.tga > .png > .jpg > .jpeg). Alchitex creates exactly this
            // situation when it rewrites a colour texture as .tga next to the original -
            // without this the list carries the same entry twice.
            filteredPaths = filteredPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            filteredPaths.Sort();

            File.WriteAllText(
                Path.Combine(texturesDir, "textures_list.json"),
                FormatMinecraftJson(filteredPaths));
        }
    }


    /// <summary>
    /// Every file the game writes into an installed pack for its own use - caches and
    /// signatures, never authored content. Both spellings of the textures list (and of the
    /// signature file) are here because packs in the wild carry either.
    /// </summary>
    private static readonly string[] BookkeepingFileNames =
    {
        "contents.json", "textures_list.json", "texture_list.json", "signatures.json", "signature.json",
    };

    /// <summary>
    /// Strips every game-generated bookkeeping file out of a pack, recursively.
    ///
    /// Used when a pack leaves the app (export): the game treats these as authoritative
    /// caches, so shipping a stale one is strictly worse than shipping none - anything it
    /// fails to list simply doesn't load. Also the first half of RegenerateBookkeepingFiles,
    /// since the singular "texture_list.json" and the signature files are never regenerated
    /// and so have to be deleted rather than overwritten.
    ///
    /// contents.json is written read-only by the game, hence the attribute clearing. A file
    /// that can't be deleted (locked by the game, AV, indexing) is logged and skipped rather
    /// than aborting the sweep.
    /// </summary>
    public static void RemoveBookkeepingFiles(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return;

        foreach (var name in BookkeepingFileNames)
        {
            string[] matches;
            try { matches = Directory.GetFiles(packRoot, name, SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Bookkeeping] Couldn't scan '{packRoot}' for '{name}': {ex.Message}");
                continue;
            }

            foreach (var file in matches)
                TryDeleteBookkeepingFile(file);
        }
    }

    /// <summary>
    /// Writes the bookkeeping files an *installed* pack is expected to have: a
    /// textures_list.json in every textures folder (GenerateTexturesLists) plus an empty
    /// contents.json next to manifest.json.
    ///
    /// Only ever for packs this app produced or deployed itself - Vanilla RTX installs
    /// (PackUpdater) and Alchitex's generated RTX packs, where regenerating caches is part
    /// of the job we were asked to do. Deliberately NOT done on plain imports: an imported
    /// pack is somebody else's work, and rewriting its bookkeeping would be mutilating it
    /// rather than importing it.
    ///
    /// contents.json is the game's own file-location cache and can't be authored by us; an
    /// empty object is enough for the game to rebuild it, and is what keeps it from trusting
    /// whatever stale one was there before.
    ///
    /// Each half is independently guarded - a failure to list textures shouldn't cost the
    /// pack its contents.json, and neither is worth failing a whole install/generation over.
    /// </summary>
    public static void GenerateBookkeepingFiles(string packRoot)
    {
        try
        {
            GenerateTexturesLists(packRoot);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] textures_list.json generation failed for '{packRoot}': {ex.Message}");
        }

        var contentsPath = Path.Combine(packRoot, "contents.json");
        try
        {
            if (File.Exists(contentsPath)) TryDeleteBookkeepingFile(contentsPath);
            File.WriteAllText(contentsPath, "{}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] Couldn't write '{contentsPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Clears out every stale bookkeeping file and then writes fresh ones. For packs whose
    /// contents we just changed on disk (Alchitex): overwriting isn't enough on its own,
    /// because the file names we no longer generate would otherwise survive and keep
    /// pointing at a pack that no longer looks like that.
    /// </summary>
    public static void RegenerateBookkeepingFiles(string packRoot)
    {
        RemoveBookkeepingFiles(packRoot);
        GenerateBookkeepingFiles(packRoot);
    }

    private static void TryDeleteBookkeepingFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & System.IO.FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~System.IO.FileAttributes.ReadOnly);

            File.Delete(path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] Couldn't delete '{path}': {ex.Message}");
        }
    }
}
