using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Windows.Storage;
using static Vanilla_RTX_App.Core.EnvironmentVariables; // For Public Pack version variables, if null or empty = not installed

namespace Vanilla_RTX_App.Modules.PackUpdater;

public sealed partial class PackUpdaterOverlay : ModuleOverlay
{
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;
    private bool _isClosing;

    // With animations suspended the hover overlay is assigned, not crossfaded - see
    // AnimateOpacity.
    private static bool AnimationsSuspended => Persistent.SuspendUIAnimations;

    private static readonly TimeSpan _fadeInDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan _fadeOutDuration = TimeSpan.FromMilliseconds(125);

    // The guard against deploying a stale cached zipball is PackUpdater.InvalidateCacheIfStaleAsync,
    // called from UpdateAllButtonStates on every refresh of this window. It asks whether the CACHE
    // is behind the remote - not whether what the user has INSTALLED is - and only that question
    // may drop the zipball: a user merely running an older pack is no reason to throw away a
    // current 11MB download. It carries no cooldown because it makes no requests: the remote
    // numbers are the ones just fetched, and the cache's own are read off the zipball on disk.

    private string? _currentInstallActionType;

    private DispatcherTimer? _installingAnimationTimer;
    private int _animationDots = 0;

    /// <summary>The refresh button, shown in MainWindow's titlebar while this module is open.</summary>
    protected internal override FrameworkElement? TitleBarStrip => TitleBarActions;

    // Next allowed refresh, stored rather than kept in memory so closing and reopening the module
    // doesn't hand the user a fresh button and another three requests.
    private const string REFRESH_COOLDOWN_KEY = "PackUpdater_RefreshCooldown_NextAllowed";
    private const int REFRESH_COOLDOWN_SECONDS = 60;
    private DispatcherTimer? _refreshCooldownTimer;
    private bool _refreshInProgress;

    public PackUpdaterOverlay(MainWindow mainWindow)
    {
        this.InitializeComponent();
        PrepareContent();

        // Dead until this window has finished opening - the strip is in the titlebar from the
        // moment the module appears, and a press during the initial load would run a second
        // version check alongside the first. StartRefreshCooldownTimer brings it back.
        RefreshButton.IsEnabled = false;

        InitializeHoverEffects();

        SpecialOccasionPanel.Visibility = Helpers.GetSpecialOccasionName() == "christmas"
            ? Visibility.Visible
            : Visibility.Collapsed;

        _mainWindow = mainWindow;
        _updater = mainWindow._updater ?? new PackUpdater();

        this.Loaded += PackUpdaterOverlay_Loaded;
    }
    private async void PackUpdaterOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= PackUpdaterOverlay_Loaded;

            if (_isClosing) return;

            await InitializePackInformation();
            if (_isClosing) return;

            SetupButtonHandlers();
            CheckAndHandleOngoingInstallation();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] The _Loaded Event Crashed: {ex.Message}");
        }
        finally
        {
            // In the finally because the button starts dead: a load that threw would otherwise
            // leave the one control that could put things right disabled for the module's life.
            if (!_isClosing) StartRefreshCooldownTimer();
        }
    }

    protected override void OnClosing()
    {
        if (_isClosing) return;
        _isClosing = true;

        this.Loaded -= PackUpdaterOverlay_Loaded;

        StopInstallingAnimation();

        _refreshCooldownTimer?.Stop();
        _refreshCooldownTimer = null;
    }

    // REFRESH =================================

    /// <summary>
    /// Asks everything again: where the packs are and what versions they are, then what the
    /// repository currently offers, ignoring the stored copy. The same work opening this module
    /// does, which is why it goes through the same two methods.
    ///
    /// <para>Refused while an install is running. A refresh re-evaluates the cached zipball and
    /// can drop it, and that zipball is the file an install is reading out of.</para>
    /// </summary>
    /// <summary>
    /// Opens the Vanilla RTX changelog in MainWindow's document overlay, over this module - so
    /// it swaps with Help and Bugs, remembers its place, and closing it returns here untouched.
    /// </summary>
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => Host.ToggleVanillaRtxChangelog();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshInProgress || IsRefreshOnCooldown()) return;

        if (_updater.IsInstallationInProgress())
        {
            Trace.WriteLine("[PackUpdaterOverlay] An installation is in progress - ignoring refresh");
            return;
        }

        _refreshInProgress = true;
        try
        {
            RefreshButton.IsEnabled = false;

            VanillaRTX_AvailableLoading.Visibility = Visibility.Visible;
            VanillaRTX_AvailableVersion.Visibility = Visibility.Collapsed;
            VanillaRTXNormals_AvailableLoading.Visibility = Visibility.Visible;
            VanillaRTXNormals_AvailableVersion.Visibility = Visibility.Collapsed;
            VanillaRTXOpus_AvailableLoading.Visibility = Visibility.Visible;
            VanillaRTXOpus_AvailableVersion.Visibility = Visibility.Collapsed;

            await RefreshInstalledVersions();
            if (_isClosing) return;

            // Armed only when the repository actually answered: a check that failed must not
            // spend the user's next attempt, which is the one thing they can still do about it.
            if (await FetchAndDisplayRemoteVersions(force: true))
                ArmRefreshCooldown();

            _ = Host?.BlinkingLamp(true, true, 0.5, 1.0);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshInProgress = false;
            UpdateRefreshButtonState();
        }
    }

    private static bool IsRefreshOnCooldown() => RefreshCooldownRemaining() > 0;

    private static int RefreshCooldownRemaining()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[REFRESH_COOLDOWN_KEY] is not string stamp) return 0;
            if (!DateTimeOffset.TryParse(stamp, out var until)) return 0;

            var remaining = until - DateTimeOffset.UtcNow;

            // Further out than the cooldown itself is a clock that moved, or a corrupt value -
            // either would otherwise disable the button for good.
            if (remaining > TimeSpan.FromSeconds(REFRESH_COOLDOWN_SECONDS)) return 0;

            return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void ArmRefreshCooldown()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[REFRESH_COOLDOWN_KEY] =
                DateTimeOffset.UtcNow.AddSeconds(REFRESH_COOLDOWN_SECONDS).ToString("o");
        }
        catch { }
    }

    /// <summary>
    /// Paints the button for the current cooldown and keeps a one-second timer running to count
    /// it down. The countdown is also the freshness readout: it is armed whenever the versions on
    /// screen came from a request rather than from the stored copy, so a live button means what
    /// is displayed was read back from disk.
    /// </summary>
    private void StartRefreshCooldownTimer()
    {
        UpdateRefreshButtonState();

        _refreshCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshCooldownTimer.Tick += (_, _) => UpdateRefreshButtonState();
        _refreshCooldownTimer.Start();
    }

    private void UpdateRefreshButtonState()
    {
        if (_isClosing) return;

        var remaining = RefreshCooldownRemaining();

        if (remaining > 0)
        {
            RefreshButton.IsEnabled = false;
            RefreshIcon.Visibility = Visibility.Collapsed;
            RefreshCountdownText.Visibility = Visibility.Visible;
            RefreshCountdownText.Text = remaining.ToString();
            return;
        }

        RefreshButton.IsEnabled = !_refreshInProgress && !_updater.IsInstallationInProgress();
        RefreshIcon.Visibility = Visibility.Visible;
        RefreshCountdownText.Visibility = Visibility.Collapsed;
    }

    // INITIALIZATION =================================
    private void InitializeHoverEffects()
    {
        // Main Panels
        SetupPanelHoverEffect(NormalsPanel, NormalsOverlay);
        SetupPanelHoverEffect(VanillaPanel, VanillaOverlay);
        SetupPanelHoverEffect(OpusPanel, OpusOverlay);

        // SpecialOccasionPanel
        SetupPanelHoverEffect(SpecialOccasionPanel, SpecialOccasionOverlay);

        // Secondary Panels
        SetupPanelHoverEffect(AddOnsPanel, AddOnsOverlay);
        SetupPanelHoverEffect(ChemistryPanel, ChemistryOverlay);
        SetupPanelHoverEffect(CreativePanel, CreativeOverlay);
    }

    private void SetupPanelHoverEffect(Border panel, Border overlay)
    {
        panel.PointerEntered += (s, e) =>
        {
            AnimateOpacity(overlay, 1.0, _fadeInDuration);
        };

        panel.PointerExited += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };
        panel.PointerCaptureLost += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };
        panel.PointerCanceled += (s, e) =>
        {
            AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        };

        // The overlay is the link now, not just a title inside it, so it is a full-card tab stop
        // that happens to be invisible until hovered. Revealing it on focus is what keeps a
        // keyboard user from landing on a link they cannot see.
        if (overlay.Child is Control link)
        {
            link.GotFocus += (s, e) => AnimateOpacity(overlay, 1.0, _fadeInDuration);
            link.LostFocus += (s, e) => AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        }
    }

    private void AnimateOpacity(UIElement element, double toValue, TimeSpan duration)
    {
        if (AnimationsSuspended)
        {
            element.Opacity = toValue;
            return;
        }

        var storyboard = new Storyboard();

        var opacityAnimation = new DoubleAnimation
        {
            To = toValue,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase
            {
                EasingMode = toValue > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        storyboard.Children.Add(opacityAnimation);
        storyboard.Begin();
    }


    // ======================= Initialization =======================

    private async Task InitializePackInformation()
    {
        TeletextCard.Populate(PackUpdateInfoPanel, TeletextContent.PackUpdateInfo, cardFontSize: 13);
        TeletextCard.Populate(PackUpdateExtraInfoPanel, TeletextContent.PackUpdateExtraInfo, cardFontSize: 13);
        UpdateInstalledVersionDisplays();
        await FetchAndDisplayRemoteVersions();
    }

    private void UpdateInstalledVersionDisplays()
    {
        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        VanillaRTX_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXVersion) ? "Not installed" : vanillaRTXVersion;

        VanillaRTXNormals_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXNormalsVersion) ? "Not installed" : vanillaRTXNormalsVersion;

        VanillaRTXOpus_InstalledVersion.Text =
            string.IsNullOrEmpty(vanillaRTXOpusVersion) ? "Not installed" : vanillaRTXOpusVersion;
    }

    /// <summary>
    /// Fills the three Available rows and the buttons under them.
    ///
    /// <para>Opening the module asks for versions no older than
    /// <see cref="PackUpdater.OverlayVersionMaxAge"/> - someone looking at these numbers is owed
    /// something fresher than the background glyph is, without every open being a request.
    /// <paramref name="force"/> ignores the stored copy entirely, which is what the refresh
    /// button asks for.</para>
    /// </summary>
    /// <returns>True when at least one version came from the repository just now, rather than from the stored copy or the cached zipball.</returns>
    private async Task<bool> FetchAndDisplayRemoteVersions(bool force = false)
    {
        (string? version, VersionSource source) rtx = (null, VersionSource.Remote);
        (string? version, VersionSource source) normals = (null, VersionSource.Remote);
        (string? version, VersionSource source) opus = (null, VersionSource.Remote);

        try
        {
            var result = await _updater.GetRemoteVersionsAsync(PackUpdater.OverlayVersionMaxAge, force);
            rtx = result.rtx;
            normals = result.normals;
            opus = result.opus;
        }
        catch
        {
            // Fetch failed completely
        }

        // Anything read straight from the remote arms the refresh cooldown, so the button says
        // which of the two produced what is on screen. A forced refresh armed it at the click.
        // A version is only "from the remote" if there is one - the initial values above carry
        // that source with no version behind them, which is what a fetch that threw leaves.
        static bool CameFromRemote((string? version, VersionSource source) pack) =>
            !string.IsNullOrEmpty(pack.version) && pack.source == VersionSource.Remote;

        var answered = CameFromRemote(rtx) || CameFromRemote(normals) || CameFromRemote(opus);

        // Opening the window arms the cooldown itself; a forced refresh lets its caller decide,
        // because it has a button to re-enable when nothing came back.
        if (!force && answered)
            ArmRefreshCooldown();

        var vanillaRTXVersion = VanillaRTXVersion;
        var vanillaRTXNormalsVersion = VanillaRTXNormalsVersion;
        var vanillaRTXOpusVersion = VanillaRTXOpusVersion;

        VanillaRTX_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTX_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTX_AvailableVersion.Text = GetAvailabilityText(rtx.version, vanillaRTXVersion, rtx.source);

        VanillaRTXNormals_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXNormals_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXNormals_AvailableVersion.Text = GetAvailabilityText(normals.version, vanillaRTXNormalsVersion, normals.source);

        VanillaRTXOpus_AvailableLoading.Visibility = Visibility.Collapsed;
        VanillaRTXOpus_AvailableVersion.Visibility = Visibility.Visible;
        VanillaRTXOpus_AvailableVersion.Text = GetAvailabilityText(opus.version, vanillaRTXOpusVersion, opus.source);

        await UpdateAllButtonStates(rtx.version, normals.version, opus.version,
            vanillaRTXVersion, vanillaRTXNormalsVersion, vanillaRTXOpusVersion);

        return answered;
    }

    private string GetAvailabilityText(string? availableVersion, string? installedVersion, VersionSource source)
    {
        if (string.IsNullOrEmpty(availableVersion))
        {
            return "Failed to check";
        }

        bool isUpToDate = !string.IsNullOrEmpty(installedVersion) && availableVersion == installedVersion;

        string suffix = "";
        if (source == VersionSource.ZipballFallback)
        {
            // does the case where installed version is older than an offline cache really ever happen? NAH! offline cache would only be there if user has updated recently
            // But we're ready! lovely overengineered bullshit
            suffix = isUpToDate ? "(Up-to-date, from offline cache)" : "(From offline cache)";
        }
        else if (source == VersionSource.CachedRemote)
        {
            suffix = isUpToDate ? "(You're up-to-date)" : "";
        }
        else
        {
            suffix = isUpToDate ? "(You're up-to-date!)" : "";
        }

        return $"{availableVersion} {suffix}";
    }

    private async Task UpdateAllButtonStates(
        string? rtxRemote,
        string? normalsRemote,
        string? opusRemote,
        string? rtxInstalled,
        string? normalsInstalled,
        string? opusInstalled)
    {
        // Drop the cached zipball if, and only if, it is actually behind the remote. Runs on every
        // refresh with no cooldown because it makes no requests at all - the remote numbers are
        // the ones just fetched above, and the cache's are read off the zipball on disk.
        await _updater.InvalidateCacheIfStaleAsync(rtxRemote, normalsRemote, opusRemote);

        await UpdateSingleButtonState(VanillaRTX_InstallButton, VanillaRTX_EnhancementsToggle,
            PackType.VanillaRTX, rtxInstalled, rtxRemote);
        await UpdateSingleButtonState(VanillaRTXNormals_InstallButton, VanillaRTXNormals_EnhancementsToggle,
            PackType.VanillaRTXNormals, normalsInstalled, normalsRemote);
        await UpdateSingleButtonState(VanillaRTXOpus_InstallButton, VanillaRTXOpus_EnhancementsToggle,
            PackType.VanillaRTXOpus, opusInstalled, opusRemote);
    }

    private async Task UpdateSingleButtonState(Button button, ToggleSwitch toggle,
        PackType packType, string? installedVersion, string? remoteVersion)
    {
        bool isInstalled = !string.IsNullOrEmpty(installedVersion);
        bool remoteAvailable = !string.IsNullOrEmpty(remoteVersion);
        bool packInCache = await _updater.DoesPackExistInCache(packType);

        button.IsEnabled = true;
        toggle.IsEnabled = true;

        if (!isInstalled)
        {
            button.Content = "Install";
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.IsEnabled = remoteAvailable || packInCache;
        }
        else if (remoteAvailable && _updater.IsRemoteVersionNewerThanInstalled(installedVersion, remoteVersion))
        {
            button.Content = "Update";
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.IsEnabled = true;
        }
        else
        {
            button.Content = "Reinstall";
            button.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            button.IsEnabled = remoteAvailable || packInCache;
        }


        // If installation is in progress, handle differently
        if (_updater.IsInstallationInProgress())
        {
            var currentlyInstalling = _updater.GetCurrentlyInstallingPack();
            if (currentlyInstalling == packType)
            {
                // This pack is being installed - show Installing... with animation
                button.IsEnabled = false;
                toggle.IsEnabled = false;
                // Animation is handled by timer
                return;
            }
            else
            {
                // Different pack is being installed - disable this button
                button.IsEnabled = false;
                toggle.IsEnabled = false;
                return;
            }
        }
    }

    // ======================= Installation State Management =======================

    private void CheckAndHandleOngoingInstallation()
    {
        if (_updater.IsInstallationInProgress())
        {
            var currentPack = _updater.GetCurrentlyInstallingPack();

            // Start animation timer
            StartInstallingAnimation(currentPack);

            // Disable all buttons
            DisableAllInstallButtons();

            // Monitor for completion
            MonitorInstallationCompletion();
        }
    }

    private void StartInstallingAnimation(PackType? packType)
    {
        if (packType == null) return;

        _animationDots = 0;

        _installingAnimationTimer = new DispatcherTimer();
        _installingAnimationTimer.Interval = TimeSpan.FromMilliseconds(500);
        _installingAnimationTimer.Tick += (s, e) =>
        {
            _animationDots = (_animationDots + 1) % 4;
            var dots = new string('.', _animationDots);


            var actionWord = _currentInstallActionType ?? "Installing";
            var actionIng = actionWord switch
            {
                "Update" => "Updating",
                "Install" => "Installing",
                "Reinstall" => "Reinstalling",
                _ => "Installing"
            };

            var buttonText = $"{actionIng}{dots}";

            switch (packType.Value)
            {
                case PackType.VanillaRTX:
                    VanillaRTX_InstallButton.Content = buttonText;
                    break;
                case PackType.VanillaRTXNormals:
                    VanillaRTXNormals_InstallButton.Content = buttonText;
                    break;
                case PackType.VanillaRTXOpus:
                    VanillaRTXOpus_InstallButton.Content = buttonText;
                    break;
            }
        };
        _installingAnimationTimer.Start();
    }

    private void StopInstallingAnimation()
    {
        if (_installingAnimationTimer != null)
        {
            _installingAnimationTimer.Stop();
            _installingAnimationTimer = null;
        }
    }

    private async void MonitorInstallationCompletion()
    {
        // Poll for installation completion
        while (_updater.IsInstallationInProgress())
        {
            await Task.Delay(500);
        }

        // Installation completed
        StopInstallingAnimation();

        // Refresh versions and re-enable buttons
        await RefreshInstalledVersions();
        await FetchAndDisplayRemoteVersions();
    }

    private void DisableAllInstallButtons()
    {
        VanillaRTX_InstallButton.IsEnabled = false;
        VanillaRTX_EnhancementsToggle.IsEnabled = false;

        VanillaRTXNormals_InstallButton.IsEnabled = false;
        VanillaRTXNormals_EnhancementsToggle.IsEnabled = false;

        VanillaRTXOpus_InstallButton.IsEnabled = false;
        VanillaRTXOpus_EnhancementsToggle.IsEnabled = false;
    }

    // ======================= Button Handlers =======================

    private void SetupButtonHandlers()
    {
        VanillaRTX_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTX, VanillaRTX_EnhancementsToggle.IsOn);

        VanillaRTXNormals_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTXNormals, VanillaRTXNormals_EnhancementsToggle.IsOn);

        VanillaRTXOpus_InstallButton.Click += (s, e) =>
            StartInstallation(PackType.VanillaRTXOpus, VanillaRTXOpus_EnhancementsToggle.IsOn);
    }

    private async void StartInstallation(PackType packType, bool enableEnhancements)
    {
        // Check if installation is already running
        if (_updater.IsInstallationInProgress())
        {
            Trace.WriteLine("Installation already in progress - ignoring button click");
            return;
        }

        _currentInstallActionType = GetButtonForPackType(packType)?.Content?.ToString();
        Button? GetButtonForPackType(PackType packType)
        {
            return packType switch
            {
                PackType.VanillaRTX => VanillaRTX_InstallButton,
                PackType.VanillaRTXNormals => VanillaRTXNormals_InstallButton,
                PackType.VanillaRTXOpus => VanillaRTXOpus_InstallButton,
                _ => null
            };
        }


        // Disable all buttons immediately
        DisableAllInstallButtons();

        // Start animation for this pack
        StartInstallingAnimation(packType);

        // A pack install is a download and an extraction over the whole pack, and the one
        // thing in this app besides tuning that runs long enough to be worth saying so in the
        // titlebar. Nothing else ever turns the continuous blink off, so the finally below is
        // the only place it stops and every path out of here has to go through it.
        _ = Host.BlinkingLamp(true);

        try
        {
            var success = await Task.Run(() =>
                _updater.UpdateSinglePackAsync(packType, enableEnhancements));

            Trace.WriteLine($"{GetPackDisplayName(packType)} " +
                            (success ? "installed successfully" : "installation failed"));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error installing {GetPackDisplayName(packType)}: {ex.Message}");
        }
        finally
        {
            _ = Host.BlinkingLamp(false);

            // Stop animation
            StopInstallingAnimation();

            // Refresh versions and button states
            await RefreshInstalledVersions();
            await FetchAndDisplayRemoteVersions();
        }
    }

    private async Task RefreshInstalledVersions()
    {
        await _mainWindow.LocatePacksTask();

        this.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateInstalledVersionDisplays();
        });
    }

    private string GetPackDisplayName(PackType packType)
    {
        return packType switch
        {
            PackType.VanillaRTX => "Vanilla RTX",
            PackType.VanillaRTXNormals => "Vanilla RTX Normals",
            PackType.VanillaRTXOpus => "Vanilla RTX Opus",
            _ => "Unknown Pack"
        };
    }
}
