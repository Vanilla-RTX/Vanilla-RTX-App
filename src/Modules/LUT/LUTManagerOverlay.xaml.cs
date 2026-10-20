using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.LUT;

/// <summary>
/// The RTX LUT manager's overlay: chrome, the preset dropdown, the crossfading preview image
/// and the install button. Everything it does to actual files - preset discovery, the backup
/// of the game's own three files, detecting what is installed and the elevated write that
/// installs a preset - lives in <see cref="LUTManager"/>. This holds one and renders it.
/// </summary>
public sealed partial class LUTManagerOverlay : ModuleOverlay
{
    private bool _isClosing;

    private readonly LUTManager _manager = new();

    /// <summary>
    /// Which edition this is for, taken once at construction rather than read live. The
    /// defaults folder, the game path and the elevated write all belong to the edition it
    /// opened under, and MainWindow disables the Preview toggle for as long as it is up - so
    /// the two can't diverge, and the snapshot is what keeps that from being load-bearing.
    /// </summary>
    private readonly bool _isPreview = Persistent.IsTargetingPreview;

    /// <summary>
    /// Whether this edition's Default backup holds what a rollback needs. False disables the
    /// dropdown and the install button outright: writing colour grading into somebody's game
    /// with no way back is the one thing this must never do.
    /// </summary>
    private bool _defaultsReady;

    /// <summary>Why, when it isn't ready - drives the wording on the notice card.</summary>
    private LUTManager.DefaultsState _defaultsState = LUTManager.DefaultsState.Ready;

    private LutPreset? _selectedPreset;
    private LutPreset? _installedPreset;

    private CancellationTokenSource? _scanCancellationTokenSource;
    private bool _crossfadeInProgress = false;

    /// <summary>
    /// True from the moment an install is started until it has finished and the button has
    /// been restored. Read and written only on the UI thread, and always cleared in a finally -
    /// see <see cref="InstallButton_Click"/>.
    /// </summary>
    private bool _installInProgress;


    public LUTManagerOverlay()
    {
        this.InitializeComponent();
        PrepareContent();

        // The install button's bevel is a ThemeService colour rather than a ThemeResource
        // binding, so it has to be repainted by hand on every theme change - the same deal as
        // the settings panel's path seams.
        ThemeService.ThemeChanged += ApplyTheme;
        InstallButton.IsEnabledChanged += (s, e) => ApplyInstallButtonBevel(_isPresetInstalled);

        this.Loaded += LUTManagerOverlay_Loaded;
    }

    private async void LUTManagerOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= LUTManagerOverlay_Loaded;

            if (_isClosing) return;

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    protected override void OnClosing()
    {
        if (_isClosing) return;
        _isClosing = true;

        this.Loaded -= LUTManagerOverlay_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        ThemeService.ThemeChanged -= ApplyTheme;
    }

    private void ApplyTheme(ElementTheme theme) => ApplyInstallButtonBevel(_isPresetInstalled);

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = _isPreview;
            var cachedPath = isPreview
                ? Persistent.MinecraftPreviewInstallPath
                : Persistent.MinecraftInstallPath;

            string? minecraftPath = null;

            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath, isPreview))
            {
                Trace.WriteLine($"[LUTManager] Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine("[LUTManager] Cache became invalid, clearing");
                    if (isPreview)
                        Persistent.MinecraftPreviewInstallPath = null;
                    else
                        Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                    ManualSelectionPanel.Visibility = Visibility.Visible);

                _scanCancellationTokenSource = new CancellationTokenSource();
                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview, _scanCancellationTokenSource.Token);

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[LUTManager] System search cancelled or failed");
                    return;
                }
            }

            if (minecraftPath != null)
                await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"LUTM EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        // Step 1: Bind to this edition's install and establish its LocalAppData defaults folder
        if (!_manager.TryAttach(minecraftPath, _isPreview))
        {
            StatusMessage = "Could not establish defaults folder";
            this.Close();
            return;
        }

        // Step 2: Back up the game's own ray tracing files, mending the install first if it
        // is the one that's incomplete. Whether that succeeded decides whether anything can
        // be installed at all, so it is settled before a single preset is offered rather than
        // discovered when someone clicks Install on a list that implied it would work.
        _defaultsState = await _manager.EnsureDefaultsBackedUpAsync();
        _defaultsReady = _defaultsState == LUTManager.DefaultsState.Ready && _manager.DefaultsComplete;
        if (!_defaultsReady)
            Trace.WriteLine($"[LUTManager] ✗ No usable Default backup ({_defaultsState}) - installing is disabled");

        // Step 3: Discover all presets (Default first, then Modules\LUT\Presets\ subfolders)
        _manager.LoadPresets();

        // Step 4: Detect which preset is currently installed
        _installedPreset = await _manager.DetectCurrentPresetAsync();
        Trace.WriteLine($"[LUTManager] Detected preset: {_installedPreset?.Name ?? "Unknown"}");

        // Step 5: Populate dropdown and settle the UI
        PopulateDropdown(_installedPreset);

        // Step 6: Show main UI
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
            ApplyDefaultsNotice();
            TeletextCard.Populate(LutManagerInfoPanel, TeletextContent.LutManagerInfo);
        });
    }

    /// <summary>
    /// Shows the blocked-state card when there is no usable backup, and takes the dropdown
    /// down with it - the card explains, the disabled controls enforce. A static
    /// Pinned-TeletextCard lookalike in the XAML, same as the shader mod manager's two: this isn't news and
    /// there is nothing to dismiss.
    /// </summary>
    private void ApplyDefaultsNotice()
    {
        if (_defaultsReady)
        {
            DefaultsMissingCard.Visibility = Visibility.Collapsed;
            return;
        }

        DefaultsMissingText.Text = _defaultsState switch
        {
            LUTManager.DefaultsState.GameRunningAPreset =>
                "Your game is already running one of this app's LUT presets, and there's no backup of your original ray tracing files to go with it - " +
                "so the app can't tell what your originals were, and backing up what's there now would make that preset permanent. Installing is disabled rather than risk that.\n\n" +
                "Repairing or reinstalling Minecraft from the Xbox app/Microsoft Store puts its own files back; reopen this module afterwards and the backup will be taken properly.",

            LUTManager.DefaultsState.GameFilesMissing =>
                "Your Minecraft installation is missing the ray tracing files this feature works with, and the app couldn't mend them - so there's nothing to back up, " +
                "and without a backup there would be no way back from a preset. Installing is disabled.\n\n" +
                "Repairing or reinstalling Minecraft from the Xbox app/Microsoft Store should sort it; reopen this module afterwards.",

            _ =>
                "The app couldn't write a backup of your game's original ray tracing files, so installing presets is disabled - " +
                "without a backup there would be no way back to how the game looked before.\n\n" +
                "This is usually free disk space or a permissions problem on the app's own data folder."
        };

        DefaultsMissingCard.Visibility = Visibility.Visible;
        SelectPresetMenu.IsEnabled = false;
        SelectPresetMenu.Content = "Unavailable";
        InstallButton.IsEnabled = false;
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellationTokenSource?.Cancel();
        var hWnd = WindowHandle;
        var pick = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(_isPreview, hWnd);

        if (pick.Path is { } path)
            await ContinueInitializationWithPath(path);
        else
        {
            StatusMessage = MinecraftGDKLocator.DescribeUnsetPick(pick, _isPreview);
            this.Close();
        }
    }

    // -------------------------------------------------------------------------
    // Dropdown population
    // -------------------------------------------------------------------------

    private void PopulateDropdown(LutPreset? installedPreset)
    {
        _ = this.DispatcherQueue.TryEnqueue(() =>
        {
            var flyout = SelectPresetMenu.Flyout as MenuFlyout;
            if (flyout == null) return;
            flyout.Items.Clear();

            foreach (var preset in _manager.Presets)
            {
                var item = new MenuFlyoutItem
                {
                    Text = preset.IsComplete ? preset.Name : $"{preset.Name} (incomplete)",
                    Tag = preset,
                    IsEnabled = preset.IsComplete
                };
                item.Click += PresetMenuItem_Click;
                flyout.Items.Add(item);
            }

            if (installedPreset != null)
            {
                ApplySelection(installedPreset);
            }
            else
            {
                _selectedPreset = null;
                SelectPresetMenu.Content = "Select a preset...";
                InstallButton.IsEnabled = false;
                UpdatePresetImage(null);
            }

            // ApplySelection above re-enables the button from the preset's own completeness,
            // which knows nothing about the backup. Re-assert the gate after it, not before.
            if (!_defaultsReady)
                ApplyDefaultsNotice();
        });
    }

    private void PresetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is LutPreset preset)
            ApplySelection(preset);
    }

    private void ApplySelection(LutPreset? preset)
    {
        _selectedPreset = preset;

        if (preset == null)
        {
            SelectPresetMenu.Content = "Select a preset...";
            InstallButton.IsEnabled = false;
            UpdatePresetImage(null);
            return;
        }

        bool isInstalled = _installedPreset != null &&
                           string.Equals(preset.Name, _installedPreset.Name, StringComparison.OrdinalIgnoreCase);

        SelectPresetMenu.Content = isInstalled
            ? $"Installed Preset: {preset.Name}"
            : $"Selected Preset: {preset.Name}";

        // An install in flight keeps the button down even if the user picks a different preset
        // from the dropdown while it runs - without this, selecting one would hand the button
        // straight back and a second click would start a second elevated copy. The backup gate
        // is in here for the same reason: this runs on every dropdown change.
        InstallButton.IsEnabled = preset.IsComplete && !_installInProgress && _defaultsReady;

        if (isInstalled)
        {
            InstallButton.Content = "Reinstall";
            InstallButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ApplyInstallButtonBevel(true);
        }
        else
        {
            InstallButton.Content = "Install";
            InstallButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ApplyInstallButtonBevel(false);
        }

        UpdatePresetImage(preset);
    }

    private bool _isPresetInstalled; // mirrors whatever local `isInstalled` your install-state method already computes
    private void ApplyInstallButtonBevel(bool isInstalled)
    {
        _isPresetInstalled = isInstalled;

        LeftEdgeOfInstallButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(LeftEdgeOfInstallButton.ActualTheme, ThemeService.BevelEdge.Left,
                accented: !isInstalled, isEnabled: InstallButton.IsEnabled));
    }

    // -------------------------------------------------------------------------
    // Preset preview image — crossfade between two layered Image vessels
    // -------------------------------------------------------------------------

    private string? _currentImagePath = null;

    private void UpdatePresetImage(LutPreset? preset)
    {
        var imagePath = _manager.ResolveImagePath(preset);

        if (string.Equals(imagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase))
            return;

        _ = this.DispatcherQueue.TryEnqueue(() => CrossfadeToImage(imagePath));
    }

    private void CrossfadeToImage(string? newImagePath)
    {
        if (_crossfadeInProgress)
            return;

        var newBitmap = LoadPreviewBitmap(newImagePath);

        // Suspended: assign straight into the settled state the Completed handler below
        // would leave things in, with no storyboard.
        if (Persistent.SuspendUIAnimations)
        {
            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 1;
            PresetImageTop.Opacity = 0;
            PresetImageTop.Source = null;
            _currentImagePath = newImagePath;
            return;
        }

        bool bottomIsEmpty = _currentImagePath == null;

        PresetImageTop.Source = newBitmap;
        PresetImageTop.Opacity = 0;

        _crossfadeInProgress = true;

        var storyboard = new Storyboard();

        const double duration = 0.2;

        if (bottomIsEmpty)
        {
            var fadeInBottom = MakeOpacityAnimation(PresetImageBottom, from: 0, to: 1, duration: duration);
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: duration);
            storyboard.Children.Add(fadeInBottom);
            storyboard.Children.Add(fadeInTop);

            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 0;
        }
        else
        {
            var fadeInTop = MakeOpacityAnimation(PresetImageTop, from: 0, to: 1, duration: duration);
            storyboard.Children.Add(fadeInTop);
        }

        storyboard.Completed += (s, e) =>
        {
            PresetImageBottom.Source = newBitmap;
            PresetImageBottom.Opacity = 1;
            PresetImageTop.Opacity = 0;
            PresetImageTop.Source = null;

            _currentImagePath = newImagePath;
            _crossfadeInProgress = false;
        };

        storyboard.Begin();
    }

    /// <summary>
    /// <summary>
    /// Decode ceiling for preset previews, in physical pixels - a guard rail rather than a
    /// correction. The bundled previews are authored at this width, so it does nothing until
    /// something larger is dropped into the Presets folder.
    ///
    /// <para>A decoded image costs width x height x 4 bytes of graphics memory whatever it
    /// weighs on disk, which is why this is the one thing worth bounding: a preview authored
    /// at 4K is ~16MB of VRAM per swap no matter how small the JPEG is, and a handful of them
    /// will exhaust a modest GPU. 2200 is the strip's own width at 200% scale.</para>
    ///
    /// <para>Constant rather than measured off the control: XAML's image cache is keyed on
    /// the URI alone, so asking it for the same file at a second size returns whichever one
    /// it feels like - including nothing.</para>
    /// </summary>
    private const int PreviewDecodeWidth = 2200;

    /// <summary>
    /// <see cref="BitmapImage.DecodePixelWidth"/> has to be set before UriSource or it is
    /// ignored outright - the whole reason this isn't <c>new BitmapImage(uri)</c>.
    /// </summary>
    private static BitmapImage? LoadPreviewBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodeWidth };
            bitmap.UriSource = new Uri(path);
            return bitmap;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Image load error: {ex.Message}");
            return null;
        }
    }

    private static DoubleAnimation MakeOpacityAnimation(UIElement target, double from, double to, double duration)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(duration)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    // -------------------------------------------------------------------------
    // Install button
    // -------------------------------------------------------------------------

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset == null || !_selectedPreset.IsComplete)
        {
            Trace.WriteLine("[LUTManager] InstallButton_Click with no valid preset - ignoring");
            return;
        }

        if (!_defaultsReady)
        {
            Trace.WriteLine("[LUTManager] InstallButton_Click with no usable Default backup - ignoring");
            return;
        }

        // Installing is an elevated file copy: it writes a batch script, raises a UAC prompt
        // and waits, with the UI thread free for most of it. The disabled button below is the
        // visible half of stopping a second one from starting; this flag is the half that
        // doesn't depend on every path that touches IsEnabled getting it right.
        // (Helpers.ReplaceFilesWithElevation refuses overlapping calls outright as a backstop.)
        if (_installInProgress)
        {
            Trace.WriteLine("[LUTManager] An install is already in progress - ignoring this click");
            return;
        }

        var preset = _selectedPreset;

        // Nothing between setting the flag and entering the try, so there is no statement that
        // could throw its way past the finally and leave the button permanently down.
        _installInProgress = true;
        try
        {
            InstallButton.IsEnabled = false; // fires IsEnabledChanged -> dims bevel using current _isPresetInstalled, correct mid-install look

            Trace.WriteLine($"[LUTManager] Installing preset [{preset.Name}]");

            bool success = await _manager.InstallAsync(preset);

            // The elevated replace has been through either way by here - see the same call in
            // the shader mod and DLSS installs.
            _ = Host.BlinkingLamp(true, true, success ? 1.0 : 0.0, 1.0);

            if (success)
            {
                OperationSuccessful = true;
                StatusMessage = $"Installed LUT preset: {preset.Name}";
                Trace.WriteLine($"[LUTManager] Preset [{preset.Name}] installed");

                _installedPreset = await _manager.DetectCurrentPresetAsync();
                Trace.WriteLine("[LUTManager] Post-install detection: " + (_installedPreset?.Name ?? "Unknown"));

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_selectedPreset != null)
                        ApplySelection(_selectedPreset);
                });
            }
            else
            {
                Trace.WriteLine($"[LUTManager] Install of [{preset.Name}] failed or was cancelled");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LUTManager] Error in InstallButton_Click: {ex.Message}");
        }
        finally
        {
            // Cleared before the callback below re-enables the button, so ApplySelection - which
            // consults this flag - agrees with what that callback is about to draw.
            _installInProgress = false;

            _ = this.DispatcherQueue.TryEnqueue(() =>
            {
                bool isInstalled = _installedPreset != null &&
                                   string.Equals(_installedPreset.Name, preset.Name, StringComparison.OrdinalIgnoreCase);

                InstallButton.Content = isInstalled ? "Reinstall" : "Install";
                InstallButton.Style = (Style)Application.Current.Resources[
                    isInstalled ? "DefaultButtonStyle" : "AccentButtonStyle"];

                // Order matters: set IsEnabled first (fires IsEnabledChanged with the
                // still-stale _isPresetInstalled), then ApplyInstallButtonBevel runs
                // explicitly with the fresh isInstalled value and wins — final bevel
                // state is always correct regardless of what the auto-handler drew first.
                InstallButton.IsEnabled = _selectedPreset?.IsComplete == true && _defaultsReady;
                ApplyInstallButtonBevel(isInstalled);
            });
        }
    }
}
