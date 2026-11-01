using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules.ShaderMods;

/// <summary>
/// The app's private copy of lazurite (veka0's .material.bin tool, Python) and everything it runs on,
/// kept in <c>LocalState\Lazurite</c> and kept current without a new app build. The install-time upgrader
/// (<see cref="ShaderModManager.PrepareInstallAsync"/>) is its only user.
///
/// <para><b>Three parts, three update rules.</b>
/// <list type="bullet">
///   <item><b>Python</b> - the official embeddable CPython, pinned by version and SHA-256 below. Lazurite
///   needs any 3.10+, so nothing is gained by moving it and a pin is the only way to verify a 12 MB
///   executable download.</item>
///   <item><b>Lazurite</b> - floats. PyPI is asked at most once a day, only while the shader mod manager is in
///   use, conditionally (its JSON is CDN-cached with an ETag), and pip fetches the wheels when a new
///   version is out. The policy file can pin or block versions.</item>
///   <item><b>The two scripts</b> (<c>material_upgrade.py</c>, <c>lazurite_patched.py</c>) and the policy -
///   online-updatable assets of this repository through <see cref="AssetUpdater"/>, so a lazurite change that
///   needs a matching script change reaches users without a Store release.</item>
/// </list></para>
///
/// <para><b>Nothing new is used until it has been proven on this machine.</b> A lazurite version and a script
/// set are adopted together, and only after the gate (<see cref="GateAsync"/>): every vanilla material in
/// both Default backups read and written back byte-identically, and each backup upgraded onto itself coming
/// out unchanged. A candidate must pass on everything the adopted toolchain passes on, so an update can add
/// formats but never lose one - lazurite 0.11.0 stopped reading earlier format-26 files, which a blind
/// <c>pip install -U</c> would have shipped to everyone. A rejected candidate is remembered for a week and
/// the adopted one stays.</para>
///
/// <para><b>Every run is out of process with a deadline</b>, and every result is a JSON object on stdout
/// carrying <see cref="Protocol"/>. Anything else - a crash, a timeout, the wrong protocol - is reported as
/// a failure and never as a verdict.</para>
///
/// <para>One lock serialises everything here, runs included: adopting deletes the folders the previous
/// toolchain ran from. <see cref="Activity"/> narrates each step to whoever is waiting, including a caller
/// queued behind a background setup it did not start.</para>
/// </summary>
internal static class LazuriteToolchain
{
    // ── Pins ─────────────────────────────────────────────────────────────────

    private const string PythonVersion = "3.14.10";

    /// <summary>
    /// Arm64 Windows gets the native build - pycryptodome and pyjson5 both publish win_arm64 wheels. The
    /// OS architecture, not the process's: an x64 build of the app emulated on Arm64 still runs Python best
    /// natively.
    /// </summary>
    private static string PythonArch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    private static string PythonUrl => $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-{PythonArch}.zip";

    private static string PythonSha256 => PythonArch == "arm64"
        ? "bbe116bb1248820359cbf25127ac5c7794ea161869d83686cfbc9a45203ff836"
        : "90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159";

    /// <summary>pip runs straight from its wheel; the embeddable Python ships without it.</summary>
    private const string PipUrl = "https://files.pythonhosted.org/packages/3f/06/3e4420302363fbb71be3c7f35ce042f54bfca330e3e66b3a2e3c4cca579f/pip-26.2.2-py3-none-any.whl";
    private const string PipSha256 = "fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70";
    private const string PipFileName = "pip-26.2.2-py3-none-any.whl";

    private const string PyPiUrl = "https://pypi.org/pypi/lazurite/json";

    /// <summary>
    /// The version this was built and measured against, tried when the newest one fails the gate on a
    /// machine that has nothing adopted yet.
    /// </summary>
    private const string BaselineLazurite = "0.12.0";

    /// <summary>The <c>PROTOCOL</c> material_upgrade.py declares. A script set declaring another is never adopted.</summary>
    public const int Protocol = 1;

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ForcedCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RejectionMemory = TimeSpan.FromDays(7);

    // ── Online-updatable parts ───────────────────────────────────────────────

    private static string Packaged(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Modules", "ShaderMods", "Lazurite", fileName);

    private const string RemoteRoot =
        "https://raw.githubusercontent.com/vanilla-rtx/Vanilla-RTX-App/refs/heads/main/src/Modules/ShaderMods/Lazurite/";

    private const string AssetFolder = "Lazurite_Assets";

    public static readonly ManagedAsset UpgradeScript = new(
        RemoteRoot + "material_upgrade.py", TimeSpan.FromDays(1), Packaged("material_upgrade.py"), AssetFolder);

    public static readonly ManagedAsset PatchScript = new(
        RemoteRoot + "lazurite_patched.py", TimeSpan.FromDays(1), Packaged("lazurite_patched.py"), AssetFolder);

    /// <summary>
    /// The developer's switches: <c>enabled</c> (false turns upgrading off everywhere), <c>pin</c> (one
    /// lazurite version to use instead of the newest) and <c>blocked</c> (versions never to adopt).
    /// </summary>
    public static readonly ManagedAsset Policy = new(
        RemoteRoot + "lazurite_policy.json", TimeSpan.FromHours(12), Packaged("lazurite_policy.json"), AssetFolder);

    private static readonly ManagedAsset[] Assets = [Policy, UpgradeScript, PatchScript];

    // ── Layout ───────────────────────────────────────────────────────────────

    private static string Root => Path.Combine(
        Helpers.LocalStateFolder ?? throw new IOException("No writable local state folder."), "Lazurite");
    private static string PythonFolder => Path.Combine(Root, "python");
    private static string PythonExe => Path.Combine(PythonFolder, "python.exe");
    private static string PythonMarker => Path.Combine(PythonFolder, "vanillartx-python.txt");
    /// <summary>
    /// One lazurite version's packages, for the pinned Python. Keyed by both: pycryptodome and pyjson5 are
    /// compiled for one CPython, so an app update that moves <see cref="PythonVersion"/> must not find the
    /// old Python's packages and call them installed.
    /// </summary>
    private static string SiteFolderName(string version) => $"{version}-py{PythonVersion}";
    private static string SiteFolder(string version) => Path.Combine(Root, "site", SiteFolderName(version));
    private static string ScriptsFolder(string id) => Path.Combine(Root, "scripts", id);
    private static string WorkRoot => Path.Combine(Root, "work");
    private static string StatePath => Path.Combine(Root, "toolchain.json");

    // ── State ────────────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Narrates whatever the toolchain is doing, from whichever thread is doing it. A window showing an
    /// install subscribes for the duration, which is how a user waiting behind a background setup still
    /// sees what it is waiting for.
    /// </summary>
    public static event Action<string>? Activity;

    private static void Say(string message)
    {
        Trace.WriteLine($"[ShaderMods] [Lazurite] {message}");
        try { Activity?.Invoke(message); } catch { }
    }

    /// <summary>The adopted lazurite version, for log lines. Null until one is adopted.</summary>
    public static string? AdoptedVersion => LoadState().Lazurite;

    private sealed class State
    {
        public string? Lazurite;
        public string? Scripts;
        public string? Latest;
        public DateTime LastCheck = DateTime.MinValue;
        public DateTime LastForcedCheck = DateTime.MinValue;
        public Dictionary<string, DateTime> Rejected = new(StringComparer.OrdinalIgnoreCase);
    }

    private static State LoadState()
    {
        var state = new State();
        try
        {
            if (!File.Exists(StatePath)) return state;
            var json = MinecraftJson.ParseObjectFile(StatePath);
            if (json == null) return state;
            state.Lazurite = MinecraftJson.GetString(json["lazurite"]);
            state.Scripts = MinecraftJson.GetString(json["scripts"]);
            state.Latest = MinecraftJson.GetString(json["latest"]);
            state.LastCheck = ParseTime(json["lastCheck"]);
            state.LastForcedCheck = ParseTime(json["lastForcedCheck"]);
            if (json["rejected"] is JsonObject rejected)
                foreach (var (key, value) in rejected)
                    state.Rejected[key] = ParseTime(value);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Lazurite] Unreadable toolchain.json, starting over: {ex.Message}");
        }
        return state;
    }

    private static void SaveState(State state)
    {
        var rejected = new JsonObject();
        foreach (var (key, time) in state.Rejected) rejected[key] = (JsonNode?)JsonValue.Create(time.ToString("O"));

        var json = new JsonObject
        {
            ["lazurite"] = (JsonNode?)JsonValue.Create(state.Lazurite),
            ["scripts"] = (JsonNode?)JsonValue.Create(state.Scripts),
            ["latest"] = (JsonNode?)JsonValue.Create(state.Latest),
            ["lastCheck"] = (JsonNode?)JsonValue.Create(state.LastCheck.ToString("O")),
            ["lastForcedCheck"] = (JsonNode?)JsonValue.Create(state.LastForcedCheck.ToString("O")),
            ["rejected"] = rejected,
        };
        Directory.CreateDirectory(Root);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString());
        File.Move(temp, StatePath, overwrite: true);
    }

    // RoundtripKind, or a UTC "O" stamp parses back as local time (CLAUDE.md §4.17).
    private static DateTime ParseTime(JsonNode? node) =>
        DateTime.TryParse(MinecraftJson.GetString(node), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : DateTime.MinValue;

    private sealed record PolicyData(bool Enabled, string? Pin, HashSet<string> Blocked);

    private static PolicyData ReadPolicy()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = AssetUpdater.Resolve(Policy);
            var json = path != null ? MinecraftJson.ParseObjectFile(path) : null;
            if (json == null) return new PolicyData(true, null, blocked);
            foreach (var v in MinecraftJson.GetStringArray(json["blocked"])) blocked.Add(v);
            var pin = MinecraftJson.GetString(json["pin"]);
            return new PolicyData(MinecraftJson.GetBool(json["enabled"]) ?? true, string.IsNullOrWhiteSpace(pin) ? null : pin, blocked);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Lazurite] Unreadable policy, using defaults: {ex.Message}");
            return new PolicyData(true, null, blocked);
        }
    }

    // ── Public surface ───────────────────────────────────────────────────────

    public enum UpdateCheck
    {
        /// <summary>Use what is adopted; only set up when nothing is.</summary>
        Never,

        /// <summary>Ask PyPI when the daily interval has passed.</summary>
        IfDue,

        /// <summary>
        /// Ask now - something just failed that a newer lazurite might fix (a game format it can't read).
        /// Still rate-limited to once every few minutes, so a preset that keeps failing can't hammer PyPI.
        /// </summary>
        Now
    }

    /// <summary>
    /// Sets up or refreshes the toolchain in the background, for the shader mod manager's open: the first
    /// install then rarely has to wait for a download. Nothing reads its result.
    /// </summary>
    public static void WarmUp(IReadOnlyList<string> vanillaFolders) =>
        _ = Task.Run(async () =>
        {
            try { await EnsureReadyAsync(vanillaFolders, UpdateCheck.IfDue, CancellationToken.None); }
            catch (Exception ex) { Trace.WriteLine($"[ShaderMods] [Lazurite] Background setup failed: {ex.Message}"); }
        });

    /// <summary>
    /// Makes sure an adopted toolchain exists, setting one up or adopting a newer one as
    /// <paramref name="check"/> allows. Null when ready; otherwise the reason it isn't, written for the user.
    /// <paramref name="vanillaFolders"/> are the Default backups the gate proves candidates on.
    /// </summary>
    public static async Task<string?> EnsureReadyAsync(IReadOnlyList<string> vanillaFolders, UpdateCheck check, CancellationToken ct)
    {
        if (!_lock.Wait(0))
        {
            Say("Waiting for the shader converter setup already in progress...");
            await _lock.WaitAsync(ct);
        }
        try
        {
            return await EnsureReadyCoreAsync(vanillaFolders, check, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Lazurite] ✗ Setup failed: {ex}");
            return $"The shader converter could not be set up ({ex.Message}).";
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A fresh empty folder for one run's output. The caller deletes it.</summary>
    public static string NewWorkFolder()
    {
        var path = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Runs material_upgrade.py with the adopted toolchain. Returns its JSON result, or the reason there
    /// isn't one. <paramref name="onProgress"/> receives the script's <c>progress</c> lines.
    /// </summary>
    public static async Task<(JsonObject? Result, string? Error)> RunAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string>? onProgress = null)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = LoadState();
            if (state.Lazurite == null || state.Scripts == null)
                return (null, "the shader converter is not set up");
            return await RunScriptAsync(state.Lazurite, state.Scripts, args, timeout, ct, onProgress);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Setup and updating ───────────────────────────────────────────────────

    private static async Task<string?> EnsureReadyCoreAsync(IReadOnlyList<string> vanillaFolders, UpdateCheck check, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        CleanWorkFolders();

        var state = LoadState();
        var now = DateTime.UtcNow;
        bool due = check switch
        {
            UpdateCheck.IfDue => now - state.LastCheck >= UpdateCheckInterval,
            UpdateCheck.Now => now - state.LastForcedCheck >= ForcedCheckInterval,
            _ => false,
        };

        bool adoptedUsable = state.Lazurite != null && state.Scripts != null
                             && IsSiteInstalled(state.Lazurite) && File.Exists(Path.Combine(ScriptsFolder(state.Scripts), "material_upgrade.py"));

        // The policy and the scripts are asked about on the same schedule as PyPI, before anything is
        // decided from them - a kill switch or a fixed script is exactly what this check is for.
        if (due || !adoptedUsable)
        {
            foreach (var asset in Assets)
                await AssetUpdater.ResolveFreshOrCachedAsync(asset, force: check == UpdateCheck.Now, FetchPriority.Background, ct);
        }

        var policy = ReadPolicy();
        if (!policy.Enabled)
            return "Upgrading presets is switched off for now by the Vanilla RTX App's developer.";

        if (adoptedUsable && policy.Blocked.Contains(state.Lazurite!))
        {
            Say($"lazurite {state.Lazurite} has been blocked by the developer - replacing it");
            adoptedUsable = false;
        }

        if (!adoptedUsable || due)
        {
            var pythonError = await EnsurePythonAsync(ct);
            if (pythonError != null)
                return adoptedUsable ? null : pythonError;
        }

        var scriptsId = StageScripts();
        if (scriptsId == null)
            return adoptedUsable ? null : "The shader converter's scripts are missing from this install of the app.";

        if (due || !adoptedUsable)
        {
            var latest = await FetchLatestVersionAsync(ct);
            if (latest != null) state.Latest = latest;
            state.LastCheck = now;
            if (check == UpdateCheck.Now) state.LastForcedCheck = now;
            SaveState(state);
        }

        // What to try, best first. With something adopted only the one we'd want is worth a gate; with
        // nothing adopted, the measured baseline is the fallback.
        var wanted = policy.Pin ?? state.Latest ?? BaselineLazurite;
        var candidates = new List<string> { wanted };
        if (!adoptedUsable && policy.Pin == null && !wanted.Equals(BaselineLazurite, StringComparison.OrdinalIgnoreCase))
            candidates.Add(BaselineLazurite);

        foreach (var version in candidates.Where(v => !policy.Blocked.Contains(v)))
        {
            if (adoptedUsable && version.Equals(state.Lazurite, StringComparison.OrdinalIgnoreCase) && scriptsId == state.Scripts)
                break;

            var key = $"{version}|{scriptsId}";
            if (state.Rejected.TryGetValue(key, out var when) && now - when < RejectionMemory && check != UpdateCheck.Now)
            {
                Trace.WriteLine($"[ShaderMods] [Lazurite] Skipping {key}: rejected {when:u}");
                continue;
            }

            if (!await EnsureSiteAsync(version, ct)) continue;

            Say(adoptedUsable
                ? $"Testing lazurite {version} on your game's own shader files before switching to it..."
                : $"Testing lazurite {version} on your game's own shader files...");

            var verdict = await GateAsync(version, scriptsId, vanillaFolders, ct);
            GateResult? incumbent = adoptedUsable ? await GateAsync(state.Lazurite!, state.Scripts!, vanillaFolders, ct) : null;

            if (Accepts(verdict, incumbent, out var reason))
            {
                Say($"Using lazurite {version} (scripts {scriptsId}){(state.Lazurite is { } old && old != version ? $", replacing {old}" : "")}");
                state.Lazurite = version;
                state.Scripts = scriptsId;
                state.Rejected.Remove(key);
                SaveState(state);
                adoptedUsable = true;
                break;
            }

            Say($"Not switching to lazurite {version}: {reason}");
            state.Rejected[key] = now;
            SaveState(state);
        }

        if (adoptedUsable) PruneUnused(state);

        return adoptedUsable ? null : "The shader converter could not be set up - check your internet connection and try again.";
    }

    /// <summary>
    /// Copies the current scripts (cached or packaged, whichever <see cref="AssetUpdater.Resolve"/> answers)
    /// into a folder named by their hash and returns that name. Runs always use such a copy, never the asset
    /// cache directly, so a script fetched mid-session can't change under a toolchain that was proven with the
    /// previous one.
    /// </summary>
    private static string? StageScripts()
    {
        var upgrade = AssetUpdater.Resolve(UpgradeScript);
        var patch = AssetUpdater.Resolve(PatchScript);
        if (upgrade == null || patch == null || !File.Exists(upgrade) || !File.Exists(patch)) return null;

        var a = File.ReadAllBytes(upgrade);
        var b = File.ReadAllBytes(patch);
        using var sha = SHA256.Create();
        sha.TransformBlock(a, 0, a.Length, null, 0);
        sha.TransformFinalBlock(b, 0, b.Length);
        var id = Convert.ToHexString(sha.Hash!, 0, 8).ToLowerInvariant();

        var folder = ScriptsFolder(id);
        if (!File.Exists(Path.Combine(folder, "material_upgrade.py")))
        {
            var staging = folder + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging, "lazurite_patched.py"), b);
            File.WriteAllBytes(Path.Combine(staging, "material_upgrade.py"), a);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
        }
        return id;
    }

    private static async Task<string?> EnsurePythonAsync(CancellationToken ct)
    {
        var marker = $"{PythonVersion} {PythonArch}";
        if (File.Exists(PythonExe) && File.Exists(PythonMarker) && File.ReadAllText(PythonMarker).Trim() == marker)
            return null;

        Say($"Downloading Python {PythonVersion} for the shader converter (about 12 MB, first time only)...");
        var zip = await DownloadVerifiedAsync(PythonUrl, PythonSha256, ct);
        if (zip == null)
            return "Python could not be downloaded for the shader converter - check your internet connection.";

        try
        {
            Say("Unpacking Python...");
            var staging = PythonFolder + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zip, staging);
            File.WriteAllText(Path.Combine(staging, Path.GetFileName(PythonMarker)), marker);
            if (Directory.Exists(PythonFolder)) Directory.Delete(PythonFolder, true);
            Directory.Move(staging, PythonFolder);
            return null;
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> and returns the local path only if its SHA-256 is
    /// <paramref name="sha256"/>; a mismatch deletes it.
    /// </summary>
    private static async Task<string?> DownloadVerifiedAsync(string url, string sha256, CancellationToken ct)
    {
        var result = await Helpers.Download(url, ct, Helpers.UpdaterHttpClient, TimeSpan.FromMinutes(10), quiet: true);
        if (!result.Success || result.Path == null) return null;

        using (var stream = File.OpenRead(result.Path))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (actual == sha256) return result.Path;
            Trace.WriteLine($"[ShaderMods] [Lazurite] ✗ {url} has SHA-256 {actual}, expected {sha256} - discarded");
        }
        try { File.Delete(result.Path); } catch { }
        return null;
    }

    /// <summary>
    /// The newest lazurite on PyPI, or null when it can't be asked. Conditional: an unchanged answer is a
    /// 304 and the version recorded last time stands.
    /// </summary>
    private static async Task<string?> FetchLatestVersionAsync(CancellationToken ct)
    {
        var state = LoadState();
        // Only quote the validator when there is a stored answer for it to stand for.
        if (state.Latest == null) Helpers.SetValidator(PyPiUrl, null);

        var fetch = await Helpers.GetStringConditionalAsync(PyPiUrl, TimeSpan.FromSeconds(15), Helpers.UpdaterHttpClient, ct);
        switch (fetch.Status)
        {
            case FetchStatus.NotModified:
                return state.Latest;
            case FetchStatus.Modified:
                var version = MinecraftJson.GetString(MinecraftJson.SelectPath(MinecraftJson.ParseObject(fetch.Body), "info.version"));
                if (version == null) return null;
                state.Latest = version;
                SaveState(state);
                Helpers.SetValidator(PyPiUrl, fetch.ETag);
                Trace.WriteLine($"[ShaderMods] [Lazurite] PyPI's newest lazurite is {version}");
                return version;
            default:
                Trace.WriteLine("[ShaderMods] [Lazurite] PyPI unreachable - keeping what is known");
                return null;
        }
    }

    private static bool IsSiteInstalled(string version) =>
        Directory.Exists(SiteFolder(version)) &&
        Directory.EnumerateDirectories(SiteFolder(version), $"lazurite-{version}.dist-info").Any();

    /// <summary>
    /// Installs one lazurite version and its dependencies into its own folder with pip, run from its wheel.
    /// Never into an existing version's folder: the adopted one keeps working whatever happens here.
    /// </summary>
    private static async Task<bool> EnsureSiteAsync(string version, CancellationToken ct)
    {
        if (IsSiteInstalled(version)) return true;

        var pip = Path.Combine(Root, PipFileName);
        if (!File.Exists(pip))
        {
            Say("Downloading pip...");
            var downloaded = await DownloadVerifiedAsync(PipUrl, PipSha256, ct);
            if (downloaded == null) return false;
            File.Move(downloaded, pip, overwrite: true);
        }

        Say($"Installing lazurite {version} (about 8 MB)...");
        var target = SiteFolder(version);
        var staging = target + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // --isolated keeps the user's own pip configuration and PIP_* variables out of it; -I does the same
        // for Python's. --only-binary because nothing here can compile a source package.
        string[] args =
        [
            "-I", "-X", "utf8", "-c",
            "import sys; sys.path.insert(0, sys.argv[1]); sys.argv = ['pip'] + sys.argv[2:]; " +
            "from pip._internal.cli.main import main; sys.exit(main())",
            pip, "install", "--isolated", "--target", staging, "--only-binary=:all:", "--no-cache-dir",
            "--disable-pip-version-check", "--no-input", "--no-compile", "--no-warn-script-location",
            "--progress-bar", "off", $"lazurite=={version}",
        ];

        var run = await RunProcessAsync(PythonExe, args, TimeSpan.FromMinutes(10), ct, line =>
        {
            // "Downloading sympy-1.14.0-py3-none-any.whl (6.3 MB)" is worth passing on; the rest is noise.
            if (line.StartsWith("Downloading ", StringComparison.Ordinal))
                Say($"Installing lazurite {version}: {line.Trim()}");
        });

        if (run.ExitCode != 0)
        {
            Trace.WriteLine($"[ShaderMods] [Lazurite] ✗ pip failed for {version} (exit {run.ExitCode}): {Tail(run.Stderr)}");
            try { Directory.Delete(staging, true); } catch { }
            return false;
        }

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(staging, target);
        return IsSiteInstalled(version);
    }

    private sealed record GateResult(bool Loaded, HashSet<string> RoundTrips, bool SelfTestPassed, string Detail);

    /// <summary>
    /// Proves one toolchain on this machine's vanilla materials: it loads and speaks <see cref="Protocol"/>;
    /// which backup files it reads and writes back byte-identically; and whether every backup upgraded onto
    /// itself comes out "current" - which exercises the whole upgrade path, its own proof included.
    /// </summary>
    private static async Task<GateResult> GateAsync(string version, string scriptsId, IReadOnlyList<string> vanillaFolders, CancellationToken ct)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (info, infoError) = await RunScriptAsync(version, scriptsId, ["info"], TimeSpan.FromMinutes(1), ct);
        if (info == null)
            return new GateResult(false, empty, false, infoError ?? "it did not start");

        var files = vanillaFolders
            .Where(Directory.Exists)
            .SelectMany(folder => ShaderModManager.RTXFiles.Select(name => Path.Combine(folder, name)))
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0)
            return new GateResult(true, empty, true, "no vanilla materials to test on");

        var (roundtrip, roundtripError) = await RunScriptAsync(version, scriptsId, ["roundtrip", .. files], TimeSpan.FromMinutes(3), ct);
        if (roundtrip?["files"] is not JsonArray results)
            return new GateResult(true, empty, false, roundtripError ?? "the round trip returned nothing");

        var passed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var entry in results.OfType<JsonObject>())
        {
            var path = MinecraftJson.GetString(entry["path"]) ?? "";
            if (MinecraftJson.GetBool(entry["ok"]) == true) passed.Add(path);
            else failures.Add($"{Path.GetFileName(Path.GetDirectoryName(path))}\\{Path.GetFileName(path)}: {MinecraftJson.GetString(entry["reason"])}");
        }

        foreach (var folder in vanillaFolders.Where(Directory.Exists))
        {
            var own = passed.Where(p => string.Equals(Path.GetDirectoryName(p), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count == 0) continue;

            var work = NewWorkFolder();
            try
            {
                var (self, selfError) = await RunScriptAsync(version, scriptsId, ["upgrade", folder, work, .. own], TimeSpan.FromMinutes(3), ct);
                var cases = (self?["materials"] as JsonArray)?.OfType<JsonObject>().Select(m => MinecraftJson.GetString(m["case"])).ToList();
                if (cases == null || cases.Count != own.Count || cases.Any(c => c != "current"))
                    return new GateResult(true, passed, false,
                        $"upgrading {Path.GetFileName(folder)} onto itself changed it ({selfError ?? string.Join(", ", cases ?? [])})");
            }
            finally
            {
                TryDelete(work);
            }
        }

        return new GateResult(true, passed, true, failures.Count == 0 ? "all files round-trip" : string.Join("; ", failures));
    }

    /// <summary>
    /// A candidate is adopted when it loads, passes its self-test, and reads back everything the incumbent
    /// does - it may add formats, never lose one.
    /// </summary>
    private static bool Accepts(GateResult candidate, GateResult? incumbent, out string reason)
    {
        if (!candidate.Loaded) { reason = $"it doesn't run ({candidate.Detail})"; return false; }
        if (!candidate.SelfTestPassed) { reason = candidate.Detail; return false; }
        if (incumbent is { Loaded: true } && !candidate.RoundTrips.IsSupersetOf(incumbent.RoundTrips))
        {
            var lost = incumbent.RoundTrips.Except(candidate.RoundTrips).Select(Path.GetFileName);
            reason = $"it no longer reads {string.Join(", ", lost)} faithfully";
            return false;
        }
        reason = candidate.Detail;
        return true;
    }

    private static void PruneUnused(State state)
    {
        foreach (var dir in SafeDirectories(Path.Combine(Root, "site")))
            if (state.Lazurite == null || !Path.GetFileName(dir).Equals(SiteFolderName(state.Lazurite), StringComparison.OrdinalIgnoreCase))
                TryDelete(dir);
        foreach (var dir in SafeDirectories(Path.Combine(Root, "scripts")))
            if (!Path.GetFileName(dir).Equals(state.Scripts, StringComparison.OrdinalIgnoreCase))
                TryDelete(dir);
    }

    /// <summary>Work folders a crash left behind. An hour old is safely nobody's.</summary>
    private static void CleanWorkFolders()
    {
        foreach (var dir in SafeDirectories(WorkRoot))
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                TryDelete(dir);
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch { return []; }
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { Trace.WriteLine($"[ShaderMods] [Lazurite] Could not delete {dir}: {ex.Message}"); }
    }

    // ── Running ──────────────────────────────────────────────────────────────

    private static async Task<(JsonObject? Result, string? Error)> RunScriptAsync(
        string version, string scriptsId, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string>? onProgress = null)
    {
        string[] full = ["-I", "-X", "utf8", Path.Combine(ScriptsFolder(scriptsId), "material_upgrade.py"), SiteFolder(version), .. args];

        var run = await RunProcessAsync(PythonExe, full, timeout, ct, onStderrLine: line =>
        {
            if (onProgress != null && line.StartsWith("progress ", StringComparison.Ordinal))
                onProgress(line["progress ".Length..].Trim());
        });

        if (run.TimedOut)
            return (null, $"lazurite took longer than {timeout.TotalMinutes:0} minutes and was stopped");

        var result = MinecraftJson.ParseObject(run.Stdout.Trim());
        if (result == null)
            return (null, $"lazurite exited {run.ExitCode} without a result: {Tail(run.Stderr)}");
        if (MinecraftJson.GetInt(result["protocol"]) != Protocol)
            return (null, $"the converter script speaks protocol {MinecraftJson.GetString(result["protocol"]) ?? "?"}, this app {Protocol}");
        if (MinecraftJson.GetString(result["error"]) is { } error)
            return (null, error);
        return (result, null);
    }

    private sealed record ProcessRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    private static async Task<ProcessRun> RunProcessAsync(
        string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string>? onStdoutLine = null, Action<string>? onStderrLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (stdout) stdout.AppendLine(e.Data); onStdoutLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (stderr) stderr.AppendLine(e.Data); onStderrLine?.Invoke(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }

        // WaitForExitAsync returns before the redirected streams drain; this one waits for them.
        if (!timedOut) process.WaitForExit();

        lock (stdout) lock (stderr)
            return new ProcessRun(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    private static string Tail(string text, int max = 600)
    {
        text = text.Trim();
        return text.Length <= max ? text : "..." + text[^max..];
    }
}
