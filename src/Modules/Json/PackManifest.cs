using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules.Json;

/// <summary>
/// One entry in a manifest's <c>modules</c> array. <see cref="Node"/> is the live object, so a
/// caller that needs to *write* (Alchitex rewriting a pack it just generated into) mutates it
/// directly; everything else reads the pre-extracted fields.
/// </summary>
public sealed class ManifestModule
{
    public required JsonObject Node { get; init; }
    public string? Uuid { get; init; }
    public string? Type { get; init; }
    public string? Description { get; init; }

    public bool IsResources => string.Equals(Type, "resources", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the four module types that make a behaviour pack. A closed list on purpose:
    /// <c>skin_pack</c> and <c>world_template</c> are neither resource nor behaviour packs, and
    /// "not resources" would send them to the behaviour folder too.
    /// </summary>
    public bool IsBehavior => Type is { } t && BehaviorModuleTypes.Contains(t);

    private static readonly HashSet<string> BehaviorModuleTypes =
        new(StringComparer.OrdinalIgnoreCase) { "data", "script", "javascript", "client_data" };
}

/// <summary>
/// The app's single reader for Minecraft Bedrock pack manifests.
///
/// <para><b>Why.</b> PackLocator, PackUpdater, PackBrowserOverlay, ExpImpDel, ShaderModManager
/// and Alchitex each grew their own parser as the app grew, each covering the subset of fields
/// it happened to need and each disagreeing slightly about leniency, about where a legacy
/// manifest keeps its UUID, and about what an unparseable file means. This class is the union
/// of all of them: parse once, tolerantly (see <see cref="MinecraftJson"/>), expose every field
/// any module ever wanted, and let each caller ignore the rest.</para>
///
/// <para><b>Two formats.</b> Modern <c>manifest.json</c> keeps identity at <c>header.uuid</c>
/// and its modules at the root. Legacy <c>pack_manifest.json</c> (pre-1.16) keeps identity at
/// <c>header.pack_id</c>, its modules nested *inside* the header, and its version as a plain
/// string at <c>header.packs_version</c>. <see cref="IsLegacy"/> selects between them, and every
/// property below already accounts for it - a caller never branches on format itself.</para>
///
/// <para><b>Never throws.</b> Every factory returns null for a file that is missing, unreadable,
/// or not a JSON object, and every property degrades to null/empty for a field that is absent or
/// of an unexpected kind. A third-party pack's manifest is data from the wild, and a malformed
/// one is a pack to skip, not an error to propagate - PackLocator states the semantic outright:
/// if a manifest is malformed, it definitively is not one of ours.</para>
/// </summary>
public sealed class PackManifest
{
    public const string ModernFileName = "manifest.json";
    public const string LegacyFileName = "pack_manifest.json";

    /// <summary>The live parsed document. Mutate this only if you intend to write it back.</summary>
    public JsonObject Root { get; }

    /// <summary>True when this came from a <c>pack_manifest.json</c> (pre-1.16 layout).</summary>
    public bool IsLegacy { get; }

    /// <summary>The header object, or null if the manifest has none (or it isn't an object).</summary>
    public JsonObject? Header { get; }

    /// <summary>Where the file was read from, when it came from one. For logging.</summary>
    public string? SourcePath { get; }

    private readonly List<ManifestModule> _modules;
    private readonly List<string> _capabilities;

    private PackManifest(JsonObject root, bool isLegacy, string? sourcePath)
    {
        Root = root;
        IsLegacy = isLegacy;
        SourcePath = sourcePath;
        Header = root["header"] as JsonObject;

        // Modern keeps modules at the root; legacy nests them inside the header.
        var modulesNode = isLegacy ? Header?["modules"] : root["modules"];
        _modules = new List<ManifestModule>();
        if (modulesNode is JsonArray moduleArray)
        {
            foreach (var entry in moduleArray)
            {
                if (entry is not JsonObject moduleObject) continue;
                _modules.Add(new ManifestModule
                {
                    Node = moduleObject,
                    Uuid = MinecraftJson.GetString(moduleObject["uuid"]),
                    Type = MinecraftJson.GetString(moduleObject["type"]),
                    Description = MinecraftJson.GetString(moduleObject["description"]),
                });
            }
        }

        _capabilities = MinecraftJson.GetStringArray(root["capabilities"]);
    }

    // ─────────────────────────── Factories ───────────────────────────

    /// <summary>Parses manifest text. <paramref name="isLegacy"/> selects the field layout.</summary>
    public static PackManifest? Parse(string? json, bool isLegacy = false, string? sourcePath = null)
    {
        var root = MinecraftJson.ParseObject(json);
        return root == null ? null : new PackManifest(root, isLegacy, sourcePath);
    }

    /// <summary>
    /// Reads a manifest from disk, inferring the format from the file's own name so callers that
    /// scan for both never have to thread an <c>isLegacy</c> flag around.
    /// </summary>
    public static PackManifest? FromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path), IsLegacyFileName(path), path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MANIFEST] Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc cref="FromFile"/>
    public static async Task<PackManifest?> FromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(await File.ReadAllTextAsync(path, cancellationToken), IsLegacyFileName(path), path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MANIFEST] Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads a manifest out of an open stream (a zip entry) without closing it.</summary>
    public static PackManifest? FromStream(Stream stream, bool isLegacy, string? sourcePath = null)
    {
        var root = MinecraftJson.ParseObjectStream(stream);
        return root == null ? null : new PackManifest(root, isLegacy, sourcePath);
    }

    /// <summary>True for a file named <c>pack_manifest.json</c>, whatever its directory.</summary>
    public static bool IsLegacyFileName(string path)
        => Path.GetFileName(path).Equals(LegacyFileName, StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────── Identity ───────────────────────────

    /// <summary>
    /// The pack's identity UUID - <c>header.uuid</c> on a modern manifest, <c>header.pack_id</c>
    /// on a legacy one. A legacy manifest that carries <c>uuid</c> instead falls back to it:
    /// the two names mean the same thing, and the tools that produced these files were not
    /// consistent about which they wrote.
    /// </summary>
    public string? HeaderUuid => IsLegacy
        ? MinecraftJson.GetString(Header?["pack_id"]) ?? MinecraftJson.GetString(Header?["uuid"])
        : MinecraftJson.GetString(Header?["uuid"]);

    public string? HeaderName => MinecraftJson.GetString(Header?["name"]);

    public string? HeaderDescription => MinecraftJson.GetString(Header?["description"]);

    /// <summary><c>format_version</c>, coerced from a quoted number if the pack wrote one.</summary>
    public int? FormatVersion => MinecraftJson.GetInt(Root["format_version"]);

    // ─────────────────────────── Modules ───────────────────────────

    public IReadOnlyList<ManifestModule> Modules => _modules;

    /// <summary>The first module object, which is the one every UUID check in this app means.</summary>
    public ManifestModule? FirstModule => _modules.Count > 0 ? _modules[0] : null;

    /// <summary>Shorthand for <c>FirstModule?.Uuid</c> - the "module UUID" pack identity check.</summary>
    public string? FirstModuleUuid => FirstModule?.Uuid;

    /// <summary>
    /// True if any module declares <c>"type": "resources"</c>. This is what distinguishes a
    /// resource pack from a behaviour pack, and it lives in the modules array alone - identity
    /// lives in the header, and neither bleeds into the other.
    /// </summary>
    public bool HasResourceModule => _modules.Any(m => m.IsResources);

    /// <summary>
    /// True if any module declares a behaviour-pack type (<see cref="ManifestModule.IsBehavior"/>).
    /// Only meaningful when <see cref="HasResourceModule"/> is false - a manifest declaring both
    /// is treated as a resource pack, as it always was.
    /// </summary>
    public bool HasBehaviorModule => _modules.Any(m => m.IsBehavior);

    // ───────────────────────── Capabilities ─────────────────────────

    /// <summary>
    /// Declared capabilities, verbatim and in file order. Non-string entries in a malformed
    /// array are dropped rather than throwing. Empty for a manifest that declares none.
    /// </summary>
    public IReadOnlyList<string> Capabilities => _capabilities;

    /// <summary>Case-insensitive, whitespace-tolerant capability test.</summary>
    public bool HasCapability(string capability)
        => _capabilities.Any(c => c.Trim().Equals(capability, StringComparison.OrdinalIgnoreCase));

    // ─────────────────────────── Version ───────────────────────────

    /// <summary>The raw <c>header.version</c> node, for a caller that needs to inspect its kind.</summary>
    public JsonNode? VersionNode => Header?["version"];

    /// <summary>
    /// <c>header.version</c> as an integer array, whatever its length. Null unless every element
    /// is a genuine JSON integer - a SemVer string, a float, or a quoted number all disqualify,
    /// because a half-understood version compares wrong and comparing wrong is worse than not
    /// comparing at all.
    /// </summary>
    public int[]? VersionArray => MinecraftJson.GetIntArray(VersionNode);

    /// <summary>
    /// <c>header.version</c> as an exactly-three-element, non-negative integer array - the shape
    /// every first-party Vanilla RTX pack ships. Null for anything else, which is how the pack
    /// locator concludes a pack definitively is not one of ours without ever throwing.
    /// </summary>
    public int[]? VersionTriplet
    {
        get
        {
            var version = MinecraftJson.GetIntArray(VersionNode, requiredLength: 3);
            if (version == null) return null;
            return Array.Exists(version, v => v < 0) ? null : version;
        }
    }

    /// <summary>
    /// A dotted display version ("1.26.15") built from <see cref="VersionArray"/>, or null when
    /// the version isn't an integer array.
    /// </summary>
    public string? VersionDisplay
    {
        get
        {
            var version = VersionArray;
            return version is { Length: > 0 } ? string.Join(".", version) : null;
        }
    }

    /// <summary>
    /// The version as the pack wrote it in string form: <c>header.packs_version</c> first (the
    /// legacy field), then <c>header.version</c> when that is itself a string rather than an
    /// array. Null when the version is only available as an array - use
    /// <see cref="VersionDisplay"/> for that. Callers decide whether the string is acceptable
    /// (PackBrowser, for instance, requires strict X.Y.Z).
    /// </summary>
    public string? VersionString
    {
        get
        {
            if (MinecraftJson.GetString(Header?["packs_version"]) is string packsVersion
                && !string.IsNullOrWhiteSpace(packsVersion))
                return packsVersion;

            return VersionNode is JsonArray ? null : MinecraftJson.GetString(VersionNode);
        }
    }
}
