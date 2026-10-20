using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Vanilla_RTX_App.Modules.Alchitex.Core;
using Vanilla_RTX_App.Modules.Alchitex.Tools;
using Vanilla_RTX_App.Modules.PackBrowser;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Vanilla_RTX_App.Modules.Alchitex;


public static class AlchitexVariables
{
    public static class Persistent
    {
        // Mirrors SecondaryPbrMode 1:1 (0=None, 1=Auto,
        // 2=Normal, 3=Heightmap) rather than storing the enum directly - ApplicationData
        // LocalSettings needs a WinRT-projectable type, and int is the simplest one that
        // round-trips cleanly through the existing reflection-based Save/LoadSettings.
        public static int SecondaryPbrModeIndex = (int)SecondaryPbrMode.Auto;
        // On by default: the fog configs are what make a generated pack look like it
        // belongs in an RTX world rather than just having PBR textures bolted on.
        public static bool AddFogEnabled = true;
        // Off by default, and deliberately so - it deletes the user's own installed pack.
        public static bool DeleteOriginalPackEnabled = false;
        // UTC "O" stamp the once-a-day Ko-fi auto-scroll cooldown counts from. Seeded to
        // "now" the first time this module ever opens (see Alchitex_Loaded) rather than
        // left empty until the scroll first plays - empty is read as "due immediately",
        // which would scroll a brand new user to a donation ask before they've even seen
        // what the feature does. Written immediately wherever it changes (not just on
        // app close, like the rest of Persistent) so the cap holds even across a crash.
        // Parse with DateTimeStyles.RoundtripKind, same trap as Teletext' own cooldown.
        public static string LastSupportScrollUtc = "";
    }
    public static class Defaults
    {

    }
    public static void SaveSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (var field in fields)
        {
            var value = field.GetValue(null);
            localSettings.Values[field.Name] = value;
        }
    }

    public static void LoadSettings()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        var fields = typeof(Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            try
            {
                if (localSettings.Values.ContainsKey(field.Name))
                {
                    var savedValue = localSettings.Values[field.Name];
                    var convertedValue = Convert.ChangeType(savedValue, field.FieldType);
                    field.SetValue(null, convertedValue);
                }
            }
            catch
            {
                Trace.WriteLine($"[AlchitexVariables] An issue occured loading settings");
            }
        }
    }
}

public sealed partial class Alchitex : ModuleOverlay
{
    private bool _isClosing; // just a secondary guard in case a future code ends up closing a module while already closing
    private CancellationTokenSource? _generateCts;

    /// <summary>
    /// Which edition's resource-pack folders orphaned-temp-folder cleanup sweeps.
    ///
    /// Snapshotted at construction rather than read live, because it has to match the
    /// edition the queued packs were selected under - a temp folder left behind by this
    /// session is in that edition's tree, wherever the toggle points later. MainWindow
    /// disables the Preview toggle for as long as this module is open, so today the two can
    /// never diverge; the snapshot is what keeps that from being load-bearing.
    ///
    /// It defaults to the current target instead of to false, so opening the module is
    /// enough - this used to require MainWindow to remember to assign it, and it never did,
    /// which left every Preview user's sweep looking in the stable folders.
    /// </summary>
    public bool IsTargetingPreview { get; set; } = EnvironmentVariables.Persistent.IsTargetingPreview;

    /// <summary>
    /// Mirrors the sibling modules' report-on-close convention (shader mod/DLSS/LUT
    /// managers): MainWindow reads <see cref="ModuleOverlay.OperationSuccessful"/> and
    /// <see cref="ModuleOverlay.StatusMessage"/> once this closes and logs accordingly. True
    /// only if at least one pack succeeded across the whole session (every Generate click,
    /// not just the last one) - a mix of some successes and some failures still reports as
    /// successful, since the message itself already separates the two lists clearly.
    /// </summary>

    // Accumulated across every Generate click in this module's lifetime, not just the
    // most recent one - the user can click Generate more than once before closing.
    private readonly List<string> _succeededPackNames = new();
    private readonly List<string> _failedPackNames = new();
    // Originals uninstalled by the "Uninstall the original pack" toggle - reported on
    // close so the user has a record of what was removed on their behalf.
    private readonly List<string> _removedOriginalNames = new();

    // Drives the Generate button's three layers. Built in ShowMainContent, since it needs
    // the XAML to exist, and shut down with the module so no loop outlives it.
    private ReactorAnimator? _reactor;

    // Whether the pointer is currently over GenerateButton - only PointerEntered/Exited
    // actually know this, so it's tracked there and read on release: a press always starts
    // with the pointer over the button, but releasing doesn't imply leaving, and without
    // this a release-without-exiting left the reactor settled at rest until the pointer
    // genuinely left and came back, rather than resuming the hover dance it was already in.
    private bool _isGenerateButtonPointerOver;

    // Generates the overlay's tile field. Unlike the reactor this isn't gated on the license
    // being accepted - the background is behind the license screen too, and was when it was
    // still a bitmap.
    private ReactorBackdrop? _backdrop;

    private string AlchitexAssetsPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Modules", "Alchitex", "Assets");

    private static string LicenseAcceptedKey = $"Alchitex_LicenseAccepted_{EnvironmentVariables.appVersion}";

    public Alchitex()
    {
        this.InitializeComponent();
        PrepareContent();

        // The queue mirrors the app-wide selection, so it has to hear about edits made from
        // the main window while this is open - see SelectedPacks_CollectionChanged.
        EnvironmentVariables.SelectedPacks.CollectionChanged += SelectedPacks_CollectionChanged;

        this.Loaded += Alchitex_Loaded;
    }

    /// <summary>The dev-tool buttons, which live in MainWindow's titlebar while this is open.</summary>
    protected internal override FrameworkElement? TitleBarStrip => TitleBarActions;
    private async void Alchitex_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= Alchitex_Loaded;

            if (_isClosing) return;

            _backdrop = new ReactorBackdrop(AlchitexBackdropHost);
            _backdrop.Start();

            AlchitexVariables.LoadSettings();
            TeletextCard.Populate(AlchitexInfoPanel, TeletextContent.AlchitexInfo, sharpCorners: true);
            BuildSupportSection();
            SeedSupportScrollCooldownIfNeeded();

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Alchitex] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    protected override void OnClosing()
    {
        // SaveSettings() only ever persists whatever's currently sitting in
        // AlchitexVariables.Persistent's static fields - those used to only get updated
        // when Generate was clicked (ReadOptionsFromUI), so changing a toggle/dropdown and
        // closing without generating silently lost the change. Sync from the live
        // controls first. Guarded on MainGrid actually being shown, so closing during the
        // license screen (controls never touched, still at XAML defaults) doesn't stomp
        // whatever was already saved from a previous session.
        if (MainGrid.Visibility == Visibility.Visible)
            SyncPersistentSettingsFromControls();

        AlchitexVariables.SaveSettings();

        if (_isClosing) return;
        _isClosing = true;

        // A test bench run holds no pack and writes only into folders the user pointed at,
        // but there's no reason to let it keep going once the module is gone.
        _testBenchCts?.Cancel();

        _reactor?.Shutdown();
        _backdrop?.Shutdown();

        EnvironmentVariables.SelectedPacks.CollectionChanged -= SelectedPacks_CollectionChanged;

        this.Loaded -= Alchitex_Loaded;
    }

    // ── Init ────────────────────────────────────────────
    private async Task InitializeAsync()
    {
        try
        {
            bool accepted = await CheckLicenseAcceptedAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;

            if (!accepted)
            {
                await PopulateLicenseTextAsync();
                LicensePanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowMainContent();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] EXCEPTION in Alchitex.InitializeAsync: {ex}");
            this.Close();
        }
    }
    // ── License ─────────────────────────────────────────
    private static Task<bool> CheckLicenseAcceptedAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            var val = settings.Values[LicenseAcceptedKey];
            return Task.FromResult(val is bool b && b);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error reading license key: {ex.Message}");
            return Task.FromResult(false);
        }
    }
    private async Task PopulateLicenseTextAsync()
    {
        try
        {
            var uri = new Uri("ms-appx:///Modules/Alchitex/ALCHITEX_LICENSE.txt");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            var body = await FileIO.ReadTextAsync(file);

            LicenseTextBlock.Blocks.Clear();

            // ── "Online version" header ──────────────────────────────────────
            var headerPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 2) };
            headerPara.Inlines.Add(new Run { Text = "Online version:  " });
            var link = new Hyperlink
            {
                NavigateUri = new Uri("https://github.com/vanilla-rtx/Vanilla-RTX-App/blob/main/src/Modules/Alchitex/ALCHITEX_LICENSE.txt")
            };
            link.Inlines.Add(new Run { Text = "View on GitHub" });
            headerPara.Inlines.Add(link);
            LicenseTextBlock.Blocks.Add(headerPara);

            // ── Separator ────────────────────────────────────────────────────
            // A TextBlock in an InlineUIContainer rather than a Run, because only an element
            // can take a style, and the style is what keeps its colour on the app's theme.
            var sepPara = new Paragraph { Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 8) };
            sepPara.Inlines.Add(new InlineUIContainer
            {
                Child = new TextBlock
                {
                    Text = "───────────────────────────────────────",
                    FontSize = LicenseTextBlock.FontSize,
                    Style = (Style)Application.Current.Resources["ThemedSecondaryTextStyle"]
                }
            });
            LicenseTextBlock.Blocks.Add(sepPara);

            // ── Body ─────────────────────────────────────────────────────────
            var bodyPara = new Paragraph();
            bodyPara.Inlines.Add(new Run { Text = body });
            LicenseTextBlock.Blocks.Add(bodyPara);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Error loading license text: {ex.Message}");
            LicenseTextBlock.Blocks.Clear();
            var err = new Paragraph();
            err.Inlines.Add(new Run { Text = $"Could not load license file: {ex.Message}" });
            LicenseTextBlock.Blocks.Add(err);
        }
    }
    private void DisagreeButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
    private async void AgreeButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LicenseAcceptedKey] = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ALCHITEX] Error writing license key: {ex.Message}");
            }
            return Task.CompletedTask;
        });
        LicensePanel.Visibility = Visibility.Collapsed;
        ShowMainContent();
    }


    // ── Main Content ───────────────────────────────────────────────────────

    // ── Reveal main content ───────────────────────────────────────────────────

    // Main content are hidden before license is accepted
    /// <summary>
    /// Reveals the app once the license has been accepted.
    ///
    /// The whole post-license UI lives inside exactly two containers - TitleBarActions and
    /// MainGrid - and children inherit a collapsed parent, so revealing it is those two
    /// lines and only those two lines. Adding a new control after the license screen needs
    /// nothing here: put it inside either container and it comes along for free. Please
    /// keep it that way rather than collapsing new controls individually and re-showing
    /// them here, which is what this used to do.
    ///
    /// The only things that still belong in this method are ones that aren't about license
    /// state at all: title text, restoring persisted control values, and the debug-only
    /// group (a different axis entirely - build configuration, not license acceptance).
    /// </summary>
    private void ShowMainContent()
    {
        SetStatus(null);

        TitleBarActions.Visibility = Visibility.Visible;
        MainGrid.Visibility = Visibility.Visible;

        BuildSecondaryPbrMenu();
        SelectSecondaryPbrMode(AlchitexVariables.Persistent.SecondaryPbrModeIndex);
        AddFogToggle.IsOn = AlchitexVariables.Persistent.AddFogEnabled;
        DeleteOriginalToggle.IsOn = AlchitexVariables.Persistent.DeleteOriginalPackEnabled;

        ResolveReactorArt();

        _reactor = new ReactorAnimator(ReactorTileGrid, ReactorBloom);
        _reactor.Initialize();

        // Press-and-hold, the flower dance and the abort stance are all pointer states, not
        // clicks - the button's own Click event fires too late and only once. Which one a
        // given gesture means depends entirely on whether a run is going: idle, hovering
        // starts the flower dance and pressing winds the reactor up; running, hovering shows
        // the red X instead and a click stops the run.
        GenerateButton.AddHandler(UIElement.PointerEnteredEvent,
            new PointerEventHandler((s, e) =>
            {
                _isGenerateButtonPointerOver = true;
                if (IsGenerating) _reactor?.BeginAbortHint();
                else _reactor?.BeginFlowerDance();
            }), handledEventsToo: true);

        GenerateButton.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler((s, e) =>
            {
                _isGenerateButtonPointerOver = false;
                _reactor?.EndAbortHint();
                if (!IsGenerating)
                {
                    _reactor?.EndPressHold();
                    _reactor?.EndFlowerDance();
                }
            }), handledEventsToo: true);

        GenerateButton.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((s, e) =>
            {
                if (IsGenerating) _reactor?.BeginAbortHint();
                else _reactor?.BeginPressHold();
            }), handledEventsToo: true);

        // Release leaves the pointer sitting on the button, so the abort stance stays up -
        // only the idle wind-up ends here. That "leaves the pointer sitting on the button"
        // is exactly the case EndPressHold alone doesn't handle: it settles to rest
        // unconditionally, so releasing without actually leaving used to require a real
        // exit-then-re-enter before hovering read as hovering again. PointerEntered/Exited
        // are the only place this module actually knows where the pointer is, so that's
        // where _isGenerateButtonPointerOver is tracked - resuming the hover dance here on
        // release is then just acting on it.
        GenerateButton.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((s, e) =>
            {
                if (IsGenerating) return;

                _reactor?.EndPressHold();
                if (_isGenerateButtonPointerOver) _reactor?.BeginFlowerDance();
            }), handledEventsToo: true);

        GenerateButton.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler((s, e) =>
            {
                if (IsGenerating) return;

                _reactor?.EndPressHold();
                if (_isGenerateButtonPointerOver) _reactor?.BeginFlowerDance();
            }), handledEventsToo: true);

        _ = RebuildPackIconsAsync();

#if DEBUG
        DevOnlyTitleBarActions.Visibility = Visibility.Visible;
#endif
    }

    /// <summary>
    /// Points the reactor's logo and bloom layers at whichever art is actually installed.
    ///
    /// The XAML asks for reactor.logo.png / reactor.bloom.png - the dedicated 1024px pair.
    /// Until those are in Assets/, the logo layer falls back to logo.large.png (the same
    /// mark, just without a matching bloom) so the button is never a bare grid of tiles,
    /// and the bloom layer is hidden rather than left as a broken image. The tile
    /// background is generated in code and never depends on a file at all.
    /// </summary>
    private void ResolveReactorArt()
    {
        var logoPath = System.IO.Path.Combine(AlchitexAssetsPath, "reactor.logo.png");
        var bloomPath = System.IO.Path.Combine(AlchitexAssetsPath, "reactor.bloom.png");

        if (!System.IO.File.Exists(logoPath))
        {
            Trace.WriteLine($"[ALCHITEX] '{logoPath}' not found - falling back to logo.large.png for the reactor's logo layer.");
            ReactorLogo.Source = new BitmapImage(new Uri("ms-appx:///Modules/Alchitex/Assets/logo.large.png"));
        }

        if (!System.IO.File.Exists(bloomPath))
        {
            Trace.WriteLine($"[ALCHITEX] '{bloomPath}' not found - the reactor's bloom layer stays hidden until it's added.");
            ReactorBloom.Source = null;
        }
    }

    // ── Titlebar status line ─────────────────────────────────────────────────

    private const string DefaultTitleText = "RTX Reactor";

    // Cancels a pending "revert the title back to RTX Reactor" when something else wants

    /// <summary>
    /// Records where a run has got to. It has no surface of its own: the titlebar it used to
    /// be written into belongs to MainWindow now and cannot say anything about one module,
    /// and what a run looks like is already carried by the reactor's own stances and by
    /// <c>GenerateProgressBar</c> under the button. Trace keeps the detail for diagnosis.
    ///
    /// <para>Kept as a method rather than deleted at its thirty-odd call sites because those
    /// lines are the clearest description of the pipeline's stages there is, and because
    /// giving this a surface again is then one edit in one place.</para>
    /// </summary>
    private void SetStatus(string? text) => Trace.WriteLine($"[Alchitex] {text ?? DefaultTitleText}");

    /// <summary>
    /// <see cref="SetStatus"/> for a run's final line. The revert it used to schedule went
    /// with the titlebar; the delay parameter stays so the call sites keep reading as the
    /// intent they were written with.
    /// </summary>
    private void SetStatusThenRevert(string text, int millisecondsBeforeRevert = 8000) => SetStatus(text);

    // ── Pack queue ───────────────────────────────────────────────────────────
    //
    // Two rows: what's still waiting to go into the reactor on top, what has come out of
    // it underneath. Both scroll horizontally rather than collapsing into a "+N", because
    // every tile now carries its own discard button and something you can't reach is
    // something you can't discard.

    private const double PackTileMinSize = 64;
    private const double PackTileMaxSize = 128;
    private double _packTileSize = 112;

    // Room left around each icon for its drop shadow (BuildPackTile), split evenly between
    // both queue rows' StackPanels having Spacing="0" - the perceived gap between two icons
    // now comes entirely from this padding on each of their containers (2x this, since two
    // neighbours each contribute one side) rather than space the panel put between them.
    // That's deliberate: a shadow drawn via Translation.Z + ThemeShadow needs room around the
    // element casting it to actually render into, and a tile sized exactly to its icon left
    // none anywhere - the panel's old Spacing="12" was room between tiles, never room INSIDE
    // one. Kept at half that old spacing (6) so the perceived gap between icons is unchanged.
    private const double TileShadowPadding = 6;

    /// <summary>
    /// Packs this module is ignoring for the rest of its lifetime: discarded by the user,
    /// skipped at a confirmation dialog, or already run (successfully or not).
    ///
    /// Deliberately NOT a change to EnvironmentVariables.SelectedPacks - that selection belongs
    /// to the main window and the pack browser. Closing and reopening this module brings
    /// everything back, which is exactly what "temporarily ignore" should mean. (The one
    /// case where SelectedPacks does change is the "Uninstall the original pack" toggle,
    /// and only because the folder genuinely stopped existing.)
    /// </summary>
    private readonly HashSet<string> _dismissedLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Packs that made it through, in the order they came out.</summary>
    private readonly List<(string Location, string Name)> _outputPacks = new();

    // Cached per pack so a resize or a queue change doesn't re-read icon files.
    private readonly Dictionary<string, BitmapImage?> _packIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;

    private List<(string Location, string Name)> InputQueue() => EnvironmentVariables.SelectedPacks
        .Where(p => !string.IsNullOrEmpty(p.Location))
        .Where(p => !_dismissedLocations.Contains(p.Location))
        .Where(p => System.IO.Directory.Exists(p.Location))
        .Select(p => (p.Location, p.Name))
        .ToList();

    /// <summary>
    /// Loads any icons that aren't cached yet, then redraws both rows. Call whenever the
    /// queue's contents change; a plain resize goes straight to RenderQueues.
    /// </summary>
    private async Task RebuildPackIconsAsync()
    {
        foreach (var (location, _) in InputQueue().Concat(_outputPacks))
        {
            if (_packIconCache.ContainsKey(location)) continue;
            _packIconCache[location] = await PackBrowserOverlay.LoadPackIconAsync(location);
        }

        RenderQueues();
    }

    // ── Keeping the queue honest when the selection changes underneath it ────

    // A redraw mid-animation destroys the tile being animated and rebuilds it in its
    // final state, so the motion is over before it's visible. Anything that animates a
    // tile holds this open, and a redraw asked for in the meantime is deferred rather
    // than dropped.
    private int _queueTransitionDepth;
    private bool _queueRedrawPending;

    private IDisposable BeginQueueTransition()
    {
        _queueTransitionDepth++;
        return new QueueTransitionScope(this);
    }

    private sealed class QueueTransitionScope : IDisposable
    {
        private readonly Alchitex _owner;
        public QueueTransitionScope(Alchitex owner) => _owner = owner;

        public void Dispose()
        {
            if (--_owner._queueTransitionDepth > 0) return;
            if (!_owner._queueRedrawPending) return;

            _owner._queueRedrawPending = false;
            _ = _owner.RebuildPackIconsAsync();
        }
    }

    /// <summary>
    /// The main window's Clear button (and anything else that edits the shared selection)
    /// empties EnvironmentVariables.SelectedPacks out from under this module. The queue is drawn
    /// from that collection, so it follows along - no need for the main window to disable
    /// its controls while this module is open just to keep the two in agreement.
    ///
    /// Generation follows too: the batch loop re-checks each pack against the live
    /// selection before starting it (IsStillQueued), so a pack cleared mid-run is skipped
    /// rather than generated for a selection the user has already emptied.
    /// </summary>
    private void SelectedPacks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isClosing) return;

        if (_queueTransitionDepth > 0)
        {
            _queueRedrawPending = true;
            return;
        }

        _ = RebuildPackIconsAsync();
    }

    // ── Progress reporting rate limit ────────────────────────────────────────

    // ~30 updates a second: past what anyone can read, and deliberately shorter than the
    // reactor's own 70ms pulse throttle so this gate never becomes what paces the
    // animation. It exists to spare the dispatcher, not to slow the reactor down.
    private const double ProgressUiIntervalMs = 33;

    private DateTime _lastProgressUiUtc = DateTime.MinValue;
    private AlchitexPhase? _lastProgressPhase;

    /// <summary>
    /// Rate-limits what a progress report is allowed to do to the UI.
    ///
    /// GenerateTexturePixels reports once per texture from inside a Parallel.ForEach, which
    /// on a real pack is thousands of reports a second. Progress&lt;T&gt; marshals every one
    /// of them onto the UI thread as its own dispatcher work item, and each was rewriting
    /// the titlebar text (a full measure/arrange) and touching the progress bar. That
    /// saturates the dispatcher, and input - hovering the reactor, clicking to abort -
    /// queues up behind the backlog, which is what a locked-up window looks like.
    ///
    /// The reactor's own throttle didn't cover this: it only limits the animation work it
    /// does, not the cost of the callback around it.
    ///
    /// Two kinds of report always get through, because dropping either loses information
    /// rather than just resolution: a phase change (the reactor's rarer, more deliberate
    /// animations fire exactly once) and a batch's final report (otherwise the bar can
    /// stop just short of full).
    /// </summary>
    private bool ShouldRenderProgress(AlchitexPipeline.AlchitexProgress p)
    {
        var isPhaseChange = _lastProgressPhase != p.Phase;
        var isFinal = p.Total > 0 && p.Completed >= p.Total;
        var now = DateTime.UtcNow;

        if (!isPhaseChange && !isFinal &&
            (now - _lastProgressUiUtc).TotalMilliseconds < ProgressUiIntervalMs)
        {
            return false;
        }

        _lastProgressPhase = p.Phase;
        _lastProgressUiUtc = now;
        return true;
    }

    /// <summary>Whether a pack queued at the start of a batch is still one the user wants
    /// generated - it can leave the queue mid-run by being discarded here or cleared from
    /// the selection upstream.</summary>
    private static bool IsStillQueued(string location, HashSet<string> dismissed)
        => !dismissed.Contains(location)
           && EnvironmentVariables.SelectedPacks.Any(p =>
                  string.Equals(p.Location, location, StringComparison.OrdinalIgnoreCase));

    private void RenderQueues()
    {
        if (InputQueuePanel == null || OutputQueuePanel == null) return;

        InputQueuePanel.Children.Clear();
        OutputQueuePanel.Children.Clear();

        // Built in reverse of run order, so the pack that goes next is the RIGHTMOST tile -
        // the end the reactor is on. InputQueue() is selection order and the batch loop takes
        // its head first, so building it straight put the pack about to run at the far left
        // with the whole queue between it and the thing it was entering.
        foreach (var (location, name) in Enumerable.Reverse(InputQueue()))
            InputQueuePanel.Children.Add(BuildPackTile(location, name, allowDiscard: true));

        foreach (var (location, name) in _outputPacks)
            OutputQueuePanel.Children.Add(BuildPackTile(location, name, allowDiscard: false));
    }

    /// <summary>
    /// One pack tile: the icon - sharp corners, matching Alchitex's own visual language
    /// rather than the pack browser's rounded ones - with a drop shadow, plus, for the input
    /// row, a discard button that fades in on hover mirroring the selection overlay in the
    /// pack browser but with the RemoveFrom glyph.
    ///
    /// The outer Grid is deliberately larger than the icon it holds (see TileShadowPadding) -
    /// the icon is centered inside it, and that margin is invisible headroom for the icon's
    /// own shadow to render into, not part of the tile's visible surface. A tile sized
    /// exactly to its icon left the shadow nothing to spread into and it got clipped flat
    /// against the icon's own edge.
    /// </summary>
    private Grid BuildPackTile(string location, string packName, bool allowDiscard)
    {
        var size = _packTileSize;
        var containerSize = size + TileShadowPadding * 2;

        var tile = new Grid
        {
            Width = containerSize,
            Height = containerSize,
            Tag = location, // how the generation loop finds this pack's tile again
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };

        var iconBorder = new Border
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 96, 96, 96)),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Shadow = new ThemeShadow(),
        };

        _packIconCache.TryGetValue(location, out var icon);

        if (icon != null)
        {
            iconBorder.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            // Same fallback chain the pack browser uses for a pack with no readable icon.
            try
            {
                iconBorder.Child = new Image
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/missing.png")),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                iconBorder.Child = new FontIcon
                {
                    Glyph = "",
                    FontSize = size * 0.32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        ToolTipService.SetToolTip(tile, Helpers.StripMinecraftFormatting(packName));
        tile.Children.Add(iconBorder);

        if (allowDiscard)
        {
            // A Border with a Tapped handler rather than a Button, mirroring the pack
            // browser's selection overlay. A Button would be wrong here for a concrete
            // reason: this overlay only exists while the pointer is over it, so it would
            // spend its whole visible life in the "pointer over" visual state and paint
            // that theme brush instead of the dark scrim it is supposed to be.
            var discard = new Border
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(150, 0, 0, 0)),
                Opacity = 0,
                Child = new FontIcon
                {
                    Glyph = "", // RemoveFrom
                    FontSize = size * 0.34,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            ToolTipService.SetToolTip(discard, $"Remove {Helpers.StripMinecraftFormatting(packName)} from this queue.");

            // Revealed by hovering the tile as a whole, so the target is the icon itself
            // rather than a control you have to find before you can use it.
            tile.PointerEntered += (s, e) => FadeTo(discard, 1.0, 120);
            tile.PointerExited += (s, e) => FadeTo(discard, 0.0, 120);

            discard.Tapped += async (s, e) =>
            {
                e.Handled = true;
                await DiscardPackAsync(location, tile);
            };

            tile.Children.Add(discard);
        }

        return tile;
    }

    /// <summary>Drops a pack from the queue for this module's lifetime, with the same
    /// send-off a skipped pack gets.</summary>
    private async Task DiscardPackAsync(string location, FrameworkElement tile)
    {
        if (!_dismissedLocations.Add(location)) return;

        await AnimateDismissAsync(tile);
        RenderQueues();
    }

    private void PackQueueHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Two rows split the host's height; a tile is a square that fits one of them with
        // a little air. Recomputed rather than fixed so the reactor area's height stays
        // the single thing that decides how big this all is.
        //
        // The extra 5 here (15 rather than 10) is deliberate headroom for the auto-hiding
        // horizontal scrollbar, which overlays the bottom of each row rather than reserving
        // its own space - measured overlapping the icon by about 8px when a row actually
        // needed to scroll. Paired with the -5 vertical nudge on both queue panels in XAML
        // (InputQueuePanel/OutputQueuePanel), that's 10px of clearance at the icon's bottom
        // edge - about as much smaller as this can get away with before packs stop being
        // recognizable at a glance.
        var rowHeight = e.NewSize.Height / 2;
        var size = Math.Clamp(Math.Floor(rowHeight - 15), PackTileMinSize, PackTileMaxSize);

        if (Math.Abs(size - _packTileSize) < 1) return;

        _packTileSize = size;
        RenderQueues();
    }

    /// <summary>Keeps the reactor square and equal to the panel's height, so changing the
    /// controls area's height is the only edit needed to resize it.</summary>
    private void AlchitexControlsArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = Math.Max(0, e.NewSize.Height - 40); // the inner Grid's 20px top/bottom margins

        GenerateButton.Width = side;
        GenerateButton.Height = side;
    }

    private static void FadeTo(UIElement element, double opacity, double durationMs)
    {
        if (AnimationsSuspended)
        {
            element.Opacity = opacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>
    /// Keeps the announcements panel filling whatever's left of the window under the
    /// controls area. Without this the acrylic panel would stop at its last card and leave
    /// the window background showing beneath it.
    /// </summary>
    private void MainScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => SyncAnnouncementMinHeight();

    private void SyncAnnouncementMinHeight()
    {
        AnnouncementBackground.MinHeight = Math.Max(
            0, MainScrollViewer.ActualHeight - AlchitexControlsArea.Height);
    }

    // ── Ko-fi support section ────────────────────────────────────────────────

    /// <summary>
    /// Fills SupportPersuasionText. The static copy here is written to end on a complete
    /// sentence AND to flow straight into TeletextContent.Credits - appended verbatim
    /// right after it - so the two read as one continuous message rather than two bolted-
    /// together blocks. If Credits hasn't loaded (no internet, fetch still pending), the
    /// persuasion text alone still stands fine on its own.
    /// </summary>
    private void BuildSupportSection()
    {
        const string Persuasion =
            "If this finally made a favorite pack look appropriate under ray tracing, or saved you the trouble of doing this by hand, and you'd like to help keep it free and maintained for everyone else too, a donation on Ko-fi goes a long way, and changes nothing about your ability to keep using the app.\n\n" +
            "RTX Reactor is the culmination of years of work that has gone on here, and it is handed to you completely free, because I wanted to help as many people as possible enjoy their packs with RTX, and any price would have worked against that goal." +
            "";
        var credits = TeletextContent.Credits?[0].Text?.Trim();

        SupportPersuasionText.Text = string.IsNullOrEmpty(credits)
            ? Persuasion
            : $"{Persuasion}\n\nThanks to these individuals, RTX Reactor was released for free after 3 years of development:\n{credits}";
    }

    private void SupportKofiButton_Click(object sender, RoutedEventArgs e)
    {
        _ = MainWindow.OpenUrl("https://ko-fi.com/cubeir");
    }

    /// <summary>
    /// Starts the once-a-day cooldown clock the first time this module is ever opened,
    /// rather than leaving LastSupportScrollUtc empty until the first scroll actually
    /// plays. Only ever writes once per install - every later call sees a non-empty value
    /// and does nothing.
    /// </summary>
    private void SeedSupportScrollCooldownIfNeeded()
    {
        if (!string.IsNullOrEmpty(AlchitexVariables.Persistent.LastSupportScrollUtc)) return;

        AlchitexVariables.Persistent.LastSupportScrollUtc = DateTime.UtcNow.ToString("O");
        AlchitexVariables.SaveSettings();
    }

    /// <summary>
    /// Smoothly scrolls down to the Ko-fi section once a batch finishes, so the option is
    /// visibly there right as the user is about to move on - never more than once a day,
    /// so it never turns into a nag on every single run.
    /// </summary>
    private async Task TriggerSupportScrollAsync()
    {
        try
        {
            if (!IsSupportScrollDue()) return;

            // Wait a little so queue output animations play out.
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            if (_isClosing) return;

            MainScrollViewer.ChangeView(null, MainScrollViewer.ScrollableHeight, null, disableAnimation: false);

            AlchitexVariables.Persistent.LastSupportScrollUtc = DateTime.UtcNow.ToString("O");
            AlchitexVariables.SaveSettings();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] TriggerSupportScrollAsync failed: {ex.Message}");
        }
    }

    private static bool IsSupportScrollDue()
    {
        var raw = AlchitexVariables.Persistent.LastSupportScrollUtc;
        if (string.IsNullOrEmpty(raw)) return true;

        // RoundtripKind matters here exactly as it does for Teletext' own cooldown without it
        // a UTC "O" stamp parses back as local time and the 24h window is off by the machine's offset.
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
            return true;

        var age = DateTime.UtcNow - last;
        return age < TimeSpan.Zero || age >= TimeSpan.FromDays(2);
    }

    // ── PBR generation ───────────────────────────────────────────────────────

    /// <summary>Labels for SecondaryPbrMode, in enum order - the button's text and its menu
    /// both come from here, so there's one list rather than one per surface.</summary>
    private static readonly string[] SecondaryPbrModeLabels =
    {
        "None (flat)", "Automatic", "Normal map", "Heightmap",
    };

    private int _secondaryPbrModeIndex = (int)SecondaryPbrMode.Auto;

    private void BuildSecondaryPbrMenu()
    {
        if (SecondaryPbrModeButton.Flyout is not MenuFlyout flyout) return;

        flyout.Items.Clear();

        for (var i = 0; i < SecondaryPbrModeLabels.Length; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = SecondaryPbrModeLabels[i],
                GroupName = "SecondaryPbrMode",
                IsTextScaleFactorEnabled = false,
            };

            item.Click += (s, e) => SelectSecondaryPbrMode(index);
            flyout.Items.Add(item);
        }
    }

    private void SelectSecondaryPbrMode(int index)
    {
        if (index < 0 || index >= SecondaryPbrModeLabels.Length) index = (int)SecondaryPbrMode.Auto;

        _secondaryPbrModeIndex = index;
        SecondaryPbrModeButton.Content = SecondaryPbrModeLabels[index];

        if (SecondaryPbrModeButton.Flyout is not MenuFlyout flyout) return;

        for (var i = 0; i < flyout.Items.Count; i++)
            if (flyout.Items[i] is RadioMenuFlyoutItem item) item.IsChecked = i == index;
    }

    /// <summary>Reads the live control values into AlchitexVariables.Persistent. Shared by
    /// ReadOptionsFromUI (on Generate) and OnClosing (on close) so a
    /// toggle/dropdown change is captured regardless of which one happens first.</summary>
    private void SyncPersistentSettingsFromControls()
    {
        AlchitexVariables.Persistent.SecondaryPbrModeIndex = _secondaryPbrModeIndex;
        AlchitexVariables.Persistent.AddFogEnabled = AddFogToggle.IsOn;
        AlchitexVariables.Persistent.DeleteOriginalPackEnabled = DeleteOriginalToggle.IsOn;
    }

    private AlchitexOptions ReadOptionsFromUI()
    {
        SyncPersistentSettingsFromControls();
        AlchitexVariables.SaveSettings();

        return new AlchitexOptions(
            (SecondaryPbrMode)AlchitexVariables.Persistent.SecondaryPbrModeIndex,
            AlchitexVariables.Persistent.AddFogEnabled);
    }

    /// <summary>True while a run is in progress and hasn't been asked to stop. The reactor
    /// button's gesture handling and its own Click both branch on this - it's the same
    /// button for "start" and "stop", so everything about it depends on which one it
    /// currently is.</summary>
    private bool IsGenerating => _generateCts is { IsCancellationRequested: false };

    /// <summary>
    /// Controls whose value is read once when a run starts, so changing them mid-run would
    /// silently mean nothing. GenerateButton is deliberately absent: during a run it IS the
    /// abort control and has to stay live.
    /// </summary>
    private static readonly string[] RunLockedControls =
    {
        "SecondaryPbrModeButton", "AddFogToggle", "DeleteOriginalToggle", "GenerateMaterialsConfigButton",
        "PipelinePreviewDevToolButton",
    };

    private void SetGenerationControlsEnabled(bool enabled)
    {
        // WindowControlsManager rather than a hand-rolled list of IsEnabled assignments -
        // it's the app's existing tool for this, and its reference counting means an
        // overlapping disable (a second run started before the first finished unwinding)
        // can't restore a control early.
        WindowControlsManager.ToggleSpecificControls(this, enabled, RunLockedControls);

        // A ComboBox dimmed its own Header when disabled; a DropDownButton has no header, so
        // its label is a separate TextBlock and has to be dimmed here - otherwise it stays
        // bright during a run while the toggle headers below it grey out.
        SecondaryPbrModeLabel.Opacity = enabled ? 1.0 : 0.4;

        // The reactor's tooltip says which of its two jobs a click would do right now.
        ToolTipService.SetToolTip(GenerateButton, enabled
            ? "Generate RTX support for every pack in the queue"
            : "Abort & clean up");
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        // A run already going: the reactor doubles as its own abort button, and the red X
        // the user is looking at while they click is what says so.
        if (IsGenerating)
        {
            AbortGeneration();
            return;
        }

        // Every selected pack is eligible now, not just the ones tagged as candidates (see
        // the confirmation phase below) - EXCEPT the ones dismissed from the queue, which
        // is the whole point of the discard button. This has to read the same list the
        // queue renders from, or the two disagree about what a click does.
        var selected = EnvironmentVariables.SelectedPacks
            .Where(p => !string.IsNullOrEmpty(p.Location))
            .Where(p => !_dismissedLocations.Contains(p.Location))
            .Where(p => System.IO.Directory.Exists(p.Location))
            .ToList();

        if (selected.Count == 0)
        {
            SetStatusThenRevert(EnvironmentVariables.SelectedPacks.Count == 0
                ? "No packs selected."
                : "The queue is empty - at least one pack is needed.");
            return;
        }

        var options = ReadOptionsFromUI();
        var deleteOriginals = AlchitexVariables.Persistent.DeleteOriginalPackEnabled;

        SetGenerationControlsEnabled(false);

        // Created *before* the confirmation dialogs rather than just before the batch:
        // SetGenerationControlsEnabled has already swapped Generate out for Abort, so the
        // button is sitting right there while the user works through the dialogs, and it
        // would do nothing at all against a null token source.
        _generateCts = new CancellationTokenSource();

        var succeeded = 0;
        var failedNames = new List<string>();
        var aborted = false;

        try
        {
            // ── Confirmation phase ───────────────────────────────────────────
            // Asked up front, for every pack, before any work begins - a dialog appearing
            // partway through a running batch (over a live progress bar, after some packs
            // are already written) would be a much worse place to ask.
            var queue = await BuildGenerationQueueAsync(selected, _generateCts.Token);

            if (_generateCts.IsCancellationRequested)
            {
                SetStatusThenRevert("Aborted before generating anything.");
                return;
            }

            if (queue.Count == 0)
            {
                SetStatusThenRevert(selected.Count == 1
                    ? "Skipped - nothing left to generate."
                    : "Skipped every selected pack - nothing left to generate.");
                return;
            }

            GenerateProgressBar.Visibility = Visibility.Visible;
            GenerateProgressBar.IsIndeterminate = true;
            SetStatus("Cleaning up leftovers from any previous run...");

            // Every batch starts by sweeping any alchitex_temp_* folder left behind by a
            // previous run that didn't finish (crash, force-close, a prior Abort). Cheap,
            // and means debris never has a chance to accumulate across sessions.
            await WhileWaitingAsync(() => Task.Run(() => AlchitexStaging.CleanupOrphanedTempFolders(IsTargetingPreview)));

            SetStatus($"Preparing ({queue.Count} pack{(queue.Count == 1 ? "" : "s")})...");
            _reactor?.BeginGeneration();

            for (var i = 0; i < queue.Count; i++)
            {
                if (_generateCts.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var (pack, stripExistingPbr) = queue[i];

                // The queue stays live for everything not yet started. A pack the user
                // discarded while an earlier one was running is simply skipped here - this
                // check is what keeps "what the queue shows" and "what actually runs" the
                // same thing, rather than two lists that agreed once at the start.
                if (!IsStillQueued(pack.Location, _dismissedLocations)) continue;

                // Dismissed the moment it's handed over - after the check above, so the two
                // uses of this set don't collide. It also has to happen BEFORE the run, not
                // after: any redraw during the run (an output icon finishing its load, say)
                // rebuilds the input row from this set, and a pack still in it at that
                // point pops back into the queue it just left.
                _dismissedLocations.Add(pack.Location);

                // The pack visibly goes into the reactor before its run starts, and the
                // rest of the queue closes the gap behind it.
                await SendTileIntoReactorAsync(pack.Location);
                var packIndex = i; // captured for the progress closure below

                // Progress<T> captures this thread's SynchronizationContext at construction
                // time (we're on the UI thread here), so every callback from inside
                // AlchitexPipeline.RunAsync - which runs its heavy work via Task.Run/
                // Parallel.ForEach on background threads - gets automatically marshalled
                // back to the UI thread. Safe to touch controls directly below.
                var progress = new Progress<AlchitexPipeline.AlchitexProgress>(p =>
                {
                    if (_isClosing || !ShouldRenderProgress(p)) return;

                    GenerateProgressBar.IsIndeterminate = p.Total == 0;
                    if (p.Total > 0)
                    {
                        GenerateProgressBar.Maximum = p.Total;
                        GenerateProgressBar.Value = p.Completed;
                    }
                    SetStatus($"[{packIndex + 1}/{queue.Count}] {pack.Name}: {p.StatusText}");

                    // The reactor reacts to the phase, not to the text - see AlchitexPhase.
                    _reactor?.Pulse(p.Phase);
                });

                var result = await AlchitexPipeline.RunAsync(
                    pack.Location,
                    pack.Name,
                    stripExistingPbr ? options with { StripExistingPbr = true } : options,
                    AlchitexAssetsPath,
                    EnvironmentVariables.appVersion,
                    progress,
                    _generateCts.Token);

                if (result.Success)
                {
                    succeeded++;
                    _succeededPackNames.Add(result.FinalManifestName ?? pack.Name);
                    _ = Host.BlinkingLamp(true, true, 1.0);

                    // Only ever after a fully successful run for this pack - a failed or
                    // aborted one leaves the user's original exactly where it was.
                    if (deleteOriginals)
                    {
                        SetStatus($"[{packIndex + 1}/{queue.Count}] {pack.Name}: Uninstalling the original pack...");
                        _reactor?.Pulse(AlchitexPhase.RemovingPack);
                        await WhileWaitingAsync(() => DeleteOriginalPackAsync(pack.Location, pack.Name));
                    }

                    // Out the bottom of the reactor and into the output row. Keyed on the
                    // generated pack's own folder, so its icon is the NEW pack's icon -
                    // and it still resolves when the original has just been uninstalled.
                    await AddToOutputRowAsync(result.OutputPackPath ?? pack.Location, result.FinalManifestName ?? pack.Name);
                }
                else if (_generateCts.IsCancellationRequested)
                {
                    // Aborted mid-pack. Nothing was produced and its temp copy is about to
                    // be swept, so the honest thing is to put it back where it came from -
                    // it's still a pack waiting to be generated, and the user can just hit
                    // Generate again. Every pack after it was never handed over, so it's
                    // still queued anyway.
                    _dismissedLocations.Remove(pack.Location);
                    await ReturnTileToQueueAsync(pack.Location);

                    aborted = true;
                    break;
                }
                else
                {
                    // A genuine failure stays out of the queue: re-offering a pack that
                    // just failed invites the same failure on the next click. It leaves
                    // visibly, and not the way a finished pack does.
                    failedNames.Add(pack.Name);
                    _failedPackNames.Add(pack.Name);
                    _ = Host.BlinkingLamp(true, true, 0.0);

                    await EjectFailedPackAsync(pack.Location, pack.Name);
                }
            }

            if (aborted)
            {
                SetStatusThenRevert($"Aborted - {succeeded} pack{(succeeded == 1 ? "" : "s")} completed before stopping.");
            }
            else
            {
                SetStatusThenRevert(failedNames.Count == 0
                    ? $"Done - {succeeded}/{queue.Count} pack{(queue.Count == 1 ? "" : "s")} processed successfully!"
                    : $"Done - {succeeded}/{queue.Count} pack{(queue.Count == 1 ? "" : "s")} succeeded. Failed: {string.Join(", ", failedNames)}");

                _ = Host.BlinkingLamp(true, true, succeeded > 0 ? 1.0 : 0.0, 1.0);

                // Only on a real, un-aborted finish, and only if something actually came out
                // of it - a batch that failed outright is not the moment to ask for support.
                if (succeeded > 0)
                    _ = TriggerSupportScrollAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] GenerateButton_Click failed: {ex}");
            SetStatusThenRevert($"Something went wrong: {ex.Message}");
            _ = Host.BlinkingLamp(true, true, 0.0);
        }
        finally
        {
            // Whatever just happened - success, a plain failure, or Abort - any temp
            // copy that didn't make it to promotion is still sitting there. Sweep now
            // rather than waiting for the next Generate click, so an aborted run's
            // half-done pack doesn't linger in the resource_packs folder in the meantime.
            await Task.Run(() => AlchitexStaging.CleanupOrphanedTempFolders(IsTargetingPreview));

            // Back out of sight until the next run. It doubles as the seam between the
            // controls and the announcements while it's up, so leaving it behind reads as
            // a permanent divider that appeared out of nowhere.
            GenerateProgressBar.IsIndeterminate = false;
            GenerateProgressBar.Visibility = Visibility.Collapsed;
            SyncAnnouncementMinHeight();

            SetGenerationControlsEnabled(true);
            _reactor?.EndGeneration();
            _generateCts?.Dispose();
            _generateCts = null;

            // Skipped, failed and aborted packs all leave the queue here, so what's left
            // on screen is only ever what a further click would actually act on.
            await RebuildPackIconsAsync();

            // Refresh even on an exception mid-batch - whatever succeeded before the
            // exception is still worth reporting when the module closes.
            UpdateSessionSummary();
        }
    }

    // ── Queue hand-off ───────────────────────────────────────────────────────

    /// <summary>
    /// Plays a pack's tile flying into the reactor, then redraws the input row without it
    /// and slides the remaining tiles into the gap. Awaited by the batch loop so the
    /// hand-off finishes before that pack's run starts - it's ~320ms against a run of
    /// seconds to minutes, and it's what makes the reactor look like it's being fed.
    /// </summary>
    private async Task SendTileIntoReactorAsync(string location)
    {
        var tile = InputQueuePanel.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(t => t.Tag is string tagged &&
                                 string.Equals(tagged, location, StringComparison.OrdinalIgnoreCase));

        if (tile == null) return;

        // The reactor washes left to right alongside the tile's own travel, so the two
        // are one event: the pack goes in, and the thing it went into reacts.
        _reactor?.PlayQueueWash(ReactorWashDirection.LeftToRight);

        await AnimateIntoReactorAsync(tile);

        // No reflow to play: the pack being handed over is the row's last child, and the
        // panel packs from the left, so removing it leaves every tile still queued exactly
        // where it was standing.
        InputQueuePanel.Children.Remove(tile);
    }

    /// <summary>
    /// Puts an aborted pack back in the input queue, coming back out of the reactor the way
    /// a finished one comes out into the output row. Nothing was generated for it, so this
    /// is a return, not a result - which is exactly what the reversed motion says.
    ///
    /// Also flashes the reactor's alert palette (ReactorAnimator.PlayErrorFlash), same as
    /// EjectFailedPackAsync - the pack itself is fine here, but leaving mid-run is still the
    /// user stopping something abruptly, not a normal hand-off, and the two should read as
    /// related events rather than one looking like any other pack coming back out.
    /// </summary>
    private async Task ReturnTileToQueueAsync(string location)
    {
        RenderQueues(); // it's un-dismissed by now, so this brings its tile back

        var tile = InputQueuePanel.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(t => t.Tag is string tagged &&
                                 string.Equals(tagged, location, StringComparison.OrdinalIgnoreCase));

        if (tile == null) return;

        // PlayErrorFlash first - it only flips which palette AnimateTile/PlayQueueWash
        // resolve against, and the wash bakes actual Color values into its keyframes the
        // moment it's called, so flipping the palette after would miss it entirely.
        _reactor?.PlayErrorFlash();
        _reactor?.PlayQueueWash(ReactorWashDirection.RightToLeft);

        using (BeginQueueTransition())
            await AnimateArrivalAsync(tile);
    }

    /// <summary>
    /// Adds a finished pack to the output row and plays it coming out of the reactor.
    ///
    /// The icon is loaded BEFORE the tile is built, not in the background: a redraw landing
    /// mid-animation would replace the tile being animated with a fresh one and the
    /// arrival would be over before it was visible. One file read against a run measured
    /// in seconds is the right trade.
    /// </summary>
    private async Task AddToOutputRowAsync(string location, string packName)
    {
        _outputPacks.Add((location, packName));

        if (!_packIconCache.ContainsKey(location))
            _packIconCache[location] = await PackBrowserOverlay.LoadPackIconAsync(location);

        var tile = BuildPackTile(location, packName, allowDiscard: false);
        OutputQueuePanel.Children.Add(tile);

        _reactor?.PlayQueueWash(ReactorWashDirection.RightToLeft);

        using (BeginQueueTransition())
            await AnimateArrivalAsync(tile);
    }

    // ── "Uninstall the original pack" ────────────────────────────────────────

    /// <summary>
    /// Deletes the pack the RTX version was generated from, via the same
    /// ExpImpDel.DeletePackAsync the main window's Delete button uses - including its
    /// guard against deleting anything that isn't inside a real resource-packs folder.
    /// No confirmation dialog: the toggle IS the confirmation, and it was answered before
    /// the run started.
    ///
    /// Also drops the pack from EnvironmentVariables.SelectedPacks. It's app-wide state and the
    /// folder is gone, so leaving it selected would hand a dead path to Export/Tune/Delete
    /// back in the main window.
    ///
    /// A failure here is reported but never fails the pack: its RTX version generated fine,
    /// which is what the user actually asked for.
    /// </summary>
    private async Task DeleteOriginalPackAsync(string location, string packName)
    {
        try
        {
            if (await ExpImpDel.DeletePackAsync(location) != null)
            {
                _removedOriginalNames.Add(packName);

                for (var i = EnvironmentVariables.SelectedPacks.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(EnvironmentVariables.SelectedPacks[i].Location, location, StringComparison.OrdinalIgnoreCase))
                        EnvironmentVariables.SelectedPacks.RemoveAt(i);
                }
            }
            else
            {
                Trace.WriteLine($"[ALCHITEX] Couldn't uninstall the original pack at '{location}' - its RTX version was still generated.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Failed uninstalling the original pack at '{location}': {ex.Message}");
        }
    }

    // ── Confirmation phase ───────────────────────────────────────────────────

    private readonly record struct QueuedPack(
        (string Location, string Name, string Type, bool IsAlchitexCandidate) Pack,
        bool StripExistingPbr);

    /// <summary>
    /// Turns the user's selection into the list of packs to actually generate for, asking
    /// about the ones that warrant a question first. Three cases:
    ///
    ///   * Tagged as an RTX Reactor candidate - exactly what this tool is for. No dialog.
    ///   * Already declaring "raytraced"/"pbr" - asks whether to wipe the PBR it ships and
    ///     regenerate from its color textures (AlchitexOptions.StripExistingPbr, applied to
    ///     the staged copy only). Increasingly this is a pack that declares a capability
    ///     while shipping almost no PBR content, which is the whole reason this path exists.
    ///   * Neither - a warning that there may be little here to work with, and a way out.
    ///
    /// Declining just drops that pack from the queue; the rest of the selection still runs.
    /// A pack's own Name is used verbatim from the selection (already resolved from the
    /// manifest or a .lang file upstream, formatting codes stripped) - nothing here re-reads
    /// a manifest to label a dialog.
    /// </summary>
    private async Task<List<QueuedPack>> BuildGenerationQueueAsync(
        List<(string Location, string Name, string Type, bool IsAlchitexCandidate)> selected,
        CancellationToken cancellationToken)
    {
        var queue = new List<QueuedPack>();

        foreach (var pack in selected)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var alreadyDeclaresPbr = pack.Type is "RTX" or "Vibrant Visuals";

            if (!alreadyDeclaresPbr && pack.IsAlchitexCandidate)
            {
                queue.Add(new QueuedPack(pack, StripExistingPbr: false));
                continue;
            }

            SetStatus($"Waiting for your decision on {pack.Name}...");

            var confirmed = await WhileWaitingAsync(() => alreadyDeclaresPbr
                ? ConfirmRegenerateExistingPbrAsync(pack.Name, pack.Type)
                : ConfirmUnsuitablePackAsync(pack.Name));

            if (confirmed)
            {
                queue.Add(new QueuedPack(pack, StripExistingPbr: alreadyDeclaresPbr));
            }
            else
            {
                // Declined: out of the queue for this module's lifetime, with the same
                // send-off the discard button gives.
                _dismissedLocations.Add(pack.Location);

                var tile = InputQueuePanel.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(t => t.Tag is string tagged &&
                                         string.Equals(tagged, pack.Location, StringComparison.OrdinalIgnoreCase));

                if (tile != null)
                {
                    await AnimateDismissAsync(tile);
                    InputQueuePanel.Children.Remove(tile);
                }
            }
        }

        return queue;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the reactor in its waiting stance (see
    /// ReactorAnimator.BeginWaiting) and drops the stance however the work ends.
    ///
    /// For anything the user is blocked on with nothing else to look at: a confirmation
    /// dialog, a pack being uninstalled, a folder sweep. Nesting is safe - BeginWaiting is a
    /// no-op while a wait is already up - but the inner wait's End would drop the outer
    /// one's stance early, so keep these calls flat, one per blocking step.
    /// </summary>
    private async Task<T> WhileWaitingAsync<T>(Func<Task<T>> work)
    {
        _reactor?.BeginWaiting();

        try
        {
            return await work();
        }
        finally
        {
            _reactor?.EndWaiting();
        }
    }

    private async Task WhileWaitingAsync(Func<Task> work)
    {
        _reactor?.BeginWaiting();

        try
        {
            await work();
        }
        finally
        {
            _reactor?.EndWaiting();
        }
    }

    /// <summary>Asked once per already-PBR pack. Defaults to Skip - the destructive option
    /// shouldn't be the one a stray Enter picks.</summary>
    private async Task<bool> ConfirmRegenerateExistingPbrAsync(string packName, string packType)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"{packName} might already have PBR textures!",
                Content =
                    $"\"{packName}\" declares itself as {packType} compatible, so it may already have its own PBR textures. " +
                    "RTX Reactor can delete all of them and generate a complete new set from the pack's color " +
                    "textures. Your installed copy is never modified - all of this happens on a copy, which becomes " +
                    "a separate RTX pack.\n\n" +
                    "This is worth doing for packs that declare Vibrant Visuals or RTX support but barely ship any " +
                    "PBR content. If this pack's own PBR work is good, the generated copy will not have it.",
                PrimaryButtonText = "Remove and regenerate",
                CloseButtonText = "Skip this pack",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // A dialog that couldn't be shown must not silently green-light a destructive
            // pass on someone's pack.
            Trace.WriteLine($"[ALCHITEX] Couldn't show the regenerate-PBR dialog for '{packName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Asked once per pack that's neither already-PBR nor a tagged candidate -
    /// nothing here is destructive, the pack just may not have much for us to work with.</summary>
    private async Task<bool> ConfirmUnsuitablePackAsync(string packName)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"{packName} may not be suitable for RTX enhancement by RTX Reactor!",
                Content =
                    $"\"{packName}\" isn't tagged as an \"{PackBrowserOverlay.AlchitexCandidateTag}\".\n\n" +
                    "You can still run it. Your installed copy is left untouched and the result is " +
                    "a separate RTX-compatible pack, so there's nothing to lose, " +
                    "but it may come out with little to nothing added to it, or it may not work at all.",
                PrimaryButtonText = "Generate anyway",
                CloseButtonText = "Skip this pack",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't show the unsuitable-pack dialog for '{packName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds StatusMessage/OperationSuccessful from every pack this module session has
    /// touched so far (see _succeededPackNames/_failedPackNames) - MainWindow's Closed
    /// handler for this module reads these once the module actually closes, mirroring how
    /// the shader mod/DLSS/LUT managers report their own outcome.
    /// </summary>
    private void UpdateSessionSummary()
    {
        if (_succeededPackNames.Count == 0 && _failedPackNames.Count == 0)
        {
            StatusMessage = "";
            OperationSuccessful = false;
            return;
        }

        var sb = new StringBuilder();

        if (_succeededPackNames.Count > 0)
        {
            sb.AppendLine($"Added the following RTX-capable pack{(_succeededPackNames.Count == 1 ? "" : "s")}:");
            foreach (var name in _succeededPackNames)
                sb.AppendLine($"{Helpers.StripMinecraftFormatting(name)}");
        }
        if (_succeededPackNames.Count > 0)
        {
            string pronouns = _succeededPackNames.Count == 1 ? "it" : "them";
            sb.AppendLine();
            sb.Append($"ℹ️ You can now activate {pronouns} in-game, you may also select {pronouns} from the Select other packs menu to Export or Tune {pronouns} from the main menu.");
        }

        if (_removedOriginalNames.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"🗑️ Uninstalled the original pack{(_removedOriginalNames.Count == 1 ? "" : "s")} they were generated from:");
            foreach (var name in _removedOriginalNames)
                sb.AppendLine($"{Helpers.StripMinecraftFormatting(name)}");
        }

        if (_failedPackNames.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("⚠️ Failed to add RTX support to the following:");
            foreach (var name in _failedPackNames)
                sb.AppendLine($"{Helpers.StripMinecraftFormatting(name)}");
            sb.AppendLine($"\nℹ️ Better luck with another pack!");
        }

        StatusMessage = sb.ToString().TrimEnd();
        OperationSuccessful = _succeededPackNames.Count > 0;
    }

    /// <summary>Stops the run in progress. Reached by clicking the reactor while it's
    /// showing the abort stance - there's no separate Abort button any more.</summary>
    private void AbortGeneration()
    {
        if (_generateCts == null || _generateCts.IsCancellationRequested) return;

        SetStatus("Aborting...");
        _generateCts.Cancel();

        // Drop the red X now, no grace period: the click landed, so it isn't a warning
        // about something that might happen any more.
        _reactor?.EndAbortHintImmediate();
    }

    // ── Debug: materials.json bootstrap ─────────────────────────────────────

    /// <summary>Click opens a menu rather than a picker, because this button now fronts two
    /// tools that happen to share a file - the same shape the test bench button uses.</summary>
    private void GenerateMaterialsConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        var bootstrap = new MenuFlyoutItem { Text = "Bootstrap from a PBR pack..." };
        bootstrap.Click += async (_, _) => await RunMaterialsBootstrapAsync();

        var optimize = new MenuFlyoutItem { Text = "Check and optimize an existing materials.json..." };
        optimize.Click += async (_, _) => await RunMaterialsOptimizeAsync();

        flyout.Items.Add(bootstrap);
        flyout.Items.Add(optimize);
        flyout.ShowAt(GenerateMaterialsConfigButton);
    }

    /// <summary>
    /// The end-of-session pass over a hand-tuned materials.json: report everything the
    /// pipeline would silently work around (Tools/MaterialsIntegrity), then collapse the file
    /// to the properties that actually change generated output (Tools/MaterialsOptimizer).
    ///
    /// <b>The integrity check has to run first</b>, and not for tidiness. A value the loader
    /// falls back on resolves to the fallback, so the optimizer - correctly - removes it as
    /// redundant, and a misspelt padding_type or a mistyped intensity disappears along with
    /// the evidence that it was ever wrong. Only the file as the artist wrote it can be asked.
    ///
    /// A finding never blocks the optimize. The optimizer preserves anything it cannot read
    /// and provably leaves every texture resolving as it did (§5.4), so there is nothing a
    /// report could warn about that makes the rewrite less safe - and refusing to do the thing
    /// that was asked for because of a note in a file on the desktop would be its own surprise.
    ///
    /// Picks the FOLDER materials.json lives in, not the file, for the same reason the
    /// bootstrapper does: WinRT's save picker empties the file it hands back. There is no
    /// open-file picker here either, because the two tools should not disagree about where
    /// the file is.
    /// </summary>
    private async Task RunMaterialsOptimizeAsync()
    {
        var folder = await PickFolderAsync("Check and optimize materials.json here");
        if (folder == null) return;

        var path = System.IO.Path.Combine(folder, "materials.json");

        SetGenerationControlsEnabled(false);
        GenerateProgressBar.Visibility = Visibility.Visible;
        GenerateProgressBar.IsIndeterminate = true;
        SetStatus("Checking materials.json against the pipeline's own parser...");

        try
        {
            var reportDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var (integrity, reportPath) = await WhileWaitingAsync(() => Task.Run(() =>
            {
                var report = MaterialsIntegrity.Check(path);
                return (report, MaterialsIntegrity.WriteReport(report, reportDirectory));
            }));

            SetStatus("Collapsing redundant materials.json properties...");

            var result = await WhileWaitingAsync(
                () => Task.Run(() => MaterialsOptimizer.Optimize(path)));

            var saved = result.BytesBefore > 0
                ? (result.BytesBefore - result.BytesAfter) * 100.0 / result.BytesBefore
                : 0;

            var verdict = integrity.IsClean
                ? "integrity clean"
                : $"integrity: {integrity.ProblemCount} not used as written, " +
                  $"{integrity.IgnoredCount} never read, {integrity.NoteCount} worth a look";

            SetStatusThenRevert(
                $"materials.json: {result.PropertiesRemoved} redundant properties removed from " +
                $"{result.EntriesCollapsed}/{result.EntriesRead} entries" +
                (result.EntriesSkipped > 0 ? $" ({result.EntriesSkipped} left untouched)" : "") +
                $" - {result.BytesBefore / 1024} KB -> {result.BytesAfter / 1024} KB, {saved:0.#}% smaller. " +
                $"{verdict} - report written to {reportPath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] RunMaterialsOptimizeAsync failed: {ex}");
            SetStatusThenRevert($"Failed to check/optimize materials.json: {ex.Message}");
        }
        finally
        {
            GenerateProgressBar.IsIndeterminate = false;
            GenerateProgressBar.Visibility = Visibility.Collapsed;
            SetGenerationControlsEnabled(true);
        }
    }

    private async Task RunMaterialsBootstrapAsync()
    {
        var sourceFolder = await PickFolderAsync("Use as source pack");
        if (sourceFolder == null) return;

        // A folder, not the materials.json itself. Pointing a save-file picker at the
        // existing file reads better and empties it before this ever opens it - see
        // MaterialsBootstrapper's class comment.
        var destinationFolder = await PickFolderAsync("Merge into this folder");
        if (destinationFolder == null) return;

        var outputPath = System.IO.Path.Combine(destinationFolder, "materials.json");

        SetGenerationControlsEnabled(false);
        GenerateProgressBar.Visibility = Visibility.Visible;
        GenerateProgressBar.IsIndeterminate = true;
        SetStatus("Reading texture sets and deriving materials.json...");

        try
        {
            var result = await WhileWaitingAsync(
                () => Task.Run(() => MaterialsBootstrapper.GenerateFromExistingPack(sourceFolder, outputPath)));
            // ExistingEntries is in the message on purpose: a merge that silently found
            // nothing to merge into is exactly the failure this tool used to have, and
            // "into 0 existing" says so at a glance.
            SetStatusThenRevert($"materials.json: {result.EntriesWritten} new entries merged into " +
                                $"{result.ExistingEntries} existing ({result.Skipped} skipped, " +
                                $"{result.Failed} failed) -> {result.OutputPath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] RunMaterialsBootstrapAsync failed: {ex}");
            SetStatusThenRevert($"Failed to generate materials.json: {ex.Message}");
        }
        finally
        {
            GenerateProgressBar.IsIndeterminate = false;
            GenerateProgressBar.Visibility = Visibility.Collapsed;
            SetGenerationControlsEnabled(true);
        }
    }

    /// <summary>
    /// Single-folder picker helper. The bootstrap button opens it twice in a row - source
    /// pack, then where materials.json lives - so <paramref name="commitText"/> labels the
    /// confirm button, or the second dialog is indistinguishable from the first.
    /// Debug-only tool, so the standard OS folder picker is simplest to build and least
    /// likely to need maintenance later.
    /// </summary>
    private async Task<string?> PickFolderAsync(string? commitText = null)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        if (commitText != null) picker.CommitButtonText = commitText;

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // ── Debug: PBR test bench ───────────────────────────────────────────────
    // Runs loose textures (or a folder of them) through the real generation path and writes
    // the results next to the originals - see Tools/PbrTestBench for what it does and,
    // more importantly, what it deliberately doesn't. Everything here is UI: pick or accept
    // a drop, confirm the destructive part, run it off the UI thread, report.

    private CancellationTokenSource? _testBenchCts;

    /// <summary>Click opens a small menu rather than a picker, because the OS has separate
    /// pickers for files and folders and this tool genuinely wants either. Dropping onto the
    /// button skips this entirely.</summary>
    private void PipelinePreviewDevToolButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        var pickFiles = new MenuFlyoutItem { Text = "Pick texture files..." };
        pickFiles.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            foreach (var ext in TextureSetOrchestratorOptions.CandidateExtensions)
                picker.FileTypeFilter.Add(ext);

            var files = await picker.PickMultipleFilesAsync();
            if (files is { Count: > 0 })
                await RunPbrTestBenchAsync(files.Select(f => f.Path).ToList());
        };

        var pickFolder = new MenuFlyoutItem { Text = "Pick a folder..." };
        pickFolder.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync();
            if (folder != null)
                await RunPbrTestBenchAsync(new List<string> { folder });
        };

        flyout.Items.Add(pickFiles);
        flyout.Items.Add(pickFolder);
        flyout.ShowAt(PipelinePreviewDevToolButton);
    }

    private void PipelinePreviewDevToolButton_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;

        // Null unless the drag came from outside the app, which is the only case here.
        if (e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = "Generate PBR for these";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void PipelinePreviewDevToolButton_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // Taken before the first await: the drop payload is only guaranteed readable while
        // the deferral is held.
        var deferral = e.GetDeferral();
        List<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] PbrTestBench: couldn't read the dropped items: {ex}");
            SetStatusThenRevert($"Couldn't read what was dropped: {ex.Message}");
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (paths.Count > 0)
            await RunPbrTestBenchAsync(paths);
    }

    private async Task RunPbrTestBenchAsync(IReadOnlyList<string> selectedPaths)
    {
        if (IsGenerating || _testBenchCts != null) return;

        var plan = await WhileWaitingAsync(() => Task.Run(() => PbrTestBench.Survey(selectedPaths)));

        if (plan.IsEmpty)
        {
            SetStatusThenRevert("Nothing to do - no .tga/.png/.jpg/.jpeg files in that selection.");
            return;
        }

        if (!await WhileWaitingAsync(() => ConfirmTestBenchRunAsync(plan))) return;

        // Read the same way a real run reads them, so the Secondary PBR dropdown means
        // exactly what it means for a pack.
        var options = ReadOptionsFromUI();

        _testBenchCts = new CancellationTokenSource();
        SetGenerationControlsEnabled(false);
        GenerateProgressBar.Visibility = Visibility.Visible;
        GenerateProgressBar.IsIndeterminate = true;
        SetStatus($"Test bench: generating PBR for {plan.Images.Count} texture(s)...");

        try
        {
            var progress = new Progress<AlchitexPipeline.AlchitexProgress>(p =>
            {
                if (_isClosing) return;

                if (p.Total > 0)
                {
                    GenerateProgressBar.IsIndeterminate = false;
                    GenerateProgressBar.Maximum = p.Total;
                    GenerateProgressBar.Value = p.Completed;
                    SetStatus($"Test bench [{p.Completed}/{p.Total}]: {p.StatusText}");
                }
                else
                {
                    GenerateProgressBar.IsIndeterminate = true;
                    SetStatus($"Test bench: {p.StatusText}");
                }
            });

            var token = _testBenchCts.Token;
            var result = await Task.Run(
                () => PbrTestBench.Run(plan, options, progress, token),
                token);

            SetStatusThenRevert(DescribeTestBenchResult(result, plan));
        }
        catch (OperationCanceledException)
        {
            SetStatusThenRevert("Test bench cancelled.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] PbrTestBench run failed: {ex}");
            SetStatusThenRevert($"Test bench failed: {ex.Message}");
        }
        finally
        {
            _testBenchCts?.Dispose();
            _testBenchCts = null;

            GenerateProgressBar.IsIndeterminate = false;
            GenerateProgressBar.Visibility = Visibility.Collapsed;
            SetGenerationControlsEnabled(true);
        }
    }

    private static string DescribeTestBenchResult(PbrTestBench.Result result, PbrTestBench.Plan plan)
    {
        if (!result.Success) return $"Test bench: {result.Error}";

        if (result.TextureSetsCreated == 0)
            return $"Test bench: nothing was generated - all {result.ImagesStaged} file(s) looked like generated PBR output or were skipped.";

        var text = $"Test bench: {result.TextureSetsCreated} texture set(s), {result.FilesWritten} file(s) written to {plan.Folders.Count} folder(s)";

        if (result.StaleTextureSetsRemoved > 0 || result.StalePbrTexturesRemoved > 0)
            text += $" (replaced {result.StaleTextureSetsRemoved} old set(s) and {result.StalePbrTexturesRemoved} old texture(s))";

        if (result.SkippedJunk > 0) text += $", {result.SkippedJunk} skipped";
        if (result.OrchestratorFailures > 0) text += $", {result.OrchestratorFailures} failed";

        return text + ".";
    }

    /// <summary>
    /// Asked before anything is deleted. Names the folders that will be written to, and is
    /// explicit that removal is scoped to the listed textures rather than the whole folder -
    /// which is what PbrTestBench actually does, and the difference matters when someone
    /// picks one file out of a folder full of finished work. Defaults to Close, same as the
    /// pack-regeneration dialog.
    /// </summary>
    private async Task<bool> ConfirmTestBenchRunAsync(PbrTestBench.Plan plan)
    {
        try
        {
            var mode = (SecondaryPbrMode)AlchitexVariables.Persistent.SecondaryPbrModeIndex;

            var folders = new StringBuilder();
            foreach (var folder in plan.Folders.Take(8))
                folders.AppendLine($"    {folder}");
            if (plan.Folders.Count > 8)
                folders.AppendLine($"    ...and {plan.Folders.Count - 8} more");

            var dialog = new ContentDialog
            {
                Title = "Generate PBR for these textures?",
                Content =
                    $"{plan.Images.Count} texture file(s) will be run through the PBR generation pipeline exactly " +
                    $"as if they had been found inside a resource pack, with Secondary PBR set to \"{mode}\".\n\n" +
                    "Results are written next to the originals, in:\n\n" +
                    folders +
                    "\nAny existing texture set and PBR maps belonging to those specific textures are replaced. " +
                    "Other textures in those folders are left alone, and color textures are never deleted. " +
                    "Nothing is touched until generation has succeeded.",
                PrimaryButtonText = "Generate",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ALCHITEX] Couldn't show the PBR test bench dialog: {ex.Message}");
            return false;
        }
    }

}


/* ### BACKLOG/TODO OF ALCHITEX (HIGHLY CONFIDENTIAL)

- CRITICAL ISSUE:
Manifests upgraded from legacy and V1 versions DO NOT HAVE min_engine_version field, and the app isn't adding them either
the result:
manifest isn't invalid, but MINECRAFT WON'T PARSE THE PACK IF IT IS V2 AND WITHOUT THAT FIELD! It must be there, and must default to 1.21.30 (or whatever was
the latest format version for texture sets) I THINK legacy pack_manifest to modern manfiest DO have the field in fact
Only modern but v1 manifest -> v2 manifest is lacking this.

- Normal maps are generally too strong by default and while users can tune it with a 50% intensity adjustment
it is better for you to decrease the intensity by 25%-ish across the board, or 1/3

- Don't test un-obvious code paths constantly, stop wasting them, instead, keep a watchful eye for things that you suspect MIGHT have issues
e.g. the next time you change managed assets, see if it actually applies, raise the alarm if it doesn't, there is no reason't it wouldn't apply.
But it can save a lot of time if testing of these inaccessible code paths happened naturally instead of setting low cooldowns, rebuilding, and testing explicitly.

- Reactor button animation transitions are sometimes not smooth
i.e. flower to scan stance is instant, switching between stances sometimes appear instant/not animated.
Not all transitions lead to one another smoothly, some do, especially default stance -> animation, those are animated
mid animation transitions are not
- New stance idea to happen during long-running generation:
A brief, more deliberate-feeling switch-up, some colors moving around/swapping positions instead of changing randomly. Could replace the random sweeps during generation
making sweeps exlcusive to "scanning" stance and eject/insert waves which are their own thing

- Plan the next Major steps besides the stuff below.
Mostly having to do with expanding what RTX Reactor can do, for Vibrant Visuals
And maybe exposing the dev tools as creator tools, having More in store for creators would not be bad.
The old idea of RTX Creator could materialize, with 3D live view
As for Vibrant Visuals, various presets of configurations could exist to dump besides any pack, but it'll be a lot more work. A lot.
And alternative pipeline has to be reintroduced for VV, which would be doable, these are the big long term plans.
For now the app sticks to RTX as its main obligation, do the stuff below first...

- The alchitex pipeline, critically, needs two more pieces:
One generalized fallback, i.e. instead of one "default" for anything not recognized, we put a new layer of fallback in front of it
which would be akin to the old pipeline with some generalized names

But more importantly, instead of relying textures names, we should also rely on BLOCK names as defined in blocks.json/terrain_texture.json
they reference more textures, sometimes in recognizable paths, and sometimes with completely unique names
We could still pick on them and use the correct materials.

Do it only once you have the means of implementing it cleanly alongside the existing pipeline.


- Be smart with the values chosen for recursion
An idea was, wheer confidence is high, higher mins are permissable, where confidence is lower, lower mins reduce error rate.
Or also think into the "sway" idea you had earlier
Cave vine berries, there is usually brown thrown in there, and you got oranges too.
Both have EQUALLY TERRIBLE red dominance, BUT it should be favoring the ones with higher value, and I think it does, the current recursion does that...
some tests will make it clearer.
Dominance of a color in its own domain matters more than the height of the value.

- A feature for inversing the rules of the recusion MIGHT come handy

- Double-channel target? would be nice to target more specific colors, but it will inevitably expand the mouth of the recurser and increasing rate of mistakes.

Note: MER recursion isn't too aggressive at suppressing other values in fact, it is perfect as it is, mayeb more aggressive would be better at reducing mistakes,
making it more usable in wider scenarios at the cost of looking worse where confidence is high.

- The normal map generator is a magnificent piece of work, but, it depends on the heightmap generation downstream,
which could be improved.

- Could have added something to objectively derive baseline normal and heightmap intensities from Vanilla RTX/Normals, get the median of the whole thing, calculate deviation from that median.
Then uniformly adjust all, until a baseline is reached, though with manual alterations and tweaks this may no longer be necessary and make actually become counterproductive.

*/
