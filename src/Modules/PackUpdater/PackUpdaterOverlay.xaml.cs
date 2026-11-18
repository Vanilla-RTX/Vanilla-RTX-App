using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Windows.Storage;
using static Vanilla_RTX_App.Core.EnvironmentVariables; // installed versions: null or empty = not installed

namespace Vanilla_RTX_App.Modules.PackUpdater;

/// <summary>
/// The "Get latest RTX packs" module: the three Vanilla RTX cards and the Vanilla RTX Extras list,
/// both read from their repository's release index and installed through the same
/// <see cref="PackUpdater.InstallAsync"/> (see <see cref="PackUpdater"/> for the model). The
/// Extras half lives in <c>PackUpdaterOverlay.Extras.cs</c>.
///
/// <para><b>One install at a time across the whole window.</b> While one runs every install
/// button is disabled and the pack being installed animates "Installing..." on its own button.
/// The work runs on MainWindow's <see cref="PackUpdater"/>, so closing the module mid-install lets
/// it finish, and a reopened module picks the state back up (<see cref="ResumeOngoingInstallation"/>).</para>
/// </summary>
public sealed partial class PackUpdaterOverlay : ModuleOverlay
{
    private readonly MainWindow _mainWindow;
    private readonly PackUpdater _updater;
    private bool _isClosing;

    // With animations suspended the hover overlay is assigned, not crossfaded - see AnimateOpacity.
    private static bool AnimationsSuspended => Persistent.SuspendUIAnimations;

    private static readonly TimeSpan _fadeInDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan _fadeOutDuration = TimeSpan.FromMilliseconds(125);

    private ReleaseIndex? _vanillaIndex;

    private DispatcherTimer? _installingAnimationTimer;
    private int _animationDots;
    private string _installVerb = "Installing";

    /// <summary>The changelog and refresh buttons, shown in MainWindow's titlebar while this module is open.</summary>
    protected internal override FrameworkElement? TitleBarStrip => TitleBarActions;

    // Next allowed refresh, stored rather than kept in memory so closing and reopening the module
    // doesn't hand the user a fresh button and another round of requests.
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
        // check alongside the first. StartRefreshCooldownTimer brings it back.
        RefreshButton.IsEnabled = false;

        InitializeHoverEffects();

        _mainWindow = mainWindow;
        _updater = mainWindow._updater ?? new PackUpdater();

        VanillaRTX_InstallButton.Click += (_, _) => StartInstall(PackUpdater.VanillaRtx, new[] { PackUpdater.Uuid(PackType.VanillaRTX) }, VanillaRTX_InstallButton);
        VanillaRTXNormals_InstallButton.Click += (_, _) => StartInstall(PackUpdater.VanillaRtx, new[] { PackUpdater.Uuid(PackType.VanillaRTXNormals) }, VanillaRTXNormals_InstallButton);
        VanillaRTXOpus_InstallButton.Click += (_, _) => StartInstall(PackUpdater.VanillaRtx, new[] { PackUpdater.Uuid(PackType.VanillaRTXOpus) }, VanillaRTXOpus_InstallButton);

        this.Loaded += PackUpdaterOverlay_Loaded;
    }

    private async void PackUpdaterOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= PackUpdaterOverlay_Loaded;
            if (_isClosing) return;

            TeletextCard.Populate(PackUpdateInfoPanel, TeletextContent.PackUpdateInfo, cardFontSize: 13);
            TeletextCard.Populate(PackUpdateExtraInfoPanel, TeletextContent.PackUpdateExtraInfo, cardFontSize: 13);

            UpdateInstalledVersionDisplays();

            // Both sources in parallel. Opening arms the refresh cooldown when either answered -
            // a countdown means opening already did what pressing the button would.
            var vanilla = LoadVanillaRtxAsync(force: false);
            var extras = LoadExtrasAsync(force: false);
            var answered = await vanilla;
            answered |= await extras;
            if (_isClosing) return;

            if (answered) ArmRefreshCooldown();
            ResumeOngoingInstallation();
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

    // ======================= Refresh =======================

    /// <summary>
    /// Opens the Vanilla RTX changelog in MainWindow's document overlay, over this module - so
    /// it swaps with Help and Bugs, remembers its place, and closing it returns here untouched.
    /// </summary>
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => Host.ToggleVanillaRtxChangelog();

    /// <summary>
    /// Asks everything again: where the packs are and what versions they are, then both indexes,
    /// past their cooldowns. Refused while an install runs - it would prune the cache an install
    /// may be reading from.
    /// </summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshInProgress || IsRefreshOnCooldown()) return;
        if (_updater.IsInstallationInProgress()) return;

        _refreshInProgress = true;
        try
        {
            RefreshButton.IsEnabled = false;
            SetVanillaLoading();
            ShowExtrasLoading(true);

            await RefreshInstalledVersions();
            if (_isClosing) return;

            var vanilla = LoadVanillaRtxAsync(force: true);
            var extras = LoadExtrasAsync(force: true);
            var answered = await vanilla;
            answered |= await extras;
            if (_isClosing) return;

            // Armed only when a repository actually answered: a check that failed must not spend
            // the user's next attempt, which is the one thing they can still do about it.
            if (answered) ArmRefreshCooldown();

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

    // ======================= Hover =======================

    private void InitializeHoverEffects()
    {
        SetupPanelHoverEffect(NormalsPanel, NormalsOverlay);
        SetupPanelHoverEffect(VanillaPanel, VanillaOverlay);
        SetupPanelHoverEffect(OpusPanel, OpusOverlay);
        SetupPanelHoverEffect(AddOnsPanel, AddOnsOverlay);
        SetupPanelHoverEffect(ChemistryPanel, ChemistryOverlay);
        SetupPanelHoverEffect(CreativePanel, CreativeOverlay);
    }

    private void SetupPanelHoverEffect(Border panel, Border overlay)
    {
        panel.PointerEntered += (_, _) => AnimateOpacity(overlay, 1.0, _fadeInDuration);
        panel.PointerExited += (_, _) => AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        panel.PointerCaptureLost += (_, _) => AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        panel.PointerCanceled += (_, _) => AnimateOpacity(overlay, 0.0, _fadeOutDuration);

        // The overlay is the link, so it is a full-card tab stop that happens to be invisible
        // until hovered. Revealing it on focus is what keeps a keyboard user from landing on a
        // link they cannot see.
        if (overlay.Child is Control link)
        {
            link.GotFocus += (_, _) => AnimateOpacity(overlay, 1.0, _fadeInDuration);
            link.LostFocus += (_, _) => AnimateOpacity(overlay, 0.0, _fadeOutDuration);
        }
    }

    private void AnimateOpacity(UIElement element, double toValue, TimeSpan duration)
    {
        if (AnimationsSuspended)
        {
            element.Opacity = toValue;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = toValue,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = toValue > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ======================= Vanilla RTX cards =======================

    private IEnumerable<(PackType Type, Button Button, ToggleSwitch Toggle, TextBlock Installed, TextBlock Available, ProgressRing Loading, string? InstalledVersion)> VanillaCards()
    {
        yield return (PackType.VanillaRTX, VanillaRTX_InstallButton, VanillaRTX_EnhancementsToggle, VanillaRTX_InstalledVersion, VanillaRTX_AvailableVersion, VanillaRTX_AvailableLoading, VanillaRTXVersion);
        yield return (PackType.VanillaRTXNormals, VanillaRTXNormals_InstallButton, VanillaRTXNormals_EnhancementsToggle, VanillaRTXNormals_InstalledVersion, VanillaRTXNormals_AvailableVersion, VanillaRTXNormals_AvailableLoading, VanillaRTXNormalsVersion);
        yield return (PackType.VanillaRTXOpus, VanillaRTXOpus_InstallButton, VanillaRTXOpus_EnhancementsToggle, VanillaRTXOpus_InstalledVersion, VanillaRTXOpus_AvailableVersion, VanillaRTXOpus_AvailableLoading, VanillaRTXOpusVersion);
    }

    private void UpdateInstalledVersionDisplays()
    {
        foreach (var card in VanillaCards())
            card.Installed.Text = string.IsNullOrEmpty(card.InstalledVersion) ? "Not installed" : card.InstalledVersion;
    }

    private void SetVanillaLoading()
    {
        foreach (var card in VanillaCards())
        {
            card.Loading.Visibility = Visibility.Visible;
            card.Available.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Reads the Vanilla RTX index and repaints the three cards. True when the repository answered just now.</summary>
    private async Task<bool> LoadVanillaRtxAsync(bool force)
    {
        var checkedRemote = false;
        try
        {
            (_vanillaIndex, checkedRemote) = await PackUpdater.GetIndexAsync(PackUpdater.VanillaRtx, force);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Vanilla RTX index failed: {ex.Message}");
        }

        if (!_isClosing) PaintVanillaCards();
        return checkedRemote;
    }

    private void PaintVanillaCards()
    {
        var busy = _updater.IsInstallationInProgress();

        foreach (var card in VanillaCards())
        {
            var pack = _vanillaIndex?.Find(PackUpdater.Uuid(card.Type));
            var installed = card.InstalledVersion;

            card.Loading.Visibility = Visibility.Collapsed;
            card.Available.Visibility = Visibility.Visible;
            card.Available.Text = pack is null
                ? "Failed to check"
                : installed == pack.VersionDisplay ? $"{pack.VersionDisplay} (You're up-to-date!)" : pack.VersionDisplay;

            var outdated = pack is not null && PackUpdater.IsNewer(installed, pack.VersionDisplay);
            card.Button.Content = string.IsNullOrEmpty(installed) ? "Install" : outdated ? "Update" : "Reinstall";
            card.Button.Style = (Style)Application.Current.Resources[string.IsNullOrEmpty(installed) || outdated ? "AccentButtonStyle" : "DefaultButtonStyle"];
            card.Button.IsEnabled = pack is not null && !busy;
            card.Toggle.IsEnabled = !busy && pack?.WithoutEnhancements is not null;

            if (pack is not null)
            {
                var asset = pack.AssetFor(card.Toggle.IsOn);
                ToolTipService.SetToolTip(card.Button, PackUpdater.IsCached(PackUpdater.VanillaRtx, pack, card.Toggle.IsOn)
                    ? "Installs from the copy the app already downloaded."
                    : $"Downloads {FormatSize(asset.Size)} from the Vanilla RTX repository.");
            }
        }
    }

    // ======================= Installing =======================

    /// <summary>
    /// Installs packs from one repository and repaints everything afterwards. The enhancements
    /// choice is read per pack: a Vanilla RTX pack follows its card's toggle, an extra always gets
    /// the full build.
    /// </summary>
    private async void StartInstall(ReleaseRepository repo, IReadOnlyList<string> uuids, Button? clicked)
    {
        if (uuids.Count == 0 || _updater.IsInstallationInProgress()) return;

        var label = clicked?.Content as string ?? "";
        _installVerb = label.StartsWith("Update") ? "Updating" : label.StartsWith("Reinstall") ? "Reinstalling" : "Installing";

        DisableAllInstallButtons();
        StartInstallingAnimation();

        // The lamp blinks for as long as anything installs, and the finally is the only place it
        // stops - every path out of here has to go through it.
        _ = Host.BlinkingLamp(true);

        try
        {
            var toggles = VanillaCards().ToDictionary(c => PackUpdater.Uuid(c.Type), c => c.Toggle.IsOn, StringComparer.OrdinalIgnoreCase);
            var (installed, failed) = await _updater.InstallAsync(repo, uuids,
                pack => !toggles.TryGetValue(pack.Uuid, out var on) || on);

            if (installed.Count > 0)
                MainWindow.Log($"Installed {string.Join(", ", installed.Select(p => $"{p.Name} {p.VersionDisplay}"))}.", MainWindow.LogLevel.Package);
            if (failed.Count > 0)
                MainWindow.Log($"Couldn't install {string.Join(", ", failed.Select(p => p.Name))}. Check your connection and try again.", MainWindow.LogLevel.Warning);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Install failed: {ex.Message}");
        }
        finally
        {
            _ = Host.BlinkingLamp(false);
            StopInstallingAnimation();

            if (!_isClosing)
            {
                await RefreshInstalledVersions();
                await LoadVanillaRtxAsync(force: false);
                await LoadExtrasAsync(force: false);
            }
        }
    }

    private void DisableAllInstallButtons()
    {
        foreach (var card in VanillaCards())
        {
            card.Button.IsEnabled = false;
            card.Toggle.IsEnabled = false;
        }
        SetExtrasButtonsEnabled(false);
    }

    /// <summary>The button standing for <paramref name="uuid"/>, on either section.</summary>
    private Button? ButtonFor(string? uuid)
    {
        if (uuid is null) return null;
        if (PackUpdater.TypeOf(uuid) is { } type) return VanillaCards().First(c => c.Type == type).Button;
        return _extraButtons.TryGetValue(uuid, out var button) ? button : null;
    }

    private void StartInstallingAnimation()
    {
        StopInstallingAnimation();
        _animationDots = 0;

        _installingAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _installingAnimationTimer.Tick += (_, _) =>
        {
            _animationDots = (_animationDots + 1) % 4;
            if (ButtonFor(_updater.CurrentlyInstallingUuid) is { } button)
                button.Content = _installVerb + new string('.', _animationDots);
        };
        _installingAnimationTimer.Start();
    }

    private void StopInstallingAnimation()
    {
        _installingAnimationTimer?.Stop();
        _installingAnimationTimer = null;
    }

    /// <summary>
    /// An install started before this module was (re)opened: everything stays disabled, the
    /// running pack animates, and the window repaints once it finishes.
    /// </summary>
    private async void ResumeOngoingInstallation()
    {
        if (!_updater.IsInstallationInProgress()) return;

        DisableAllInstallButtons();
        StartInstallingAnimation();

        while (_updater.IsInstallationInProgress() && !_isClosing)
            await Task.Delay(500);

        StopInstallingAnimation();
        if (_isClosing) return;

        await RefreshInstalledVersions();
        await LoadVanillaRtxAsync(force: false);
        await LoadExtrasAsync(force: false);
    }

    private async Task RefreshInstalledVersions()
    {
        await _mainWindow.LocatePacksTask();
        if (!_isClosing) UpdateInstalledVersionDisplays();
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
