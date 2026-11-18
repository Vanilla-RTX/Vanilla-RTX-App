using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// One internal number the advanced settings expose: where it is shown, what it means, its built-in
/// value and the range it may take. <see cref="Value"/> is a plain field so the per-pixel loops that
/// read some of these pay a field load, not a lookup.
/// </summary>
public sealed class Tunable
{
    public required string Key { get; init; }
    public required string Section { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required double Default { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public bool IsInteger { get; init; }

    /// <summary>Read once at startup by whatever uses it, so a change shows after the app restarts.</summary>
    public bool NeedsRestart { get; init; }

    public double Value;

    public bool IsModified => Math.Abs(Value - Default) > 1e-9;

    internal double Clamp(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return Default;
        v = Math.Clamp(v, Min, Max);
        return IsInteger ? Math.Round(v) : v;
    }
}

/// <summary>
/// The numbers the app normally decides for itself, exposed behind the settings panel's advanced
/// section for anyone who wants to change them - RTX Reactor's generation dials, the water and
/// glass passes, download and launch timeouts. The code that uses each reads it from here instead
/// of from a constant, and the reasoning behind each built-in value stays as a comment where it
/// is used.
///
/// <para><b>Overrides only.</b> What is stored (one JSON object under <c>LocalSettings</c>) is the
/// values that differ from their default, so a default changed in a later build reaches everyone
/// who never touched it. Every stored value is clamped to its range on load: <c>LocalSettings</c>
/// is editable by hand and an older build may have written something this one no longer accepts.</para>
///
/// <para><b>What is deliberately not here:</b> anything whose wrong value breaks a feature rather
/// than changes a result (file formats, protocol numbers, keys), and any address with no
/// alternative provider (the Vanilla RTX and Extras repositories, bedrock.graphics, the announcement
/// feed) - see CLAUDE.md §2h for why those were exposed once and taken back out. Cooldowns that
/// protect someone else's server have floors.</para>
///
/// <para>Adding one: an entry below, then read <c>Tunables.X.Value</c> where the constant was.
/// The settings panel builds its rows from <see cref="All"/>, in this order.</para>
/// </summary>
public static class Tunables
{
    private const string StoreKey = "AdvancedOverrides";

    public static readonly List<Tunable> All = new();

    private static Tunable Add(string key, string section, string label, string description,
        double value, double min, double max, bool integer = false, bool restart = false)
    {
        var t = new Tunable
        {
            Key = key, Section = section, Label = label, Description = description,
            Default = value, Min = min, Max = max, IsInteger = integer, NeedsRestart = restart, Value = value,
        };
        All.Add(t);
        return t;
    }

    private const string NormalMaps = "RTX Reactor - normal maps";
    private const string Heightmaps = "RTX Reactor - heightmaps";
    private const string Choosing = "RTX Reactor - which secondary PBR";
    private const string Recursion = "RTX Reactor - recursive MER passes";
    private const string WaterGlass = "RTX Reactor - water and glass";
    private const string Network = "Downloads and launching";

    // ── RTX Reactor: normal maps ────────────────────────────────────────────
    public static readonly Tunable NormalStrength = Add("reactor.normal.max_slope", NormalMaps,
        "Normal map strength", "How far a texture's strongest edges lean at normal intensity 1.0. Scales every generated normal map without changing its character.",
        3.6, 0.5, 12);
    public static readonly Tunable NormalCleanExponent = Add("reactor.normal.clean_exponent", NormalMaps,
        "Response curve on clean textures", "Above 1 keeps only real steps on calm textures like planks and bricks; lower lets more fine detail through.",
        2.2, 0.3, 5);
    public static readonly Tunable NormalNoisyExponent = Add("reactor.normal.noisy_exponent", NormalMaps,
        "Response curve on noisy textures", "Below 1 lifts the small differences busy textures like stone are made of. Raise toward 1 if they come out too busy.",
        0.65, 0.3, 5);
    public static readonly Tunable NormalResponseFloor = Add("reactor.normal.response_floor", NormalMaps,
        "Weak relief kept", "How much shallow relief survives the response curve instead of encoding as flat.",
        0.30, 0, 1);
    public static readonly Tunable NormalHeightBlend = Add("reactor.normal.heightmap_blend", NormalMaps,
        "Clustered height share", "How much of the surface comes from the clustered heightmap rather than the texture's own shading.",
        0.75, 0, 1);
    public static readonly Tunable NormalReferencePercentile = Add("reactor.normal.reference_percentile", NormalMaps,
        "Full-strength edge percentile", "Which of a texture's edges count as full strength. Lower makes more of every texture read as strong.",
        0.95, 0.5, 0.999);
    public static readonly Tunable PomContrastReduction = Add("reactor.normal.pom_contrast_reduction", NormalMaps,
        "Parallax depth reduction", "How much the parallax depth in a normal map's blue channel is pulled toward the surface. 0 keeps it at full depth.",
        0.67, 0, 1);

    // ── RTX Reactor: heightmaps ─────────────────────────────────────────────
    public static readonly Tunable HeightMaxLevels = Add("reactor.height.max_levels", Heightmaps,
        "Height levels", "The most distinct elevations a heightmap may have. Fewer reads as clean flat surfaces; more keeps detail and noise.",
        4, 2, 16, integer: true);
    public static readonly Tunable HeightMergeTolerance = Add("reactor.height.merge_tolerance", Heightmaps,
        "Merge levels closer than", "Levels this close together are merged into one.",
        8, 0, 64);
    public static readonly Tunable HeightRangeBandwidth = Add("reactor.height.range_bandwidth", Heightmaps,
        "Clustering bandwidth", "How different two brightnesses can be and still be pulled into the same level.",
        48, 4, 128);
    public static readonly Tunable HeightIterations = Add("reactor.height.iterations", Heightmaps,
        "Clustering passes", "More passes give flatter plateaus at the cost of generation time.",
        5, 1, 20, integer: true);
    public static readonly Tunable HeightSpatialRadius = Add("reactor.height.spatial_radius", Heightmaps,
        "Clustering radius", "How far, in pixels, each pass looks around a pixel. Wider can blur mortar into the bricks beside it.",
        1, 1, 4, integer: true);
    public static readonly Tunable HeightSpatialSigma = Add("reactor.height.spatial_sigma", Heightmaps,
        "Clustering distance falloff", "How quickly a neighbour's influence fades with distance inside that radius.",
        2.5, 0.5, 10);
    public static readonly Tunable HeightTransparencyOverlay = Add("reactor.height.transparency_overlay", Heightmaps,
        "Darkening under transparency", "How strongly transparent areas of a texture are pushed down in its heightmap.",
        0.5, 0, 1);

    // ── RTX Reactor: which secondary PBR ────────────────────────────────────
    public static readonly Tunable AutoHeightmapMaxWidth = Add("reactor.auto_heightmap_max_width", Choosing,
        "Auto: heightmaps up to", "Under Auto, textures this many pixels wide or narrower get a heightmap; wider ones get a normal map.",
        16, 1, 1024, integer: true);
    public static readonly Tunable ExplicitHeightmapMaxWidth = Add("reactor.explicit_heightmap_max_width", Choosing,
        "Heightmap: never above", "Even with Heightmap chosen, wider textures get a normal map - Minecraft RTX's bevels from large heightmaps are too thin to see.",
        32, 1, 1024, integer: true);

    // ── RTX Reactor: recursive passes ───────────────────────────────────────
    public static readonly Tunable RecursiveMaxOpacity = Add("reactor.recursive.max_opacity", Recursion,
        "Strongest blend", "How much of a recursive pass a fully dominant pixel takes, out of 255. 128 is a half-and-half tint.",
        128, 0, 255);
    public static readonly Tunable RecursiveChromaExponent = Add("reactor.recursive.chroma_exponent", Recursion,
        "Grey penalty", "How hard washed-out colours are punished when judging whether a pixel belongs to a pass's colour.",
        3.0, 0.5, 8);
    public static readonly Tunable RecursiveShareExponent = Add("reactor.recursive.share_exponent", Recursion,
        "Shared-hue penalty", "How hard a pixel is punished for sharing its hue with another channel.",
        2.0, 0.5, 8);

    // ── RTX Reactor: water and glass ────────────────────────────────────────
    public static readonly Tunable WaterMinOpacity = Add("reactor.water.min_opacity", WaterGlass,
        "Water: lowest opacity", "Below 129 Minecraft RTX casts shadows underneath the water surface.",
        129, 0, 255, integer: true);
    public static readonly Tunable WaterOpacityCurve = Add("reactor.water.opacity_curve", WaterGlass,
        "Water: reflection curve", "Above 1 gives more of the water sharper reflections; 1 maps brightness to opacity in a straight line.",
        1.75, 0.2, 5);
    public static readonly Tunable WaterWhitePull = Add("reactor.water.white_pull", WaterGlass,
        "Water: brightening", "How far the water's flat grey is pulled toward white. Dark water reads as murky.",
        0.5, 0, 1);
    public static readonly Tunable GlassMinTransmission = Add("reactor.glass.min_transmission", WaterGlass,
        "Glass: least light let through", "How much light the darkest saturated glass is lifted to let through, keeping its hue.",
        0.4, 0, 1);
    public static readonly Tunable GlassChromaBias = Add("reactor.glass.chroma_bias", WaterGlass,
        "Glass: colour sensitivity", "How strongly that lift favours saturated glass over grey glass.",
        0.3, 0, 2);

    // ── Downloads and launching ─────────────────────────────────────────────
    public static readonly Tunable DownloadStallSeconds = Add("network.stall_timeout_seconds", Network,
        "Give up on a stalled download after (s)", "How long a download may go without receiving anything before it is abandoned and retried.",
        60, 10, 600, integer: true);
    public static readonly Tunable PackIndexCooldownMinutes = Add("updates.index_cooldown_minutes", Network,
        "Re-check pack releases after (min)", "How long Get latest RTX packs reuses what it last learned about the latest releases. Installing always checks.",
        10, 5, 1440, integer: true, restart: true);
    public static readonly Tunable LaunchCloseTimeoutSeconds = Add("launch.close_timeout_seconds", Network,
        "Wait for the game to close (s)", "With Try to close the game turned on, how long a launch waits for the old game to exit before going ahead.",
        5, 1, 60, integer: true);

    static Tunables() => Load();

    private static void Load()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[StoreKey] is not string json) return;
            if (MinecraftJson.ParseObject(json) is not { } stored) return;

            foreach (var t in All)
            {
                if (MinecraftJson.TryGetDouble(stored[t.Key], out var v))
                    t.Value = t.Clamp(v);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Tunables] Couldn't read overrides: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var obj = new JsonObject();
            foreach (var t in All.Where(t => t.IsModified))
                obj[t.Key] = (JsonNode?)JsonValue.Create(t.Value);

            var values = ApplicationData.Current.LocalSettings.Values;
            if (obj.Count == 0) values.Remove(StoreKey);
            else values[StoreKey] = obj.ToJsonString();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Tunables] Couldn't save overrides: {ex.Message}");
        }
    }

    public static void Set(Tunable t, double value)
    {
        t.Value = t.Clamp(value);
        Save();
    }

    public static void Reset(Tunable t) => Set(t, t.Default);

    public static void ResetAll()
    {
        foreach (var t in All) t.Value = t.Default;
        Save();
    }

    public static string Format(Tunable t, double v)
        => t.IsInteger ? ((int)v).ToString(CultureInfo.CurrentCulture) : v.ToString("0.###", CultureInfo.CurrentCulture);
}
