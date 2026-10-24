using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Modules;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The app's one settings surface - everything that configures the app rather than a pack.
/// A sibling of <see cref="MarkdownOverlay"/> in placement and chrome (36px down, acrylic,
/// shadowed, fades in and out), but it claims only half the width and leaves the tuning surface
/// visible behind a dismiss scrim.
///
/// <para><b>The file is named for the window it serves, not for the class</b> - the same
/// convention <c>MainWindow.LampAnimator.cs</c> and <c>PackBrowserOverlay.BadgeVFX.cs</c> follow.
/// It is not a partial of MainWindow (two XAML files cannot compile into one class) and it does
/// not need to be: the split is by what you are looking at. What is on the main window is
/// handled in <c>MainWindow.xaml.cs</c>; what is on this panel - including the wipe and the
/// debug report, which used to sit over there and be called across - is handled here.</para>
///
/// <para><b>What it still reaches into MainWindow for is only what belongs to that window:</b>
/// its progress bar, its control lock, its log and its lamp. Those are reached through
/// <see cref="_host"/> rather than raised as events, because every one of them is a change to
/// that window and this control only ever lives inside it.</para>
///
/// <para><b>Nothing here is applied optimistically.</b> A path is shown only once the locator
/// that owns it has validated and cached it - the same check that runs at every startup. An
/// address is written only once <see cref="EnvironmentVariables.IsValidLink"/> accepts it. A
/// setting accepted here that startup would reject is a setting that appears to silently revert
/// itself, which reads as a bug rather than as a rejection.</para>
///
/// <para><b>The panel is never reachable while the window is busy.</b> MainWindow's
/// <c>LockControls</c> disables the titlebar's Settings button for the duration of every
/// operation, because half of what is in here - the four Minecraft locations - is what those
/// operations are reading from while they run.</para>
/// </summary>
public sealed partial class SettingsOverlay : UserControl
{
    /// <summary>
    /// The scrim's fade. Matches <see cref="MarkdownOverlay"/>'s so the two overlays feel
    /// like one surface being swapped rather than two different panels.
    /// </summary>
    private const double FADE_MS = 150;

    /// <summary>
    /// How long the panel takes to cross its own width, fading as it goes. Longer than the
    /// scrim's fade because this is travel rather than a level change - over that distance
    /// 100ms reads as a jump - and short enough to stay ahead of a second click on the gear.
    /// </summary>
    private const double SLIDE_MS = 175;

    private static bool AnimationsSuspended => Persistent.SuspendUIAnimations;

    private MainWindow? _host;
    private bool _isOpen;
    private Storyboard? _scrimStoryboard;
    private Storyboard? _panelStoryboard;

    /// <summary>
    /// Guards the handlers that write a setting while <see cref="Refresh"/> is painting the
    /// controls from that same setting. Without it, assigning <c>IsOn</c> or a TextBox's
    /// <c>Text</c> in code raises the very handler that would then write it back - harmless for
    /// the theme, but it would make the animations toggle flip itself.
    /// </summary>
    private bool _suppressCallbacks;

    /// <summary>
    /// The live launch-option rows. The UI is the model while the panel is open; it is
    /// serialized into <see cref="Persistent.LaunchOptions"/> on every edit, so there is no
    /// "unsaved" state to lose if the window closes or crashes.
    /// </summary>
    private readonly List<LaunchOptionRow> _launchRows = new();

    /// <summary>The four editable addresses, built once in the constructor - see <see cref="UrlField"/>.</summary>
    private readonly List<UrlField> _urlFields = new();

    /// <summary>Every path row, so refreshing and bevel repainting can walk them rather than naming eight controls each time.</summary>
    private PathRow[] _pathRows = Array.Empty<PathRow>();

    /// <summary>How long "Copied to clipboard!" stays on the Copy debug logs button.</summary>
    private const int CopyFeedbackMs = 1800;

    /// <summary>What that button says the rest of the time - captured rather than re-typed so the two can't drift.</summary>
    private readonly string CopyLogsDefaultLabel;

    /// <summary>Bumped on every copy so a late timer can tell whether it is still the one that owns the label.</summary>
    private int _copyFeedbackToken;

    public SettingsOverlay()
    {
        InitializeComponent();

        CopyLogsDefaultLabel = CopyLogsButtonText.Text;

        BuildPathRows();
        BuildUrlFields();

        // The path selectors' 3px seams are a ThemeService color choice, not a ThemeResource
        // binding that re-resolves itself, so they have to be repainted by hand on every theme
        // change - the same deal as OverlayHeaderBar's Close button.
        ApplyBevelColors(ThemeService.Current);
        ThemeService.ThemeChanged += ApplyBevelColors;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= ApplyBevelColors;

        VersionText.Text = $"Copyright (c) {DateTime.Today.Year} Cubeir\nVersion: {appVersion}";
    }

    /// <summary>True while the panel is showing. MainWindow reads it to toggle and to decide which overlay has to close first.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Binds the panel to its window and paints it for the first time. Called from
    /// <c>MainWindow_Loaded</c> once settings are loaded and both locators have run, so the
    /// first paint shows resolved paths rather than empty ones.
    /// </summary>
    public void Initialize(MainWindow host)
    {
        _host = host;
        BuildLaunchOptionRows(MinecraftLauncher.ParseOptions(Persistent.LaunchOptions));
        Refresh();
    }

    // =========================================================================
    //  Open / close
    // =========================================================================

    public void Show()
    {
        if (_isOpen) return;
        _isOpen = true;

        // Paths, the theme and the animation flag can all have moved since the panel was last
        // shown (the pack browser locates user data too, and a hard reset clears everything),
        // so every open repaints rather than trusting what is on screen.
        Refresh();

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        Animate(opening: true, onCompleted: null);
    }

    /// <summary>
    /// Closes the panel and flushes every setting to disk.
    ///
    /// <para><b>The save is here rather than only in MainWindow's Closed handler</b> because
    /// this panel is the one place a user changes several settings in a row and then expects
    /// them kept. Everything in here writes its value into <see cref="Persistent"/> the moment
    /// it changes, but that is memory - a crash, a hard kill, or the app being restarted by
    /// something else between now and window close would take the lot. Saving on close costs
    /// one pass over a dozen fields at the one moment the user has finished.</para>
    /// </summary>
    public void Hide()
    {
        if (!_isOpen) return;
        _isOpen = false;

        SaveSettings();

        IsHitTestVisible = false;
        Animate(opening: false, onCompleted: () => Visibility = Visibility.Collapsed);
    }

    private void ModalBlocker_Tapped(object sender, TappedRoutedEventArgs e) => Hide();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// Runs the open or close transition over the two layers independently: the scrim only
    /// fades, while the panel fades and slides across its own width from the window's left
    /// edge. Both directions are the same call - closing is opening with the ends swapped.
    ///
    /// <para><b>The travel distance is measured rather than assumed</b>, so the panel comes to
    /// rest exactly off screen at whatever width the window currently is. It needs the
    /// <see cref="UIElement.UpdateLayout"/>: the control has been Collapsed since the last
    /// close, so until a layout pass runs the panel's <c>ActualWidth</c> is still zero.</para>
    ///
    /// <para><b><paramref name="onCompleted"/> hangs off the panel's storyboard</b>, which is
    /// the longer of the two - on the scrim's it would collapse the control while the panel was
    /// still visibly moving.</para>
    /// </summary>
    private void Animate(bool opening, Action? onCompleted)
    {
        _scrimStoryboard?.Stop();
        _panelStoryboard?.Stop();
        _scrimStoryboard = null;
        _panelStoryboard = null;

        UpdateLayout();
        var offScreen = -(PanelSurface.ActualWidth > 0 ? PanelSurface.ActualWidth : ActualWidth / 2);

        var scrimTo = opening ? 1.0 : 0.0;
        var panelTo = opening ? 1.0 : 0.0;
        var slideTo = opening ? 0.0 : offScreen;

        if (AnimationsSuspended)
        {
            ModalBlocker.Opacity = scrimTo;
            PanelSurface.Opacity = panelTo;
            PanelSlide.X = slideTo;
            onCompleted?.Invoke();
            return;
        }

        // Park the panel off screen before an open. A close already left it there, but the
        // first open of the session - and any open following a close that snapped because
        // animations were suspended - starts from a panel that has never been moved.
        if (opening) PanelSlide.X = offScreen;

        _scrimStoryboard = Run(FADE_MS,
            () => ModalBlocker.Opacity = scrimTo,
            Leg(ModalBlocker, "Opacity", scrimTo));

        _panelStoryboard = Run(SLIDE_MS,
            () =>
            {
                PanelSurface.Opacity = panelTo;
                PanelSlide.X = slideTo;
                onCompleted?.Invoke();
            },
            Leg(PanelSurface, "Opacity", panelTo),
            Leg(PanelSlide, "X", slideTo));
    }

    /// <summary>
    /// Starts every leg of one layer's transition together over <paramref name="durationMs"/>.
    ///
    /// <para><b><paramref name="onCompleted"/> has to write the values the transition ended on
    /// back as its targets' base values.</b> A storyboard holds its end value but never assigns
    /// it, and <c>Stop</c> - the first thing the next transition does - reverts every target to
    /// whatever base it still has. Without the write-back, closing would snap the panel to
    /// where opening started it and then animate from there to the same place.</para>
    /// </summary>
    private static Storyboard Run(double durationMs, Action onCompleted, params DoubleAnimation[] legs)
    {
        var storyboard = new Storyboard();

        foreach (var leg in legs)
        {
            leg.Duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
            leg.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            storyboard.Children.Add(leg);
        }

        storyboard.Completed += (_, _) => onCompleted();
        storyboard.Begin();
        return storyboard;
    }

    /// <summary>
    /// One property's leg of a transition. No <c>From</c>, so an interrupted transition picks
    /// up from wherever its target currently sits; no <c>EnableDependentAnimation</c>, because
    /// an opacity and a translate transform are both animated off the UI thread already.
    /// </summary>
    private static DoubleAnimation Leg(DependencyObject target, string property, double to)
    {
        var leg = new DoubleAnimation { To = to };
        Storyboard.SetTarget(leg, target);
        Storyboard.SetTargetProperty(leg, property);
        return leg;
    }

    // =========================================================================
    //  Painting the panel from current state
    // =========================================================================

    /// <summary>
    /// Pulls every control back in line with what the app currently believes. Safe to call at
    /// any time; it writes nothing.
    /// </summary>
    public void Refresh()
    {
        _suppressCallbacks = true;
        try
        {
            var mode = Persistent.AppThemeMode ?? "System";
            ThemeSystemItem.IsChecked = mode == "System";
            ThemeLightItem.IsChecked = mode == "Light";
            ThemeDarkItem.IsChecked = mode == "Dark";
            ThemeModeLabel.Text = mode switch
            {
                "Light" => "Light",
                "Dark" => "Dark",
                _ => "Auto"
            };
            ThemeModeGlyph.Glyph = mode switch
            {
                "Light" => "",  // Brightness
                "Dark" => "",   // QuietHours - the moon the old titlebar button used
                _ => ""         // DevUpdate - "whatever Windows is set to"
            };

            SuspendAnimationsSwitch.IsOn = Persistent.SuspendUIAnimations;
            CloseGameBeforeLaunchSwitch.IsOn = Persistent.CloseGameBeforeLaunch;

            RefreshPaths();
            foreach (var row in _pathRows) ShowPathHint(row, rejection: null);
            RefreshUrlFields();
            RefreshCredits();
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    private void RefreshCredits()
    {
        var credits = TeletextContent.Credits?.FirstOrDefault()?.Text;
        CreditsText.Text = credits ?? string.Empty;
        CreditsText.Visibility = string.IsNullOrWhiteSpace(credits) ? Visibility.Collapsed : Visibility.Visible;
    }

    // =========================================================================
    //  Appearance
    // =========================================================================

    private void ThemeModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string mode) return;

        Persistent.AppThemeMode = mode;
        _host?.ApplyThemeMode();
        Refresh();

        // The lamp answers with the thing the mode means: off for dark, lit for light, and a
        // rapid flicker for Auto, which is neither and follows whatever Windows decides.
        if (_host is null) return;
        _ = mode switch
        {
            "Light" => _host.BlinkingLamp(true, true, 1.0, 0.0),
            "Dark" => _host.BlinkingLamp(true, true, 0.0, 0.0),
            _ => _host.BlinkingLamp(true, true, 0.5, 1.0)
        };
    }

    private void SuspendAnimationsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;

        Persistent.SuspendUIAnimations = SuspendAnimationsSwitch.IsOn;
        _host?.ApplySuspendUIAnimations(invokedByUser: true);
    }

    // =========================================================================
    //  Minecraft locations
    // =========================================================================

    /// <summary>
    /// One (edition x kind) location: the clickable path, the Select/Change button, the seam
    /// between them, the line under them, and the two operations that differ per kind - how to
    /// read the current path, and how to ask the user for a new one.
    /// </summary>
    private sealed class PathRow
    {
        public required TextBlock Text { get; init; }
        public required Button PathButton { get; init; }
        public required Button ChangeButton { get; init; }
        public required Border Bevel { get; init; }
        public required TextBlock Hint { get; init; }
        public required Func<string?> Current { get; init; }
        public required Func<Task<LocationPick>> Pick { get; init; }

        /// <summary>
        /// The hint's own text as written in XAML, read once at build time so the markup stays
        /// the place to edit it - see <see cref="ShowPathHint"/>, which swaps it out for a
        /// rejection and back.
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    private void BuildPathRows()
    {
        _pathRows =
        [
            new PathRow
            {
                Text = ReleaseInstallPathText, PathButton = ReleaseInstallPathButton,
                ChangeButton = ReleaseInstallButton, Bevel = ReleaseInstallBevel, Hint = ReleaseInstallHint,
                // Install paths are read straight out of the cache: MinecraftGDKLocator only
                // ever writes a path it has verified, and re-verifying here would mean a
                // filesystem walk every time the panel opens.
                Current = () => Persistent.MinecraftInstallPath,
                Pick = () => PickInstallPathAsync(isPreview: false)
            },
            new PathRow
            {
                Text = PreviewInstallPathText, PathButton = PreviewInstallPathButton,
                ChangeButton = PreviewInstallButton, Bevel = PreviewInstallBevel, Hint = PreviewInstallHint,
                Current = () => Persistent.MinecraftPreviewInstallPath,
                Pick = () => PickInstallPathAsync(isPreview: true)
            },
            new PathRow
            {
                Text = ReleaseDataPathText, PathButton = ReleaseDataPathButton,
                ChangeButton = ReleaseDataButton, Bevel = ReleaseDataBevel, Hint = ReleaseDataHint,
                // Data roots go through GetDataRoot rather than the raw field, so a folder that
                // has gone missing since startup reads as "not set" instead of as a path that
                // still works.
                Current = () => MinecraftUserDataLocator.GetDataRoot(isPreview: false),
                Pick = () => PickDataPathAsync(isPreview: false)
            },
            new PathRow
            {
                Text = PreviewDataPathText, PathButton = PreviewDataPathButton,
                ChangeButton = PreviewDataButton, Bevel = PreviewDataBevel, Hint = PreviewDataHint,
                Current = () => MinecraftUserDataLocator.GetDataRoot(isPreview: true),
                Pick = () => PickDataPathAsync(isPreview: true)
            },
        ];

        foreach (var row in _pathRows)
        {
            row.Description = row.Hint.Text;

            // The seam is drawn from the button's accent, so it has to follow that button's
            // enabled state the way MainWindow's Preview toggle bevels follow theirs - an
            // accent stripe glued to a greyed-out button reads as a rendering bug. Subscribing
            // here covers both ways it gets disabled: a picker being open, and
            // WindowControlsManager locking the window down.
            var captured = row;
            row.ChangeButton.IsEnabledChanged += (_, _) => ApplyBevelColor(captured);
        }
    }

    private void RefreshPaths()
    {
        foreach (var row in _pathRows)
        {
            var path = row.Current();
            var known = !string.IsNullOrWhiteSpace(path);

            row.Text.Text = known ? path! : "Not found";
            row.Text.Opacity = known ? 1.0 : 0.55;

            // "Select" when there is nothing set and "Change" when there is - which is also the
            // only cue that the path above it is real rather than a placeholder.
            row.ChangeButton.Content = known ? "Change" : "Select";

            row.PathButton.IsEnabled = known;
            ToolTipService.SetToolTip(row.PathButton, known
                ? "Open this folder in File Explorer."
                : "Nothing to open yet - the app hasn't found this location.");
        }
    }

    /// <summary>Repaints the four path-selector seams for a theme. See <see cref="BuildPathRows"/> for why by hand.</summary>
    private void ApplyBevelColors(ElementTheme theme)
    {
        foreach (var row in _pathRows)
            ApplyBevelColor(row, theme);
    }

    private void ApplyBevelColor(PathRow row, ElementTheme? theme = null)
        => row.Bevel.BorderBrush = new SolidColorBrush(ThemeService.GetBevelColor(
            theme ?? ActualTheme,
            ThemeService.BevelEdge.Left,
            accented: true,
            isEnabled: row.ChangeButton.IsEnabled));

    private void ReleaseInstallPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(Persistent.MinecraftInstallPath);
    private void PreviewInstallPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(Persistent.MinecraftPreviewInstallPath);
    private void ReleaseDataPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(MinecraftUserDataLocator.GetDataRoot(isPreview: false));
    private void PreviewDataPathButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer(MinecraftUserDataLocator.GetDataRoot(isPreview: true));

    /// <summary>
    /// Opens a folder in File Explorer. A path the app is showing can still be gone by the time
    /// it is clicked, and <c>explorer.exe</c> answers a missing folder by opening Documents
    /// instead of failing - so the existence check is what keeps a stale path from looking like
    /// the app navigated somewhere random on purpose.
    /// </summary>
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!Directory.Exists(path))
        {
            MainWindow.Log($"That folder isn't there any more: {path}", MainWindow.LogLevel.Warning);
            RefreshPaths();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Couldn't open '{path}' in Explorer: {ex.Message}");
            MainWindow.Log($"Couldn't open that folder: {ex.Message}", MainWindow.LogLevel.Error);
        }
    }

    private void ReleaseInstallButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[0]);
    private void PreviewInstallButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[1]);
    private void ReleaseDataButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[2]);
    private void PreviewDataButton_Click(object sender, RoutedEventArgs e) => _ = RunPickerAsync(_pathRows[3]);

    /// <summary>
    /// Runs one row's picker with its button disabled for the duration. The disable is not
    /// cosmetic: a second picker opened on top of the first resolves against the same cached
    /// field, and whichever finishes last silently wins.
    ///
    /// <para>The hint under the row then describes this pick: a rejection replaces it with
    /// why, anything else puts the description back. A cancel counts as "anything else" - the
    /// user backed out, and a warning about the attempt before that is no longer about what
    /// they are doing.</para>
    /// </summary>
    private async Task RunPickerAsync(PathRow row)
    {
        if (_host is null) return;

        row.ChangeButton.IsEnabled = false;
        try
        {
            var pick = await row.Pick();
            ShowPathHint(row, pick.Rejection);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SettingsOverlay] Path selection failed: {ex}");
        }
        finally
        {
            row.ChangeButton.IsEnabled = true;
            RefreshPaths();
        }
    }

    /// <summary>
    /// The same two styles the content-source hints swap between (<see cref="ShowUrlFieldHint"/>),
    /// so a rejected folder reads exactly like a rejected address. Says the stored path was left
    /// alone, because the path shown above the hint is still the old one and would otherwise read
    /// as the app having ignored the pick.
    /// </summary>
    private void ShowPathHint(PathRow row, string? rejection)
    {
        var rejected = rejection is not null;
        row.Hint.Text = rejected ? $"Invalid path. {rejection}" : row.Description;
        row.Hint.Style = (Style)Resources[rejected ? "SettingsHintRejectedTextStyle" : "SettingsHintTextStyle"];
    }

    private async Task<LocationPick> PickInstallPathAsync(bool isPreview)
    {
        _ = _host!.BlinkingLamp(false, true, 0.5, 1.0);

        var edition = isPreview ? "Minecraft Preview" : "Minecraft";
        var hWnd = WindowNative.GetWindowHandle(_host);

        // LocateMinecraftManuallyAsync does the picking (starting at the current path), the
        // one-level-deep tolerance, the MicrosoftGame.Config edition check and the caching.
        // Nothing is written here.
        var pick = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (pick.Path is not null)
            MainWindow.Log($"{edition} installation set: {pick.Path}", MainWindow.LogLevel.Success);
        else if (pick.Rejection is not null)
            MainWindow.Log(MinecraftGDKLocator.DescribeUnsetPick(pick, isPreview), MainWindow.LogLevel.Warning);

        return pick;
    }

    private Task<LocationPick> PickDataPathAsync(bool isPreview) => _host!.HandleManualDataLocationAsync(isPreview);

    // =========================================================================
    //  Launch options
    // =========================================================================

    /// <summary>One editable options.txt entry: its name box, its value box, and the row they live in.</summary>
    private sealed class LaunchOptionRow
    {
        public required Grid Container { get; init; }
        public required TextBox NameBox { get; init; }
        public required NumberBox ValueBox { get; init; }
    }

    private void BuildLaunchOptionRows(IEnumerable<LaunchOption> options)
    {
        _suppressCallbacks = true;
        try
        {
            _launchRows.Clear();
            LaunchOptionRows.Children.Clear();

            foreach (var option in options)
                AddRow(option.Name, option.Value);
        }
        finally
        {
            _suppressCallbacks = false;
        }

        UpdateLaunchOptionsEmptyState();
    }

    /// <summary>
    /// Builds one row in code because the number of them is the user's choice. Everything here
    /// writes through <see cref="CommitLaunchOptions"/> on change, so what the Launch button
    /// does is always exactly what is on screen.
    /// </summary>
    private void AddRow(string name, int value)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = name,
            PlaceholderText = "option name",
            FontSize = 12,
            IsTextScaleFactorEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(nameBox, "The options.txt parameter name, exactly as the game spells it (e.g. graphics_mode).");
        Grid.SetColumn(nameBox, 0);

        // NumberBox rather than a TextBox: every options.txt value the app writes is an
        // integer, and letting a non-number be typed only to be silently dropped at launch is
        // the kind of thing nobody ever finds out about.
        var valueBox = new NumberBox
        {
            Value = value,
            SmallChange = 1,
            LargeChange = 1,
            Width = 96,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            IsTextScaleFactorEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(valueBox, "The whole number to write for this parameter.");
        Grid.SetColumn(valueBox, 1);

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center
        };

        // TryGetValue rather than an indexer: the key comes from XamlControlsResources and is
        // certainly there, but a missing style should cost this button its subtlety rather than
        // throw out of a row build and leave the list half-constructed.
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle) && subtleStyle is Style style)
            removeButton.Style = style;

        ToolTipService.SetToolTip(removeButton, "Remove this option. The game keeps whatever value it already has.");
        Grid.SetColumn(removeButton, 2);

        var row = new LaunchOptionRow { Container = grid, NameBox = nameBox, ValueBox = valueBox };

        nameBox.TextChanged += (_, _) => CommitLaunchOptions();
        valueBox.ValueChanged += (_, _) => CommitLaunchOptions();
        removeButton.Click += (_, _) =>
        {
            _launchRows.Remove(row);
            LaunchOptionRows.Children.Remove(grid);
            CommitLaunchOptions();
            UpdateLaunchOptionsEmptyState();
        };

        grid.Children.Add(nameBox);
        grid.Children.Add(valueBox);
        grid.Children.Add(removeButton);

        _launchRows.Add(row);
        LaunchOptionRows.Children.Add(grid);
    }

    private void CloseGameBeforeLaunchSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;

        Persistent.CloseGameBeforeLaunch = CloseGameBeforeLaunchSwitch.IsOn;
    }

    private void AddLaunchOptionButton_Click(object sender, RoutedEventArgs e)
    {
        AddRow(string.Empty, 0);
        UpdateLaunchOptionsEmptyState();

        // Straight into the empty name box - an added row with nothing in it and no cursor
        // reads as though the button did nothing.
        _launchRows[^1].NameBox.Focus(FocusState.Programmatic);
    }

    private void ResetLaunchOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        BuildLaunchOptionRows(MinecraftLauncher.DefaultOptions);
        CommitLaunchOptions();
        MainWindow.Log("Launch options restored to defaults: ray tracing on, in-game graphics mode switching on, VSync off.", MainWindow.LogLevel.Reset);
        _ = _host?.BlinkingLamp(true, true, 0.0);
    }

    /// <summary>
    /// Serializes the rows into the persisted setting. A row whose name is still blank is left
    /// out by <see cref="MinecraftLauncher.SerializeOptions"/> rather than rejected, so a row
    /// can sit half-typed without the setting flickering between valid states.
    /// </summary>
    private void CommitLaunchOptions()
    {
        if (_suppressCallbacks) return;

        var options = _launchRows.Select(r => new LaunchOption(
            r.NameBox.Text,
            // NaN is what a NumberBox holds while its text is empty or mid-edit; 0 is the
            // value the row was created with and the only sane reading of "nothing typed".
            double.IsNaN(r.ValueBox.Value) ? 0 : (int)Math.Round(r.ValueBox.Value)));

        Persistent.LaunchOptions = MinecraftLauncher.SerializeOptions(options);
    }

    private void UpdateLaunchOptionsEmptyState() =>
        LaunchOptionsEmptyText.Visibility = _launchRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // =========================================================================
    //  Content sources
    // =========================================================================

    /// <summary>
    /// One editable address: its box, its revert button, the line under it, and the things that
    /// differ per field - what it must look like, what the built-in value is, and how to read
    /// and write the stored one.
    /// </summary>
    private sealed class UrlField
    {
        public required TextBox Box { get; init; }
        public required Button ResetButton { get; init; }
        public required TextBlock Hint { get; init; }
        public required string Description { get; init; }
        public required string Fallback { get; init; }
        public required LinkKind Kind { get; init; }
        public required Func<string?> Read { get; init; }
        public required Action<string> Write { get; init; }
    }

    private void BuildUrlFields()
    {
        _urlFields.AddRange(
        [
            new UrlField
            {
                Box = DocumentationBox, ResetButton = DocumentationResetButton, Hint = DocumentationHint,
                Description = $"The markdown file the help page renders.",
                Fallback = Defaults.DocumentationUrl, Kind = LinkKind.Markdown,
                Read = () => Persistent.DocumentationUrl, Write = v => Persistent.DocumentationUrl = v
            },
            new UrlField
            {
                Box = BugTrackerBox, ResetButton = BugTrackerResetButton, Hint = BugTrackerHint,
                Description = "The markdown file the bugs list page renders.",
                Fallback = Defaults.BugTrackerUrl, Kind = LinkKind.Markdown,
                Read = () => Persistent.BugTrackerUrl, Write = v => Persistent.BugTrackerUrl = v
            },
            new UrlField
            {
                Box = DlssProviderBox, ResetButton = DlssProviderResetButton, Hint = DlssProviderHint,
                Description = "The page the DLSS swapper's \"Download DLLs\" button browses to. Feel free to change to any other website that you can get DLSS .dll files from.",
                Fallback = Defaults.DlssProviderUrl, Kind = LinkKind.WebPage,
                Read = () => Persistent.DlssProviderUrl, Write = v => Persistent.DlssProviderUrl = v
            },
            new UrlField
            {
                Box = BetterRtxCreatorBox, ResetButton = BetterRtxCreatorResetButton, Hint = BetterRtxCreatorHint,
                Description = "The page the shader mod manager's \"Create your own preset\" button browses to. Feel free to change to any other website that you can download `.rtpack` files from.",
                Fallback = Defaults.BetterRtxCreatorUrl, Kind = LinkKind.WebPage,
                Read = () => Persistent.BetterRtxCreatorUrl, Write = v => Persistent.BetterRtxCreatorUrl = v
            },
        ]);

        foreach (var field in _urlFields)
        {
            var captured = field;

            // Committed on focus loss and on Enter, never per keystroke: a half-typed address
            // is invalid for most of the time it is being typed, and rejecting it letter by
            // letter turns the hint into a flicker nobody can read.
            captured.Box.LostFocus += (_, _) => CommitUrlField(captured);
            captured.Box.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                CommitUrlField(captured);
                DropTextFocus();
                e.Handled = true;
            };

            captured.ResetButton.Click += (_, _) =>
            {
                captured.Write(captured.Fallback);
                SetBoxText(captured, captured.Fallback);
                ShowUrlFieldHint(captured, accepted: true);
            };
        }
    }

    /// <summary>
    /// Takes focus off whatever text box has it, which is what makes committing visible: until
    /// the caret leaves, nothing has told the user whether what they typed was taken. Tapping
    /// anywhere in the panel that isn't itself an input lands here.
    ///
    /// <para>Focus goes to the close button with <see cref="FocusState.Pointer"/> rather than
    /// Programmatic - Pointer draws no keyboard focus ring, so the panel ends up with nothing
    /// visibly focused, which is the state a click on empty space should produce.</para>
    /// </summary>
    private void DropTextFocus()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox)
            CloseButton.Focus(FocusState.Pointer);
    }

    private void PanelBody_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // A tap that started inside a text box is the user placing a caret, not leaving one.
        if (e.OriginalSource is DependencyObject source && IsWithinTextInput(source)) return;
        DropTextFocus();
    }

    private static bool IsWithinTextInput(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or NumberBox) return true;

        return false;
    }

    private void RefreshUrlFields()
    {
        foreach (var field in _urlFields)
        {
            field.Box.Text = ResolveLink(field.Read(), field.Fallback, field.Kind);
            ShowUrlFieldHint(field, accepted: true);
        }
    }

    /// <summary>Writes into the box without the write coming back around as an edit to commit.</summary>
    private void SetBoxText(UrlField field, string text)
    {
        _suppressCallbacks = true;
        try { field.Box.Text = text; }
        finally { _suppressCallbacks = false; }
    }

    /// <summary>
    /// Validates what's in the box and stores it if it passes. A rejected value is left on
    /// screen with the reason under it rather than being snapped back: the user is looking at
    /// what they typed and needs to see what is wrong with it, and nothing downstream has
    /// changed because the stored value was never touched.
    /// </summary>
    private void CommitUrlField(UrlField field)
    {
        if (_suppressCallbacks) return;

        var typed = field.Box.Text?.Trim() ?? string.Empty;

        // An emptied box means "go back to the built-in one" - the same thing the revert button
        // does, and a more discoverable way to ask for it than finding that button.
        if (typed.Length == 0)
        {
            field.Write(field.Fallback);
            SetBoxText(field, field.Fallback);
            ShowUrlFieldHint(field, accepted: true);
            return;
        }

        var accepted = IsValidLink(typed, field.Kind);
        if (accepted) field.Write(typed);

        ShowUrlFieldHint(field, accepted);
    }

    /// <summary>
    /// Swaps the hint between its two styles rather than setting a colour: both are declared
    /// in markup, so both follow the app's theme - including a theme changed from this same
    /// panel while the hint is on screen.
    /// </summary>
    private void ShowUrlFieldHint(UrlField field, bool accepted)
    {
        field.Hint.Text = accepted ? field.Description : LinkRejectionReason(field.Kind);
        field.Hint.Style = (Style)Resources[accepted ? "SettingsHintTextStyle" : "SettingsHintRejectedTextStyle"];
    }

    // =========================================================================
    //  Maintenance
    //
    //  Both of these used to live on MainWindow and be called across from here. They are the
    //  settings panel's own work - nothing else in the app offers either - so they sit with the
    //  panel that offers them. What they still reach into MainWindow for is only what genuinely
    //  belongs to that window: its progress bar, its control lock, its log and its lamp.
    // =========================================================================

    private void HardResetButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _host?.BlinkingLamp(true, true, 0.0, 1.0);

        // The panel gets out of the way first: the confirmation dialog and the progress bar it
        // leads to both belong to the window behind this, and a modal scrim over them would
        // leave the user looking at a dimmed app they can't see the progress of.
        Hide();
        _ = RequestHardResetAsync();
    }

    /// <summary>
    /// The settings panel's Hard reset. Confirms, locks the window down and hands off to
    /// <see cref="WipeAllStorageData"/>, which takes it from there (including re-enabling the
    /// window when it's done).
    ///
    /// <para>The confirmation is not a formality: this deletes the Default RTX and LUT backups
    /// taken out of the user's own game files, so it may have to hand those back through
    /// several elevation prompts before they're gone. Declining leaves everything untouched.</para>
    /// </summary>
    private async Task RequestHardResetAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "You're about to completely wipe all of app's data.",
                Content = $"This will delete all of application's data across your device, including Default RTX & LUT files which the app obtained from your actual game files!" +
                $"\nAs such, you may be prompted to accept multiple admin privilege requests in order to let the app restore your game's default files before they're gone from app's data.",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _host!.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)_host!.Content).ActualTheme
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                MainWindow.Log("Wiping app's data was cancelled by user.", MainWindow.LogLevel.Warning);
                return;
            }

            WindowControlsManagerExtensions.DisableAllControls(_host);
            _host!._progressManager.ShowProgress();
            _ = _host!.BlinkingLamp(true);

            _ = WipeAllStorageData();
        }
        catch (Exception ex)
        {
            MainWindow.Log($"Hard Reset Error: {ex.Message}", MainWindow.LogLevel.Error);
            WindowControlsManagerExtensions.RestoreAllControls(_host!);
            _ = _host!.BlinkingLamp(false);
            _host!._progressManager.HideProgress();
        }
    }

    private async Task WipeAllStorageData()
    {
        try
        {
            MainWindow.Log("Starting hard reset, this will wipe all of app's storage and temporary files...", MainWindow.LogLevel.Warning);
            await Task.Delay(250);

            await GuardActivePresetsBeforeWipeAsync();

            // ── 1. Local Settings (recursive containers) ─────────────────────────
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var roamingSettings = Windows.Storage.ApplicationData.Current.RoamingSettings;
            int totalKeysWiped = 0;

            foreach (var (root, rootName) in new[] { (localSettings, "LocalSettings"), (roamingSettings, "RoamingSettings") })
            {
                foreach (var key in root.Values.Keys.ToList())
                {
                    root.Values.Remove(key);
                    MainWindow.Log($"Deleted key: {rootName}/{key}", MainWindow.LogLevel.Cache);
                    totalKeysWiped++;
                }

                foreach (var containerKey in root.Containers.Keys.ToList())
                {
                    root.DeleteContainer(containerKey);
                    MainWindow.Log($"Deleted container: {rootName}/{containerKey}", MainWindow.LogLevel.Cache);
                }
            }

            MainWindow.Log($"Wiped {totalKeysWiped} setting key(s) across all containers.", MainWindow.LogLevel.Cache);
            await Task.Delay(100);

            // ── 2. Wipe all storage folders ───────────────────────────────────────
            var foldersToWipe = new[]
            {
            (path: Windows.Storage.ApplicationData.Current.LocalFolder.Path,      label: "LocalFolder (LocalState)"),
            (path: Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, label: "LocalCacheFolder"),
            (path: Windows.Storage.ApplicationData.Current.TemporaryFolder.Path,  label: "TemporaryFolder"),
            };

            int totalItemsDeleted = 0;

            foreach (var (path, label) in foldersToWipe)
            {
                MainWindow.Log($"Wiping {label}: {path}", MainWindow.LogLevel.Cache);
                int deletedInFolder = 0;

                if (!Directory.Exists(path))
                {
                    MainWindow.Log($"{label} not found, skipping.", MainWindow.LogLevel.Cache);
                    continue;
                }

                foreach (var file in Directory.GetFiles(path))
                {
                    try
                    {
                        File.Delete(file);
                        MainWindow.Log($"Deleted file: {Path.GetFileName(file)}", MainWindow.LogLevel.Cache);
                        deletedInFolder++;
                        await Task.Delay(10);
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"Could not delete file {Path.GetFileName(file)}: {ex.Message}", MainWindow.LogLevel.Warning);
                    }
                }

                foreach (var dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        MainWindow.Log($"Deleted folder: {Path.GetFileName(dir)}", MainWindow.LogLevel.Cache);
                        deletedInFolder++;
                        await Task.Delay(15);
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"Could not delete folder {Path.GetFileName(dir)}: {ex.Message}", MainWindow.LogLevel.Warning);
                    }
                }

                MainWindow.Log($"{label} wiped ({deletedInFolder} item(s)).", MainWindow.LogLevel.Cache);
                totalItemsDeleted += deletedInFolder;
            }

            MainWindow.Log($"Deleted {totalItemsDeleted} file/folder item(s) total.", MainWindow.LogLevel.Cache);
            await Task.Delay(500);
            MainWindow.Log("Hard reset complete! The app will restart in a moment...", MainWindow.LogLevel.Lengthy);
            await Task.Delay(4444);

            Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
        }
        catch (Exception ex)
        {
            MainWindow.Log($"Error during hard reset: {ex.Message}", MainWindow.LogLevel.Error);
        }

        // ── local helpers, only meaningful when trying to wipe and user doesn't have their default presets installed if any ───────────

        async Task GuardActivePresetsBeforeWipeAsync()
        {
            MainWindow.Log("Checking for active custom presets that need to be reverted first...", MainWindow.LogLevel.ShaderMods);

            await RunGuard("Shader mods",
                DefaultsGuard.RestoreShaderModDefaultIfNeededAsync(
                    msg => MainWindow.Log(msg, MainWindow.LogLevel.Informational)));

            await RunGuard("RTX LUT (Release)",
                DefaultsGuard.RestoreLutDefaultIfNeededAsync(
                    targetPreview: false, log: msg => MainWindow.Log(msg, MainWindow.LogLevel.Informational)));

            await RunGuard("RTX LUT (Preview)",
                DefaultsGuard.RestoreLutDefaultIfNeededAsync(
                    targetPreview: true, log: msg => MainWindow.Log(msg, MainWindow.LogLevel.Informational)));

            await Task.Delay(150);
        }

        async Task RunGuard(string featureName, Task<RTXDefaultsGuard> guardTask)
        {
            var result = await guardTask;
            switch (result)
            {
                case RTXDefaultsGuard.Restored:
                    MainWindow.Log($"{featureName}: reverted to Default before wipe.", MainWindow.LogLevel.Success);
                    break;
                case RTXDefaultsGuard.RestoreFailed:
                    MainWindow.Log($"{featureName}: tried to revert to Default but it failed - the game may still be on a modified preset.", MainWindow.LogLevel.Warning);
                    break;
                case RTXDefaultsGuard.Skipped:
                    MainWindow.Log($"{featureName}: couldn't safely verify preset state - left untouched.", MainWindow.LogLevel.Warning);
                    break;
                case RTXDefaultsGuard.NoActionNeeded:
                    MainWindow.Log($"{featureName}: already on Default or nothing to protect.", MainWindow.LogLevel.Informational);
                    break;
            }
        }
    }

    /// <summary>
    /// Puts the report on the clipboard and says so on the button itself for a moment. The
    /// transient label is the only feedback there is - the clipboard gives none of its own, and
    /// the log line scrolls away in a sidebar the user may not be looking at.
    /// </summary>
    private async void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyDebugReportToClipboard();

        // Re-entrant by design: a second click restarts the countdown rather than letting the
        // first one's timer put the original label back while the second is still showing it.
        var token = ++_copyFeedbackToken;
        CopyLogsButtonText.Text = "Copied to clipboard!";

        await Task.Delay(CopyFeedbackMs);

        if (token == _copyFeedbackToken)
            CopyLogsButtonText.Text = CopyLogsDefaultLabel;
    }

    /// <summary>
    /// Builds the whole diagnostic snapshot - system info, the sidebar log, every tuner and
    /// persistent variable, the trace buffer and the live state of every supported control in
    /// this window - and puts it on the clipboard. The "Copy debug logs" button is its only caller.
    ///
    /// <para>It reflects over <c>this</c> to enumerate controls, which is what lets a control
    /// added to MainWindow.xaml years from now show up in a report without anyone remembering
    /// to list it. Nothing here throws outward: a report that can't be built is worth a log
    /// line, never a crash in the middle of someone trying to report a bug.</para>
    /// </summary>
    private void CopyDebugReportToClipboard()
    {
        try
        {
            var sb = new StringBuilder();
            AppendSystemInfo(sb);
            sb.AppendLine($"===== Sidebar Log (Last {MainWindow.MaxLogChars.ToString()} Chars)");
            string logSnapshot;
            lock (MainWindow._logGate) logSnapshot = MainWindow.LogText;
            sb.AppendLine(logSnapshot.Replace(MainWindow.EntrySentinel, Environment.NewLine));
            sb.AppendLine();
            sb.AppendLine("===== Environment Variables");
            var fields = typeof(EnvironmentVariables).GetFields(BindingFlags.Public | BindingFlags.Static);

            foreach (var field in fields)
            {
                var value = field.GetValue(null);

                // Special-case SelectedPacks - the tuple list won't print usefully via ToString()
                if (field.Name == nameof(EnvironmentVariables.SelectedPacks) &&
                    value is ObservableCollection<(string Location, string Name, string Type, bool IsAlchitexCandidate)> selectedPacks)
                {
                    if (selectedPacks.Count == 0)
                    {
                        sb.AppendLine("SelectedPacks: (empty)");
                    }
                    else
                    {
                        sb.AppendLine("SelectedPacks:");
                        foreach (var (location, name, type, isAlchitexCandidate) in selectedPacks)
                            sb.AppendLine($"  [{type}] {name} → {location}{(isAlchitexCandidate ? " (Alchitex candidate)" : "")}");
                    }
                    continue;
                }
                else if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    var items = enumerable.Cast<object>().ToList();
                    sb.AppendLine(items.Count == 0 ? $"{field.Name}: (empty)" : $"{field.Name}:");
                    foreach (var item in items)
                        sb.AppendLine($"  {FormatValue(item)}");
                    continue;
                }

                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }
            static string FormatValue(object? value)
            {
                if (value is null) return "null";
                if (value is System.Runtime.CompilerServices.ITuple tuple)
                {
                    var items = new object?[tuple.Length];
                    for (int i = 0; i < tuple.Length; i++)
                        items[i] = tuple[i]?.ToString() ?? "null";
                    return string.Join(", ", items);
                }
                return value.ToString() ?? "null";
            }

            sb.AppendLine();
            // Persistent variables
            sb.AppendLine("===== Persistent Environment Variables");
            var persistentFields = typeof(EnvironmentVariables.Persistent).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (var field in persistentFields)
            {
                var value = field.GetValue(null);
                sb.AppendLine($"{field.Name}: {value ?? "null"}");
            }
            sb.AppendLine();
            // Trace logs
            sb.AppendLine(TraceManager.GetAllTraceLogs());

            // UI Controls State
            sb.AppendLine();
            sb.AppendLine("===== UI Controls State");
            CollectUIControlsState(sb);

            // Last, beside the trace it explains: a failure the log only names by its message
            // shows up here with the stack that threw it.
            sb.AppendLine();
            sb.AppendLine(TraceManager.GetRecentExceptions());

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);
            MainWindow.Log("Copied app logs to clipboard.", MainWindow.LogLevel.Success);
            _ = _host!.BlinkingLamp(true, true, 0.0, 1.0);

            // ============================================
            void CollectUIControlsState(StringBuilder sb)
            {
                var fields = _host!.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                foreach (var field in fields)
                {
                    var value = field.GetValue(_host);
                    if (value == null) continue;

                    var type = value.GetType();
                    var name = field.Name;

                    // Toggle-type controls
                    if (value is ToggleButton toggleBtn)
                    {
                        sb.AppendLine($"{name} (ToggleButton): {toggleBtn.IsChecked?.ToString() ?? "null"}");
                    }
                    else if (value is CheckBox checkBox)
                    {
                        sb.AppendLine($"{name} (CheckBox): {checkBox.IsChecked?.ToString() ?? "null"}");
                    }
                    else if (value is ToggleSwitch toggleSwitch)
                    {
                        sb.AppendLine($"{name} (ToggleSwitch): {toggleSwitch.IsOn}");
                    }
                    else if (value is RadioButton radioBtn)
                    {
                        sb.AppendLine($"{name} (RadioButton): {radioBtn.IsChecked?.ToString() ?? "null"}");
                    }
                    // Value controls
                    else if (value is Slider slider)
                    {
                        sb.AppendLine($"{name} (Slider): {slider.Value}");
                    }
                    else if (value is NumberBox numberBox)
                    {
                        sb.AppendLine($"{name} (NumberBox): {numberBox.Value}");
                    }
                    else if (value is ComboBox comboBox)
                    {
                        sb.AppendLine($"{name} (ComboBox): SelectedIndex={comboBox.SelectedIndex}, SelectedItem={comboBox.SelectedItem?.ToString() ?? "null"}");
                    }
                    else if (value is TextBox textBox)
                    {
                        var text = textBox.Text;
                        if (!string.IsNullOrEmpty(text) && text.Length > 50)
                            text = text.Substring(0, 50) + "...";
                        sb.AppendLine($"{name} (TextBox): \"{text}\"");
                    }
                    else if (value is RatingControl rating)
                    {
                        sb.AppendLine($"{name} (RatingControl): {rating.Value}");
                    }
                    else if (value is ColorPicker colorPicker)
                    {
                        sb.AppendLine($"{name} (ColorPicker): {colorPicker.Color}");
                    }
                    else if (value is DatePicker datePicker)
                    {
                        sb.AppendLine($"{name} (DatePicker): {datePicker.Date}");
                    }
                    else if (value is TimePicker timePicker)
                    {
                        sb.AppendLine($"{name} (TimePicker): {timePicker.Time}");
                    }
                }
            }
            void AppendSystemInfo(StringBuilder sb)
            {
                sb.AppendLine("===== System Info");

                // Process architecture = what's actually executing right now
                sb.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
                // OS architecture = the machine's native architecture (differs from above if running under emulation)
                sb.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
                sb.AppendLine($"Is Emulated (x64-on-ARM64): {(RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)}");

                sb.AppendLine($"OS Version: {RuntimeInformation.OSDescription}");
                sb.AppendLine($".NET Runtime: {RuntimeInformation.FrameworkDescription}");

                sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
                sb.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
                sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
                sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");

                try
                {
                    var package = Windows.ApplicationModel.Package.Current;
                    var v = package.Id.Version;
                    sb.AppendLine($"Package Version: {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                    sb.AppendLine($"Package Architecture: {package.Id.Architecture}");
                    sb.AppendLine($"Package Full Name: {package.Id.FullName}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Package Info: unavailable ({ex.Message})");
                }

                try
                {
                    var culture = System.Globalization.CultureInfo.CurrentUICulture;
                    sb.AppendLine($"UI Culture: {culture.Name}");
                }
                catch { /* non-critical */ }

#if DEBUG
                sb.AppendLine("Build: Debug");
#else
                sb.AppendLine("Build: Release");
#endif

                // Every timestamp in the trace is local time with no offset, so this is what lines
                // the report up against a user saying "it broke around 3pm".
                var now = DateTimeOffset.Now;
                sb.AppendLine($"Report Time: {now:yyyy-MM-dd HH:mm:ss} (UTC{now:zzz})");

                try
                {
                    using var process = Process.GetCurrentProcess();
                    sb.AppendLine($"Session Uptime: {DateTime.Now - process.StartTime:d\\.hh\\:mm\\:ss}");
                    sb.AppendLine($"Threads: {process.Threads.Count}, Handles: {process.HandleCount}");
                }
                catch { /* non-critical */ }

                sb.AppendLine($"Managed Heap: {GC.GetTotalMemory(false) / 1024 / 1024} MB");

                // Energy saver switches off the app's own motion (the Alchitex backdrop, the log's
                // tick rate), so it explains "the animation doesn't play" reports on its own.
                try { sb.AppendLine($"Energy Saver: {Windows.System.Power.PowerManager.EnergySaverStatus}"); }
                catch { /* non-critical */ }

                // Layout reports are unreadable without the size and scale they happened at.
                try
                {
                    if (_host is not null)
                    {
                        var size = _host.AppWindow.Size;
                        var scale = _host.Content?.XamlRoot?.RasterizationScale ?? 1.0;
                        sb.AppendLine($"Window: {size.Width}x{size.Height} px at {scale * 100:0}% scale");
                    }
                }
                catch { /* non-critical */ }

                // The DLSS and shader mod import flows are the only WebView2 users and degrade to an
                // error card without it, so a report from either wants to know what is installed.
                try { sb.AppendLine($"WebView2 Runtime: {Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()}"); }
                catch (Exception ex) { sb.AppendLine($"WebView2 Runtime: unavailable ({ex.GetType().Name})"); }

                // A control switched off from the announcements .md looks exactly like a bug from
                // the outside - the controls section below shows it disabled, never why.
                var suspended = TeletextContent.SuspendControls;
                sb.AppendLine($"Remotely Suspended Controls: {(suspended is { Length: > 0 } ? string.Join(", ", suspended) : "none")}");

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] Error building the debug report: {ex}");
            MainWindow.Log("Something went wrong while building the debug report.", MainWindow.LogLevel.Error);
        }
    }

    // =========================================================================
    //  Links
    // =========================================================================

    private void GitHubLink_Click(object sender, RoutedEventArgs e)
        => _ = MainWindow.OpenUrl("https://github.com/vanilla-rtx/Vanilla-RTX-App");

    private void KoFiLink_Click(object sender, RoutedEventArgs e)
    {
        _ = MainWindow.OpenUrl("https://ko-fi.com/cubeir");
    }

    private void DiscordLink_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Log("Here is the invitation!\nDiscord.gg/A4wv4wwYud", MainWindow.LogLevel.VanillaRTX);
        _ = MainWindow.OpenUrl("https://discord.gg/A4wv4wwYud");
    }

    /// <summary>
    /// The two vanillartx.net links resolve their origin on click - the domain, or its
    /// workers.dev fallback when the domain isn't answering. The link is disabled while that
    /// probe runs, so a second click can't open the page twice.
    /// </summary>
    private async void WebsiteLink_Click(object sender, RoutedEventArgs e)
    {
        WebsiteLink.IsEnabled = false;
        try { _ = MainWindow.OpenUrl(await Helpers.ResolveVanillaRtxSiteUrlAsync()); }
        finally { WebsiteLink.IsEnabled = true; }
    }

    /// <summary>Opens over the panel, so Return lands back here - see <see cref="MainWindow.OpenVanillaRtxChessAsync"/>.</summary>
    private async void ChessLink_Click(object sender, RoutedEventArgs e)
    {
        ChessLink.IsEnabled = false;
        try { if (_host is not null) await _host.OpenVanillaRtxChessAsync(); }
        finally { ChessLink.IsEnabled = true; }
    }
}
