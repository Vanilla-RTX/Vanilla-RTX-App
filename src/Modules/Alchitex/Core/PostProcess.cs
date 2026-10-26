using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using ImageMagick.Drawing; // Drawables, DrawableFillColor, DrawableRectangle
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules.Alchitex.Core;

/// <summary>
/// Everything that happens to a pack after its PBR textures exist: water, glass, manifest,
/// terrain_texture.json, icon, and a bit of housekeeping. Named generically ("PostProcess",
/// not "WaterGlassProcessor") on purpose - this is where any *future* post-generation pass
/// belongs too, not just these five. If a new pass gets added later, it gets a new region
/// in this file and a new call in AlchitexPipeline, not a new file.
/// </summary>
public static class PostProcess
{
    #region Required Assets

    // The two zips are online-updatable, so they come from AssetUpdater.Resolve rather than
    // from a path - see AssetUpdater. icon_badge.png isn't, so it still resolves against the
    // assets folder the pipeline passes in.
    //
    //   Assets/water-fallback.zip
    //     -> four flat files, no folders inside: water_flow_grey.tga + .texture_set.json,
    //        water_still_grey.tga + .texture_set.json.
    //
    //   Assets/icon_badge.png
    //     -> 42x42 watermark composited onto the bottom-left corner of every regenerated pack icon.
    //
    //   Assets/vanilla-rtx-fog.zip
    //     -> top-level "biomes/" and "fogs/" folders, deployed into the pack root and
    //        every subpack root when the (opt-in, off-by-default) fog toggle is enabled.

    private const string IconBadgeFileName = "icon_badge.png";


    #endregion

    #region Water

    // Minecraft RTX gives water its own scrolling normals that a pack cannot override, and
    // takes the rest of its material from the colour texture's ALPHA: lower alpha means
    // lower roughness, i.e. more mirror-like water. So a water texture carries no PBR at all
    // (water is blacklisted in pbr_blacklist.json) - the whole job here is turning an
    // ordinary painted water texture into "brightness encoded as alpha over a flat bright
    // fill", which is the only shape that renders as good water.

    // TODO(tuning): visual knobs for water-to-grey conversion.

    /// <summary>Floor opacity. Below this Bedrock RTX starts casting shadows *underneath*
    /// the water surface - nonsensical, but it is what the renderer does, so the encoded
    /// range is 129-255 rather than 0-255. Ported unchanged from legacy RTX Reactor.</summary>
    private const int MinWaterOpacity = 129;

    /// <summary>
    /// Bend applied to brightness before it becomes alpha. 1.0 would be the straight line
    /// from (darkest -> 129) to (brightest -> 255); above 1.0 the curve sags below that
    /// line, so every pixel except the two endpoints lands at a *lower* alpha than a linear
    /// mapping would give it.
    ///
    /// That is the point rather than a side effect: lower alpha is lower roughness, so
    /// bending the curve hands more of the texture sharper reflections while leaving its
    /// composition - which pixel is brighter than which - completely intact. At 1.5 a
    /// mid-brightness pixel lands at 174 instead of 192.
    /// </summary>
    private const double WaterOpacityCurve = 1.75;

    /// <summary>How far the flat fill is pushed toward white after averaging. 0.5 is a
    /// halfway blend, which is what makes the fill land in the 192-225 band for almost any
    /// input - see ResolveFlatFill.</summary>
    private const double WaterWhitePull = 0.5;

    /// <summary>
    /// Converts a colour water texture into the flat-fill, brightness-as-alpha form Bedrock
    /// RTX expects (water_still_grey / water_flow_grey), written as a sibling "_grey" TGA.
    ///
    /// Two independent things come out of the source texture:
    ///
    ///   ALPHA carries the composition. Per-pixel brightness is stretched onto 129-255 (see
    ///   MinWaterOpacity) and bent by WaterOpacityCurve. Brightness is where the artist said
    ///   "this part is watery", and alpha is how the renderer reads that.
    ///
    ///   RGB is thrown away and replaced by one flat value. It has to be: the game tints
    ///   water per biome by multiplying this texture, so any colour baked in here would
    ///   double up with every biome's own. "Grey" in the vanilla file name is literal.
    ///
    /// **The stretch used to be a shift, and that was a real bug.** The old code added
    /// (129 - darkest) to every pixel and clamped, so a texture whose brightness already
    /// spanned 0-255 had everything above 126 clamped flat to 255 - half the composition
    /// gone, on exactly the well-drawn textures that had the most to lose. A stretch maps
    /// darkest to 129 and brightest to 255 with everything in between kept in proportion,
    /// which is what the floor was always meant to do.
    /// </summary>
    public static void ConvertWaterToGrey(string imagePath)
    {
        using var source = Helpers.ReadImage(imagePath, maxOpacity: false);

        var width = source.Width;
        var height = source.Height;

        var brightness = new int[width, height];
        var minBrightness = 255;
        var maxBrightness = 0;
        var linearSum = 0.0;

        using (var srcFb = new FastBitmap(source, writable: false))
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = srcFb[x, y];

                    // Flat average, the module's standing convention for "value" (§4.11) -
                    // not perceptual luminosity, which would weight blue at 7% and read most
                    // water textures as nearly black.
                    var b = (p.R + p.G + p.B) / 3;
                    brightness[x, y] = b;

                    if (b < minBrightness) minBrightness = b;
                    if (b > maxBrightness) maxBrightness = b;

                    linearSum += SrgbToLinear(p.R) + SrgbToLinear(p.G) + SrgbToLinear(p.B);
                }
            }
        }

        var channelCount = width * height * 3;
        var fill = ResolveFlatFill(channelCount > 0 ? linearSum / channelCount : 0.0);
        var span = maxBrightness - minBrightness;

        using var output = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var outFb = new FastBitmap(output, writable: true))
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // A texture with no brightness range at all has no composition to
                    // preserve, so it gets maximum clarity rather than an arbitrary midpoint.
                    var t = span > 0 ? (brightness[x, y] - minBrightness) / (double)span : 1.0;

                    var alpha = (byte)Math.Clamp(
                        (int)Math.Round(MinWaterOpacity + Math.Pow(t, WaterOpacityCurve) * (255 - MinWaterOpacity)),
                        MinWaterOpacity, 255);

                    outFb[x, y] = Color.FromArgb(alpha, fill, fill, fill);
                }
            }
        }

        var nameNoExt = Path.GetFileNameWithoutExtension(imagePath);
        var directory = Path.GetDirectoryName(imagePath)!;
        // Always .tga regardless of the source's own extension - highest game priority
        // (.tga > .png > .jpg > .jpeg), and avoids ever writing TGA-formatted bytes into a
        // non-.tga-named file when the source was already "_grey"-named but not itself a
        // .tga (e.g. a pack's own water_still_grey.png).
        var greyNameNoExt = nameNoExt.EndsWith("_grey", StringComparison.OrdinalIgnoreCase) ? nameNoExt : nameNoExt + "_grey";
        var greyPath = Path.Combine(directory, greyNameNoExt + ".tga");

        Helpers.WriteImageAsTGA(output, greyPath);
    }

    /// <summary>
    /// The single grey the whole texture is filled with, from the mean of its channels in
    /// LINEAR light, pulled halfway toward white.
    ///
    /// Linear-light matters and is not pedantry: averaging sRGB values directly
    /// under-reports a texture's real brightness badly. Half black and half white averages
    /// to 128 in sRGB and to 188 done properly - and water wants the bright answer, because
    /// a dark fill reads as murky no matter what the alpha says.
    ///
    /// The pull toward white then does the rest, and the halfway point is what makes the
    /// result land in a useful band for any input at all: a fully black texture still comes
    /// out at 128, a mid-grey one at 192, a bright one at ~222. A texture that was already
    /// bright is nudged less in absolute terms than a dark one, which is the right
    /// behaviour - it needed less help.
    ///
    /// Pulling toward white rather than toward the texture's own brightest pixel is
    /// deliberate. The brightest pixel is only useful if it happens to *be* bright; white is
    /// bright by definition, so the floor this puts under the result doesn't depend on the
    /// input having a lucky highlight in it.
    /// </summary>
    private static byte ResolveFlatFill(double meanLinear)
    {
        var mean = LinearToSrgb(meanLinear) * 255.0;
        var pulled = mean + (255.0 - mean) * WaterWhitePull;

        return (byte)Math.Clamp((int)Math.Round(pulled), 0, 255);
    }

    private static double SrgbToLinear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double linear)
    {
        var l = Math.Clamp(linear, 0.0, 1.0);
        return l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
    }

    /// <summary>
    /// Tries to make sure `blocksFolder` ends up with a proper RTX-encoded
    /// water_still_grey.tga and water_flow_grey.tga, per texture independently (not
    /// atomic across the pair - each one can get there from a different source). A pack's
    /// own water_still_grey/water_flow_grey isn't guaranteed to already carry Bedrock
    /// RTX's specific brightness-as-opacity encoding (it might just be the plain in-world
    /// tinting texture from a non-RTX pack) - and even if it does, .tga has to win
    /// priority over whatever extension it shipped as - so it always gets run through
    /// ConvertWaterToGrey, overwriting in place, same as everything else in this pipeline.
    /// Source preference (TextureSetHelper.FindTextureFile - same .tga &gt; .png &gt; .jpg
    /// &gt; .jpeg priority order used everywhere else a texture name gets resolved to a
    /// file): the pack's own "_grey"-named texture if present, otherwise the colored/
    /// inventory variant (packs sometimes ship only that and forget the in-world grey one
    /// the game actually tints per-biome).
    /// Returns whether this folder produced ANY grey water at all - not whether it produced
    /// both. The two textures are resolved independently and either can succeed alone, and
    /// the caller's question is a pack-level one: the packaged fallback exists for a pack
    /// with no water textures whatsoever, not for a folder that happens to be missing one.
    /// See AlchitexPipeline.RunWaterGlassPass.
    /// </summary>
    public static bool EnsureGreyWaterTextures(string blocksFolder)
    {
        // Both, always - `|` rather than `||`, or a successful "still" would skip "flow".
        return EnsureOneGreyWaterTexture(blocksFolder, "water_still")
             | EnsureOneGreyWaterTexture(blocksFolder, "water_flow");
    }

    private static bool EnsureOneGreyWaterTexture(string blocksFolder, string baseName)
    {
        var source = TextureSetHelper.FindTextureFile(blocksFolder, baseName + "_grey")
                  ?? TextureSetHelper.FindTextureFile(blocksFolder, baseName);
        if (source == null) return false;

        try
        {
            ConvertWaterToGrey(source);
            // TextureSetOrchestrator's scan (Phase 2a) already ran and completed before
            // this pass ever creates baseName + "_grey.tga" - if the pack didn't already
            // ship one, nothing would otherwise ever give it a .texture_set.json at all
            // (water_flow_grey/water_still_grey are always PBR-blacklisted anyway, so a
            // color-only one is exactly what TextureSetOrchestrator would have written had
            // it run after this file existed). Deliberately narrow fix scoped to this one
            // gap rather than reordering the pipeline - the zip-fallback path doesn't need
            // this, its .texture_set.json ships inside water-fallback.zip already.
            EnsureColorOnlyTextureSet(blocksFolder, baseName + "_grey");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to derive '{baseName}_grey' from '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes a minimal color-only .texture_set.json ("color": textureName, no
    /// PBR keys - water is always blacklisted) for `textureName` in `blocksFolder`, unless
    /// one already exists (never clobbers a hand-authored/pack-provided file, same
    /// convention as TextureSetOrchestrator).</summary>
    private static void EnsureColorOnlyTextureSet(string blocksFolder, string textureName)
    {
        var jsonPath = Path.Combine(blocksFolder, textureName + ".texture_set.json");
        if (File.Exists(jsonPath)) return;

        var root = new JsonObject
        {
            ["format_version"] = "1.21.30",
            ["minecraft:texture_set"] = new JsonObject { ["color"] = textureName },
        };

        try
        {
            MinecraftJson.WriteIndented(jsonPath, root);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to write texture set for '{textureName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts Alchitex's packaged water-fallback.zip (four flat files, no folders inside -
    /// both grey TGAs and their .texture_set.json descriptors) directly into `blocksFolder`,
    /// overwriting anything already there. Only called when the pack turned out to have no
    /// water textures anywhere at all, in which case it goes into EVERY blocks folder - see
    /// AlchitexPipeline.RunWaterGlassPass for why that is the right shape. Never invents
    /// content - if the
    /// packaged zip isn't present under Assets/, this logs exactly what's missing and leaves
    /// the pack without fallback water rather than pretending to have handled it. Returns
    /// true only on a successful extraction.
    /// </summary>
    public static bool DeployFallbackWaterZip(string blocksFolder)
    {
        var zipPath = AssetUpdater.Resolve(AlchitexAssets.WaterFallbackZip);
        if (!File.Exists(zipPath))
        {
            Trace.WriteLine($"[ALCHITEX] Water fallback asset missing - expected '{zipPath}'. Skipping fallback deployment for '{blocksFolder}'.");
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // defensive - zip is flat, no folder entries expected
                entry.ExtractToFile(Path.Combine(blocksFolder, entry.Name), overwrite: true);
            }

            Trace.WriteLine($"[ALCHITEX] Deployed water fallback into '{blocksFolder}'.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to deploy water fallback into '{blocksFolder}': {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Fog

    /// <summary>
    /// Deploys Alchitex's packaged fog asset (biomes_client fog references + fog
    /// definition files) into the pack root and every subpack's own root - not
    /// textures/blocks, since fog files aren't block-texture-scoped the way water
    /// fallback is. Opt-in (AlchitexOptions.AddFog, off by default) and overwrites
    /// whatever's already at each destination path. Never invents content - if the
    /// packaged zip isn't present under Assets/, this logs exactly what's missing and
    /// leaves the pack without fog rather than pretending to have handled it.
    /// </summary>
    public static void DeployFog(string packRoot)
    {
        var zipPath = AssetUpdater.Resolve(AlchitexAssets.FogZip);
        if (!File.Exists(zipPath))
        {
            Trace.WriteLine($"[ALCHITEX] Fog asset missing - expected '{zipPath}'. Copy the fog distribution zip (top-level 'biomes/' and 'fogs/' folders) there. Skipping fog deployment for '{packRoot}'.");
            return;
        }

        var targetRoots = new List<string> { packRoot };
        var subpacksDir = Path.Combine(packRoot, "subpacks");
        if (Directory.Exists(subpacksDir))
            targetRoots.AddRange(Directory.GetDirectories(subpacksDir));

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var targetRoot in targetRoots)
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                    var destPath = Path.Combine(targetRoot, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            Trace.WriteLine($"[ALCHITEX] Deployed fog into {targetRoots.Count} location(s) under '{packRoot}'.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to deploy fog into '{packRoot}': {ex.Message}");
        }
    }

    #endregion

    #region Blend render method suitability

    // Minecraft RTX renders a fixed set of blocks with the "blend" material instance: glass,
    // glass panes, tinted glass, hard glass (Education Edition), copper grates, slime and
    // honey. What follows exists because a texture authored to look right in Fancy graphics
    // is usually authored *wrong* for that renderer, in two specific ways.
    //
    // -- WHAT THE RENDERER ACTUALLY DOES ---------------------------------------------------
    //
    // Two independent things decide how much light reaches the far side of a blend surface:
    //
    //   alpha       - transparency. At full opacity the block reads as an ordinary solid; the
    //                 refraction and internal reflection only appear below it.
    //   the colour  - clarity. The colour acts as a per-channel filter on what passes
    //                 through. Fully black at alpha 0 transmits nothing at all; fully white
    //                 at alpha 0 is crystal-clear glass.
    //
    // The second is the one texture artists don't expect, and it is why deeply saturated
    // glass comes out nearly opaque under RTX. A pure red (255,0,0) absorbs *all* the green
    // and blue; (255,64,64) reads as almost the same red to the eye while letting far more
    // light through. So the quantity that matters is the **smallest channel** - the amount of
    // white light the filter passes - which in HSV terms is exactly value x (1 - saturation).
    //
    // -- THE THREE THINGS THIS DOES --------------------------------------------------------
    //
    //   1. Maximise transparency without flattening it. Every translucent pixel drops by the
    //      same amount, until the most transparent one reaches 0. Uniform, so the texture's
    //      internal composition survives intact.
    //   2. Open the holes. A pixel that is near-invisible *and* near-black is a hole - a gap
    //      in a copper grate, the empty middle of a pane - and black at zero alpha blocks all
    //      light, which is never what a hole is for. It becomes white at the same alpha.
    //   3. Lift transmission, keeping the colour. See LiftTransmission - this is the part
    //      with an actual idea in it.
    //
    // Opaque pixels are spared by all three. Alpha at or above BlendOpaqueThreshold was set
    // deliberately, reads as solid in Fancy, and should read as solid here too.
    //
    // -- WHAT THIS REPLACED, AND WHY IT WAS BACKWARDS --------------------------------------
    //
    // The legacy pass (two of them, actually - a "glass" modifier and a "basic glass" one,
    // dispatched by substring-matching the file name) did the opposite of the above on both
    // counts. It forced `value = 1.0` on every translucent pixel, erasing the difference
    // between clear, grey, smoked and black glass outright; and it *added* 0.1 saturation,
    // which by the paragraph above makes glass strictly more absorptive - the single change
    // most guaranteed to darken it. It also whited out every pixel below alpha 64 regardless
    // of colour, so a deliberately tinted low-alpha pane lost its tint entirely.

    // TODO(tuning): every constant below wants an eye against real generated glass.

    /// <summary>At or above this alpha a pixel is left completely alone. It was set opaque on
    /// purpose, and a block that reads solid in Fancy should read solid under RTX.</summary>
    private const byte BlendOpaqueThreshold = 245;

    /// <summary>How invisible, and how black, a pixel has to be before it counts as a hole
    /// rather than as dark glass. Both deliberately tight - this rule rewrites a pixel's
    /// colour outright, so it may only fire where there is demonstrably no artwork to
    /// destroy.</summary>
    private const byte BlendHoleAlpha = 8;
    private const byte BlendHoleChannel = 8;

    /// <summary>The share of white light a fully-saturated pixel should transmit, as a
    /// fraction of full. 0.30 is not arbitrary: it takes a pure (255,0,0) to (255,77,77)
    /// as an example. </summary>
    private const double BlendMinTransmission = 0.4;

    /// <summary>
    /// Exponent on the saturation gate. Below 1 makes the gate rise quickly, so anything
    /// genuinely chromatic gets most of the correction while near-greys still get almost
    /// none; above 1 spares near-greys harder at the cost of under-serving mid-saturation
    /// colours.
    ///
    /// 0.5 rather than a plain 1.0 because 1.0 measurably under-served the middle. An olive
    /// (95,107,42) - saturation 0.61, transmitting 16% - came out lifted by four levels,
    /// which is nothing; at 0.5 it reaches 60, a 43% gain, while a barely-tinted dark grey
    /// (50,48,45) is still left alone because its target lands below what it already
    /// transmits. Genuinely coloured glass is the case this pass exists for.
    /// </summary>
    private const double BlendChromaBias = 0.3;

    /// <summary>
    /// Rewrites one colour texture to render correctly under the blend material instance.
    ///
    /// Whether a texture wants this is materials.json's call (`blend_suitable`), not a name
    /// match here - see AlchitexPipeline.RunWaterGlassPass. The legacy version substring-matched
    /// "glass" and "copper_grate", which both missed the rest of the fixed set (slime, honey,
    /// tinted and hard glass) and caught anything else with "glass" in its name.
    ///
    /// Reads once, writes once, as a real .tga beside the original (§4.4: .tga outranks
    /// .png/.jpg/.jpeg, so the game reads ours and the source is simply never loaded again;
    /// the source stays put for anything in this app still holding its path).
    /// </summary>
    public static void MakeBlendSuitable(string imagePath)
    {
        using var bitmap = Helpers.ReadImage(imagePath, maxOpacity: false);

        // Scoped so the writable view is released before WriteImageAsTGA reads the bitmap.
        using (var fb = new FastBitmap(bitmap, writable: true))
        {
            ApplyBlendAdaptation(fb, FindTranslucentAlphaFloor(fb));
        }

        Helpers.WriteImageAsTGA(bitmap, Path.ChangeExtension(imagePath, ".tga"));
    }

    /// <summary>The lowest alpha among the translucent pixels, or -1 if there are none - a
    /// fully opaque texture is left exactly as it was, which is the right answer for a solid
    /// slime block.</summary>
    private static int FindTranslucentAlphaFloor(FastBitmap fb)
    {
        var floor = -1;

        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                int alpha = fb[x, y].A;
                if (alpha >= BlendOpaqueThreshold) continue;
                if (floor < 0 || alpha < floor) floor = alpha;
            }
        }

        return floor;
    }

    private static void ApplyBlendAdaptation(FastBitmap fb, int alphaFloor)
    {
        // Subtracting the floor from every translucent pixel slides the whole range down
        // until its most transparent pixel reaches 0, without compressing it - so "which part
        // of this texture is more see-through than which" is preserved exactly while the
        // texture as a whole becomes as transparent as its own composition allows.
        var shift = Math.Max(alphaFloor, 0);

        for (var y = 0; y < fb.Height; y++)
        {
            for (var x = 0; x < fb.Width; x++)
            {
                var c = fb[x, y];
                if (c.A >= BlendOpaqueThreshold) continue;

                var alpha = (byte)(c.A - shift);

                if (alpha <= BlendHoleAlpha &&
                    c.R <= BlendHoleChannel && c.G <= BlendHoleChannel && c.B <= BlendHoleChannel)
                {
                    // A hole, not a colour. White at this alpha is a clear opening; black at
                    // this alpha is a pane you cannot see through, which is what a grate's
                    // gaps used to render as.
                    fb[x, y] = Color.FromArgb(alpha, 255, 255, 255);
                    continue;
                }

                var (r, g, b) = LiftTransmission(c.R, c.G, c.B);
                fb[x, y] = Color.FromArgb(alpha, r, g, b);
            }
        }
    }

    /// <summary>
    /// Raises how much white light a colour transmits, without changing what colour it is.
    ///
    /// **The whole operation is a lerp toward white, and that is the idea.** Mixing every
    /// channel toward 255 by the same fraction scales every channel *difference* by the same
    /// (1 - k), and hue in HSV is defined purely by the ratios between those differences - so
    /// the hue comes out bit-for-bit unchanged while saturation falls and value rises. The
    /// three things wanted here are one operation, not three that have to be reconciled.
    ///
    /// **How far** is set by the smallest channel, because that is literally the achromatic
    /// transmission - the amount of light that gets through regardless of hue. It is lifted
    /// toward BlendMinTransmission.
    ///
    /// **How much of that lift applies** is gated on saturation, and that gate is what keeps
    /// this from wrecking half the textures it touches:
    ///
    ///   - A saturated pixel is dark *because it is absorbing*, so it gets the full lift.
    ///   - A grey pixel absorbs nothing selectively; it is dark because an artist made it
    ///     dark. Gate = 0, so it is returned untouched. Grey, smoked and silver glass keep
    ///     what makes them distinct, and a black region inside a lime texture stays black.
    ///   - Anything already transmitting enough is returned untouched whatever its
    ///     saturation, since the lift is measured against the shortfall and there isn't one.
    ///
    /// That is the dichotomy stated as one formula rather than as a special case, which
    /// matters because a Minecraft texture is free to contain both kinds of pixel at once.
    /// </summary>
    private static (byte R, byte G, byte B) LiftTransmission(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));

        // Pure black with no hue to preserve, and not transparent enough to be a hole (that
        // was handled above). Deliberately opaque-black glass; leave it.
        if (max == 0) return (r, g, b);

        var saturation = (max - min) / (double)max;
        var target = BlendMinTransmission * 255.0 * Math.Pow(saturation, BlendChromaBias);

        if (min >= target) return (r, g, b);

        var k = (target - min) / (255.0 - min);
        return (MixToWhite(r, k), MixToWhite(g, k), MixToWhite(b, k));
    }

    private static byte MixToWhite(byte channel, double k)
        => (byte)Math.Clamp((int)Math.Round(channel + k * (255 - channel)), 0, 255);

    #endregion

    #region Manifest

    private const string AuthorName = "Cubeir";
    private const string MetadataUrl = "https://github.com/vanilla-rtx/Vanilla-RTX-App";
    private const string LicenseNotice = "Alchitex license applies to files generated by RTX Reactor.";
    private const string NameSuffix = " §r-§a RTX§r";

    // Reading a pack file tolerantly - "//" comments, trailing commas and duplicate keys,
    // all of which real hand-edited manifests carry and Bedrock's own reader accepts - is
    // MinecraftJson's job, shared with every other module in the app. Note in particular
    // that plain JsonNode.Parse cannot stand in for it: it *succeeds* on a duplicate key
    // and then throws ArgumentException at the first enumeration, when JsonObject builds
    // its backing dictionary. MinecraftJson rebuilds from a JsonDocument instead, so a
    // later duplicate simply overwrites the earlier one - last-one-wins, the same
    // resolution Bedrock applies.
    private static JsonObject? SafeParseJsonObject(string text) => MinecraftJson.ParseObject(text);

    /// <summary>
    /// Updates manifest.json in place: new header/module uuids, an RTX tag appended to the
    /// name/description, the "raytraced" capability added (and "pbr" dropped), Alchitex
    /// metadata, and format_version bumped to at least 2.
    ///
    /// <para>A pack that only has a legacy pack_manifest.json has it promoted first (see
    /// <see cref="PromoteLegacyManifest"/>), then goes through every edit below like any other;
    /// the legacy file is removed once manifest.json is written.</para>
    ///
    /// <para><b>What comes in goes out, plus exactly the edits listed above.</b> This method
    /// is not a manifest fixer-upper. It does not normalise, upgrade, complete or repair
    /// anything it was not asked to change: a field's value, its JSON kind, the order of
    /// keys, the order of array entries and the presence or absence of every field this
    /// method doesn't name are all the pack author's business, and they round-trip
    /// untouched. If a manifest arrives broken it leaves broken - diagnosing our own output
    /// is impossible if we are also silently rewriting things nobody asked us to.</para>
    ///
    /// <para>This rule was learned the hard way. An <c>EnsureMinEngineVersion</c> step used to
    /// force <c>header.min_engine_version</c> to the int triplet <c>[1,21,50]</c> whenever it
    /// wasn't already an int array of length 3 - which meant a SemVer *string* was rewritten
    /// as an array, and an absent field was invented from nothing. That silently broke every
    /// format_version 3 pack, because v3 accepts only the string form: MultiPixel came in
    /// with <c>"min_engine_version": "1.20.60"</c> and went out with <c>[1,21,50]</c>, which
    /// the game rejects. Nothing about producing an RTX pack requires touching that field, so
    /// the step is gone rather than made format-aware. Branching on format_version would have
    /// bought a second code path to maintain for a change we never needed to make.</para>
    ///
    /// <para>format_version is the one bump that stays, and it is not an exception to the
    /// rule: capabilities are a v2+ concept, so a v1 manifest that kept v1 would silently
    /// ignore the "raytraced" capability this method just added. It is the cost of an edit we
    /// do make, not an uninvited improvement. A v2 or v3 manifest is left exactly where it
    /// is. The bump brings the one field v2 requires that v1 doesn't - see
    /// <see cref="EnsureFormatVersion"/>.</para>
    ///
    /// Every field access below degrades gracefully instead of throwing on a missing or
    /// unexpectedly-shaped value (a quoted "format_version": "2" instead of a number, a
    /// missing/empty "modules" array, a "capabilities" that's some other JSON kind
    /// entirely, etc.) - manifest.json in the wild comes from many different tools across
    /// years of format evolution, so this can't assume any of it is well-formed beyond the
    /// minimum needed to identify header/modules. Anything that can't be salvaged just
    /// skips the update and logs why, same as a missing file - it never leaves a
    /// half-written manifest.json behind (the write only happens once, at the very end,
    /// after every field has already been resolved successfully).
    ///
    /// Returns the pack's final header.name (post RTX-suffix) on success, so the caller
    /// can report the pack's *actual* in-game display name rather than guessing from the
    /// filesystem - null if the update was skipped or failed for any reason.
    /// </summary>
    public static string? UpdateManifest(string packRoot, string appVersion)
    {
        var manifestPath = Path.Combine(packRoot, PackManifest.ModernFileName);
        var legacyPath = Path.Combine(packRoot, PackManifest.LegacyFileName);
        var fromLegacy = !File.Exists(manifestPath) && File.Exists(legacyPath);

        if (!fromLegacy && !File.Exists(manifestPath))
        {
            Trace.WriteLine($"[ALCHITEX] No manifest.json or pack_manifest.json found at '{packRoot}' - skipping manifest update.");
            return null;
        }

        try
        {
            var root = fromLegacy
                ? PromoteLegacyManifest(legacyPath)
                : SafeParseJsonObject(File.ReadAllText(manifestPath));
            if (root == null)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' doesn't parse to a JSON object at its root - skipping manifest update.");
                return null;
            }

            if (root["header"] is not JsonObject header)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' has no valid \"header\" object - skipping manifest update.");
                return null;
            }

            if (root["modules"] is not JsonArray modulesArray
                || modulesArray.FirstOrDefault(m => m is JsonObject) is not JsonObject module)
            {
                Trace.WriteLine($"[ALCHITEX] '{manifestPath}' has no valid \"modules\" entry - skipping manifest update.");
                return null;
            }

            EnsureFormatVersion(root, header);

            var (resolvedName, resolvedDescription, wasPlaceholder) = ResolvePackName(header, manifestPath);

            header["uuid"] = Guid.NewGuid().ToString();
            module["uuid"] = Guid.NewGuid().ToString();

            var tag = $"RTX Reactor {appVersion}";
            string finalName;

            if (wasPlaceholder)
            {
                var description = string.IsNullOrEmpty(resolvedDescription) ? tag : $"{resolvedDescription}\n{tag}";
                header["description"] = description;
                module["description"] = description;
                finalName = resolvedName + NameSuffix;
                header["name"] = finalName;
            }
            else
            {
                header["description"] = AppendLine(MinecraftJson.GetString(header["description"]), tag);
                module["description"] = AppendLine(MinecraftJson.GetString(module["description"]), tag);
                finalName = (MinecraftJson.GetString(header["name"]) ?? resolvedName) + NameSuffix;
                header["name"] = finalName;
            }

            EnsureMetadata(root);
            EnsureCapability(root, "raytraced", removeCapability: "pbr");

            MinecraftJson.WriteIndented(manifestPath, root);

            // Only once manifest.json is safely written. Left behind, it would be a second
            // manifest in the output still carrying the source pack's pack_id - the same
            // identity as the pack this one was generated from.
            if (fromLegacy)
                File.Delete(legacyPath);

            return finalName;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to update manifest.json at '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds a format_version 2 manifest, in memory, from a legacy <c>pack_manifest.json</c>, for
    /// <see cref="UpdateManifest"/> to finish exactly as it finishes any other manifest. Null if
    /// the file can't be read as a manifest at all.
    ///
    /// <para><b>Why this exists.</b> A legacy manifest cannot carry the edits UpdateManifest has to
    /// make: it has no <c>format_version</c> and no <c>capabilities</c>, so there is nowhere to
    /// declare <c>raytraced</c>, and its identity is <c>header.pack_id</c>, which the modern path
    /// never rerolls. Left alone, the output kept the source pack's identity and declared nothing -
    /// an RTX pack the game didn't know was one, sharing a UUID with the pack it came from. Like
    /// the format_version bump, this is the cost of an edit we do make, not a tidy-up.</para>
    ///
    /// <para><b>Rebuilt from a template, not converted field by field.</b> The two layouts share
    /// almost nothing: identity, modules and version all live somewhere else and in a different
    /// shape. What carries over is what the pack's author actually expressed - its name, its
    /// description, its version, and each module's type and description. Everything
    /// UpdateManifest is about to overwrite anyway (UUIDs, metadata, capabilities) is not
    /// carried, and legacy fields with no modern meaning are dropped.</para>
    ///
    /// <para><b><c>min_engine_version</c> is always <see cref="LegacyMinEngineVersion"/>.</b>
    /// A legacy manifest has no such field and nothing in it says which game version it targets,
    /// so there is no value to carry; this is a fixed default rather than a guess about the pack.
    /// Written as an int array, the form format_version 2 takes. It does not contradict §4.18's
    /// rule against touching this field: that rule protects a value the author wrote, and here
    /// the author wrote none and the manifest is being built from scratch. A modern manifest's
    /// <c>min_engine_version</c>, present or absent, is still never touched.</para>
    ///
    /// <para>Versions become the modern three-integer form: an array is taken as-is, a string like
    /// <c>"1.4.4.1"</c> keeps its first three numeric parts, and anything unreadable becomes
    /// <c>[1, 0, 0]</c>. A pack with no module, or no resources module, gets one, since it
    /// reached Alchitex as a resource pack.</para>
    /// </summary>
    private static readonly int[] LegacyMinEngineVersion = { 1, 21, 30 };

    private static JsonObject? PromoteLegacyManifest(string legacyPath)
    {
        var legacy = PackManifest.FromFile(legacyPath);
        if (legacy?.Header is null)
        {
            Trace.WriteLine($"[ALCHITEX] '{legacyPath}' isn't a readable legacy manifest - skipping manifest update.");
            return null;
        }

        var version = legacy.VersionArray is { Length: > 0 } array
            ? ToVersionTriplet(array)
            : ParseVersionTriplet(legacy.VersionString);

        var header = new JsonObject();
        if (legacy.HeaderName is { } name) header["name"] = name;
        if (legacy.HeaderDescription is { } description) header["description"] = description;
        header["uuid"] = Guid.NewGuid().ToString();
        header["version"] = IntArray(version);
        header["min_engine_version"] = IntArray(LegacyMinEngineVersion);

        var modules = new JsonArray();
        foreach (var legacyModule in legacy.Modules)
        {
            var moduleVersion = MinecraftJson.GetIntArray(legacyModule.Node["version"]) is { Length: > 0 } moduleArray
                ? ToVersionTriplet(moduleArray)
                : ParseVersionTriplet(MinecraftJson.GetString(legacyModule.Node["version"]));

            var module = new JsonObject();
            if (legacyModule.Description is { } moduleDescription) module["description"] = moduleDescription;
            module["type"] = legacyModule.Type ?? "resources";
            module["uuid"] = Guid.NewGuid().ToString();
            module["version"] = IntArray(moduleVersion);
            // The cast selects the non-generic Add(JsonNode?); a bare JsonObject binds to the reflective Add<T> and trips the trimmer (IL2026)
            modules.Add((JsonNode)module);
        }

        if (!legacy.HasResourceModule)
        {
            var module = new JsonObject();
            if (legacy.HeaderDescription is { } moduleDescription) module["description"] = moduleDescription;
            module["type"] = "resources";
            module["uuid"] = Guid.NewGuid().ToString();
            module["version"] = IntArray(version);
            // First, because UpdateManifest edits the first module and this is the one that matters.
            modules.Insert(0, module);
        }

        Trace.WriteLine($"[ALCHITEX] Promoting legacy '{legacyPath}' to a format_version 2 manifest.json");

        return new JsonObject
        {
            ["format_version"] = 2,
            ["header"] = header,
            ["modules"] = modules,
        };

        static int[] ToVersionTriplet(int[] parts) =>
            Array.Exists(parts, p => p < 0) ? new[] { 1, 0, 0 } : new[] { At(parts, 0), At(parts, 1), At(parts, 2) };

        static int At(int[] parts, int index) => index < parts.Length ? parts[index] : 0;

        static int[] ParseVersionTriplet(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new[] { 1, 0, 0 };

            var parts = text.Trim().Split('.');
            var result = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (i >= parts.Length) break;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
                    return new[] { 1, 0, 0 };
            }
            return result;
        }

        // JsonValue.Create(int) rather than the generic Add<T>: the generic path reflects and
        // this file ships trimmed (see EnsureMetadata).
        static JsonArray IntArray(int[] values)
        {
            var array = new JsonArray();
            foreach (var value in values) array.Add((JsonNode?)JsonValue.Create(value));
            return array;
        }
    }

    /// <summary>Best-effort int coercion for a JSON node that's supposed to be a number
    /// but - in the wild - is sometimes a numeric string instead (e.g.
    /// "format_version": "2"). Returns null if the node is missing, null, or genuinely
    /// not coercible to an int.</summary>
    private static int? TryGetInt(JsonNode? node) => MinecraftJson.GetInt(node);

    /// <summary>
    /// Raises format_version to 2 when it is below that, and gives the header the
    /// <c>min_engine_version</c> v2 requires when the manifest it raised had none - the game
    /// refuses a v2 pack without one, and v1 never needed it, so every v1 pack arrives without
    /// it. Like the bump itself this is owed to an edit we make, which is why it happens only
    /// on the bump and only when the field is absent: a v1 manifest that already carries one
    /// keeps the author's value, and a v2/v3 manifest is never touched.
    ///
    /// <para>The value is <see cref="LegacyMinEngineVersion"/>, the same fixed default a
    /// promoted legacy manifest gets, for the same reason: nothing in the pack says what it
    /// targets. It goes straight after <c>header.version</c>, where the game's own manifests
    /// keep it.</para>
    /// </summary>
    private static void EnsureFormatVersion(JsonObject root, JsonObject header)
    {
        var current = TryGetInt(root["format_version"]);
        if (current is not (null or < 2)) return;

        root["format_version"] = 2;

        if (header.ContainsKey("min_engine_version")) return;

        var minEngine = new JsonArray();
        foreach (var part in LegacyMinEngineVersion) minEngine.Add((JsonNode?)JsonValue.Create(part));

        var versionIndex = header.IndexOf("version");
        if (versionIndex >= 0)
            header.Insert(versionIndex + 1, "min_engine_version", minEngine);
        else
            header["min_engine_version"] = minEngine;
    }

    /// <summary>
    /// If header.name is the literal placeholder "pack.name" (meaning the real name/
    /// description live in a .lang file instead), resolves them from texts/en_US.lang (or
    /// en_GB.lang, or whatever .lang is available) and strips section-sign (§) formatting
    /// codes from the resolved name.
    /// </summary>
    private static (string name, string? description, bool wasPlaceholder) ResolvePackName(JsonObject header, string manifestPath)
    {
        var rawName = MinecraftJson.GetString(header["name"]) ?? string.Empty;

        if (!rawName.Equals("pack.name", StringComparison.OrdinalIgnoreCase))
            return (rawName, null, false);

        var textsFolder = Path.Combine(Path.GetDirectoryName(manifestPath)!, "texts");
        if (!Directory.Exists(textsFolder))
            return (rawName, null, true);

        var langFiles = Directory.GetFiles(textsFolder, "*.lang");
        if (langFiles.Length == 0)
            return (rawName, null, true);

        var englishFile = langFiles.FirstOrDefault(f => f.EndsWith("en_US.lang", StringComparison.OrdinalIgnoreCase))
                        ?? langFiles.FirstOrDefault(f => f.EndsWith("en_GB.lang", StringComparison.OrdinalIgnoreCase))
                        ?? langFiles[0];

        string? name = null;
        string? description = null;

        foreach (var line in File.ReadAllLines(englishFile))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("pack.name=", StringComparison.OrdinalIgnoreCase))
                name = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            else if (trimmed.StartsWith("pack.description=", StringComparison.OrdinalIgnoreCase))
                description = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
        }

        return (Helpers.StripMinecraftFormatting(name ?? rawName), description, true);
    }

    private static string AppendLine(string? existing, string line)
        => string.IsNullOrEmpty(existing) ? line : $"{existing}\n{line}";

    /// <summary>
    /// Credits us in metadata.authors (kept, ahead of whoever the pack already credited),
    /// and sets metadata.license/metadata.url. Does NOT set metadata.generated_with any
    /// more - the game itself warns "The property '/metadata/generated_with' is not used
    /// for this type of content. The field will be ignored." for resource packs, so it was
    /// pure dead weight that existed only to trip that warning.
    ///
    /// license is overwritten unconditionally with LicenseNotice, same as url already was -
    /// whatever license the source pack declared doesn't apply to the PBR content we just
    /// added throughout it, so it's replaced rather than merged/appended.
    /// </summary>
    private static void EnsureMetadata(JsonObject root)
    {
        // `as` rather than `?.AsObject()`/`?.AsArray()` throughout this method - a
        // present-but-wrong-JSON-kind value (e.g. a malformed pack's "metadata": "none")
        // degrades to "treat as missing and replace" instead of throwing.
        if (root["metadata"] is not JsonObject metadata)
        {
            metadata = new JsonObject();
            root["metadata"] = metadata;
        }

        if (metadata["authors"] is not JsonArray authors)
        {
            authors = new JsonArray();
            metadata["authors"] = authors;
        }

        var alreadyCredited = authors.Any(a =>
            string.Equals(MinecraftJson.GetString(a), AuthorName, StringComparison.OrdinalIgnoreCase));
        if (!alreadyCredited)
        {
            // JsonValue.Create(string) rather than the generic Add<T>/Insert<T>: those go
            // through a reflection path the trimmer flags (IL2026), and this whole file
            // ships in a PublishTrimmed Release build. Same reason the materials.json
            // shapes go through AlchitexJsonContext.
            var wasEmpty = authors.Count == 0;
            authors.Insert(0, JsonValue.Create(AuthorName));
            if (wasEmpty)
                authors.Add((JsonNode?)JsonValue.Create("Original Authors of Resource Pack"));
        }

        metadata["license"] = LicenseNotice;
        metadata["url"] = MetadataUrl;
    }

    /// <summary>
    /// Adds <paramref name="capability"/> to the manifest's capabilities, and drops
    /// <paramref name="removeCapability"/> if it's there. Everything else the pack declared
    /// (e.g. "chemistry") is preserved - this only ever touches the two capabilities it's
    /// told about.
    ///
    /// The removal exists for "pbr" (Vibrant Visuals): RTX Reactor produces RTX-capable
    /// packs, full stop. Whatever Vibrant Visuals content a source pack claimed, it either
    /// never existed or was just stripped and regenerated as RTX PBR (see PbrStripper), so
    /// leaving "pbr" declared would advertise a capability this pack no longer backs.
    /// </summary>
    private static void EnsureCapability(JsonObject root, string capability, string? removeCapability = null)
    {
        // GetStringArray drops non-string entries in a malformed capabilities array and
        // returns empty for a "capabilities" that is some other JSON kind entirely, so a
        // present-but-wrong-kind value degrades to "treat as missing" as it always did.
        //
        // Built by walking the pack's own list in order rather than through a HashSet. A set
        // reorders, and the order a pack declared its capabilities in is not ours to change -
        // same rule as the rest of this method. Duplicates the pack itself wrote are kept for
        // the same reason; only the one capability we were told to remove goes.
        var existing = MinecraftJson.GetStringArray(root["capabilities"]);
        var kept = new List<string>(existing.Count + 1);
        var alreadyPresent = false;

        foreach (var declared in existing)
        {
            if (removeCapability != null && declared.Equals(removeCapability, StringComparison.OrdinalIgnoreCase))
                continue;

            if (declared.Equals(capability, StringComparison.OrdinalIgnoreCase))
                alreadyPresent = true;

            kept.Add(declared);
        }

        if (!alreadyPresent) kept.Add(capability);

        root["capabilities"] = MinecraftJson.StringArray(kept);
    }

    #endregion

    #region Terrain Texture

    /// <summary>
    /// Makes sure every terrain_texture.json in the pack (root pack + every subpack - see
    /// AlchitexStaging.DiscoverTexturesFolders) has mipmaps fully disabled: num_mip_levels
    /// forced to 0, not 1 - 0 is "off", and full-off is the goal, not merely reduced. It's
    /// *set*, not adjusted: every field in this file is optional, so a pack that simply
    /// never declared num_mip_levels is the common case, not an edge one - the rebuilt
    /// object always seeds it and then copies the original's other fields in over the top.
    /// "padding" is dropped the same way if present: Mojang has since removed that property
    /// from the game entirely, so a pack still carrying it is just stale.
    ///
    /// No longer touches "variations"/texture selection. That existed in legacy only to
    /// work around a game bug where texture-variation slots couldn't load PBR data -
    /// Bedrock RTX can do that now, so forcibly collapsing variations here would just be
    /// actively wrong.
    ///
    /// A textures/ folder with no terrain_texture.json at all (or one that's empty/doesn't
    /// parse to a JSON object) gets a minimal one written from scratch, so every folder
    /// PBR was generated into ends up with mipmaps disabled one way or another.
    /// resourcePackName should be the pack's final header.name (AlchitexPipeline already
    /// has it from UpdateManifest's return value); falls back to the pack's folder name,
    /// then a random string, if that's unavailable.
    ///
    /// Each folder is handled independently in its own try/catch, parsed with the same
    /// comment/trailing-comma/duplicate-key tolerance as UpdateManifest (see
    /// SafeParseJsonObject) - this is one post-process step among several, so one folder's
    /// malformed JSON must not fail the whole pack.
    /// </summary>
    public static void UpdateTerrainTexture(string packRoot, string? resourcePackName = null)
    {
        var texturesFolders = AlchitexStaging.DiscoverTexturesFolders(packRoot);
        var fallbackName = ResolveResourcePackName(resourcePackName, packRoot);

        foreach (var texturesFolder in texturesFolders)
        {
            var path = Path.Combine(texturesFolder, "terrain_texture.json");
            try
            {
                JsonObject? existing = null;
                if (File.Exists(path))
                {
                    existing = MinecraftJson.ParseObjectFile(path);
                    if (existing == null && new FileInfo(path).Length > 0)
                        Trace.WriteLine($"[ALCHITEX] '{path}' doesn't parse to a JSON object at its root - rebuilding it.");
                }

                JsonObject root;
                if (existing != null)
                {
                    root = new JsonObject { ["num_mip_levels"] = 0 };
                    foreach (var kvp in existing)
                    {
                        if (kvp.Key is "num_mip_levels" or "padding") continue;
                        root[kvp.Key] = kvp.Value?.DeepClone();
                    }
                }
                else
                {
                    root = new JsonObject
                    {
                        ["resource_pack_name"] = fallbackName,
                        ["texture_name"] = "atlas.terrain",
                        ["num_mip_levels"] = 0,
                    };
                }

                MinecraftJson.WriteIndented(path, root);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Failed to update terrain_texture.json at '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>Best-effort "resource_pack_name" for a freshly-constructed
    /// terrain_texture.json: the pack's real final name if the caller has it (from
    /// UpdateManifest), else its folder name, else a random string - anything valid, since
    /// the game doesn't treat this field as meaningful beyond being present. Sanitized down
    /// to plain a-z0-9 (see SanitizeResourcePackName) - unlike header.name, this is a
    /// backend identifier, not display text, so it shouldn't be carrying section-sign
    /// formatting codes, spaces, or punctuation.</summary>
    private static string ResolveResourcePackName(string? resourcePackName, string packRoot)
    {
        var fromManifest = SanitizeResourcePackName(resourcePackName);
        if (fromManifest.Length > 0) return fromManifest;

        var folderName = Path.GetFileName(packRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var fromFolder = SanitizeResourcePackName(folderName);
        if (fromFolder.Length > 0) return fromFolder;

        return $"pack{Guid.NewGuid():N}";
    }

    /// <summary>Strips everything down to plain lowercase a-z0-9 - section-sign formatting
    /// codes (via Helpers.StripMinecraftFormatting), spaces, dashes, punctuation, all of it.
    /// Returns empty string (never null) if nothing alphanumeric survives, so callers can
    /// chain fallback candidates with a simple Length check.</summary>
    private static string SanitizeResourcePackName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var stripped = Helpers.StripMinecraftFormatting(name);
        return new string(stripped.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    #endregion

    #region Housekeeping

    /// <summary>
    /// Clears out the pack's game-generated bookkeeping files and writes fresh ones - an
    /// empty contents.json at the root plus a textures_list.json per textures folder (see
    /// Helpers.RegenerateBookkeepingFiles, shared with the Vanilla RTX pack updater).
    ///
    /// Deleting alone isn't enough here: whatever the source pack shipped describes a pack
    /// that no longer exists, since we've just added a MERS/normal/heightmap set beside
    /// nearly every color texture, plus grey water and possibly fog. Regenerating is in
    /// scope precisely because this pack is our output - contrast plain imports, which are
    /// left exactly as their author built them.
    ///
    /// Must stay the last thing that touches the pack's files: it snapshots what's on disk,
    /// so anything written afterwards would be missing from the list it just built.
    /// </summary>
    public static void RegenerateBookkeepingFiles(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return;

        Helpers.RegenerateBookkeepingFiles(packRoot);
    }

    #endregion

    #region Pack Icon

    private const int IconCanvasSize = 512;
    private const int IconContentSize = 416; // original icon's size once centered on the canvas
    private const int IconBadgeSize = 48; // must match icon_badge.png's actual pixel dimensions - also drives the accent frame's band width

    private const string PackIconName = "pack_icon";
    private const string BugPackIconName = "bug_pack_icon";

    /// <summary>
    /// The extensions an icon may arrive as, in the order one is chosen when a pack ships
    /// more than one. Same shape as §4.4's texture rule and a different list for a different
    /// reason: this is not what the game would load, because the game loads
    /// <c>pack_icon.png</c> and nothing else. It is a preference between candidates a pack
    /// author left lying around, and PNG leads it because a lossless source is the one worth
    /// reading when there is a choice.
    /// </summary>
    private static readonly string[] IconExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Regenerates a pack's icons: its pack icon always, and its bug icon for the packs that
    /// ship one.
    ///
    /// The two differ only in which colours they are drawn from, and that difference is the
    /// point of having both here: the bug icon is the one a player is looking at when
    /// something is wrong, so it reads against <see cref="ReactorAnimator.AlertPalette"/> -
    /// the same red ramp the reactor itself turns when a run fails - and takes no seasonal
    /// colours, because a festive bug report is a worse bug report.
    ///
    /// A missing pack icon is worth a log line; a missing bug icon is the normal case for
    /// almost every pack and is passed over silently.
    /// </summary>
    public static void RegeneratePackIcon(string packRoot, string alchitexAssetsPath)
    {
        RegenerateIcon(
            packRoot, PackIconName, alchitexAssetsPath,
            BuildFramePalette(ReactorAnimator.Palette, withSpecialOccasion: true),
            announceIfMissing: true);

        RegenerateIcon(
            packRoot, BugPackIconName, alchitexAssetsPath,
            BuildFramePalette(ReactorAnimator.AlertPalette, withSpecialOccasion: false),
            announceIfMissing: false);
    }

    /// <summary>
    /// One icon: the original centered on a square canvas with a gradient background
    /// (average-icon-color -> a colour of its own palette) showing through any transparent
    /// area, a per-side accent frame picked at random from <paramref name="palette"/>, and
    /// Alchitex's badge in the bottom-left corner. Ported from legacy IconDesigner off GDI+
    /// onto Magick.NET's Composite/Draw API and the `gradient:` pseudo-format.
    ///
    /// <b>The output is always <c>&lt;name&gt;.png</c>, whatever was read</b>, because
    /// <c>pack_icon.png</c> is the only thing Bedrock looks for - a pack shipping its icon as
    /// a .jpg has no icon in game, and comes out of here with one. Every other variant is
    /// deleted afterwards, so a pack that arrived with a .png beside a leftover .jpg leaves
    /// with exactly one icon file rather than our output beside somebody's stale draft.
    ///
    /// <b>Deletion happens only after the write succeeds.</b> A failure anywhere above it
    /// leaves every file the pack arrived with exactly where it was, which is the same
    /// promise §4.2 makes one level up.
    /// </summary>
    private static void RegenerateIcon(
        string packRoot, string iconName, string alchitexAssetsPath, IReadOnlyList<string> palette, bool announceIfMissing)
    {
        var variants = IconExtensions
            .Select(extension => Path.Combine(packRoot, iconName + extension))
            .Where(File.Exists)
            .ToList();

        if (variants.Count == 0)
        {
            if (announceIfMissing)
                Trace.WriteLine($"[ALCHITEX] No {iconName}.png/.jpg/.jpeg at '{packRoot}' - skipping icon regeneration.");

            return;
        }

        // First by IconExtensions order, which is the order they were collected in.
        var sourcePath = variants[0];
        var outputPath = Path.Combine(packRoot, iconName + ".png");

        var offset = (IconCanvasSize - IconContentSize) / 2;

        try
        {
            // Read wholly into memory before anything is written, which is what makes
            // overwriting the source in place safe when the source already is the .png.
            using var original = new MagickImage(sourcePath);

            var averageColor = ComputeAverageColor(original);
            var bottomColor = new MagickColor(Pick(palette));

            using var canvas = new MagickImage(MagickColors.Transparent, IconCanvasSize, IconCanvasSize);

            using (var gradient = new MagickImage($"gradient:{ToHex(averageColor)}-{bottomColor}",
                       new MagickReadSettings { Width = IconCanvasSize, Height = IconCanvasSize }))
            {
                canvas.Composite(gradient, 0, 0, CompositeOperator.Over);
            }

            using (var content = original.Clone())
            {
                content.FilterType = original.Width < IconCanvasSize && original.Height < IconCanvasSize ? FilterType.Point : FilterType.Lanczos;
                content.Resize(new MagickGeometry(IconContentSize, IconContentSize) { IgnoreAspectRatio = true });
                canvas.Composite(content, offset, offset, CompositeOperator.Over);
            }

            DrawAccentFrame(canvas, IconCanvasSize, palette);

            var badgePath = Path.Combine(alchitexAssetsPath, IconBadgeFileName);
            if (File.Exists(badgePath))
            {
                using var badge = new MagickImage(badgePath);
                canvas.Composite(badge, 0, IconCanvasSize - IconBadgeSize, CompositeOperator.Over);
            }
            else
            {
                Trace.WriteLine($"[ALCHITEX] Icon badge asset missing - expected '{badgePath}'.");
            }

            // The format comes from the extension, so naming it .png is all it takes to get a
            // PNG out of a JPEG source.
            canvas.Write(outputPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed to regenerate '{sourcePath}': {ex.Message}");
            return;
        }

        foreach (var stale in variants.Where(v => !string.Equals(v, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex)
            {
                // Cleanup, not correctness - the icon the game reads is already written and
                // correct. A locked leftover is worth a line and nothing more.
                Trace.WriteLine($"[ALCHITEX] Couldn't remove the superseded icon '{stale}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// This project's Magick.NET package is built for Q16 quantum depth (channel values
    /// are 0-65535, not 0-255 - IMagickColor&lt;ushort&gt; is the actual pixel-color type
    /// here), so raw channel values need the same &gt;&gt; 8 scale-down Helpers.ReadImage
    /// already uses elsewhere before they're usable as standard 0-255 RGB.
    /// new MagickColor(hexString) elsewhere in this region doesn't need this - it already
    /// handles quantum depth internally - only manual pixel-value math like this does.
    /// </summary>
    private static IMagickColor<ushort> ComputeAverageColor(MagickImage source)
    {
        using var tiny = source.Clone();
        tiny.Resize(1, 1);
        using var pixels = tiny.GetPixels();
        var pixel = pixels.GetPixel(0, 0);
        return pixel.ToColor() ?? MagickColors.Gray;
    }

    private static string ToHex(IMagickColor<ushort> c)
    {
        byte r = (byte)(c.R >> 8);
        byte g = (byte)(c.G >> 8);
        byte b = (byte)(c.B >> 8);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>A UI colour as a hex string Magick.NET can take. Alpha is dropped: everything
    /// drawn on an icon is opaque, and the reactor's palettes are opaque throughout.</summary>
    private static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ── Seasonal ramps ────────────────────────────────────────────────────────────────
    //
    // Each of these REPLACES the reactor's ramp outright rather than being mixed into it, and
    // each is the same shape as the two it can stand in for: five colours, brightest first,
    // darkest last, all of one family. That shape is what makes the frame read as one thing -
    // eight bands drawn at random out of a single ramp look deliberate, and eight drawn out of
    // a ramp with a handful of unrelated accents stirred through it look like a fault. Mixing
    // was tried and is what this replaced; see §4.19.
    //
    // The dates are not here and must not come back: Helpers.GetSpecialOccasionName owns when
    // each one runs, for the whole app (§2k's lamp, the pack updater's panel, the titlebar),
    // and these are only what this surface does about it. The names are its return values.
    //
    // They follow the lamp art in src/Assets/special, which is the app's existing vocabulary
    // for each occasion - christmas.on.png is icy cyan over white, pumpkin.on.png is candle
    // gold down to charred brown. Birthday departs from it deliberately: that asset is cream
    // and red, which on a frame reads as a warning rather than a celebration, so this is the
    // regal end of purple instead.

    /// <summary>Pale amethyst down to near-black aubergine.</summary>
    private static readonly string[] BirthdayPalette = { "#C9A6FF", "#9B5CF6", "#7A33D9", "#54199E", "#320C63" };

    /// <summary>Candle gold down to charred brown, after <c>pumpkin.on.png</c>.</summary>
    private static readonly string[] PumpkinPalette = { "#FFD98A", "#FFA726", "#E8751A", "#B4500F", "#6E2F0A" };

    /// <summary>Icy white down to deep glacier blue, after <c>christmas.on.png</c>. Bright on
    /// purpose - it is the one occasion whose family overlaps the default's, and only the
    /// brightness tells the two apart.</summary>
    private static readonly string[] ChristmasPalette = { "#F2FBFF", "#B8ECFF", "#6FD8FF", "#2BAEE8", "#0E6E9E" };

    /// <summary>
    /// The colours one icon is drawn from: today's seasonal ramp if it takes one, else the
    /// reactor's own.
    ///
    /// Taking the ordinary case from <see cref="ReactorAnimator"/> rather than restating it
    /// means the icon is made of the mark, by construction - the same relationship the window
    /// backdrop already has with it (§11.3) - and a change to the mark's blues reaches the icon
    /// with nothing else to update. A seasonal ramp is the one thing this file colours in
    /// itself, because a costume is by definition not derived from the thing wearing it.
    /// </summary>
    private static List<string> BuildFramePalette(IReadOnlyList<Windows.UI.Color> basePalette, bool withSpecialOccasion)
    {
        var seasonal = withSpecialOccasion
            ? Helpers.GetSpecialOccasionName() switch
            {
                "birthday" => BirthdayPalette,
                "pumpkin" => PumpkinPalette,
                "christmas" => ChristmasPalette,
                _ => null,
            }
            : null;

        return seasonal?.ToList() ?? basePalette.Select(ToHex).ToList();
    }

    /// <summary>
    /// One colour out of an icon's palette. <see cref="Random.Shared"/> rather than an
    /// instance: the icons are the only thing here that wants entropy, nothing about them
    /// needs reproducing, and two icons are drawn back to back - a per-call `new Random()`
    /// is the shape that historically produces two identical "random" results.
    /// </summary>
    private static string Pick(IReadOnlyList<string> palette) => palette[Random.Shared.Next(palette.Count)];

    /// <summary>
    /// A colour from the darker half of a ramp. Every palette here is ordered brightest first,
    /// so this is just its back half.
    ///
    /// Exists for one square: the badge is a white watermark with no outline, so a band bright
    /// enough behind it makes it disappear. That was invisible while the only ramps were the
    /// mark's navies and the alert reds, and arrived with the christmas ramp - which is
    /// near-white at the top by design, and put white-on-white under the badge two draws in
    /// five. Constraining the one square the badge occupies costs nothing anywhere else: on a
    /// ramp that is dark throughout it changes nothing at all.
    /// </summary>
    private static string PickDark(IReadOnlyList<string> palette) =>
        palette[Random.Shared.Next(palette.Count / 2, palette.Count)];

    private static void DrawAccentFrame(MagickImage canvas, int canvasSize, IReadOnlyList<string> palette)
    {
        const int band = IconBadgeSize; // frame band width matches the badge's own size, so the badge sits flush in the bottom-left corner
        var edge = canvasSize - band;

        var drawables = new Drawables()
            .FillColor(new MagickColor(Pick(palette))).Rectangle(0, band, band, edge)                 // left
            .FillColor(new MagickColor(Pick(palette))).Rectangle(edge, band, canvasSize, edge)         // right
            .FillColor(new MagickColor(Pick(palette))).Rectangle(band, 0, edge, band)                  // top
            .FillColor(new MagickColor(Pick(palette))).Rectangle(band, edge, edge, canvasSize)          // bottom
            .FillColor(new MagickColor(PickDark(palette))).Rectangle(0, edge, band, canvasSize)         // bottom-left - the badge sits here
            .FillColor(new MagickColor(Pick(palette))).Rectangle(0, 0, band, band)                      // top-left
            .FillColor(new MagickColor(Pick(palette))).Rectangle(edge, 0, canvasSize, band)             // top-right
            .FillColor(new MagickColor(Pick(palette))).Rectangle(edge, edge, canvasSize, canvasSize);   // bottom-right

        canvas.Draw(drawables);
    }

    #endregion
}
