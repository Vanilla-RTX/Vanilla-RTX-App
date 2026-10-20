# Vanilla RTX App
Everything you need to play Minecraft with ray tracing at its best and unlock its full potential, in one place.

Install and update the Vanilla RTX resource packs, give ordinary texture packs RTX support with RTX Reactor, tune any ray-traced pack to your taste, install shader mods like Supervanilla RTX and BetterRTX, swap DLSS versions, launch the game with ray tracing already switched on, and much more...  
The Vanilla RTX App ensures ray tracing is accessible and frictionless to new and experienced players.

<!-- Microsoft Store badge -->
<p align="center">
  <a href="https://apps.microsoft.com/detail/9N6PCRZ5V9DJ?referrer=appbadge&mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="400"/>
  </a>
</p>

<!-- Cover image -->
<p align="center">
<img alt="vanilla-rtx-app-cover-render" src="https://github.com/user-attachments/assets/c2a08c85-8c59-438d-8e7d-7213bee9fbf2" />
</p>

<!-- Badges -->
<p align="center">
  <a href="https://discord.gg/A4wv4wwYud">
    <img src="https://img.shields.io/discord/721377277480402985?style=flat-square&logo=discord&logoColor=F4E9D3&label=Discord&color=F4E9D3&cacheSeconds=86400"/>
  </a>
  <a href="https://ko-fi.com/cubeir">
    <img src="https://img.shields.io/badge/-support%20my%20work-F4E9D3?style=flat-square&logo=ko-fi&logoColor=F4E9D3&labelColor=555555"/>
  </a>
  <img src="https://img.shields.io/github/repo-size/vanilla-rtx/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Repo%20Size&cacheSeconds=86400"/>
  <img src="https://img.shields.io/github/last-commit/vanilla-rtx/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Last%20Commit&cacheSeconds=86400"/>
</p>

## Table of Contents

- [Quick start](#quick-start)
- [Overview](#overview--main-menu-features)
- [Get latest RTX packs](#get-latest-rtx-packs)
- [Select other packs](#select-other-packs)
- [Tuning packs](#tuning-packs)
- [RTX Reactor](#rtx-reactor)
- [Shader mod manager](#shader-mod-manager)
- [DLSS swapper](#dlss-swapper)
- [RTX LUT manager](#rtx-lut-manager)
- [Launch Minecraft RTX](#launch-minecraft-rtx)
- [Opening files with the app](#opening-files-with-the-app)
- [Settings](#settings)
- [Troubleshooting](#troubleshooting)

<!-- Second Cover Image -->
<p align="center">
  <img alt="vanilla-rtx-app-cover" src="https://github.com/user-attachments/assets/64882d50-a751-4e6b-a46d-5becb9fe190a" />
</p>

# Quick start

For anyone who just wants to quickly get ray tracing working:

1. Install the Vanilla RTX App from the [Microsoft Store](https://apps.microsoft.com/detail/9N6PCRZ5V9DJ).
2. Open it and click **Get latest RTX packs**, then **Install** Vanilla RTX (or one of its variants).
3. Once it finishes, click **Launch Minecraft RTX**.
4. In Minecraft, go to **Settings -> Global Resources** and activate the Vanilla RTX pack you installed.
5. Enter a world. That's it.

You now have ray tracing set up properly, with the latest [Vanilla RTX](https://github.com/vanilla-rtx/Vanilla-RTX) for your version of Minecraft and [fixes](https://vanillartx.net/enhancements) for as many of the game's RTX issues as possible.

> *The rest of this page is the full documentation, for when you take things further or whenever you get stuck. Continue to get an in-depth understanding of every feature.*

# Overview & main menu features

Features split into two main groups by what they touch:

- **Your packs and user data** - installing Vanilla RTX, importing or choosing packs to tuning them, RTX Reactor, and launching the game. These work on the resource packs in Minecraft's user data folder.
- **Your game installations** - the shader mod manager, the RTX LUT manager and the DLSS swapper. These replace files inside Minecraft itself, and each keeps a backup of your original files so you can always go back.

> The app detects both Minecraft installations and their user data locations automatically. If it ever fails to, see [Troubleshooting](#troubleshooting). You may also manually select other paths through [Settings](#settings).

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/fe39284b-4275-4fb6-9339-26dec8057e5b" />

## Title bar

Three buttons sit next to the app's name. Each opens a page:

- **Settings** - everything that configures the app itself. See [Settings](#settings).
- **Help** - a convenient way to browse the contents of this page while using the app.
- **Bugs** - an up-to-date [list of known Minecraft RTX bugs](https://github.com/vanilla-rtx/Minecraft-RTX-Bug-Tracking) and their status on Mojang's tracker.

> Help and Bugs can be opened on top of any feature, and closing them puts you back exactly where you were.

> Next to their title are buttons to reload the page and jump back to the top, and on the right, a search button (**Ctrl+F**). Both pages are saved for offline reading and refreshed at most once an hour; if a refresh fails, the saved copy is shown. The reload button shows a countdown for a minute after the page has been fetched, so a live button means you're reading the saved copy.

> While a feature is open, a **return button** appears at the right of the title bar, beside the window controls. It closes the feature, and so does **Esc**. Some features also put their own buttons in the title bar, next to Help and Bugs - for example the pack list's refresh button.

> The Settings button is unavailable while a feature is open or the app is busy, because several settings (like where Minecraft is installed) would change what that work is using.

## Preview

The **Preview** toggle points the whole app at Minecraft Preview instead of the regular release: installs, tuning, launching, shader mods, LUTs and DLSS all target Preview while it's on. The cached files related to the two games are kept completely separate, including each one's backups of its original files.

## Log area, the lamp and the preview images

- The **log** on the left tells you what the app is doing for transparency, warns you when something needs attention, and shows occasional announcements. It may also point you toward features worth knowing about.
- The **lamp** logo next to the app's name is the app's status light. It blinks while something long is running, and flashes when things finish, succeed or fail. It sometimes has a mind of its own - that's intended. It serves no actual purpose beyond being a fun visual cue as you interact with the app.
- Hover over almost anything and a small piece of pixel art appears in the bottom-left of the main menu, showing what that control does. Sliders show how they'll change the game's look, and toggles show a before and after.
- **Info/Announcement cards** appear throughout features, holding news, warnings and tips. The app looks for new ones when it starts, at most once an hour. Some can be dismissed for good, some for a while, and some that are important stay pinned.

# Get latest RTX packs

Installs, updates and reinstalls the three Vanilla RTX packs, delivered straight from the [Vanilla RTX repository](https://github.com/vanilla-rtx/Vanilla-RTX).

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/a5aa871a-468e-4bfc-9c58-d7283e25b58e" />

- **[Vanilla RTX](https://www.curseforge.com/minecraft-bedrock/texture-packs/vanilla-rtx)** - the default ray-traced look, covering everything, uses 16x heightmaps.
- **[Vanilla RTX Normals](https://www.curseforge.com/minecraft-bedrock/texture-packs/truly-vanilla-rtx)** - features handcrafted 16x normal maps instead, which simulate curvature by defining the direction each pixel in the texture is facing.
- **[Vanilla RTX Opus](https://www.curseforge.com/minecraft-bedrock/texture-packs/vanilla-rtx-opus)** - this variant is the two above combined together. You can find details of their differences on their respective CurseForge download pages.

For each pack you'll see the version **Installed** and the version **Available**, and one button that can have different statuses:

- **Install** - not installed yet.
- **Update** - a newer version is available.
- **Reinstall** - you're up to date. Use it to put a pack back to its original state, for example after tuning.

The **Enhancements** toggle (on by default) adds the pack's enhancements - fixes for many rendering errors and incompatibilities with ray tracing, and improvements to things like particles. They're classified as mildly non-vanilla changes, which is why you can turn them off - for instance if they clash with another pack you use.

The **refresh** button in the title bar checks everything again: it rescans the packs you have installed against the latest available versions. Use it when a release has just dropped and you'd rather not wait for the app to notice on its own. It can be used once a minute, and shows a countdown for that minute after the versions have just been fetched - so a live button is a subtle nod that the numbers on screen came from the app's cache.

> Other things worth mentioning:
> - An update replaces your existing copy of that pack, and all older versions that may be installed are cleared, leaving only the latest installation.
> - The `available` versions are remembered for a while rather than asked for every time. Opening this page uses anything fetched in the last few minutes, and the main menu's own notices can check hourly. Installing always asks the repository directly first, whatever those say, so an install never deploys a version that has just been superseded moments ago and didn't show up in the UI.
> - Downloads stay cached, so a reinstall is instant until a newer version comes out. The **Get latest RTX packs** button's icon in the main menu tells you which to expect: a sync icon when a click can install straight from the cache, and a cloud when it will download.
> - The log tells you once per session when an update is waiting.
> - Packs at the bottom are optional **add-ons and extensions** (Chemistry RTX, Creative RTX, Vanilla RTX Add-Ons, Everwinter). They're downloaded manually from their pages, need to be activated above one of the three main packs in-game, and aren't updated by the app, since they rarely change or receive updates due to their forward-compatible nature.
> - An install keeps running if you close this page, and the page shows its progress again when you come back. This was worth mentioning because other features in the app tend to terminate if you close their page.

# Select other packs

Your full list of installed resource packs, where you pick packs for tuning, exporting, deleting or RTX Reactor, and import new ones.

Each pack shows its icon, name, description, version, and tags:

| Tag | Meaning |
|---|---|
| Ray Traced | An RTX pack. The resource apck Tuner can tune its files. |
| Vibrant Visuals | Declares Vibrant Visuals support. The Tuner can tune it too, but results may vary because of the differences. |
| Incompatible with Tuner | Neither of the above. The Tuner skips it, but you can still export, delete, or run it through RTX Reactor. |
| RTX Reactor Candidate | Looks like a good fit for [RTX Reactor](#rtx-reactor) to add RTX support to! |
| Chemistry | Uses Education Edition chemistry content. Does not impact how the app treats this pack. |
| Unknown Capability | Declares a feature the app doesn't recognize. Does not impact how the app treats this pack. |

- Click packs to select them, then **Confirm selection** to return with them selected.
- The **select** menu can select all packs with a given tag, select everything, or clear the selection.
- **Import texture packs**: click to browse, or drag files onto the page. It accepts `.mcpack`, `.zip`, `.mcaddon` (every pack bundled inside, whether as `.mcpack` files or as folders), and whole folders of packs. If a pack is already installed, you're asked whether to replace it.
  - **Behavior packs** too are recognized, and you're asked whether to **Import as behavior pack** or skip it. If you select to Import it, it goes into the game's behavior packs folder properly, where the game will find it - so an add-on's resource and behavior packs both install in one go. Behavior packs don't appear in this menu's list, since the app works with resource packs.
  - **Anything else** that doesn't look like a resource pack/behavior pack asks whether to import it anyway to the resource packs folder.
  > The app essentially has a very versatile/capable importer that allows you to properly import Add-Ons in bulk too (as .mcaddon), even though that is outside the scope of what Vanilla RTX App is supposed to deal with, i.e. resource packs.
- The **refresh** button in the title bar rescans the folder and measures how much disk space each pack takes. That measuring is the only thing that shows the size badges, because it can take a while.

> Note: If the app can't find your user data, this button becomes a highlighted **Locate user data** button and temporarily serves a different purpose while you select the user data location the app must use. See [Troubleshooting](#troubleshooting).

# Tuning packs

The Tuner adjusts the look of Vanilla RTX, or any other ray-traced or Vibrant Visuals resource pack, to your preferences. Every option is built to keep a texture's original composition intact, so it nudges a pack in your direction rather than wrecking its look or inventing new materials.

**Tuning changes a pack's files permanently, and each pass stacks on the one before.** For Vanilla RTX, going back is one click: Reinstall it from [Get latest RTX packs](#get-latest-rtx-packs). For anything else, export it first so you have a copy to quickly re-import via the app.

<img alt="Vanilla RTX UI Screenshots" src="https://github.com/user-attachments/assets/3daab14b-12f5-4a14-8a5a-34c8dc8d2aad" />

## Choosing what to tune

- **Vanilla RTX, Vanilla RTX Normals, Vanilla RTX Opus** - There is a checkbox for each in the main menu. A checkbox you can't tick means that pack isn't installed; install it from Get latest RTX packs.
- **Select other packs** - opens your full pack list, to pick any other pack. See [Select other packs](#select-other-packs). Only packs that are not tagged as `Incompatible with Tuner` can be tuned.

## The options

| Option | Range | What it does |
|---|---|---|
| Fog density multiplier | 0 to 10 | Scales all air fog: `0.5` halves it, `3` triples it, `0` effectively removes it. Fogs already at zero are set to a sensible literal value instead derived from the multiplier (e.g. `5` becomes `0.5`), and anything past maximum density goes into scattering more light through the air. Underwater fog is affected too, much more gently, so underwater visibility and colors survive the original intent of the artist. |
| Emissive strength multiplier | 0.2 to 16 | Makes glowing blocks brighter or dimmer while keeping their relative brightness and look intact, even at extreme values. |
| Increase ambient lighting | on/off | Adds a small, even glow to every surface, like ambient light. Its strength follows the emissive multiplier. Too much gives a night-vision look. [1] |
| Surface 3D effect intensity | 0 to 900% | How strong normal maps (including POM) and heightmaps are. `0%` flattens surfaces, values higher than `100%` deepen the differences, while keeping each texture's own composition intact, so details are not lost. |
| Material grain offset | 0 to 64 | Adds a fine layer of noise to materials, up to the given amount. Emissive parts are touched to a lesser degree, and the pattern stays the same across animation frames and block variants, so a lit and unlit redstone lamp still match. The pattern resembles the grain in Vibrant Visuals' own textures. |
| Roughness control | -16 to 48 | Positive values make surfaces generally more rough, impacting glossy surfaces the most, and slightly reduces metalness relative to how much roughness is boosted. Negative values do the opposite: rough surfaces get more glossy, impacting surfaces more the rougher they are, and boosts metalness relative to how much roughness was reduced. Does not invent materials, keeps the composition intact. |
| Lazify surface normals | 0 to 255 | Blends the pack's normal maps and heightmaps toward a version derived from its color textures, making them less refined. `0` changes nothing, `255` is fully "lazy". The overall strength of the normal map is preserved. A small value like `1-10` adds subtle organic detail. It only works when the normal map matches the color texture's resolution, so it has no effect on Vanilla RTX Opus. |

> **[1] Increase ambient lighting marks a pack.** On every later pass, the emissive multiplier only scales the ambient light, not the pack's regular glow, so repeated passes can't compound into a blown-out pack. Use it sparingly, ideally as your last step. A warning icon next to the toggle reminds you of this.

> If you use Vanilla RTX with BetterRTX installed, fog looks quite different than in unmodded RTX. A fog multiplier of `0.5-0.7` compensates.

![fog-panel](https://github.com/user-attachments/assets/a013dc6a-bd46-41f1-b980-0620f0514588)

![street-default-vanilla-rtx](https://github.com/user-attachments/assets/19e802e9-42c6-4e70-a931-6474f5e10716)
![street-3x-emissivity-tuned-vanilla-rtx](https://github.com/user-attachments/assets/90e7d2a4-afdc-4250-9ecf-d5cc15fd9dc7)

![ambient-lighting](https://github.com/user-attachments/assets/e44fe5f8-06d0-4d43-978c-fc954f5d83e8)

## The action buttons

- **Tune selection** - applies the options above to every selected pack. While it runs it becomes **Abort tuning operation**: textures already finished keep their changes, and nothing else is touched. The app spends a few seconds gathering what it needs before changing anything, so aborting quickly after an accidental click usually means nothing was modified. Close Minecraft while tuning, or at least reload your world afterwards.
- **Export** - saves each selected pack as a `.mcpack` file, asking where for each one. Good for backups before tuning, or for sharing your tuned pack.
- **Delete** (the bin icon) - permanently deletes the selected packs from Minecraft's data folder.
- **Clear selection** - unticks every selected pack.
- **Reset** - puts every tuning option back to its default. This doesn't undo any tuning already done to a pack.

These tools are powerful on the right pack: Vanilla RTX, for instance, can be tuned to closely match the style of most other vanilla-style PBR packs, or even Mojang's own Vibrant Visuals.

# RTX Reactor

RTX Reactor gives ordinary texture packs RTX support. It takes a pack's regular textures and generates a full set of materials for it - how metallic, emissive, rough, etc. each surface is, plus normal maps or heightmaps for addd depth - along with adjustments for water and translucent textures, optional atmospheric fog, and everything else a pack needs to function decently with ray tracing.  
Results may vary, but after over 3 years of development, the algorithms responsible for this take it much further than you'd expect, and you'll likely enjoy it!

By default **your installed pack is never touched.** RTX Reactor works on a copy and installs the result as a new pack beside it, named after the original with a new icon and **- RTX** suffix. If anything goes wrong or you abort, the unfinished copy is cleaned up and nothing is left behind.

<p align="center">
  <img alt="RTX Reactor UI Image" src="https://github.com/user-attachments/assets/8f8c2ea2-d2a5-4ef5-9746-a6be78ea461b" />
</p>

### Getting started

1. Select one or more packs from [Select other packs](#select-other-packs) menu. Packs tagged **RTX Reactor Candidate** likely work best.
2. Click **RTX Reactor**.
3. The first time, and again after app updates that change it, you're asked to accept RTX Reactor's license (see below).
4. Choose your options and press the giant reactor button to generate.

When it's done, activate the new pack in-game in place of the original. Old packs, including ones from before Minecraft's current pack format, are converted as they go.

### The queue

The packs you selected wait in a queue on the top and move across to the right as they are being worked on. Hover a pack sitting in the queue and click it to take it out of the queue for now. If a pack isn't a candidate, or already declares its own RTX or Vibrant Visuals support, you're asked about it before it's processed:

- **Not a candidate** - it may have too few block textures to work with. You can **Generate anyway**; there's nothing to lose, but it might not result in anything worthwhile.
- **Already declares RTX or Vibrant Visuals** - RTX Reactor can **remove and regenerate** all of its PBR textures. That's worth it for packs that claim support for RTX or VV but ship very little or no actual files. If the pack's own PBR work is good, RTX Reactor can still strip it and regenerate the PBR entirely for your curiosity.

### Options

- **Secondary PBR texture** - how surface depth is generated:
  - **Automatic** (default) - heightmaps for low-resolution packs (lower than 32x), normal maps for everything else.
  - **Normal map** - works at any resolution.
  - **Heightmap** - best for low-resolution packs. Packs above 64x get normal maps even if you set this option explicitly, because the game's heightmap effect becomes too thin to see at these resolutions.
  - **None (flat)** - no depth, only the metalness, emissive, roughness and subsurface scattering materials.
- **Add per-biome RTX atmospheric configs** (on by default) - adds Vanilla RTX's per-biome fog. Recommended for most packs: without it there's likely no fog, light shafts, or per-biome variation like different water colors. It may replace a pack's own biome fog settings, however.
- **Uninstall the original pack** (off by default) - deletes each original pack once its RTX version is finished and installed. Nothing is deleted if generation fails or is stopped.

### The license

RTX Reactor used to be an independent premium project, but it is now free to use. The output is covered by its own license: **you can use them yourself and share them for free, but you can't sell them** or put them behind any paywall (e.g. Marketplace, ad-gated downloads, paid communities). For commercial use, you must contact Cubeir first. The full license is shown and you must accept it before using RTX Reactor, it is also in the [repository](https://github.com/vanilla-rtx/Vanilla-RTX-App/blob/main/src/Modules/Alchitex/ALCHITEX_LICENSE.txt) for you to read it at any time.

# Shader mod manager

Installs shader mods - replacements for Minecraft RTX's own shaders that change how ray tracing looks. It offers two: **Supervanilla RTX**, and presets of [BetterRTX](https://bedrock.graphics), an unofficial third-party mod. The first time you open it you'll see a notice about where these files come from; accept it to continue.

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/022acfe7-6b5e-4135-a11d-acfaea4d9f11" />

The list is split into three sections:

- **Default** - **Default RTX**, a backup of your game's own shader files, taken the first time you open this manager. Click it to roll back to the unmodded look at any time.
- **Supervanilla RTX** - **Get latest Supervanilla RTX** downloads the newest build made for the game you're targeting (Release or Preview). Then it'll be ready to install; when a newer build comes out, the button comes back offering it. Builds you import yourself are listed here too, newest first. Only builds for the game you're targeting are shown - Preview builds (named *Supervanilla-RTX-Preview-...*) appear while the app targets Minecraft Preview, the others while it targets Release.
- **BetterRTX** - an up-to-date list of pre-made presets from the BetterRTX website, followed by any BetterRTX presets you imported. Click a preset to download it (several can queue up), then click again to install it.

The installed preset is highlighted, whichever section it's in. Along the bottom:

- **Add customized preset** - import `.rtpack` files, by browsing, dragging them onto the button, or opening them with the app from File Explorer. Each lands in the section it belongs to - a file with Supervanilla RTX (or SVRTX) in its name goes under Supervanilla RTX, anything else under BetterRTX. Imported presets, and every Supervanilla RTX build, can be deleted with the button (bin icon) beside them.
- **Supervanilla RTX for older versions** - opens the Supervanilla RTX downloads page inside the app, where you can pick a build for any game version. Download one, click **Done**, and it's imported automatically.
- **Create your own preset** - opens the BetterRTX preset creator inside the app. Customize a preset, export it as `.rtpack`, then click **Done** and it will be imported automatically!
- **Refresh** (title bar) - clears downloaded and imported presets and fetches both lists again. If one of the two sites can't be reached, its presets are left as they were. It never clears the Default RTX preset. It can be used once a minute, and shows a countdown for that minute after it has been used.

The app goes to great lengths to make sure you never install stale files, and that your Default backup always matches your game - mostly without you having to do anything:

- **When Minecraft updates**, Default RTX is re-taken from the updated game, and every Supervanilla RTX build for that game (Release or Preview) is cleared, so you can fetch the one made for the new version.
- **When Minecraft (Release) updates**, BetterRTX presets are cleared as well, so you can't accidentally install shaders built for an older version. You'll need to download them again, which also depends on BetterRTX itself having caught up. A **Preview** update leaves BetterRTX presets alone.
- The app checks for a newer Supervanilla RTX build every so often, and when one is out, Get latest comes back offering it. The builds you already have stay until the game updates.
- The app also checks the BetterRTX API for changes every so often and clears the downloaded BetterRTX presets. Use Refresh when it's slow to notice an update that may have dropped moments ago.

On **Minecraft Preview**, use Get latest Supervanilla RTX (Preview): it fetches a build made for Preview. BetterRTX works there too but isn't recommended: its presets are built for the regular release, and Preview can refuse to start with them. If that happens, roll back to **Default RTX (Preview)**.

If a preset makes the game crash, roll back to Default RTX. You can use the [RTX LUT manager](#rtx-lut-manager) to give Minecraft RTX a different look in the meantime.

# DLSS swapper

Keeps your collection of DLSS versions and swaps the one Minecraft uses.

- The version your game has now is added to the collection automatically, so you can always go back to it. It's highlighted in the list.
- Click any version to swap to it.
- **Add DLSS files** - add `nvngx_dlss.dll` files, or `.zip` archives containing any number of them, by browsing or dragging onto the page. Versions older than 2.0 aren't supported and are removed.
- **Download DLSS files** - opens a DLSS download page inside the app (TechPowerUp by default; you can change it in [Settings](#settings)). Download what you want, then click **Done** and the files are imported automatically. You can also open the page in your normal browser and add the files yourself.
- Versions you don't need can be deleted from the collection, except the one currently in use.

# RTX LUT manager

A simpler, more reliable way to change how RTX looks, and one that keeps working across game updates and on Preview. It replaces RTX's color look-up tables and a few related files (the sky, water ripples, and light patterns under water).

Pick a preset from the dropdown to see a preview, then click **Install**.

- **Default** - a backup of your game's own files, taken the first time you open this manager. Always there to roll back to.
- **Default (MCPE-138222 Fix)** - same as the default look, with a corrected sky texture (Fixes [MCPE-138222](https://bugs.mojang.com/browse/MCPE/issues/MCPE-138222))
- **Gamescom 2019 Demo V2** - recreates the look of Minecraft RTX's early public demos; a good alternative to the default.
- **Gaming** - its own sky, water, underwater light and color grading.
- **Inverted Day-Night Cycle** - day becomes night, and night becomes day.
- **Matrix Fever** - based on [Matrix Fever](https://www.curseforge.com/minecraft-bedrock/texture-packs/matrix).
- **Night Night** - extremely dark nights, great for a horror feel.
- **RTX Beta 1.15 Era** - from the 1.15 RTX betas, an alternative to the default LUT.
- **Whimsical Bright** - the opposite of Night Night: colorful and bright.

Have an idea for a new preset? Suggest it!

# Launch Minecraft RTX

Starts Minecraft with ray tracing already switched on. Before launching, it writes a few game settings into every player profile's `options.txt`:

- `graphics_mode: 3` - ray tracing on.
- `graphics_mode_switch: 1` - lets you switch graphics modes in-game.
- `gfx_vsync: 0` - VSync off, which fixes a large ray tracing performance drop.

This works around several long-standing game bugs that make turning on ray tracing difficult, including:
- [MCPE-191513](https://bugs.mojang.com/browse/MCPE/issues/MCPE-191513) - ray tracing can't be enabled from the main menu.
- [MCPE-152158](https://bugs.mojang.com/browse/MCPE/issues/MCPE-152158) - PBR textures not loading after enabling ray tracing in a fresh launch.
- [MCPE-121850](https://bugs.mojang.com/browse/MCPE/issues/MCPE-121850) - performance starvation with VSync on.

Which settings it writes is up to you; to change them, see **Launch options** in [Settings](#settings). When it changes a file, the version before the change is kept beside it as `options.txt.backup`.

# Opening files with the app

The app can open two kinds of file straight from File Explorer (right-click -> **Import into Vanilla RTX App** or **Open with**, or set it as the default):

- **`.mcpack`** - imported as a resource pack, install into Minecraft, hence appearing in [Select other packs](#select-other-packs) menu too.
- **`.rtpack`** - imported as a shader preset into the [Shader mod manager](#shader-mod-manager), under Supervanilla RTX or BetterRTX. Ready to install!

If the app isn't running, it starts. Imports aren't silent, because some may need your confirmation.

## Links and the command alias

Type **`vanillartx`** into Win+R or a terminal to start the app.

**`vanillartx://` links** open the app (or bring it forward if it's already running) and can go straight to a screen:

| Link | Opens |
|---|---|
| `vanillartx://packupdater` | Get latest RTX packs |
| `vanillartx://packbrowser` | Select other packs |
| `vanillartx://rtxreactor` | RTX Reactor |
| `vanillartx://shadermods` | Shader mod manager (`vanillartx://betterrtx` still works too) |
| `vanillartx://dlss` | DLSS swapper |
| `vanillartx://lut` | RTX LUT manager |
| `vanillartx://settings` | Settings |
| `vanillartx://help` | This page - add a section to jump to it, e.g. `vanillartx://help/rtx-reactor` |
| `vanillartx://bugs` | The Minecraft RTX bug list |

Names aren't case-sensitive, and an `open` in front works too (`vanillartx://OpenPackBrowser`). A link only ever opens a screen - it can't install, tune or delete anything - and it behaves exactly like clicking that button yourself: if the button is unavailable right now, so is the link, and if another feature is already open, you're asked to close it first.

Two links launch the game instead without opening the app, handy as a desktop shortcut:

| Link | Does |
|---|---|
| `vanillartx://launchminecraftrtx` | Launches Minecraft with your launch options, (i.e. triggers [Launch Minecraft RTX](#launch-minecraft-rtx)). |
| `vanillartx://launchminecraftrtxpreview` | The same, but for Minecraft Preview |

They use the launch options from Settings and nothing else. If the app is already open, the game launches and the app stays where it is. If something goes wrong - the game's user data folder can't be found, say - the app opens to tell you why.

# Settings

Opened with the gear in the title bar. Changes are saved automatically.

#### Appearance

- **App theme** - Light, Dark, or Auto to follow Windows.
- **Suspend UI animations** - turns off the app's animations: fades, the lamp, the log's typing effect, the pixel art, RTX Reactor effects, and so on. Recommended if flashing bothers you, it also makes the app start faster and use a little less energy.

#### Game installations and Game user data

Where Minecraft and Minecraft Preview are installed, and where each keeps your worlds, settings and packs. The app takes extensive measures to find all four by itself, but you can set or change them here if one is missing, wrong, or you wish to point it elsewhere.

- Click a path to open it in File Explorer.
- **Select** or **Change** picks a different folder, starting from the current one. A folder is only accepted if it passes some checks the app runs to ensure it is the right directory. For instance, the app also reads the game's own config to make sure Release and Preview aren't mixed up.

#### Launch options

The `options.txt` settings [Launch Minecraft RTX](#launch-minecraft-rtx) or `vanillartx://` protocols write before starting the game. Edit any value, **Add option** for any other setting, or remove a row - the game then simply keeps whatever value it already has for it. With no options at all, the button just launches the game without changing anything. **Defaults** puts the original three back. 
> A useful example field you could add is `raytracing_viewdistance`, allowing you to override the 24 chunk ray tracing view distance cap. Minecraft settings have a tendency to occasionally revert, so creating a launch shortcut that goes through the Vanilla RTX App instead is a good assurance that your desired settings are always applied before each launch.

#### Content sources

The addresses the app uses for things it doesn't ship itself: the **Documentation page** (this page) and **Minecraft RTX bug list** that Help and Bugs show, the **DLSS downloads** page, and the **BetterRTX preset creator**. Change one and that feature uses your address instead. An address that isn't usable is rejected with the reason shown, and emptying a box goes back to the built-in one. The two documents must be `.md` files.

#### Maintenance

- **Copy debug logs** - copies a full diagnostic report to your clipboard: system details, the app's log, every setting, recent errors, and the state of every control. Paste it into a bug report.
- **Wipe all app data** - deletes everything the app has stored and restarts it as if freshly installed: settings, caches, downloaded presets, remembered game locations, all of it. Before it does, it restores your game's original shader and LUT files from the app's backups, which may show several admin prompts. **Approve every one.** If you don't and the game isn't back on its original files, the app's backups of them are gone for good, and fixing that means reinstalling Minecraft.

At the very bottom are links to **[this repo](https://github.com/vanilla-rtx/Vanilla-RTX-App/)**, **[Discord](https://discord.gg/A4wv4wwYud)** and **Ko-fi**, and the list of Vanilla RTX Insiders - the supporters who make this project possible. **[Please consider joining them!](https://ko-fi.com/cubeir)**

# Troubleshooting

### Minecraft or its user data isn't found

The app finds your game and your user data on its own and for almost everyone both will succeed silently, but if it does not, and you use a feature that needs them, the app tries to guide you to point it to the correct locations:

**The game:** the app asks Windows where Minecraft is installed and follows that to the real files, wherever you installed them. If that fails, it searches the usual locations on every drive, then the whole system, usually succeeding in under a minute. If it still can't, it asks you: pick the folder containing `Minecraft.Windows.exe`, or the folder one level above it. You can also set either game's location manually in Settings, under **Game installations**. The shader mod manager, the LUT manager and the DLSS swapper need this.

**User data:** this folder only exists once you've played the game at least once. If it can't be found, the **Select other packs** button temporarily turns into a highlighted **Locate user data** button. Click it and pick the folder named `Minecraft Bedrock` (or `Minecraft Bedrock Preview`), the one with a `Users` folder inside, usually under `%appdata%`. If it isn't there, you may be using an unofficial launcher that keeps it elsewhere. You can also set it in Settings, under **Game user data**. Pack selection, tuning, installing, importing, exporting, RTX Reactor and the Launch button all need this.

### The game doesn't start after Launch Minecraft RTX

Your computer may not have the link/protocol Minecraft uses to be launched. Just start the game yourself - the settings were most likely already written, which is the only part that actually matters.

### Minecraft crashes after installing a shader preset

Roll back to **Default RTX** in the Shader mod manager. Shader mods sometimes take a while to catch up with Minecraft updates. Meanwhile, the [RTX LUT manager](#rtx-lut-manager) changes the look of Minecraft RTX reliably without depending on updates. Keep an eye on the in-app announcements and news as I usually update those to communicate whether the current version of BetterRTX supports the latest game version, or not.

### Known limitations

- The app does not ship any font files, if you see boxes instead of icons, your Windows is missing the Segoe Fluent Icons font. Install it from Microsoft: [Segoe Fluent Icons](https://aka.ms/SegoeFluentIcons) (or [Segoe MDL2](http://aka.ms/SegoeMDL2) on Windows 10).
- Only English is supported, and other system languages haven't been tested.
- Windows accessibility settings aren't fully accounted for in the app's appearance, including high contrast themes.

### Getting help, reporting bugs, suggestions

Open an issue [on GitHub](https://github.com/vanilla-rtx/Vanilla-RTX-App/issues), post in the forum channel of the [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud), or mention @cubeir on the [Minecraft RTX Community Discord](https://discord.gg/eKVKD3c). Anything works!

When reporting a bug, reproduce it first, then use **Settings -> Copy debug logs** and paste the result into your report. It helps a lot.

# Disclaimer

Vanilla RTX App (formerly Vanilla RTX Tuner) is not associated or affiliated with Mojang Studios or NVIDIA.
