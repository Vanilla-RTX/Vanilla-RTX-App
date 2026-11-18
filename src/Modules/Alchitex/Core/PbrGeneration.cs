using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using Vanilla_RTX_App.Modules.Json;
using Tunables = Vanilla_RTX_App.Core.Tunables;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

#region Texture Set Orchestration (Phase 2a - write .texture_set.json, then discover what needs generating)

public sealed class TextureSetOrchestratorOptions
{
    public static readonly string[] CandidateExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// The same file name under every supported extension, the given path's own included.
    /// TextureSetHelper only ever hands back the one file the game would actually load
    /// (.tga > .png > .jpg > .jpeg), so anything reasoning about "this texture" rather than
    /// "this file" has to widen a resolved path back out to all four. The three that don't
    /// exist are the normal case and cost nothing to name.
    /// </summary>
    public static IEnumerable<string> ExtensionVariants(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);

        foreach (var extension in CandidateExtensions)
            yield return Path.Combine(folder, nameNoExt + extension);
    }
}

/// <summary>
/// One texture set that needs (or might need) generated PBR files, resolved directly from
/// its .texture_set.json's own text content rather than through TextureSetHelper.
/// ResolveTextureSets. That distinction matters: TextureSetHelper's Mer/NormalOrHeight
/// resolution is file-existence-gated (it was built for Tuner, which only ever touches
/// texture sets whose files already fully exist) - it returns null for a layer whose
/// referenced file doesn't exist on disk *yet*, which is the normal state of every
/// texture set the moment TextureSetOrchestrator just wrote it. Relying on that here
/// silently produced zero generation targets for every fresh texture set. This type and
/// DiscoverGenerationTargets below read the JSON directly and never gate on file
/// existence for anything except the color texture, which must already be real.
/// </summary>
public sealed record GenerationTarget(
    string ColorPath,
    string MersPath,
    string? SecondaryPath,
    bool IsHeightmap,
    string TextureName);

/// <summary>
/// Phase 2a of the Alchitex pipeline. Walks every textures/blocks folder in the pack
/// (root + subpacks - see AlchitexStaging.DiscoverBlocksFolders), figures out which color
/// textures are NOT already claimed by an existing .texture_set.json (via
/// TextureSetHelper - safe to use for this exclusion check specifically, since a null
/// Mer/NormalOrHeight here only means "don't count this as already covered", never a
/// false exclusion), and writes a fresh .texture_set.json for each remaining one.
///
/// Every texture set Alchitex writes always uses metalness_emissive_roughness_subsurface -
/// never plain metalness_emissive_roughness - and always gets real SSS data, see
/// MersGenerator below - except for textures matching pbr_blacklist.json (Configuration.
/// PbrBlacklist), which still get a texture set, just a color-only one with no PBR keys.
///
/// Running this twice on an already-processed pack is a no-op for every texture that
/// already got a texture set on the first run - which is exactly the "safe to re-run
/// Alchitex on a pack" behavior we want.
/// </summary>
public static class TextureSetOrchestrator
{
    public readonly record struct Result(int Created, int SkippedAlreadyCovered, int SkippedJunk, int Failed);

    /// <param name="materials">Consulted only for the per-texture heightmap/normal "skip"
    /// flags. Everything else in a material is a question about pixels, which is phase 2b's
    /// business - this phase only decides which layers a texture set claims, and skip is the
    /// one material setting that changes that answer.</param>
    public static Result GenerateMissingTextureSets(
        string packRoot,
        AlchitexOptions options,
        PbrBlacklist blacklist,
        MaterialsConfig materials)
    {
        var created = 0;
        var skippedJunk = 0;
        var failed = 0;

        var blocksFolders = AlchitexStaging.DiscoverBlocksFolders(packRoot);
        if (blocksFolders.Count == 0)
        {
            Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: no textures/blocks folder found anywhere under '{packRoot}'.");
            return new Result(0, 0, 0, 0);
        }

        var candidates = new List<string>();
        foreach (var blocksFolder in blocksFolders)
        {
            foreach (var ext in TextureSetOrchestratorOptions.CandidateExtensions)
            {
                candidates.AddRange(Directory.GetFiles(blocksFolder, "*" + ext, SearchOption.AllDirectories));
            }
        }

        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: no candidate textures found under '{packRoot}'.");
            return new Result(0, 0, 0, 0);
        }

        // ── What an existing texture set already owns ────────────────────────
        //
        // Ownership is the ONLY thing that excludes a texture from generation, and it is
        // deliberately not helped along by a name check. Plenty of ordinary *color* textures
        // end in _normal or _heightmap - sandstone_normal, red_sandstone_normal,
        // rail_normal_turned - where "normal" means the plain variant of a block, not a
        // normal map. A suffix filter used to sit here as a second line of defence and was
        // simply wrong twice over: it made those textures invisible to the entire module (no
        // PBR, and not even the color-only texture set a blacklisted name still gets), and it
        // did the same to a genuinely orphaned foo_heightmap.tga that no texture set claims -
        // which is exactly the file this pass exists to pick up.
        //
        // Widened to every extension because TextureSetHelper resolves only the one file the
        // game would load: a foo_mer.png sitting beside the foo_mer.tga a set resolved is the
        // same texture and has to be skipped with it, or it comes back as a color texture on
        // the next line. Same widening PbrStripper applies when it deletes.
        var alreadyCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every color texture in the pack, so a generated file can't be named over one below.
        var colorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Both sides of this comparison go through GetFullPath. They happen to agree today
        // (each is built up from the same packRoot string), but this set is now the only
        // thing standing between generation and a pack's existing PBR, and a texture set is
        // free to name its layer with a "/" in it - which Path.Combine leaves as a mixed
        // separator that would never match a Directory.GetFiles result.
        void CoverAllVariants(string? path)
        {
            if (path == null) return;
            foreach (var variant in TextureSetOrchestratorOptions.ExtensionVariants(path))
                alreadyCovered.Add(Path.GetFullPath(variant));
        }

        foreach (var resolved in TextureSetHelper.ResolveTextureSets(packRoot))
        {
            CoverAllVariants(resolved.Color.FilePath);
            CoverAllVariants(resolved.Mer?.FilePath);
            CoverAllVariants(resolved.NormalOrHeight?.FilePath);

            if (resolved.Color.FilePath is { } ownedColor)
                colorNames.Add(Path.GetFileNameWithoutExtension(ownedColor));
        }

        // Ownership is the only thing that removes a texture from this list. Exclusion by
        // name - specific textures and wildcards alike - belongs to pbr_blacklist.json, and
        // a blacklisted texture is still a color texture: it gets a color-only set below
        // rather than disappearing. A hardcoded list of names used to run ahead of the
        // blacklist here, which made its water_still, water_flow, bubble_* and *_placeholder
        // entries unreachable and gave those textures no texture set at all.
        var colorTextures = candidates
            .Where(path => !alreadyCovered.Contains(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skippedCovered = candidates.Count - colorTextures.Count;

        foreach (var colorPath in colorTextures)
            colorNames.Add(Path.GetFileNameWithoutExtension(colorPath));

        foreach (var colorPath in colorTextures)
        {
            try
            {
                var directory = Path.GetDirectoryName(colorPath)!;
                var nameNoExt = Path.GetFileNameWithoutExtension(colorPath);
                var jsonPath = Path.Combine(directory, nameNoExt + ".texture_set.json");

                if (File.Exists(jsonPath))
                {
                    // Exists but TextureSetHelper couldn't resolve it (malformed, or
                    // references a color layer under a different name) - don't clobber a
                    // hand-authored/broken file blindly.
                    Trace.WriteLine($"[ALCHITEX] Skipping '{colorPath}': a .texture_set.json already exists at '{jsonPath}' but wasn't resolvable - leaving it alone.");
                    skippedJunk++;
                    continue;
                }

                var set = new JsonObject { ["color"] = nameNoExt };

                if (blacklist.IsBlacklisted(nameNoExt))
                {
                    // pbr_blacklist.json match - color-only texture set, no PBR keys at all.
                    Trace.WriteLine($"[ALCHITEX] '{nameNoExt}' matches pbr_blacklist.json - writing a color-only texture set, no PBR.");
                }
                else
                {
                    // Generated files are always .tga, which outranks every other extension
                    // (§4.4) - so an output named after a real color texture doesn't land
                    // beside it, it hides it, and that block renders as somebody's normal
                    // map. Art wins: the colliding layer is dropped and the rest of the set
                    // stands. Only reachable when a pack ships both "foo" and "foo_normal"
                    // as artwork, which is rare and silent enough to be worth the check.
                    string? Claim(string suffix)
                    {
                        var layerName = nameNoExt + suffix;
                        if (!colorNames.Contains(layerName)) return layerName;

                        Trace.WriteLine($"[ALCHITEX] '{nameNoExt}': '{layerName}' is a color texture in this pack - dropping that layer rather than overwriting it.");
                        return null;
                    }

                    if (Claim("_mers") is { } mersName)
                        set["metalness_emissive_roughness_subsurface"] = mersName;

                    var secondaryMode = ResolveSecondaryMode(options.SecondaryPbr, colorPath);

                    // "skip" means this texture behaves as though Secondary PBR were None,
                    // for this texture alone: no layer in the set, so phase 2b never sees a
                    // target and the block stays flat. Deliberately NOT a fallback to the
                    // other kind - an artist skipping a normal map is saying this block wants
                    // no derived relief, not that it wants the other sort. Lava is the case:
                    // any height read off it is invented.
                    var material = materials.Resolve(nameNoExt);

                    switch (secondaryMode)
                    {
                        case SecondaryPbrMode.Normal when material.Normal.Skip:
                        case SecondaryPbrMode.Heightmap when material.Heightmap.Skip:
                            Trace.WriteLine($"[ALCHITEX] '{nameNoExt}': materials.json skips its {secondaryMode.ToString().ToLowerInvariant()} - writing the texture set without a secondary layer.");
                            break;

                        case SecondaryPbrMode.Normal:
                            if (Claim("_normal") is { } normalName) set["normal"] = normalName;
                            break;
                        case SecondaryPbrMode.Heightmap:
                            if (Claim("_heightmap") is { } heightmapName) set["heightmap"] = heightmapName;
                            break;
                        case SecondaryPbrMode.None:
                        case SecondaryPbrMode.Auto: // Auto never reaches here unresolved - see ResolveSecondaryMode
                            break;
                    }
                }

                var root = new JsonObject
                {
                    ["format_version"] = "1.21.30",
                    ["minecraft:texture_set"] = set,
                };

                MinecraftJson.WriteIndented(jsonPath, root);
                created++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Failed to create texture set for '{colorPath}': {ex.Message}");
                failed++;
            }
        }

        Trace.WriteLine($"[ALCHITEX] TextureSetOrchestrator: created {created}, already-covered {skippedCovered}, skipped-junk {skippedJunk}, failed {failed}.");
        return new Result(created, skippedCovered, skippedJunk, failed);
    }

    /// <summary>
    /// Resolves Auto mode per-texture by probing just the image header (MagickImageInfo -
    /// no full decode) for width, per the agreed rule: width &lt;= 32 -> heightmap,
    /// otherwise -> normal map. Also overrides an *explicit* Heightmap request above
    /// AlchitexOptions.ExplicitHeightmapMaxWidth - the game manifests a heightmap texture
    /// set in-game via its own internal Sobel-derived bump effect (unrelated to our own
    /// generated normal maps), which thins out and stops reading as height at higher
    /// resolutions, so a normal map is generated instead.
    /// </summary>
    private static SecondaryPbrMode ResolveSecondaryMode(SecondaryPbrMode requested, string colorPath)
    {
        if (requested == SecondaryPbrMode.None || requested == SecondaryPbrMode.Normal)
            return requested;

        int width;
        try
        {
            width = (int)new MagickImageInfo(colorPath).Width;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't read dimensions for '{colorPath}' ({ex.Message}), defaulting to normal map.");
            return SecondaryPbrMode.Normal;
        }

        if (requested == SecondaryPbrMode.Auto)
        {
            return width <= AlchitexOptions.AutoModeHeightmapMaxWidth
                ? SecondaryPbrMode.Heightmap
                : SecondaryPbrMode.Normal;
        }

        // requested == Heightmap (explicit).
        if (width > AlchitexOptions.ExplicitHeightmapMaxWidth)
        {
            Trace.WriteLine($"[ALCHITEX] '{colorPath}' is {width}px wide - too large for a heightmap to manifest itself well in Minecraft RTX (>{AlchitexOptions.ExplicitHeightmapMaxWidth}px). Generating a normal map instead of the explicitly-requested heightmap.");
            return SecondaryPbrMode.Normal;
        }

        return SecondaryPbrMode.Heightmap;
    }

    /// <summary>
    /// Phase 2b's starting point. Scans every .texture_set.json under the pack directly
    /// (System.Text.Json, not TextureSetHelper) and resolves each one's *intended*
    /// MERS/normal/heightmap file paths regardless of whether those files exist yet -
    /// that's the whole point of this method existing separately from TextureSetHelper's
    /// resolution. PBR outputs are always .tga (the app's standing convention), so target
    /// paths are built directly rather than probed by extension.
    /// </summary>
    public static IReadOnlyList<GenerationTarget> DiscoverGenerationTargets(string packRoot)
    {
        var targets = new List<GenerationTarget>();

        foreach (var jsonPath in Directory.GetFiles(packRoot, "*.texture_set.json", SearchOption.AllDirectories))
        {
            try
            {
                // Through MinecraftJson, not a bare JsonNode.Parse: these files come from
                // third-party packs, so comments and trailing commas turn up in them, and a
                // duplicate key makes plain JsonNode.Parse succeed and then throw later at
                // the first enumeration. Either way the set was silently skipped and that
                // texture came out with no PBR at all.
                var set = MinecraftJson.ParseObjectFile(jsonPath)?["minecraft:texture_set"] as JsonObject;
                if (set == null) continue;

                var colorName = MinecraftJson.GetString(set["color"]);
                if (string.IsNullOrEmpty(colorName)) continue;

                var folder = Path.GetDirectoryName(jsonPath)!;
                var colorPath = TextureSetHelper.FindTextureFile(folder, colorName);
                if (colorPath == null) continue; // color texture itself missing - nothing to do

                var mersName = MinecraftJson.GetString(set["metalness_emissive_roughness_subsurface"]);
                if (string.IsNullOrEmpty(mersName))
                {
                    // Not one of ours (or hand-authored as plain MER) - Alchitex only
                    // ever generates MERS, so a set without that key is out of scope.
                    continue;
                }

                var mersPath = Path.Combine(folder, mersName + ".tga");

                string? secondaryPath = null;
                var isHeightmap = false;

                var normalName = MinecraftJson.GetString(set["normal"]);
                var heightmapName = MinecraftJson.GetString(set["heightmap"]);

                if (!string.IsNullOrEmpty(normalName))
                {
                    secondaryPath = Path.Combine(folder, normalName + ".tga");
                }
                else if (!string.IsNullOrEmpty(heightmapName))
                {
                    secondaryPath = Path.Combine(folder, heightmapName + ".tga");
                    isHeightmap = true;
                }

                targets.Add(new GenerationTarget(colorPath, mersPath, secondaryPath, isHeightmap, Path.GetFileNameWithoutExtension(colorPath)));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Couldn't parse '{jsonPath}' while discovering generation targets: {ex.Message}");
            }
        }

        return targets;
    }
}

#endregion

#region Shared Colour Analysis

/// <summary>
/// The single place the "what counts as real colour data" rule lives, and the
/// contrast-maximized greyscale every generator derives from it. MERS, heightmap and
/// normal generation all reduce a colour texture to the same flat-average greyscale and
/// all need the same answer to the same question - which pixels are allowed a *vote* in
/// what that texture's value range actually is - so it lives here rather than being
/// re-derived (and drifting) in three places.
/// </summary>
public static class ColorField
{
    /// <summary>
    /// A pixel counts toward a texture's value domain if it's not fully transparent
    /// (opacity &gt;= 1), or - for the fully-transparent case - if its underlying colour
    /// isn't one of the two conventional "background padding" fills (pure black or pure
    /// white at alpha 0). Texture authors routinely leave real colour data under a
    /// collapsed alpha channel too (e.g. grass_side's dirt portion, transparent so the
    /// game skips tinting it, but still real colour) - that data should still count.
    ///
    /// Excluded pixels are only denied a vote in the domain; they still get a real output
    /// value from whatever stretch that domain feeds, clamped to the nearest extreme.
    /// </summary>
    public static bool IsRealColorData(Color c)
    {
        if (c.A >= 1) return true;

        var isPureBlack = c.R == 0 && c.G == 0 && c.B == 0;
        var isPureWhite = c.R == 255 && c.G == 255 && c.B == 255;
        return !(isPureBlack || isPureWhite);
    }

    /// <summary>
    /// Flat-average greyscale ((R+G+B)/3, deliberately not luminosity-weighted - the same
    /// "value" convention across every generator) for every pixel, plus the min/max of
    /// that greyscale taken over real-colour pixels only. Callers that stretch into their
    /// own arbitrary target ranges (MersGenerator) want this raw form; callers that just
    /// want the texture normalized to full range want BuildContrastMaximized below.
    /// </summary>
    public static (int[,] Grey, int Min, int Max) ComputeGreyField(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var grey = new int[w, h];

        int min = 255, max = 0;
        var sawRealPixel = false;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];
                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;

                    if (!ColorField.IsRealColorData(c)) continue;

                    sawRealPixel = true;
                    if (g < min) min = g;
                    if (g > max) max = g;
                }
            }
        }

        // Nothing real anywhere (a fully blank/padding-only texture) - fall back to the
        // full range so the stretch degenerates to identity instead of dividing by zero.
        if (!sawRealPixel) { min = 0; max = 255; }

        return (grey, min, max);
    }

    /// <summary>
    /// The contrast-maximized greyscale: flat-average grey stretched so the darkest real
    /// pixel lands at 0.0 and the brightest at 1.0, everything between interpolated.
    /// Pixels outside the real-colour domain still get a value, clamped to whichever end
    /// they fall past.
    ///
    /// Note this is a *full* min-to-max stretch, distinct from the ceiling-maximize used
    /// for POM data (ApplyPomBlueChannel), which deliberately only scales up so the
    /// brightest pixel hits the top while leaving the dark end where it sits.
    /// </summary>
    public static float[,] BuildContrastMaximized(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var (grey, min, max) = ComputeGreyField(colorBitmap);

        var result = new float[w, h];
        var range = max - min;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var t = range > 0 ? (grey[x, y] - min) / (double)range : 0.5;
                result[x, y] = (float)Math.Clamp(t, 0.0, 1.0);
            }
        }

        return result;
    }
}

#endregion

#region MERS Generation

/// <summary>
/// Generates a MERS (metalness/emissive/roughness/subsurface) bitmap from a color texture
/// and a resolved MaterialEntry. Alchitex only ever produces MERS, never plain MER - see
/// TextureSetOrchestrator above - and always writes each block's own materials.json SSS
/// range; there's no run-time toggle to suppress it (same reasoning as POM: a shader
/// choosing not to read the data is downstream of us, not something to gate here).
///
/// Base algorithm:
///   1. Greyscale the color texture with a flat channel average (NOT luminosity - a
///      literal (R+G+B)/3 per pixel). This single "value" shape is the common source for
///      all three MER channels.
///   2. Stretch that grey value's own min/max across the image into each output channel's
///      configured target range independently (metal, emissive, roughness), each with its
///      own invert flag.
///   3. Layer on any recursive/advanced passes.
///   4. Compute the color texture's real luminosity (unaltered - standard perceptual
///      weighting, not the flat average from step 1), stretch it into the effective SSS
///      min/max range, and write that as the output alpha.
///
/// The min/max *domain* used for every stretch above (steps 2 and 4, and the recursive
/// pass's own sub-stretch) only ever considers "real" pixels - see ColorField.IsRealColorData -
/// so background junk (typically a fully-black or fully-white fill left under a fully
/// collapsed alpha channel, the common way texture pack authors pad cutout regions) can't
/// artificially widen the domain and flatten the contrast of the actual visible content.
/// Excluded pixels still get a real output value (via the same Stretch call, using the
/// domain computed without them - Stretch already clamps out-of-range input to the
/// nearest extreme), they just don't get a *vote* in what that domain is.
/// </summary>
public static class MersGenerator
{
    // ── Recursive-pass dominance tuning ──────────────────────────────────────────────
    //
    // TODO(tuning): the ceiling on how much of a recursive pass ever replaces the base
    // MERS at a single pixel. 128/255 means even a perfectly dominant pixel is a half-and-
    // half blend, never a full overwrite - the pass is meant to tint a region, not to cut
    // a hole in the base material and drop a different one in.
    private static double MaxRecursiveOpacity => Tunables.RecursiveMaxOpacity.Value;

    // TODO(tuning): how hard desaturation is punished. Chroma (the gap between the target
    // channel and the weakest one, so "how much of this pixel isn't grey") is raised to
    // this power before it scales the score. This exponent is what separates a flame
    // orange (237,135,10) from a dusty rose (237,135,128): both lead red by the same 102,
    // but the rose is over 40% grey and the flame is barely 4%. At 1.0 they score within
    // 2x of each other; at 3.0 they're 7x apart, which is what "the rose is not a flame"
    // has to mean numerically.
    private static double ChromaExponent => Tunables.RecursiveChromaExponent.Value;

    // TODO(tuning): how hard a pixel is punished for sharing its hue with a rival channel.
    // Share is 1.0 when the target owns the hue outright and 0.5 at a dead tie, so squaring
    // it turns a tie into quarter credit rather than half. Measured against real cases: at
    // 1.0 a barely-leading colour (end stone, whose green beats red by 2) scored as high as
    // a solidly green one, because its larger chroma cancelled its worse share out. Raising
    // this is what restores the ordering, and 2.0 is as far as it can go before a genuine
    // tie - a yellow flame asked about red - stops registering at all.
    private static double ShareExponent => Tunables.RecursiveShareExponent.Value;

    // TODO(tuning): the raw score at which a texture is considered to genuinely contain a
    // strongly-dominant example of the channel. Pure red scores 1.0; a real flame orange
    // lands around 0.5; the desaturated greens on an end portal frame score under 0.01.
    private const double StrongDominanceReference = 0.35;

    // TODO(tuning): the two ends of the falloff curve, chosen by whether a strong example
    // exists (see ResolveFalloffExponent).
    private const double WeakDominanceExponent = 0.90;
    private const double StrongDominanceExponent = 1.35;

    // TODO(tuning): what a neutral bright pixel gets, as a fraction of the strongest
    // dominance in the same texture. A white-hot flame core has no dominant channel at
    // all - white leads nothing - but excluding it would punch an unlit hole through the
    // middle of a lit region. Expressed relative to the texture's own maximum rather than
    // as a constant so it means the same thing on a faint texture and a vivid one.
    private const double NeutralBrightShare = 0.5;

    // Every channel at or above this, with no meaningful chroma, counts as neutral bright.
    private const int NeutralBrightMinChannel = 250;

    // A pixel has to be masked at least this much (0-255) before it gets a vote in the
    // pass's own contrast domain. Without a floor, the thousands of pixels sitting at an
    // opacity of 1 or 2 - which are visually not part of the region at all - would widen
    // the domain and flatten the contrast of the pixels that actually are.
    private const int DomainVoteMinOpacity = 8;

    public static Bitmap Generate(Bitmap colorBitmap, ResolvedMaterial material)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var grey = new int[w, h];
        var luminosity = new int[w, h];

        int greyMin = 255, greyMax = 0;
        int lumMin = 255, lumMax = 0;

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];

                    var g = (c.R + c.G + c.B) / 3;
                    grey[x, y] = g;

                    var l = (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                    luminosity[x, y] = l;

                    if (!ColorField.IsRealColorData(c)) continue;

                    if (g < greyMin) greyMin = g;
                    if (g > greyMax) greyMax = g;
                    if (l < lumMin) lumMin = l;
                    if (l > lumMax) lumMax = l;
                }
            }
        }

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var g = grey[x, y];
                    var r = Stretch(g, greyMin, greyMax, material.Mer.MetalMin, material.Mer.MetalMax, material.Mer.InvertMetal);
                    var gr = Stretch(g, greyMin, greyMax, material.Mer.EmissiveMin, material.Mer.EmissiveMax, material.Mer.InvertEmissive);
                    var b = Stretch(g, greyMin, greyMax, material.Mer.RoughnessMin, material.Mer.RoughnessMax, material.Mer.InvertRoughness);
                    var alpha = Stretch(luminosity[x, y], lumMin, lumMax, material.Sss.Min, material.Sss.Max, material.Sss.Invert);

                    outFb[x, y] = Color.FromArgb(alpha, r, gr, b);
                }
            }

            foreach (var pass in material.Recursive)
            {
                ApplyRecursivePass(colorBitmap, grey, outFb, pass);
            }
        }

        return output;
    }

    /// <summary>
    /// One recursive pass: find where this pass's channel dominates the color texture,
    /// generate an independent MER over just those pixels, and blend it back over the base
    /// MERS weighted by how strongly each pixel actually dominates.
    ///
    /// The whole difficulty is the weighting, because the question it answers is genuinely
    /// relative. On a furnace front a pixel of (237,135,10) is flame and must come through
    /// strongly; on some other texture that identical pixel might be the most red-ish thing
    /// present and still not be a flame at all. So the mask is built in two stages: a raw,
    /// absolute dominance score per pixel, then a normalization against the strongest score
    /// this texture itself contains. Nothing is judged against a fixed threshold - a texture
    /// is always measured against its own best example.
    ///
    /// See RawDominance for the score and ResolveFalloffExponent for the part that decides
    /// how harshly the weak examples are punished for not being the strong one.
    ///
    /// A pass never touches alpha. Subsurface is computed once, from the whole texture, in
    /// Generate - it's the final channel of the finished MERS, not something a region of it
    /// gets its own version of.
    /// </summary>
    private static void ApplyRecursivePass(
        Bitmap colorBitmap,
        int[,] grey,
        FastBitmap outFb,
        ResolvedRecursivePass pass)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        var raw = new double[w, h];
        var neutralBright = new bool[w, h];
        var dominanceMax = 0.0;

        // ── Stage 1: raw scores, and the best one in this texture ────────────
        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = colorFb[x, y];

                    // Padding under a collapsed alpha channel isn't part of the artwork and
                    // must not light up - the same rule the base pass uses for its domain,
                    // see ColorField.IsRealColorData.
                    if (!ColorField.IsRealColorData(c)) continue;

                    if (IsNeutralBright(c))
                    {
                        neutralBright[x, y] = true;
                        continue;
                    }

                    var d = RawDominance(c, pass.Channel);
                    raw[x, y] = d;

                    if (d > dominanceMax) dominanceMax = d;
                }
            }
        }

        // Nothing in this texture leads on this channel at all - the pass has no region to
        // work on and leaves the base MERS exactly as it is.
        if (dominanceMax <= 0) return;

        var exponent = ResolveFalloffExponent(dominanceMax);
        var neutralScore = dominanceMax * NeutralBrightShare;

        // ── Stage 2: normalize into opacity, and collect the pass's own domain ──
        var mask = new int[w, h];
        int subGreyMin = 255, subGreyMax = 0;
        var anyMasked = false;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var score = neutralBright[x, y] ? neutralScore : raw[x, y];
                if (score <= 0) continue;

                var opacity = (int)Math.Round(
                    Math.Pow(score / dominanceMax, exponent) * MaxRecursiveOpacity);

                if (opacity <= 0) continue;

                mask[x, y] = opacity;
                anyMasked = true;

                if (opacity < DomainVoteMinOpacity) continue;

                var g = grey[x, y];
                if (g < subGreyMin) subGreyMin = g;
                if (g > subGreyMax) subGreyMax = g;
            }
        }

        if (!anyMasked) return;

        // Every masked pixel scored below the voting floor - a texture whose channel barely
        // leads anywhere. Let them all vote rather than skip the pass: a flat domain is a
        // better answer than no pass at all.
        if (subGreyMax < subGreyMin)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (mask[x, y] <= 0) continue;

                    var g = grey[x, y];
                    if (g < subGreyMin) subGreyMin = g;
                    if (g > subGreyMax) subGreyMax = g;
                }
            }
        }

        // ── Stage 3: the pass's own MER, blended in by mask opacity ──────────
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var weight = mask[x, y];
                if (weight <= 0) continue;

                var t = weight / 255.0;
                var g = grey[x, y];

                var passR = Stretch(g, subGreyMin, subGreyMax, pass.Mer.MetalMin, pass.Mer.MetalMax, pass.Mer.InvertMetal);
                var passG = Stretch(g, subGreyMin, subGreyMax, pass.Mer.EmissiveMin, pass.Mer.EmissiveMax, pass.Mer.InvertEmissive);
                var passB = Stretch(g, subGreyMin, subGreyMax, pass.Mer.RoughnessMin, pass.Mer.RoughnessMax, pass.Mer.InvertRoughness);

                var baseColor = outFb[x, y];

                outFb[x, y] = Color.FromArgb(
                    baseColor.A, // subsurface belongs to the whole texture, not to a pass
                    Lerp(baseColor.R, passR, t),
                    Lerp(baseColor.G, passG, t),
                    Lerp(baseColor.B, passB, t));
            }
        }
    }

    /// <summary>
    /// How much one pixel "is" its target channel, as an absolute 0-1 score. Two factors,
    /// multiplied, because a pixel needs both and either one alone is misleading:
    ///
    ///   chroma^ChromaExponent - how far the pixel is from grey, measured as the target
    ///     channel's lead over the WEAKEST channel. This is the dilution term, and it is
    ///     the only thing separating a dusty rose (237,135,128) from a flame orange
    ///     (237,135,10): both lead red by exactly 102, but the rose is half grey and the
    ///     flame is barely a tenth. Raised to a power because a linear penalty leaves
    ///     washed-out colors far too strong - see ChromaExponent.
    ///
    ///   share - of the color that ISN'T grey, how much belongs to the target channel
    ///     rather than to its nearest rival. 1.0 when the target owns the hue outright
    ///     (255,0,0); 0.5 at a dead tie (yellow asked about red is half red, and half
    ///     credit is exactly right). A ratio rather than a difference so it stays
    ///     continuous through the tie - a pixel leading by one scores barely more than one
    ///     that ties, instead of jumping.
    ///
    /// A pixel whose target channel isn't the highest scores zero and isn't in the region
    /// at all. Dominance means leading, and second place isn't leading.
    /// </summary>
    private static double RawDominance(Color c, string channel)
    {
        int target, rival, weakest;

        switch (channel)
        {
            case "G":
                target = c.G;
                rival = Math.Max(c.R, c.B);
                weakest = Math.Min(c.R, c.B);
                break;
            case "B":
                target = c.B;
                rival = Math.Max(c.R, c.G);
                weakest = Math.Min(c.R, c.G);
                break;
            default: // "R"
                target = c.R;
                rival = Math.Max(c.G, c.B);
                weakest = Math.Min(c.G, c.B);
                break;
        }

        if (target < rival) return 0;

        var targetChroma = target - weakest;
        if (targetChroma <= 0) return 0; // fully neutral: nothing leads anything

        var rivalChroma = rival - weakest;

        var chroma = targetChroma / 255.0;
        var share = targetChroma / (double)(targetChroma + rivalChroma);

        return Math.Pow(chroma, ChromaExponent) * Math.Pow(share, ShareExponent);
    }

    /// <summary>
    /// The curve applied to each pixel's score once it has been divided by the texture's
    /// best score - the part that makes this adaptive instead of a fixed threshold.
    ///
    /// Above 1.0 the curve is convex: the gap between the best example and everything else
    /// widens and weak candidates fade out. Below 1.0 it's concave, and weak candidates get
    /// lifted instead.
    ///
    /// Which applies depends on whether the texture actually contains a strong example of
    /// the channel. A furnace front does - the flames - so the merely-reddish stone around
    /// them should fall away, and the exponent goes convex. An end portal frame does not:
    /// its greens are all desaturated, they are the best it has, and punishing them for not
    /// being a pure green would mean generating nothing for the one region the pass was
    /// written for. There it goes concave and they come through.
    ///
    /// "Weak examples only matter when there are no strong ones" is the whole idea, and it
    /// can only be expressed relative to the texture in hand.
    /// </summary>
    private static double ResolveFalloffExponent(double dominanceMax)
    {
        var strength = Math.Clamp(dominanceMax / StrongDominanceReference, 0.0, 1.0);
        return WeakDominanceExponent + strength * (StrongDominanceExponent - WeakDominanceExponent);
    }

    /// <summary>A near-white pixel: no channel can lead, but it is far more likely to be
    /// the hot core of whatever the pass targets than something to leave unlit.</summary>
    private static bool IsNeutralBright(Color c)
        => c.R >= NeutralBrightMinChannel && c.G >= NeutralBrightMinChannel && c.B >= NeutralBrightMinChannel;

    private static byte Lerp(byte a, int b, double t)
        => (byte)Math.Clamp((int)Math.Round(a * (1.0 - t) + b * t), 0, 255);

    /// <summary>
    /// Stretches `value` from the [oldMin, oldMax] range found in the source data into
    /// [targetMin, targetMax], optionally reversed.
    /// </summary>
    private static byte Stretch(int value, int oldMin, int oldMax, int targetMin, int targetMax, bool invert)
    {
        double t = oldMax > oldMin ? (value - oldMin) / (double)(oldMax - oldMin) : 0.5;
        t = Math.Clamp(t, 0.0, 1.0);
        if (invert) t = 1.0 - t;

        var result = targetMin + t * (targetMax - targetMin);
        return (byte)Math.Clamp((int)Math.Round(result), 0, 255);
    }
}

/// <summary>
/// Invisible emission: light coming out of pixels the player can't see.
///
/// Minecraft RTX reads emitted light's COLOUR from the colour texture and its STRENGTH from
/// the MERS green channel, per pixel, without caring whether the pixel is visible. A fully
/// transparent pixel that still carries RGB and emissive data therefore glows while drawing
/// nothing at all. Vanilla RTX leans on this constantly - monster spawners, torches, trial
/// spawners: anything whose lit interior is a hole in the artwork rather than painted
/// surface. See InvisibleEmissionParams for the schema, and MaterialsBootstrapper for the
/// pass that derives both values back out of a pack already using the trick.
///
/// Both edits have to agree on which pixels they touch, which is the entire reason this is
/// one class and not two passes in two files: colour without strength emits nothing, and
/// strength without colour emits black. The rule is "alpha is exactly 0 in the colour
/// texture", nothing more - a pixel at alpha 1 is faintly visible and is the artist's to
/// paint, not ours to overwrite.
///
/// Runs AFTER the MERS is otherwise finished (base pass, recursive passes, subsurface), and
/// after it, deliberately: it's an override of a specific region rather than another layer
/// to blend, and a recursive pass writing over it would defeat the point.
///
/// It also runs on the colour bitmap only AFTER that bitmap has been used to generate the
/// MERS. Filling those pixels first would feed the emission colour into ColorField's
/// real-colour-data domain (§4.11) - a padded texture whose alpha-0 pixels were pure black
/// and thus excluded would suddenly have them counted, shifting the contrast domain of the
/// whole visible material. The invisible region must not influence the visible one.
/// </summary>
public static class InvisibleEmission
{
    /// <summary>
    /// Writes the emission colour into every alpha-0 pixel of <paramref name="colorBitmap"/>
    /// and the emission strength into the same pixels' green channel in
    /// <paramref name="mersBitmap"/>.
    ///
    /// Returns true if the colour bitmap was actually changed, so the caller knows whether
    /// it needs writing back to disk - a texture with no transparent pixels at all is the
    /// common case and shouldn't cost a file write.
    /// </summary>
    public static bool Apply(Bitmap colorBitmap, Bitmap mersBitmap, ResolvedInvisibleEmission emission)
    {
        if (!emission.IsEnabled) return false;

        var w = Math.Min(colorBitmap.Width, mersBitmap.Width);
        var h = Math.Min(colorBitmap.Height, mersBitmap.Height);

        var changed = false;

        using var colorFb = new FastBitmap(colorBitmap, writable: true);
        using var mersFb = new FastBitmap(mersBitmap, writable: true);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = colorFb[x, y];
                if (c.A != 0) continue;

                // Alpha stays 0 - the pixel must remain invisible. Only its RGB changes,
                // and that RGB is never seen; it exists purely to tint the light.
                colorFb[x, y] = Color.FromArgb(0, emission.R, emission.G, emission.B);

                var m = mersFb[x, y];
                mersFb[x, y] = Color.FromArgb(m.A, m.R, (byte)emission.Strength, m.B);

                changed = true;
            }
        }

        return changed;
    }
}


#endregion

#region Normal Map Generation

/// <summary>
/// Generates a normal map from a color texture, built on top of
/// HeightmapGenerator.ComputeClusteredHeights rather than raw color brightness - the
/// mean-shift clustered heightmap is the shared height-field basis for both texture
/// types now:
///   1. The clustered heightmap (raw, untouched) is blended with a ceiling-maximized flat
///      greyscale of the color texture, weighted by HeightmapBlendRatio (default 75%
///      clustered / 25% color), giving a clean height field that still carries some of
///      the original texture's own shading. The ceiling-maximize here only ever applies
///      to that color-texture greyscale, never to the clustered heightmap itself - a
///      *second*, independent ceiling-maximize of the clustered heightmap happens later,
///      purely for the POM blue channel (step 5) - the two are unrelated uses of the same
///      source array, not the same operation.
///   2. Gradients come from a Sobel operator sampled with wraparound indexing, so a pixel
///      on any edge sees its real neighbor from the opposite edge and the result tiles
///      seamlessly - which is what Bedrock's random rotation of isometric block textures
///      needs, since any two edges can end up meeting each other.
///   3. Those gradient magnitudes are normalized against this texture's *own* strong-edge
///      reference (a high percentile of its non-flat gradients - see
///      ResolveGradientReference), so a low-contrast texture still gets a well-formed
///      normal map and a bold one doesn't saturate into a wall of maxed-out edges. This is
///      what the old version lacked: with an unscaled gradient against a fixed Z of 1, a
///      one-level difference and a full black-to-white edge both landed within a degree
///      of each other, so nothing was ever weighted.
///   4. The normalized magnitude then goes through a response curve (ShapeResponse) whose
///      exponent is driven by the per-texture noise index (GetNoiseIndex). A clean texture
///      with well-defined edges gets an exponent above 1: small differences are suppressed
///      and only genuinely big steps produce strong normals, so planks/bricks/tiles read
///      crisply. A noisy texture gets an exponent below 1, lifting its small differences
///      instead - subtle variation is all such a texture has to work with, and its edges
///      were never well-defined to begin with. The curve is pinned at both ends and
///      carries a linear term (ResponseFloor) so that relief far below the reference edge
///      still encodes as something rather than as nothing, and its exponent is scaled back
///      when the slope budget is too small to express any shaping (ResolveResponseExponent).
///   5. `intensity` (materials.json, default 0.25) scales the resulting slope *before* the
///      normal is built and normalized, so it controls real surface steepness rather than
///      fading an already-encoded normal toward flat. Every output stays a true unit
///      normal at any intensity, and 0 gives a perfectly flat map.
///   6. The blue channel is always overwritten with parallax-occlusion-mapping height
///      data sourced from the *raw* clustered heightmap, separately ceiling-maximized and
///      contrast-reduced (Bedrock RTX reads POM from the normal map's blue channel) - see
///      ApplyPomBlueChannel. This is on for every texture unconditionally; there's no
///      per-block opt-out at generation time. Downstream shader/renderer settings (e.g.
///      BetterRTX) are the right place to let a *player* disable reading POM data - that's
///      not something to gate here.
///
/// Deliberately does no blurring at any stage. Blurring the finished map can't respect the
/// wraparound its gradients were built with - the blur's own edge handling doesn't wrap -
/// so the outermost pixels stop matching the opposite edge. That's invisible on a statically
/// placed tile, but Bedrock randomly rotates isometric block textures, and those mismatched
/// edges then meet each other and show up as seams. The noise index shapes the response
/// curve (step 4) instead, which calms a busy texture without ever touching edge continuity.
/// </summary>
public static class NormalMapGenerator
{
    // TODO(tuning): how much of the height field comes from the mean-shift clustered
    // heightmap vs. a ceiling-maximized flat greyscale of the color texture itself.
    // Higher favors the clean, banded clustered result; lower brings back more of the
    // original texture's own shading detail.
    private static double HeightmapBlendRatio => Tunables.NormalHeightBlend.Value;

    // TODO(tuning): the response curve. Exponent applied to each pixel's normalized
    // gradient magnitude, interpolated by noise index: above 1 crushes small differences
    // and rewards big ones, below 1 lifts small ones. If noisy textures come out too busy,
    // raising NoisyTextureExponent toward 1.0 is the first lever to reach for.
    private static double CleanTextureExponent => Tunables.NormalCleanExponent.Value;  // noise index 0
    private static double NoisyTextureExponent => Tunables.NormalNoisyExponent.Value; // noise index 100

    /// <summary>
    /// TODO(tuning): how much of a linear response is mixed back into the shaped one, at the
    /// full clean exponent - see <see cref="ShapeResponse"/>. The single lever on how much
    /// weak relief survives.
    ///
    /// A bare power curve has a derivative of zero at the origin, so a gradient far enough
    /// below this texture's reference edge encodes to no slope at all rather than to a small
    /// one. That is what puts a hard floor under the map, and it is not a rare case: the
    /// reference is a high percentile, so most of a texture's relief sits well down the
    /// curve. Measured over 464 real textures at the default intensity, a bare curve leaves
    /// 21.1% of every relief-carrying pixel within 4 levels of flat, and the 90th-percentile
    /// texture loses 64% of its relief that way.
    ///
    /// At 0.30: relief erased 21.1% -> 14.6%, the median relief pixel rises from 13.3 to 18.6
    /// levels, the 90th-percentile pixel moves 79.0 -> 79.9 and the 99th does not move at
    /// all. So the structure a texture already showed is left where it was and what comes
    /// back is the detail underneath it.
    /// </summary>
    private static double ResponseFloor => Tunables.NormalResponseFloor.Value;

    /// <summary>
    /// The slope budget at which the response curve applies its full exponent. Below it the
    /// exponent is interpolated toward 1 (linear) in proportion - see
    /// <see cref="ResolveResponseExponent"/>.
    ///
    /// The curve's job is to allocate a texture's relief across the range the map has to
    /// spend, suppressing small differences so that big ones read as steps. That only means
    /// something while there is a range to suppress into. `normal.intensity` sets that range,
    /// and at a tenth of the default it is about 13 encodable levels - spending a 2.2 power
    /// on 13 levels leaves roughly three usable, so a block an artist asked to read as nearly
    /// flat comes back as a handful of maxed-out edges over dead flat, which is not what
    /// "nearly flat" means.
    ///
    /// It is the slope the default intensity produces, and derived from it so the two cannot
    /// drift: this is a no-op at and above the default and only softens the shaping for
    /// blocks deliberately configured weaker than that. Because the backoff reads only the
    /// ratio of slope to this value, scaling <see cref="MaxSlope"/> changes how steep every
    /// map is and nothing about the shape of any texture's curve, at any intensity.
    /// </summary>
    private static double FullShapingSlope => MaxSlope * MaterialDefaults.NormalIntensity;

    // TODO(tuning): what counts as this texture's "full strength" edge - a percentile
    // taken over its non-flat gradients only. Restricting the population that way matters:
    // on a texture that's mostly empty space, including every flat pixel would drag the
    // percentile down to nothing and then normalize the faint remainder up to full.
    private static double GradientReferencePercentile => Tunables.NormalReferencePercentile.Value;
    private const double GradientFlatThreshold = 1.0 / 255.0;
    // Floor for that reference, so a genuinely flat texture's faint noise never gets
    // normalized up into a full-strength normal map.
    private const double MinGradientReference = 0.02;

    // TODO(tuning): slope the shaped gradient reaches at normal.intensity = 1.0. At the
    // 0.25 default that is 0.9, a 42-degree tilt on a texture's strongest edges. It is the
    // one uniform strength knob: FullShapingSlope follows it, so changing it never changes a
    // texture's character, only how far every map leans.
    private static double MaxSlope => Tunables.NormalStrength.Value;

    // TODO(tuning): calibration ceiling for GetNoiseIndex - what average per-pixel
    // brightness delta counts as "maximally noisy" (index 100).
    private const double NoiseCalibrationCeiling = 40.0;

    // Magnitude histogram used to resolve the percentile above without sorting every
    // pixel - fixed cost regardless of texture size (animation strips get enormous).
    private const int GradientHistogramBins = 1024;
    private const double GradientHistogramMax = 2.0;

    /// <summary>
    /// Entry point. Handles the flipbook case and then defers to GenerateFrame, which is
    /// the real generator and knows nothing about strips.
    /// </summary>
    public static Bitmap Generate(
        Bitmap colorBitmap,
        ResolvedNormal normalParams,
        ResolvedHeightmap heightmapParams)
    {
        var frames = ResolveFlipbookFrames(colorBitmap.Width, colorBitmap.Height);

        if (frames < 2)
            return GenerateFrame(colorBitmap, normalParams, heightmapParams);

        try
        {
            return GenerateFlipbook(colorBitmap, normalParams, heightmapParams, frames);
        }
        catch (Exception ex)
        {
            // Cosmetic-grade fallback on purpose: a strip that couldn't be taken apart is
            // still a texture that needs a normal map, and the whole-strip result is what
            // this produced before frames were understood at all. Worse, never broken.
            Trace.WriteLine($"[ALCHITEX] Couldn't generate a normal map frame-by-frame for a {colorBitmap.Width}x{colorBitmap.Height} flipbook ({ex.Message}) - falling back to treating it as one image.");
            return GenerateFrame(colorBitmap, normalParams, heightmapParams);
        }
    }

    /// <summary>
    /// How many square frames a texture is, by Minecraft's own convention: an animated
    /// texture is a vertical strip of frames each as tall as the texture is wide. 1 means
    /// "not a flipbook", which is every ordinary block texture.
    ///
    /// Deliberately just the arithmetic, with no attempt to read the pack's
    /// flipbook_textures.json. That file is authoritative and this is a guess, but the guess
    /// is only ever wrong in one direction that matters - a genuinely tall, non-animated
    /// texture whose height happens to be an exact multiple of its width - and being wrong
    /// there costs a normal map generated per square region instead of across the whole
    /// thing, not a broken pack. Reading the manifest to be sure is exactly the kind of
    /// per-case correctness this module trades away for staying simple.
    /// </summary>
    private static int ResolveFlipbookFrames(int width, int height)
        => width > 0 && height > width && height % width == 0 ? height / width : 1;

    /// <summary>
    /// A flipbook's frames are separate pictures that happen to be stored in one file. Run
    /// whole, every gradient at a frame boundary reads the next frame of the animation as if
    /// it were the ground below - and with Tile padding the top of frame 0 wraps to the
    /// bottom of the last frame as well - so every seam in the strip becomes a hard edge in
    /// the normal map. Cutting the strip up first makes each frame get exactly the treatment
    /// a single texture would, padding included.
    /// </summary>
    private static Bitmap GenerateFlipbook(
        Bitmap colorBitmap,
        ResolvedNormal normalParams,
        ResolvedHeightmap heightmapParams,
        int frames)
    {
        var size = colorBitmap.Width;
        var output = new Bitmap(size, colorBitmap.Height, PixelFormat.Format32bppArgb);

        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var top = frame * size;

                using var source = CopyRegion(colorBitmap, top, size, size);
                using var generated = GenerateFrame(source, normalParams, heightmapParams);

                BlitInto(output, generated, top);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }

        return output;
    }

    /// <summary>One frame out of a strip. Pixels are copied through FastBitmap rather than
    /// GDI+ drawing, which mangles colour under a collapsed alpha channel - and alpha-0
    /// pixels carrying real colour are load-bearing here (ColorField.IsRealColorData).</summary>
    private static Bitmap CopyRegion(Bitmap source, int top, int width, int height)
    {
        var region = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var srcFb = new FastBitmap(source, writable: false))
        using (var dstFb = new FastBitmap(region, writable: true))
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    dstFb[x, y] = srcFb[x, top + y];
        }

        return region;
    }

    private static void BlitInto(Bitmap destination, Bitmap frame, int top)
    {
        using var dstFb = new FastBitmap(destination, writable: true);
        using var srcFb = new FastBitmap(frame, writable: false);

        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                dstFb[x, top + y] = srcFb[x, y];
    }

    private static Bitmap GenerateFrame(
        Bitmap colorBitmap,
        ResolvedNormal normalParams,
        ResolvedHeightmap heightmapParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        var clustered = HeightmapGenerator.ComputeClusteredHeights(colorBitmap);
        var height = BuildHeightField(colorBitmap, clustered);

        var gradX = new float[w, h];
        var gradY = new float[w, h];
        ComputeSobelGradients(height, w, h, normalParams.Padding, gradX, gradY);

        var reference = ResolveGradientReference(gradX, gradY, w, h);

        var noise = Math.Clamp(GetNoiseIndex(colorBitmap) / 100.0, 0.0, 1.0);
        var strength = MaxSlope * Math.Clamp(normalParams.Intensity, 0.0, 1.0);
        var exponent = ResolveResponseExponent(Lerp(CleanTextureExponent, NoisyTextureExponent, noise), strength);

        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    double gx = gradX[x, y];
                    double gy = gradY[x, y];
                    var magnitude = Math.Sqrt(gx * gx + gy * gy);

                    double slopeX = 0, slopeY = 0;
                    if (magnitude > 0)
                    {
                        // Shape the magnitude, keep the direction.
                        var shaped = ShapeResponse(Math.Clamp(magnitude / reference, 0.0, 1.0), exponent) * strength;
                        slopeX = gx / magnitude * shaped;
                        slopeY = gy / magnitude * shaped;
                    }

                    // (-slopeX, -slopeY, 1) is already DirectX convention (the format
                    // Bedrock RTX expects) mapped straight to (R, G, B) - no axis swap or
                    // extra sign flip belongs here. An earlier version transposed X and Y
                    // trying to "fix" this, which was invisible on a symmetric bump's main
                    // diagonal but swapped the other two corners - don't reintroduce it.
                    var normal = Vector3.Normalize(new Vector3((float)-slopeX, (float)-slopeY, 1f));

                    outFb[x, y] = Color.FromArgb(
                        255,
                        EncodeChannel(normal.X),
                        EncodeChannel(normal.Y),
                        EncodeChannel(normal.Z));
                }
            }
        }

        ApplyPomBlueChannel(output, clustered, heightmapParams.Intensity);

        if (normalParams.Invert)
            InvertRedGreenInPlace(output);

        return output;
    }

    private static byte EncodeChannel(float component)
        => (byte)Math.Clamp((int)Math.Round((component + 1f) * 0.5f * 255f), 0, 255);

    /// <summary>
    /// The response curve: a normalized gradient magnitude (0-1 against this texture's own
    /// reference edge) in, the fraction of the slope budget it earns out.
    ///
    /// Monotone, and pinned at both ends - 0 maps to 0 and 1 maps to 1 whatever the mix, so
    /// a texture's strongest edges are decided by the exponent alone and the linear term can
    /// only ever lift what sits between them.
    ///
    /// The mix is scaled by how far the exponent sits ABOVE 1, and that gate is load-bearing
    /// rather than tidy. Above 1 the curve runs below the linear line and mixing lifts it;
    /// below 1 it runs above that line, so the same mix would pull it DOWN - taking relief
    /// away from precisely the noisy textures the exponent is already treating gently.
    /// Gating on the exponent makes this a no-op for every one of them, and gives the
    /// largest correction to the cleanest textures, which are the ones the curve crushes
    /// hardest.
    /// </summary>
    private static double ShapeResponse(double normalized, double exponent)
    {
        var linearMix = ResponseFloor * Math.Clamp(exponent - 1.0, 0.0, 1.0);
        return (1.0 - linearMix) * Math.Pow(normalized, exponent) + linearMix * normalized;
    }

    /// <summary>
    /// The exponent actually used, given how much slope there is to shape into. Full
    /// exponent at or above <see cref="FullShapingSlope"/>, interpolated toward 1 (linear)
    /// in proportion below it.
    ///
    /// Contrast shaping and amplitude are not independent once the result has to survive an
    /// 8-bit encoding: the same curve that separates a plank's seams from its grain across
    /// 90 levels separates nothing across 13. Backing the exponent off as the budget shrinks
    /// keeps a deliberately-weak block's relief proportional instead of quantizing it away.
    /// </summary>
    private static double ResolveResponseExponent(double exponent, double strength)
        => 1.0 + (exponent - 1.0) * Math.Clamp(strength / FullShapingSlope, 0.0, 1.0);

    /// <summary>
    /// Blends the mean-shift clustered heightmap (raw, untouched here) with a
    /// ceiling-maximized flat greyscale of the color texture, weighted by
    /// HeightmapBlendRatio (regular linear blend, not overlay), into the single 0-1 height
    /// field the gradients are measured against. Kept as floats rather than round-tripped
    /// through an 8-bit bitmap, so the gradient pass sees the real blend instead of a
    /// re-quantized copy of it. The ceiling-maximize step here only ever touches the
    /// color-texture greyscale computed in this method - the clustered heightmap's own
    /// separate ceiling-maximize, for the POM blue channel, happens later in
    /// ApplyPomBlueChannel and has nothing to do with this blend.
    /// </summary>
    private static float[,] BuildHeightField(Bitmap colorBitmap, int[,] clustered)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        // Shared, alpha-aware contrast maximization (ColorField) rather than a local
        // greyscale pass - background padding under a collapsed alpha channel gets no vote
        // in the range here for exactly the same reason it gets none in MERS generation,
        // and both now go through one implementation instead of two that could drift.
        var maximizedGrey = ColorField.BuildContrastMaximized(colorBitmap);
        var height = new float[w, h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var clusteredNormalized = clustered[x, y] / 255.0;
                var blended = clusteredNormalized * HeightmapBlendRatio + maximizedGrey[x, y] * (1.0 - HeightmapBlendRatio);
                height[x, y] = (float)Math.Clamp(blended, 0.0, 1.0);
            }
        }

        return height;
    }

    /// <summary>
    /// Sobel gradients over the height field, sampled with wraparound indexing so an edge
    /// pixel sees its real neighbor from the opposite edge - that's what makes the result
    /// tile seamlessly, and it replaces the old approach of building a 3x3-tiled copy of
    /// the whole bitmap just to read nine neighbors per pixel.
    ///
    /// Output is in height-per-pixel units - a full 0-to-1 step across one pixel boundary
    /// reads as exactly 1.0 - which is what lets MaxSlope and MinGradientReference be
    /// expressed as real slopes rather than arbitrary kernel-sum numbers.
    ///
    /// (Scharr was measured here as an alternative, on the theory that its rotational
    /// symmetry would matter for the isometric textures Bedrock randomly rotates. It
    /// didn't: the two are identical on smooth gradients - both are exact for linear
    /// signals - and on hard pixel-art edges Scharr came out marginally worse, with both
    /// dominated by the ~32% anisotropy inherent to staircasing a diagonal onto a pixel
    /// grid. Not worth the change.)
    /// </summary>
    private static void ComputeSobelGradients(
        float[,] height, int w, int h, NormalPadding padding, float[,] gradX, float[,] gradY)
    {
        ReadOnlySpan<float> kx = stackalloc float[] { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
        ReadOnlySpan<float> ky = stackalloc float[] { -1, -2, -1, 0, 0, 0, 1, 2, 1 };
        const float weightSum = 4f; // 1 + 2 + 1 down each signed side of the kernel

        // The padding modes are entirely this pair of choices, per axis: wrap to the far
        // edge (the texture repeats that way) or clamp to the nearest row/column (it doesn't,
        // and the edge should read as flat rather than as a step).
        //
        // That is the whole implementation, and it is worth seeing why the 3x3-canvas picture
        // in NormalPadding reduces to it. "Copies left and right, top and bottom flooded from
        // the strip's own edge rows" is: wrap x, clamp y. The flood is what clamping IS. And
        // because the two axes are decided independently, a corner sample clamps on both and
        // lands on the corner pixel - so ColorFill fills its corners with no special case.
        //
        // Padding is NOT a local edit to the edges, and expecting it to be will mislead you
        // when comparing output. ResolveGradientReference takes a percentile over the whole
        // texture, so a wrap that invents a hard edge inflates that reference and suppresses
        // every genuine gradient in the map. Measured on a plain vertical ramp - the
        // grass_side shape - the invented top-edge step read 123/128 off flat, and removing
        // it took the *interior* from 44 to 124: the real surface was being flattened almost
        // threefold by a seam that isn't there. Choosing the right mode is worth more than the
        // edge pixels alone suggest.
        var wrapX = padding is NormalPadding.Tile or NormalPadding.Horizontal;
        var wrapY = padding is NormalPadding.Tile or NormalPadding.Vertical;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float dx = 0, dy = 0;
                var k = 0;

                for (var j = -1; j <= 1; j++)
                {
                    for (var i = -1; i <= 1; i++)
                    {
                        var nx = wrapX ? ((x + i) % w + w) % w : Math.Clamp(x + i, 0, w - 1);
                        var ny = wrapY ? ((y + j) % h + h) % h : Math.Clamp(y + j, 0, h - 1);
                        var v = height[nx, ny];
                        dx += v * kx[k];
                        dy += v * ky[k];
                        k++;
                    }
                }

                gradX[x, y] = dx / weightSum;
                gradY[x, y] = dy / weightSum;
            }
        }
    }

    /// <summary>
    /// The gradient magnitude this texture's "full strength" edges sit at - a high
    /// percentile over its non-flat pixels only. Using the texture's own edges as the
    /// reference is what lets a soft, low-contrast texture still produce a well-formed
    /// normal map while a very bold one doesn't collapse into uniformly maxed-out edges,
    /// and it's what gives the response curve a meaningful 0-1 range to shape in the first
    /// place. Excluding flat pixels matters: on a texture that's mostly empty space,
    /// counting every flat pixel would drag any percentile to nothing, and its faint
    /// remainder would then normalize up to full strength.
    ///
    /// Uses a fixed-size histogram rather than sorting every magnitude, so cost doesn't
    /// scale with the (occasionally enormous - animation strips run to hundreds of
    /// thousands of pixels) texture size.
    /// </summary>
    private static double ResolveGradientReference(float[,] gradX, float[,] gradY, int w, int h)
    {
        var histogram = new int[GradientHistogramBins];
        var counted = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double gx = gradX[x, y];
                double gy = gradY[x, y];
                var magnitude = Math.Sqrt(gx * gx + gy * gy);
                if (magnitude <= GradientFlatThreshold) continue;

                var bin = (int)(magnitude / GradientHistogramMax * (GradientHistogramBins - 1));
                histogram[Math.Clamp(bin, 0, GradientHistogramBins - 1)]++;
                counted++;
            }
        }

        if (counted == 0) return MinGradientReference;

        var target = (long)Math.Ceiling(counted * GradientReferencePercentile);
        long running = 0;

        for (var i = 0; i < GradientHistogramBins; i++)
        {
            running += histogram[i];
            if (running < target) continue;

            var magnitude = (i + 0.5) / (GradientHistogramBins - 1) * GradientHistogramMax;
            return Math.Max(magnitude, MinGradientReference);
        }

        return Math.Max(GradientHistogramMax, MinGradientReference);
    }

    // Built-in (not a materials.json knob) default POM contrast reduction - the mean-shift
    // heightmap can still sink quite deep even after ceiling-maximizing it, so every
    // pixel's remaining distance from the surface (255) gets pulled in by this fraction.
    // Same mechanic as Tuner.ApplyNormalMapIntensity's own blue-channel handling:
    // recession = 255 - value; shrinking recession pulls pixels toward the surface and can
    // never overflow past 255, so no separate compression pass is needed. TODO(tuning).
    private static double PomContrastReduction => Tunables.PomContrastReduction.Value;

    /// <summary>
    /// POM height source: the mean-shift clustered heightmap (HeightmapGenerator.
    /// ComputeClusteredHeights), scaled up so its brightest pixel hits exactly 255 -
    /// deliberately a pure upward scale, not a full min/max stretch, so the darkest
    /// regions don't get lifted off zero and relative height differences stay
    /// proportional to the clustered heightmap's own range - then has its remaining
    /// recession from 255 reduced by PomContrastReduction, since the ceiling-maximize
    /// alone still leaves some clusters sitting quite deep.
    /// </summary>
    private static void ApplyPomBlueChannel(Bitmap normalBitmap, int[,] clustered, double heightmapIntensity)
    {
        var w = normalBitmap.Width;
        var h = normalBitmap.Height;

        var maxVal = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (clustered[x, y] > maxVal) maxVal = clustered[x, y];

        var scale = maxVal > 0 ? 255.0 / maxVal : 1.0;

        using var normalFb = new FastBitmap(normalBitmap, writable: true);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var maximized = clustered[x, y] * scale;

                // POM is ABSOLUTE recession from 255, not a relative height field - which is
                // exactly what the ceiling-maximize above exists to establish. Weakening it
                // therefore means pulling every pixel back toward 255, never rescaling
                // around a midpoint. That is the same mechanic Tuner.ApplyNormalMapIntensity
                // uses on this channel, and it can't overflow past 255 by construction.
                //
                // Two factors do that pulling. PomContrastReduction is the fixed one every
                // texture gets, because the clustered heightmap still sinks very deep even
                // after ceiling-maximizing. heightmap.intensity rides on top of it - it's
                // the heightmap's own contrast reduction, and a block told to read flatter
                // should read flatter here too.
                //
                // Note they compound. heightmap.intensity defaults to 1.0 so this changes
                // nothing today, but if that default ever drops, PomContrastReduction
                // probably wants loosening to compensate.
                var recession = (255.0 - maximized)
                                * (1.0 - PomContrastReduction)
                                * Math.Clamp(heightmapIntensity, 0.0, 1.0);

                var pom = (byte)Math.Clamp((int)Math.Round(255.0 - recession), 0, 255);
                var c = normalFb[x, y];
                normalFb[x, y] = Color.FromArgb(c.A, c.R, c.G, pom);
            }
        }
    }

    /// <summary>
    /// Average local gradient magnitude between each pixel and its immediate right/down
    /// neighbor, normalized to a 0-100 index. Unlike a raw unique-color ratio, this tracks
    /// visual noisiness: soft painterly gradients (many unique colors, small per-pixel
    /// jumps) score low, while genuinely noisy/high-frequency textures (large abrupt jumps)
    /// score high.
    ///
    /// Counts every neighbour pair, including ones straddling a cutout's boundary against
    /// its transparent padding - so a complex silhouette raises the index as surely as a busy
    /// surface does. That is deliberate. Restricting the walk to real colour data
    /// (ColorField.IsRealColorData), which is the module's rule everywhere else, is more
    /// consistent and measurably worse: a texture's silhouette is real evidence that it is
    /// high-frequency art, and taking it away crushed the interiors of exactly the cutouts
    /// that depend on it - doors, grates, vines, leaf litter - while improving little else.
    ///
    /// Deliberately does NOT wrap at the edges, unlike the Sobel sampler this feeds. What it
    /// asks is how busy the texture is; the seam where a tile meets its own opposite edge is
    /// a property of the tiling.
    /// </summary>
    public static int GetNoiseIndex(Bitmap image)
    {
        var w = image.Width;
        var h = image.Height;
        var grey = new int[w, h];

        using (var fb = new FastBitmap(image, writable: false))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = fb[x, y];
                    grey[x, y] = (c.R + c.G + c.B) / 3;
                }
            }
        }

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (x + 1 < w)
                {
                    totalDelta += Math.Abs(grey[x, y] - grey[x + 1, y]);
                    sampleCount++;
                }
                if (y + 1 < h)
                {
                    totalDelta += Math.Abs(grey[x, y] - grey[x, y + 1]);
                    sampleCount++;
                }
            }
        }

        var averageDelta = sampleCount > 0 ? totalDelta / sampleCount : 0;
        return (int)Math.Clamp(averageDelta / NoiseCalibrationCeiling * 100.0, 0, 100);
    }

    private static void InvertRedGreenInPlace(Bitmap bitmap)
    {
        using var fb = new FastBitmap(bitmap, writable: true);
        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                fb[x, y] = Color.FromArgb(c.A, 255 - c.R, 255 - c.G, c.B); // blue (POM) untouched
            }
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

#endregion

#region Heightmap Generation

/// <summary>
/// Generates a heightmap from a color texture:
///   1. ComputeClusteredHeights groups the texture's grey values into however many
///      distinct height bands it actually has via mean-shift filtering (iterated joint
///      spatial+range weighted averaging, which converges to the same piecewise-flat
///      result as literal mode-seeking mean-shift), then places each band at the spacing
///      the texture's own composition implies, normalized to span 0-255. Shared with
///      NormalMapGenerator, which uses this same clustering as its own height-field basis.
///   2. Transparent regions of the color texture get darkened using an overlay blend
///      (not linear) - preserves midtone detail on the darkened side, which matters for
///      cases like grass_side where the dirt portion is transparent in the color texture
///      but should still read as "below" the opaque grass part.
///   3. `intensity` blends the clustered result toward a flat neutral median (128).
///   4. `invert` (workaround for the known game-side inverted-display bug) does a final
///      full color inversion.
/// Output is single-channel-equivalent grayscale (R=G=B), fully opaque.
/// </summary>
public static class HeightmapGenerator
{
    private const byte LevelMid = 128;

    // TODO(tuning): how strongly a fully-transparent color-texture pixel's darkening
    // overlay applies (0 = no darkening, 1 = full overlay strength) - see class doc
    // comment above for why this exists (grass_side-style "read as beneath" regions).
    private static double TransparencyOverlayStrength => Tunables.HeightTransparencyOverlay.Value;

    // Mean-shift filtering knobs, tuned against a real corpus (28 textures across an 8x,
    // a 16x and a 32x pack) rather than guessed - see the measurements in CLAUDE.md 9.7.
    // The objective was "speckle": the share of pixels whose level disagrees with the
    // majority of their 8 neighbours. Clustering exists to produce plateaus, and speckle
    // is precisely the absence of one. Baseline 16.2%, these settings 8.5%.
    private static int MeanShiftIterations => (int)Tunables.HeightIterations.Value;

    // A 3x3 window, NOT the 5x5 it used to be. Measured across every combination, a
    // smaller window won on every texture type: a wide window reaches across a mortar line
    // into the face on the other side and averages the two together, and five iterations
    // propagate a 3x3 far enough anyway. Radius 2 measured 10.6% against radius 1's 8.5%.
    private static int SpatialRadius => (int)Tunables.HeightSpatialRadius.Value;

    // Large relative to a radius of 1, which makes the spatial term nearly flat across the
    // 3x3 (weights 1.00 / 0.92 / 0.85) - deliberately so. What separates features here is
    // value distance, not pixel distance, and letting position discriminate as well was
    // measurably worse. Kept as a knob rather than removed because it regains its meaning
    // the moment SpatialRadius goes back up.
    private static double SpatialSigma => Tunables.HeightSpatialSigma.Value;

    // How far apart two values can be and still get pulled together.
    //
    // Once narrowed to 12, to stop mean-shift bridging a shallow mortar line into the plank
    // above it. That fixed the bridging but produced far too many modes, and combined with
    // the value placement tried alongside it the output became a posterized greyscale.
    //
    // Measured across the corpus the pressure runs the other way: speckle falls
    // monotonically as this rises (24 -> 16.2%, 32 -> 14.4%, 40 -> 12.7%, 48 -> 12.2%),
    // while the mortar lines that motivated narrowing it stay fully intact at 48 - verified
    // by rendering planks and stonebrick at each step. Past 48 the returns flatten and the
    // risk of bridging real features grows, so this is where it sits.
    private static double RangeBandwidth => Tunables.HeightRangeBandwidth.Value;

    // Above this pixel count, ComputeClusteredHeights skips the spatial neighbor search
    // (which scales with W*H*R^2*iterations) and falls back to range-only clustering off
    // a 256-bin histogram instead - still mean-shift filtering, just position-independent
    // and bounded regardless of texture size. Automatic, not a user-facing setting.
    private const int SpatialFallbackPixelCount = 256 * 256;

    // Converged values within this distance of each other are folded into the same
    // cluster during ranking.
    private static double ClusterMergeTolerance => Tunables.HeightMergeTolerance.Value;

    // Below this total brightness span across all surviving buckets, composition-derived
    // placement has nothing real to work from and falls back to even spacing - see the
    // placement step in ClusterAndPlace.
    private const double MinPlacementSpan = 1.0;

    // Hard cap on the final number of elevation levels a texture can produce, regardless
    // of how many distinct clusters mean-shift converged to. A busy/high-color-count
    // texture that would otherwise land on many clusters gets merged down harder to reach
    // this; a calm texture that already converged to fewer is untouched. TODO(tuning).
    // TODO(tuning): the hard ceiling on distinct elevations, and the single most direct
    // lever on "this heightmap looks like a mess". Output has at most this many distinct
    // greys for an opaque texture; WHERE they sit is the texture's business now (see the
    // placement step), but how many there can be is this.
    //
    // Raising it does keep lowering speckle (4 -> 8.5%, 6 -> 6.9%, 8 -> 6.2% across the
    // corpus), but for the wrong reason: the gain comes from declining to merge noise into
    // its neighbours, and every extra rung is another elevation competing with the ones
    // that describe real structure. Four still reads as distinct flat surfaces in game,
    // which is the whole point.
    //
    // Note this is only the ceiling on the *clustered ladder*. A cutout texture gets a
    // second darkened variant of each level from the transparency overlay in Generate
    // below, so the count a cutout texture actually shows is up to double this.
    private static int MaxClusters => (int)Tunables.HeightMaxLevels.Value;

    // Pairing mean-shift with a fixed-step quantization of the same values, and keeping
    // only regions the two agree on, was tried here as a guard against mean-shift bridging
    // features it shouldn't. It didn't survive measurement: fixed band boundaries sit at
    // arbitrary values, so on a plank texture whose mortar line straddled one, the line
    // came out as two different heights - worse than the problem it was meant to fix - and
    // once the bandwidth below was narrowed the guard had nothing left to catch anyway.
    // Narrowing the bandwidth is the real cure; the merge pass makes it safe.

    public static Bitmap Generate(Bitmap colorBitmap, ResolvedHeightmap heightmapParams)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;
        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var clustered = ComputeClusteredHeights(colorBitmap);
        var alpha = new byte[w, h];

        using (var colorFb = new FastBitmap(colorBitmap, writable: false))
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    alpha[x, y] = colorFb[x, y].A;
        }

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var clusteredValue = (byte)clustered[x, y];

                    var transparencyAmount = 255 - alpha[x, y];
                    var overlayStrength = transparencyAmount * TransparencyOverlayStrength / 255.0;

                    var withOverlay = clusteredValue;
                    if (overlayStrength > 0)
                    {
                        var blended = OverlayBlendWithBlack(clusteredValue);
                        withOverlay = Lerp(clusteredValue, blended, overlayStrength);
                    }

                    var withIntensity = Lerp(LevelMid, withOverlay, Math.Clamp(heightmapParams.Intensity, 0.0, 1.0));
                    var final = heightmapParams.Invert ? (byte)(255 - withIntensity) : withIntensity;

                    outFb[x, y] = Color.FromArgb(255, final, final, final);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Mean-shift filtering core, shared with NormalMapGenerator (which uses this as its
    /// own height-field input, and separately as its POM blue-channel source). Returns
    /// raw clustered grey values (0-255) with no artist-facing post-processing applied.
    /// </summary>
    public static int[,] ComputeClusteredHeights(Bitmap colorBitmap)
    {
        var w = colorBitmap.Width;
        var h = colorBitmap.Height;

        // Contrast-maximized up front (and alpha-aware - background padding under a
        // collapsed alpha channel gets no vote in the range, see ColorField), so the
        // bandwidth/tolerance constants below mean the same thing on a washed-out texture
        // as on a punchy one instead of drifting with whatever range the source happened
        // to occupy.
        var normalized = ColorField.BuildContrastMaximized(colorBitmap);

        var grey = new double[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grey[x, y] = normalized[x, y] * 255.0;

        var converged = (long)w * h <= SpatialFallbackPixelCount
            ? ConvergeSpatial(grey, w, h)
            : ConvergeRangeOnly(grey, w, h);

        return ClusterAndPlace(converged, grey, w, h);
    }

    /// <summary>Full joint spatial+range mean-shift filtering: each pixel's value is
    /// repeatedly replaced by a weighted average of its small spatial neighborhood,
    /// weighted by both spatial closeness and value closeness. Neighbors are sampled with
    /// wraparound/modulo indexing so results stay seamless-tileable without needing a full
    /// 3x tiled copy at this small radius.</summary>
    private static double[,] ConvergeSpatial(double[,] grey, int w, int h)
    {
        var current = grey;

        for (var iter = 0; iter < MeanShiftIterations; iter++)
        {
            var next = new double[w, h];

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var centerValue = current[x, y];
                    double weightSum = 0, valueSum = 0;

                    for (var dy = -SpatialRadius; dy <= SpatialRadius; dy++)
                    {
                        for (var dx = -SpatialRadius; dx <= SpatialRadius; dx++)
                        {
                            var nx = ((x + dx) % w + w) % w;
                            var ny = ((y + dy) % h + h) % h;
                            var neighborValue = grey[nx, ny];

                            var spatialDist2 = dx * dx + dy * dy;
                            var rangeDist = neighborValue - centerValue;
                            var weight = Math.Exp(-spatialDist2 / (2 * SpatialSigma * SpatialSigma))
                                       * Math.Exp(-(rangeDist * rangeDist) / (2 * RangeBandwidth * RangeBandwidth));

                            weightSum += weight;
                            valueSum += weight * neighborValue;
                        }
                    }

                    next[x, y] = weightSum > 0 ? valueSum / weightSum : centerValue;
                }
            }

            current = next;
        }

        return current;
    }

    /// <summary>Value-only mean-shift for very large textures, where a per-pixel spatial
    /// neighbor search would scale too aggressively: clusters purely on a 256-bin
    /// grey-value histogram (position-independent), which is O(256^2*iterations)
    /// regardless of texture size, then looks each pixel's converged value up by its
    /// rounded grey value.</summary>
    private static double[,] ConvergeRangeOnly(double[,] grey, int w, int h)
    {
        var histogram = new int[256];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                histogram[Math.Clamp((int)Math.Round(grey[x, y]), 0, 255)]++;

        var representative = new double[256];
        for (var i = 0; i < 256; i++) representative[i] = i;

        for (var iter = 0; iter < MeanShiftIterations; iter++)
        {
            var next = new double[256];

            for (var i = 0; i < 256; i++)
            {
                double weightSum = 0, valueSum = 0;

                for (var j = 0; j < 256; j++)
                {
                    if (histogram[j] == 0) continue;
                    var rangeDist = j - representative[i];
                    var weight = histogram[j] * Math.Exp(-(rangeDist * rangeDist) / (2 * RangeBandwidth * RangeBandwidth));
                    weightSum += weight;
                    valueSum += weight * j;
                }

                next[i] = weightSum > 0 ? valueSum / weightSum : representative[i];
            }

            representative = next;
        }

        var converged = new double[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                converged[x, y] = representative[Math.Clamp((int)Math.Round(grey[x, y]), 0, 255)];

        return converged;
    }

    /// <summary>Merges per-pixel converged values into clusters (values within
    /// ClusterMergeTolerance of their neighbor in sorted order are folded together), caps
    /// the result at MaxClusters by repeatedly merging whichever two brightness-adjacent
    /// clusters are closest together (weighted by pixel count) until at or under the cap,
    /// then places the survivors at their own relative spacing, normalized to span 0-255 -
    /// see the comment at the placement step for why the texture rather than the rank
    /// decides how far apart two elevations sit.</summary>
    private static int[,] ClusterAndPlace(double[,] converged, double[,] original, int w, int h)
    {
        var distinctValues = new SortedSet<double>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                distinctValues.Add(Math.Round(converged[x, y], 1));

        var clusterOf = new Dictionary<double, int>();
        var clusterId = 0;
        var clusterAnchor = 0.0;
        var first = true;

        foreach (var v in distinctValues)
        {
            if (first || v - clusterAnchor > ClusterMergeTolerance)
            {
                if (!first) clusterId++;
                clusterAnchor = v;
                first = false;
            }
            clusterOf[v] = clusterId;
        }

        var clusterCount = clusterId + 1;
        var brightnessSum = new double[clusterCount];
        var brightnessCount = new int[clusterCount];
        var pixelCluster = new int[w, h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var id = clusterOf[Math.Round(converged[x, y], 1)];
                pixelCluster[x, y] = id;
                brightnessSum[id] += original[x, y];
                brightnessCount[id]++;
            }
        }

        var clusterMeanBrightness = new double[clusterCount];
        for (var i = 0; i < clusterCount; i++)
            clusterMeanBrightness[i] = brightnessCount[i] > 0 ? brightnessSum[i] / brightnessCount[i] : 0;

        // Brightness-sorted list of surviving buckets, each carrying which original region
        // ids it absorbed. Starts as one bucket per region; merging two adjacent buckets
        // (always adjacent in this sorted order, since nothing ever reorders) is just a
        // local list splice - cheap even repeated all the way down.
        var order = Enumerable.Range(0, clusterCount).OrderBy(i => clusterMeanBrightness[i]).ToList();
        var bucketMeans = order.Select(i => clusterMeanBrightness[i]).ToList();
        var bucketCounts = order.Select(i => brightnessCount[i]).ToList();
        var bucketMembers = order.Select(i => new List<int> { i }).ToList();

        while (bucketMeans.Count > 1)
        {
            var mergeAt = 0;
            var smallestGap = double.MaxValue;
            for (var i = 0; i < bucketMeans.Count - 1; i++)
            {
                var gap = bucketMeans[i + 1] - bucketMeans[i];
                if (gap < smallestGap) { smallestGap = gap; mergeAt = i; }
            }

            // Two reasons to keep merging: still over the cap, or the two closest levels
            // are near enough that keeping them apart would just be two nearly-identical
            // heights sitting next to each other - which is the definition of a heightmap
            // that reads as a mess in game. Under the cap AND well separated means done.
            if (bucketMeans.Count <= MaxClusters && smallestGap > ClusterMergeTolerance) break;

            var combinedCount = bucketCounts[mergeAt] + bucketCounts[mergeAt + 1];
            bucketMeans[mergeAt] = combinedCount > 0
                ? (bucketMeans[mergeAt] * bucketCounts[mergeAt] + bucketMeans[mergeAt + 1] * bucketCounts[mergeAt + 1]) / combinedCount
                : (bucketMeans[mergeAt] + bucketMeans[mergeAt + 1]) / 2.0;
            bucketCounts[mergeAt] = combinedCount;
            bucketMembers[mergeAt].AddRange(bucketMembers[mergeAt + 1]);

            bucketMeans.RemoveAt(mergeAt + 1);
            bucketCounts.RemoveAt(mergeAt + 1);
            bucketMembers.RemoveAt(mergeAt + 1);
        }

        var bucketOf = new int[clusterCount];
        for (var bucket = 0; bucket < bucketMembers.Count; bucket++)
            foreach (var originalId in bucketMembers[bucket])
                bucketOf[originalId] = bucket;

        // Buckets are placed at the spacing the TEXTURE'S OWN COMPOSITION implies, then
        // normalized so the ladder always spans the full 0-255 range.
        //
        // Each bucket already knows its mean brightness on the contrast-maximized source
        // (bucketMeans, accumulated above), so the gaps between those means are a real
        // measurement of how far apart the texture's own materials sit. Placing at evenly
        // spaced slots by rank - which this used to do - throws that measurement away and
        // asserts that every step is the same size. It never is. On a plank texture that
        // put a board's internal edge shading exactly as far below its face (85 levels) as
        // the mortar gap between boards, which is the single most obvious thing a heightmap
        // has to get right.
        //
        // Normalizing rather than using the means directly is what keeps this from being
        // the value-based placement that was tried and reverted (CLAUDE.md 9.3). Anchoring
        // the darkest bucket at 0 and the brightest at 255 guarantees full contrast no
        // matter how narrow the source's range was, so a low-contrast texture can't come
        // out as a flat grey smear; only the RELATIVE spacing in between is the texture's
        // to decide. The other half of why 9.3 failed - dozens of surviving levels, each
        // faithfully reproducing wood grain as elevation - is handled by MaxClusters, which
        // caps the ladder at four rungs before any of this runs.
        //
        // Measured on the same 28-texture corpus as the constants above: the resulting
        // ladder departs from even spacing by a median of 22 levels and up to 72, with over
        // half the corpus moving more than 20 - so this is a real change in output, not a
        // rounding difference. Planks go from [0,85,170,255] to [0,95,220,255]: the two
        // board levels close ranks near the top while the mortar gap stays far below,
        // which is exactly the shape the texture actually has.
        var placed = new int[bucketMeans.Count];

        if (bucketMeans.Count == 1)
        {
            placed[0] = LevelMid;
        }
        else
        {
            var lowest = bucketMeans[0];
            var span = bucketMeans[^1] - lowest;

            if (span < MinPlacementSpan)
            {
                // Every surviving bucket sits at essentially the same brightness, so there
                // are no meaningful relative distances to preserve and normalizing would
                // amplify noise into a full-range ladder. Fall back to even spacing, which
                // at least keeps the levels distinct and ordered.
                for (var i = 0; i < placed.Length; i++)
                    placed[i] = (int)Math.Clamp(Math.Round(i / (double)(placed.Length - 1) * 255.0), 0, 255);
            }
            else
            {
                for (var i = 0; i < placed.Length; i++)
                    placed[i] = (int)Math.Clamp(Math.Round((bucketMeans[i] - lowest) / span * 255.0), 0, 255);
            }
        }

        var result = new int[w, h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                result[x, y] = placed[bucketOf[pixelCluster[x, y]]];
            }
        }

        return result;
    }

    /// <summary>Photoshop-style "Overlay" blend against a black overlay, at full strength
    /// (caller lerps by overlayStrength afterwards).</summary>
    private static byte OverlayBlendWithBlack(byte baseValue)
    {
        var baseNorm = baseValue / 255.0;
        var result = baseNorm < 0.5 ? 0.0 : 2 * baseNorm - 1;
        return (byte)Math.Clamp((int)Math.Round(result * 255.0), 0, 255);
    }

    private static byte Lerp(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a * (1.0 - t) + b * t), 0, 255);
}

#endregion
