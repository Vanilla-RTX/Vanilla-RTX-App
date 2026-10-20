using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Vanilla_RTX_App.Core;

// Does it handle the same entry existing twice?! which one gets picked? make sure it is deterministic and documented what happens
// with such conflicts

// =====================================================================================================================
// TeletextItem — A single announcement entry.
//
// MinVersion / MaxVersion are the [minver:""] / [maxver:""] gate, inclusive at both ends, null meaning unbounded.
// The gate is applied by Teletext.GetFiltered, not by the parser, so a gated item stays in TeletextContent:
// CleanupOrphanedDismissals reads that content, and an item missing from it would have its dismissal pruned -
// widening the range later would then resurface something the user had already dismissed.
// =====================================================================================================================

public record TeletextItem(
    string Text,
    TeletextKind Kind,
    string? Glyph = null,
    int? CooldownMinutes = null,
    Version? MinVersion = null,
    Version? MaxVersion = null
);


// =====================================================================================================================
// TeletextContent — Static store populated by Teletext.
//
// HOW TO ADD A NEW VARIABLE:
//   1. Add a public static TeletextItem[]? property here.
//   2. Add a matching # Section to TELETEXT.md.
//      The section name must match the property name exactly (trimmed, case-insensitive).
//
// null always means "nothing to show" — absent section, empty content, or fetch failed.
//
// READING VALUES:
//   For a TeletextCard panel, call TeletextCard.Populate(panel, TeletextContent.YourProperty) - it
//   filters, and shows a "couldn't retrieve" notice rather than leaving the panel empty when
//   the fetch failed or the .md has no section for that module.
//   Anywhere else, call Teletext.GetFiltered(TeletextContent.YourProperty), which
//   strips dismissed entries according to each item's TeletextKind.
//
// SuspendControls is the one property that is not a TeletextItem[] and is not filled by that
// reflection map - see the # SuspendControls section in the .md format notes on Teletext.
// =====================================================================================================================

public static class TeletextContent
{
    /// <summary>
    /// Control names the .md asks to have disabled, already version-gated for this build.
    /// Null when the section is absent or lists nothing. Read by
    /// <see cref="WindowControlsManager.SuspendControls(Microsoft.UI.Xaml.UIElement?, System.Collections.Generic.IEnumerable{string}?)"/>.
    /// </summary>
    public static string[]? SuspendControls { get; set; }


    public static TeletextItem[]? Credits { get; set; }
    public static TeletextItem[]? LogAnnouncements { get; set; }
    public static TeletextItem[]? PackUpdateInfo { get; set; }
    public static TeletextItem[]? PackUpdateExtraInfo { get; set; }
    public static TeletextItem[]? ShaderModsInfo { get; set; }
    public static TeletextItem[]? LutManagerInfo { get; set; }
    public static TeletextItem[]? DLSSInfo { get; set; }
    public static TeletextItem[]? PackBrowserInfo { get; set; }
    public static TeletextItem[]? AlchitexInfo { get; set; }
}


// =====================================================================================================================
// TeletextKind — Controls dismiss behaviour and button visibility for a TeletextItem.
//
//   Pinned    (#  body)  — No dismiss button. Always shown. Cannot be hidden by the user.
//   Timed     (## body)  — Dismiss button with "Dismiss for a day" tooltip.
//                          Reappears after cooldown. Not added to the permanent blacklist.
//   Permanent (### body) — Dismiss button with "Dismiss" tooltip.
//                          Dismissed once → gone forever (until text changes in the .md).
// =====================================================================================================================

public enum TeletextKind
{
    Pinned,
    Timed,
    Permanent
}

// =====================================================================================================================
// TeletextJsonContext — Source-generated JSON metadata for trim-safe (de)serialization.
// Only the two shapes Teletext actually persists: a string[] and a Dictionary<string,string>.
// =====================================================================================================================

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class TeletextJsonContext : JsonSerializerContext
{
}

// =====================================================================================================================
// Teletext — Online text retrieval, caching, and per-user dismiss tracking.
//
// ── .md FILE FORMAT ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   # PropertyName          ← Section header. Must match a property in TeletextContent (case-insensitive).
//                             Spaces before/after the name are trimmed automatically.
//
//   Pinned text here.       ← PINNED: no dismiss button, always shown.
//                              This is any text that appears before the first ## or ### in the section.
//
//   ## (any title)          ← Opens a TIMED block. Title is ignored beyond modifier extraction.
//   Timed text here.        ← TIMED: dismiss button says "Dismiss for a day", reappears after cooldown.
//
//   ### (any title)         ← Opens a PERMANENT block. Title is ignored beyond modifier extraction.
//   Permanent text here.    ← PERMANENT: dismiss button says "Dismiss", gone forever once dismissed.
//
// ── RULES ───────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   • Single # only = section header. The space after it is optional, as it is after ## and ###.
//   • ## (not ###) = Timed item separator.
//   • ### or deeper = Permanent item separator.
//   • Separators are checked ### first, then ## to avoid misclassification.
//   • Text before the first separator in a section = Pinned.
//   • Empty / whitespace-only blocks are discarded.
//   • Properties whose # header is absent from the file are set to null.
//
// ── MODIFIER FIELDS ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Optional fields can be embedded anywhere in a section or item title line.
//   They are extracted and removed before the title is used for anything else,
//   so they never appear in displayed text and never break section name resolution.
//
//   Format:  [key:"value"]
//
//   Supported fields (all optional, order-independent, case-insensitive key):
//
//     [glyph:"E946"]      — Replaces the default info icon (&#xE946;) with the given Segoe Fluent
//                           Icons glyph. Value must be a 4–5 character hex code (no prefix/suffix or anything).
//                           Examples: E946, EF2C, F003
//     [cd:"120"]          — Cooldown in minutes before a Timed item reappears after being dismissed.
//     [minver:"1.2.3.4"]  — Only shown when the app version is at least this. Inclusive.
//     [maxver:"1.2.3.4"]  — Only shown when the app version is at most this. Inclusive.
//                           1 to 4 numeric parts; missing parts are 0, so "1.26" means 1.26.0.0.
//                           Use both for a range. A min above the max matches nothing.
//
//   Examples:
//     # PackUpdateInfo [glyph:"E7BA"]
//     ## Chaos Cubes [cd:"60"] [glyph:"E946"]
//     ### Update [glyph:"EF2C"] [cd:"1440"] // cd useless here
//     ##  [cd:"720"]
//     ### Please update [maxver:"1.26.20.0"]
//
//   Builds older than the version gate ignore [minver]/[maxver] as unknown keys and show the
//   item regardless - a [minver] announcement still reaches everyone still on such a build.
//
//   Failure handling:
//     • Unknown field names are logged and ignored.
//     • Malformed values (wrong type, out of range) are logged and ignored; defaults apply.
//     • Any exception during modifier parsing is caught; the item is still created with defaults.
//
// ── FORMATTING ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Body text supports markdown's inline formatting and nothing else (MarkdownRenderer.RenderInlines):
//     *italic* _italic_ **bold** __bold__ ~~strikethrough~~ `code`
//     [label](https://example.com), and bare https://… addresses, as clickable links
//   Lines starting "- " or "1. " stay literal text, every newline stays a line break, and an
//   underscore inside a word (terrain_texture.json) is never emphasis - so text written as plain
//   text reads exactly as it always has. Escape a literal * or _ with a backslash when it would
//   otherwise pair up: \*.
//
//   Links open only if they are absolute http, https or mailto. The sidebar log shows formatting
//   stripped and links as "label (url)". Builds older than this show the raw markdown, so keep it
//   light enough to read that way. The dismiss hash is over the raw text, markup included.
//
// ── # SuspendControls ───────────────────────────────────────────────────────────────────────────────────────────────
//
//   A reserved section name. Every non-empty line in it, with its leading #s and surrounding
//   whitespace stripped, is the x:Name of a control to disable for the rest of the session - the
//   remote kill switch for a feature that has broken badly enough that nobody should reach it
//   until an update ships. These all name the same kind of thing:
//
//     # SuspendControls
//     ### BetterRTXButton
//     ## DLSSButton [maxver:"1.26.21.0"]      ← only on builds up to the one that fixes it
//     LaunchButton
//
//   [minver]/[maxver] gate a line here exactly as they gate a Teletext, and they are the reason to keep
//   a line after the fix ships: dropping it outright re-enables the broken control on every build
//   that still has the bug. Names are matched case-insensitively. A name nothing carries does
//   nothing. Builds older than this section ignore it as an unknown section.
//
// ── DISMISS SYSTEM ──────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Teletext.Dismiss(text)                     — Permanently blacklists text.
//   Teletext.DismissTimed(text, cooldownMins?) — Records expiry. Pass item.CooldownMinutes.
//   Teletext.GetFiltered(…)                    — Returns only items that should currently show.
//
//   TeletextCard calls the correct method automatically — you never call these directly.
//
// ── DISMISS CLEANUP ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//   After every successful fresh fetch, orphaned dismiss hashes are pruned automatically.
//   A hash is "orphaned" when the Teletext it was dismissing no longer exists in the current .md.
//   Only permanent dismissals that have no matching item in the current content are removed.
//   Timed dismissals are already self-expiring and are pruned on load — no extra cleanup needed.
//
//   This runs only after a confirmed successful network fetch, never when applying stale cache,
//   so a temporary fetch failure can never accidentally wipe valid dismissals.
// =====================================================================================================================

public static class Teletext
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const string URL =
        "https://raw.githubusercontent.com/vanilla-rtx/Vanilla-RTX-App/main/TELETEXT.md";

    /// <summary>
    /// The announcements file as <see cref="AssetUpdater"/> sees it: the cooldown, the cached
    /// copy, the conditional request and the retries all belong to it, and this file only reads
    /// what it hands back. No packaged copy - a build's own announcements would be stale the day
    /// it shipped - so a first run has nothing to fall back on and fetches whatever the schedule
    /// says.
    ///
    /// <para>The 8s deadline is short on purpose: this runs at startup, nothing waits on it, and
    /// a slow host must not hold the app up when a cached copy will do.</para>
    /// </summary>
    private static readonly ManagedAsset Announcements = new(
        URL,
        TimeSpan.FromHours(1),
        FileName: "Teletext_Cache.md",
        Timeout: TimeSpan.FromSeconds(8));

    private const string KEY_DISMISSED = "Teletext_Dismissed";
    private const string KEY_TIMED_DISMISSED = "Teletext_TimedDismissed";

    private static readonly TimeSpan TIMED_DURATION = TimeSpan.FromDays(1); // Default cooldown of dismissable-but-returning Teletext
    public static TimeSpan TimedDuration => TIMED_DURATION;

    // ── Reflection map: lowercase property name → PropertyInfo ────────────────

    private static readonly Dictionary<string, PropertyInfo> _propMap =
        typeof(TeletextContent)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(TeletextItem[]))
            .ToDictionary(p => p.Name.ToLowerInvariant(), p => p, StringComparer.Ordinal);

    private const string SUSPEND_SECTION = "suspendcontrols";

    // ── Modifier regex ────────────────────────────────────────────────────────
    // Matches [key:"value"] anywhere in a line. Key is word chars; value is anything except ".

    private static readonly Regex _modifierRegex = new(
        @"\[(\w+):""([^""]*)""\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The running app's version, parsed once for the [minver]/[maxver] gate.</summary>
    private static readonly Lazy<Version?> _appVersion =
        new(() => ParseGateVersion(EnvironmentVariables.appVersion));

    // ── Concurrency ───────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static volatile bool _fetching;

    // ── Permanent dismissed set ───────────────────────────────────────────────

    private static HashSet<string>? _dismissed;
    private static readonly object _dismissLock = new();

    // ── Timed dismissed dictionary: hash → expiry UTC ─────────────────────────

    private static Dictionary<string, DateTime>? _timedDismissed;
    private static readonly object _timedDismissLock = new();

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Fire-and-forget startup call. Applies any cached content immediately,
    /// then re-fetches in the background if the cooldown has expired.
    /// Safe to call from any thread. Never throws.
    /// </summary>
    public static void TriggerUpdate() => _ = TriggerUpdateAsync();

    /// <summary>
    /// Awaitable version of <see cref="TriggerUpdate"/>.
    /// Returns true if a fresh network fetch succeeded, false otherwise.
    /// </summary>
    public static Task<bool> TriggerUpdateAsync() => LatestUpdate = RunUpdateAsync(force: false);

    /// <summary>
    /// The most recent <see cref="TriggerUpdateAsync"/>, so something that didn't start the
    /// update can still act on its outcome. App starts the fetch before MainWindow exists, and
    /// MainWindow awaits this to re-apply <see cref="TeletextContent.SuspendControls"/> once
    /// fresh content has landed. Completed-false until the first trigger.
    /// </summary>
    public static Task<bool> LatestUpdate { get; private set; } = Task.FromResult(false);

    private static async Task<bool> RunUpdateAsync(bool force)
    {
        Trace.WriteLine("[Teletext] TriggerUpdateAsync");
        TryApplyCache();

        // The cache, the cooldown, the retries and the conditional request all belong to
        // AssetUpdater; what stays here is what only this file knows - how to read the markdown,
        // and that a genuinely new copy is the one occasion to prune dead dismissals.
        if (_fetching || !await _lock.WaitAsync(0))
        {
            Trace.WriteLine("[Teletext] Fetch already in progress — skipping");
            return false;
        }

        _fetching = true;
        try
        {
            var read = await AssetUpdater.ResolveFreshOrCachedAsync(Announcements, force);

            if (read.Source != AssetSource.Fetched || read.Path is null)
            {
                Trace.WriteLine($"[Teletext] Nothing new ({read.Source}) - staying on cache (if available).");
                return false;
            }

            var raw = File.ReadAllText(read.Path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Trace.WriteLine("[Teletext] Fetched copy was empty");
                return false;
            }

            ParseAndApply(raw);
            CleanupOrphanedDismissals();
            Trace.WriteLine("[Teletext] Fetch and parse succeeded");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] Unexpected error: {ex}");
            return false;
        }
        finally
        {
            _fetching = false;
            _lock.Release();
        }
    }

    /// <summary>Fetches now, whatever the cooldown says.</summary>
    public static void ForceRefresh() => LatestUpdate = RunUpdateAsync(force: true);

    /// <summary>
    /// Filters <paramref name="source"/> down to items that should currently be shown,
    /// according to each item's TeletextKind and the user's dismiss history.
    /// <para>
    /// Pinned    — always passes through.
    /// Timed     — passes through once its cooldown has elapsed.
    /// Permanent — passes through only if never permanently dismissed.
    /// </para>
    /// Any kind is dropped first if its [minver]/[maxver] range excludes this app version.
    /// Always use this instead of reading TeletextContent properties directly.
    /// </summary>
    public static TeletextItem[]? GetFiltered(TeletextItem[]? source)
    {
        if (source is null || source.Length == 0) return null;

        var dismissed = GetDismissed();
        var timedDismissed = GetTimedDismissed();
        var now = DateTime.UtcNow;

        var kept = source
            .Where(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Text)) return false;
                if (!IsInVersionRange(item.MinVersion, item.MaxVersion)) return false;
                var hash = DismissHash(item.Text);
                return item.Kind switch
                {
                    TeletextKind.Pinned => true,
                    TeletextKind.Timed => !timedDismissed.TryGetValue(hash, out var expiry) || now >= expiry,
                    TeletextKind.Permanent => !dismissed.Contains(hash),
                    _ => true
                };
            })
            .ToArray();

        return kept.Length > 0 ? kept : null;
    }

    // ── Version gate ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this build falls inside [min, max], inclusive, null meaning unbounded. An app
    /// version that can't be read passes every gate: failing open shows an announcement to
    /// someone it wasn't meant for, failing closed would silently hide every gated one.
    /// </summary>
    private static bool IsInVersionRange(Version? min, Version? max)
    {
        var app = _appVersion.Value;
        if (app is null) return true;
        if (min is not null && app < min) return false;
        if (max is not null && app > max) return false;
        return true;
    }

    /// <summary>
    /// 1 to 4 non-negative integer parts, padded to 4 with zeros; null for anything else.
    ///
    /// <para>The padding is load-bearing. <see cref="Version"/> treats an absent build or
    /// revision as -1, so <c>new Version("1.26")</c> sorts <i>below</i> 1.26.0.0 and a
    /// [maxver:"1.26"] would exclude the very build it names.</para>
    /// </summary>
    private static Version? ParseGateVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Trim().Split('.');
        if (parts.Length is < 1 or > 4) return null;

        var n = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                return null;

        return new Version(n[0], n[1], n[2], n[3]);
    }

    /// <summary>
    /// Permanently blacklists <paramref name="text"/>.
    /// It will never appear in <see cref="GetFiltered"/> results again until the text changes.
    /// Only call for Permanent items — TeletextCard handles this automatically.
    /// </summary>
    public static void Dismiss(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_dismissLock)
            {
                var d = GetDismissed();
                if (d.Add(DismissHash(text)))
                {
                    SaveDismissed(d);
                    Trace.WriteLine($"[Teletext] Dismissed: \"{text.Substring(0, Math.Min(60, text.Length))}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] Dismiss failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Hides <paramref name="text"/> for the item's cooldown duration (or the global default).
    /// After the window elapses it will reappear in <see cref="GetFiltered"/> results.
    /// Only call for Timed items — TeletextCard handles this automatically.
    /// Pass <paramref name="cooldownMinutes"/> from <see cref="TeletextItem.CooldownMinutes"/> to
    /// respect per-item [cd:""] overrides from the .md file.
    /// </summary>
    public static void DismissTimed(string text, int? cooldownMinutes = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_timedDismissLock)
            {
                var d = GetTimedDismissed();
                var hash = DismissHash(text);
                var duration = cooldownMinutes.HasValue
                    ? (cooldownMinutes.Value == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(cooldownMinutes.Value))
                    : TIMED_DURATION;
                d[hash] = DateTime.UtcNow.Add(duration);
                SaveTimedDismissed(d);
                Trace.WriteLine($"[Teletext] Timed dismiss until {d[hash]:HH:mm:ss} " +
                    $"(cd={duration.TotalMinutes:F0}min): \"{text.Substring(0, Math.Min(60, text.Length))}\"");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] DismissTimed failed: {ex.Message}");
        }
    }

    // ── Hash helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// The identity a dismissal is stored under: the first 8 bytes of SHA-256 over the Teletext's
    /// own text, as 16 hex characters.
    ///
    /// <para><b>The text is the identity.</b> Editing a published Teletext - even fixing a typo -
    /// produces a different hash, so everyone who dismissed the old wording sees the new one
    /// again. That is the intended behaviour for a changed announcement, and the reason not
    /// to touch the text of one that is merely still running.</para>
    ///
    /// <para>Truncated because these are stored per user and only ever compared against each
    /// other; a collision costs one wrongly-hidden announcement, not correctness.</para>
    /// </summary>
    private static string DismissHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    // =========================================================================
    // Permanent dismiss storage
    // =========================================================================

    /// <summary>
    /// The permanently-dismissed hash set, loaded from LocalSettings on first use and cached
    /// for the session. Double-checked so concurrent first callers load it once.
    /// </summary>
    private static HashSet<string> GetDismissed()
    {
        if (_dismissed is not null) return _dismissed;
        lock (_dismissLock)
        {
            _dismissed ??= LoadDismissed();
        }
        return _dismissed;
    }

    private static HashSet<string> LoadDismissed()
    {
        try
        {
            var raw = ApplicationData.Current.LocalSettings.Values[KEY_DISMISSED] as string;
            if (!string.IsNullOrEmpty(raw))
            {
                var arr = JsonSerializer.Deserialize(raw, TeletextJsonContext.Default.StringArray);
                if (arr is not null)
                    return new HashSet<string>(arr, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] LoadDismissed failed: {ex.Message}");
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static void SaveDismissed(HashSet<string> dismissed)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[KEY_DISMISSED] =
                JsonSerializer.Serialize(dismissed.ToArray(), TeletextJsonContext.Default.StringArray);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] SaveDismissed failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Timed dismiss storage
    // =========================================================================

    /// <summary>
    /// Hash to expiry time for temporarily-dismissed Teletext, loaded once and cached like
    /// <see cref="GetDismissed"/>. An entry whose time has passed is simply no longer
    /// dismissed; pruning is separate housekeeping, not a precondition for correctness.
    /// </summary>
    private static Dictionary<string, DateTime> GetTimedDismissed()
    {
        if (_timedDismissed is not null) return _timedDismissed;
        lock (_timedDismissLock)
        {
            _timedDismissed ??= LoadTimedDismissed();
        }
        return _timedDismissed;
    }

    private static Dictionary<string, DateTime> LoadTimedDismissed()
    {
        try
        {
            var raw = ApplicationData.Current.LocalSettings.Values[KEY_TIMED_DISMISSED] as string;
            if (!string.IsNullOrEmpty(raw))
            {
                var dict = JsonSerializer.Deserialize(raw, TeletextJsonContext.Default.DictionaryStringString);
                if (dict is not null)
                {
                    var now = DateTime.UtcNow;
                    var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    foreach (var (k, v) in dict)
                        // RoundtripKind: these were written as UTC with "O", and without it
                        // they parse back as local time, off by the machine's offset.
                        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) && dt > now)
                            result[k] = dt;
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] LoadTimedDismissed failed: {ex.Message}");
        }
        return new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    private static void SaveTimedDismissed(Dictionary<string, DateTime> dismissed)
    {
        try
        {
            var toStore = dismissed.ToDictionary(k => k.Key, v => v.Value.ToString("O"));
            ApplicationData.Current.LocalSettings.Values[KEY_TIMED_DISMISSED] =
                JsonSerializer.Serialize(toStore, TeletextJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] SaveTimedDismissed failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Cache
    // =========================================================================

    /// <summary>
    /// Populates <see cref="TeletextContent"/> from the cached copy AssetUpdater holds, so
    /// the app has something to show before - or instead of - a successful fetch. Silent on
    /// failure: no cache is a normal first-run state, not an error.
    /// </summary>
    private static void TryApplyCache()
    {
        try
        {
            var path = AssetUpdater.Resolve(Announcements);
            if (path is not null && File.Exists(path))
            {
                var cached = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    Trace.WriteLine($"[Teletext] Applying cache ({cached.Length} chars)");
                    ParseAndApply(cached);
                    return;
                }
            }
            Trace.WriteLine("[Teletext] No cache found");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] TryApplyCache failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Modifier parser
    // =========================================================================

    /// <summary>
    /// Extracts all [key:"value"] modifier fields from a title line.
    /// Returns the cleaned title (fields removed, trimmed) plus parsed modifier values.
    /// Any field that fails to parse is logged and skipped; defaults remain null.
    /// </summary>
    private readonly record struct Modifiers(
        string? Glyph, int? CooldownMinutes, Version? MinVersion, Version? MaxVersion);

    private static (string cleanTitle, Modifiers modifiers) ExtractModifiers(string titleLine)
    {
        string? glyph = null;
        int? cooldownMinutes = null;
        Version? minVersion = null;
        Version? maxVersion = null;

        var clean = _modifierRegex.Replace(titleLine, match =>
        {
            var key = match.Groups[1].Value.ToLowerInvariant();
            var val = match.Groups[2].Value.Trim();
            try
            {
                switch (key)
                {
                    case "glyph":
                        // Accept 4–5 hex digit codes, e.g. E946, EF2C, F003F
                        if (val.Length is >= 4 and <= 5 &&
                            uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out _))
                            glyph = val.ToUpperInvariant();
                        else
                            Trace.WriteLine($"[Teletext] Invalid glyph value: '{val}' — must be 4–5 hex digits, ignored");
                        break;

                    case "cd":
                        if (int.TryParse(val, out var cd) && cd >= 0)
                            cooldownMinutes = cd;
                        else
                            Trace.WriteLine($"[Teletext] Invalid cd value: '{val}' — must be a positive integer (minutes), ignored");
                        break;

                    case "minver":
                    case "maxver":
                        var version = ParseGateVersion(val);
                        if (version is null)
                            Trace.WriteLine($"[Teletext] Invalid {key} value: '{val}' — must be 1–4 dot-separated numbers, ignored");
                        else if (key == "minver")
                            minVersion = version;
                        else
                            maxVersion = version;
                        break;

                    default:
                        Trace.WriteLine($"[Teletext] Unknown modifier key: '{key}' — ignored");
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Teletext] Modifier parse error for [{key}:\"{val}\"]: {ex.Message}");
            }

            return string.Empty; // remove the field token from the title string
        });

        return (clean.Trim(), new Modifiers(glyph, cooldownMinutes, minVersion, maxVersion));
    }

    // =========================================================================
    // Parser
    // =========================================================================

    /// <summary>
    /// Nullifies all TeletextContent properties, parses the raw .md,
    /// and writes results via reflection. Missing sections stay null.
    /// </summary>
    private static void ParseAndApply(string raw)
    {
        try
        {
            foreach (var prop in _propMap.Values)
                prop.SetValue(null, null);
            TeletextContent.SuspendControls = null;

            var (sections, suspended) = Parse(raw);

            TeletextContent.SuspendControls = suspended.Count > 0 ? suspended.ToArray() : null;
            if (suspended.Count > 0)
                Trace.WriteLine($"[Teletext] Suspending {suspended.Count} control(s): {string.Join(", ", suspended)}");

            foreach (var (key, blocks) in sections)
            {
                if (!_propMap.TryGetValue(key, out var prop))
                {
                    Trace.WriteLine($"[Teletext] No property for section '{key}' — ignoring");
                    continue;
                }

                var items = blocks
                    .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                    .Select(b => new TeletextItem(
                        b.Text.Trim(),
                        b.Kind,
                        Glyph: b.Modifiers.Glyph,
                        CooldownMinutes: b.Modifiers.CooldownMinutes,
                        MinVersion: b.Modifiers.MinVersion,
                        MaxVersion: b.Modifiers.MaxVersion))
                    .ToArray();

                prop.SetValue(null, items.Length > 0 ? items : null);
                Trace.WriteLine($"[Teletext] '{key}' → {items.Length} item(s) " +
                    $"(pinned={items.Count(i => i.Kind == TeletextKind.Pinned)}, " +
                    $"timed={items.Count(i => i.Kind == TeletextKind.Timed)}, " +
                    $"permanent={items.Count(i => i.Kind == TeletextKind.Permanent)})");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] ParseAndApply exception: {ex}");
        }
    }

    private readonly record struct ParsedBlock(string Text, TeletextKind Kind, Modifiers Modifiers);

    /// <summary>
    /// Core parser. Returns lowercase-section-name → ordered blocks, plus the control names
    /// listed under # SuspendControls.
    ///
    /// Single #     = opens a new section. Modifiers on this line apply to the Pinned block only.
    ///               [glyph:] on a # line does NOT inherit to ## / ### child blocks.
    ///               Each child block uses the default glyph unless it specifies its own [glyph:].
    /// ###           = Permanent item separator (checked before ## to avoid misclassification).
    /// ## (not ###)  = Timed item separator.
    /// No separator  = entire section body is one Pinned item.
    /// Separator titles are ignored beyond modifier extraction.
    ///
    /// # SuspendControls is not a section of blocks: every line in it is a control name, with
    /// ##/### read as decoration rather than as separators. Its version gate is applied here
    /// rather than at display time, since there is no dismissal state for it to protect.
    /// </summary>
    private static (Dictionary<string, List<ParsedBlock>> sections, List<string> suspended) Parse(string raw)
    {
        var result = new Dictionary<string, List<ParsedBlock>>(StringComparer.Ordinal);
        var suspended = new List<string>();
        var lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        List<ParsedBlock>? currentBlocks = null;
        var inSuspendSection = false;
        var block = new StringBuilder();

        // Per-block state — reset for every ## / ### separator
        var currentKind = TeletextKind.Pinned;
        var currentModifiers = default(Modifiers);

        void CommitBlock()
        {
            if (currentBlocks is null) return;
            currentBlocks.Add(new ParsedBlock(block.ToString(), currentKind, currentModifiers));
            block.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // ── Single # header ───────────────────────────────────────────────
            if (line.StartsWith('#') && !line.StartsWith("##") && line.Length > 1)
            {
                CommitBlock();

                var (cleanTitle, modifiers) = ExtractModifiers(line.Substring(1));

                var name = cleanTitle.ToLowerInvariant();
                if (string.IsNullOrEmpty(name)) continue;

                inSuspendSection = name == SUSPEND_SECTION;
                if (inSuspendSection)
                {
                    currentBlocks = null;
                    Trace.WriteLine("[Teletext] Section: SuspendControls");
                    continue;
                }

                currentBlocks = new List<ParsedBlock>();
                result[name] = currentBlocks;
                block.Clear();
                currentKind = TeletextKind.Pinned;
                // Applies to the Pinned block only, not inherited by children. cd is dropped
                // because Pinned blocks are never dismissed.
                currentModifiers = modifiers with { CooldownMinutes = null };

                Trace.WriteLine($"[Teletext] Section: '{name}'" +
                    (modifiers.Glyph != null ? $" glyph={modifiers.Glyph}" : ""));
            }
            // ── Any line under # SuspendControls → a control name ─────────────
            else if (inSuspendSection)
            {
                var (controlName, modifiers) = ExtractModifiers(line.TrimStart().TrimStart('#'));
                if (string.IsNullOrEmpty(controlName)) continue;

                if (IsInVersionRange(modifiers.MinVersion, modifiers.MaxVersion))
                    suspended.Add(controlName);
                else
                    Trace.WriteLine($"[Teletext] Suspension of '{controlName}' is outside this version's range — skipped");
            }
            // ── ### or deeper → Permanent ─────────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("###"))
            {
                CommitBlock();
                currentKind = TeletextKind.Permanent;

                // Null glyph means default — no inheritance from the # line
                (_, currentModifiers) = ExtractModifiers(line.Substring(3));
                LogSeparator("### → Permanent", currentModifiers);
            }
            // ── ## (exactly, not ###) → Timed ─────────────────────────────────
            else if (currentBlocks is not null && line.StartsWith("##"))
            {
                CommitBlock();
                currentKind = TeletextKind.Timed;

                (_, currentModifiers) = ExtractModifiers(line.Substring(2));
                LogSeparator("## → Timed", currentModifiers);
            }
            // ── Body line ─────────────────────────────────────────────────────
            else if (currentBlocks is not null)
            {
                block.Append(line).Append('\n');
            }
        }

        CommitBlock();
        return (result, suspended);
    }

    private static void LogSeparator(string label, Modifiers m) =>
        Trace.WriteLine($"[Teletext] {label}" +
            (m.Glyph != null ? $" glyph={m.Glyph}" : "") +
            (m.CooldownMinutes != null ? $" cd={m.CooldownMinutes}" : "") +
            (m.MinVersion != null ? $" minver={m.MinVersion}" : "") +
            (m.MaxVersion != null ? $" maxver={m.MaxVersion}" : ""));

    /// <summary>
    /// Removes permanently dismissed hashes that no longer match any item in the current .md.
    /// Called automatically after every successful fresh fetch — never on stale cache.
    ///
    /// A dismissal is orphaned when the Teletext it suppressed has been removed or reworded in the .md.
    /// Since the hash is derived from the item text, any text change produces a new hash,
    /// making the old dismissal inert. This pass finds and removes those inert entries.
    ///
    /// Safety: if anything fails (content unset, storage error, etc.) the method returns silently.
    /// Existing dismissals are never touched unless they are confirmed to be orphaned.
    /// </summary>
    private static void CleanupOrphanedDismissals()
    {
        try
        {
            // Collect hashes of every Permanent item currently in content.
            // Only Permanent items are ever added to the dismissed set, so we only
            // need to consider those — Pinned and Timed items are irrelevant here.
            var liveHashes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prop in _propMap.Values)
            {
                if (prop.GetValue(null) is not TeletextItem[] items) continue;
                foreach (var item in items)
                {
                    if (item.Kind == TeletextKind.Permanent && !string.IsNullOrWhiteSpace(item.Text))
                        liveHashes.Add(DismissHash(item.Text));
                }
            }

            lock (_dismissLock)
            {
                var d = GetDismissed();
                var before = d.Count;
                var removed = d.RemoveWhere(hash => !liveHashes.Contains(hash));

                if (removed > 0)
                {
                    SaveDismissed(d);
                    Trace.WriteLine($"[Teletext] Cleanup: removed {removed} orphaned dismissal(s) " +
                        $"({before} → {d.Count})");
                }
                else
                {
                    Trace.WriteLine($"[Teletext] Cleanup: no orphaned dismissals ({d.Count} current)");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Teletext] CleanupOrphanedDismissals failed: {ex.Message}");
        }
    }

}
