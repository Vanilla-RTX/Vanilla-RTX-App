using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Windows.Storage;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.ShaderMods;

internal enum DownloadStatus
{
    NotDownloaded,
    Queued,
    Downloading,
    Downloaded
}

internal class DownloadQueueItem
{
    public DownloadQueueItem() { }

    /// <summary>The row's key in the status map - a BetterRTX UUID, or one of the manager's <c>svrtx:</c> keys.</summary>
    public string? Key { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// The download itself. The queue only paces and reports; what a download is - an API preset
    /// by UUID, Supervanilla RTX's /latest, one build from its index - is the row's to say.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? Download { get; set; }
}


/// <summary>
/// The shader mod manager's window: chrome, the disclaimer dialog, the sectioned preset list it
/// draws, the refresh cooldown, drag/drop, the bottom bar's three import routes, and the download
/// queue that feeds the list. Everything below that - the cache, both providers, reading presets
/// off disk, downloading, importing, hashing and the elevated install - lives in
/// <see cref="ShaderModManager"/>. This holds one and renders it.
///
/// <para><b>The list is three sections, in a fixed order</b>: Default, Supervanilla RTX, BetterRTX.
/// Which section a preset is drawn in is never decided here - it is
/// <see cref="ShaderModManager.ClassifyFolder"/>'s answer, the same one that decides which
/// provider's wipes may delete it, so a preset can never sit in one section while being cleared
/// with the other's.</para>
/// </summary>
public sealed partial class ShaderModManagerOverlay : ModuleOverlay, Core.FileActivation.IFileActivationTarget
{
    private bool _isClosing;

    private readonly ShaderModManager _manager = new();

    /// <summary>
    /// Which edition this module is for, taken once at construction rather than read live:
    /// the cache folder, the Default backup and the elevated write all belong to the edition
    /// it opened under, and a mid-session flip would leave them pointing at different games.
    /// MainWindow disables the Preview toggle while this module is up, so the snapshot is
    /// what keeps that from being load-bearing. AlchitexOverlay does the same.
    /// </summary>
    private readonly bool _isPreview = Persistent.IsTargetingPreview;

    /// <summary>
    /// Whether this edition's Default backup is present and usable. False disables every
    /// preset row in the list: without a rollback there is no safe way to write shader files
    /// into somebody's game. Importing and deleting stay live - neither touches the game.
    /// </summary>
    private bool _defaultReady;

    private CancellationTokenSource? _scanCancellationTokenSource;

    // The download queue is the window's, not the manager's: every step of it exists to
    // move a preset's row from "Click to download" to "In queue" to installed, so it is
    // interleaved with redrawing the list all the way through.
    private Dictionary<string, DownloadStatus> _downloadStatuses;
    private readonly Queue<DownloadQueueItem> _downloadQueue;
    private bool _isProcessingQueue;
    private readonly CancellationTokenSource _closingCts = new();
    private readonly object _downloadStatusLock = new object();

    private const string REFRESH_COOLDOWN_KEY = "BetterRTXManager_RefreshCooldown_LastClickTimestamp";
    private const int REFRESH_COOLDOWN_SECONDS = 60;
    private DispatcherTimer? _cooldownTimer;

    /// <summary>
    /// True from the moment a preset install is started until it has finished and the list has
    /// been redrawn. Read and written only on the UI thread, and always cleared in a finally -
    /// see <see cref="PresetButton_Click"/>. Refresh honours it too, because a soft wipe would
    /// delete the folder an install is copying out of.
    /// </summary>
    private bool _applyInProgress;

    /// <summary>
    /// True from the click until the refresh has finished one way or the other. The cooldown is
    /// armed by a successful rebuild rather than by the click, so without this the one-second
    /// repaint below would hand the button straight back mid-fetch and a second press would
    /// start a rebuild on top of the first.
    /// </summary>
    private bool _refreshInProgress;

    /// <summary>
    /// True once the manager is attached and the list is on screen. Until then there is no cache
    /// folder and no index to rebuild, so refreshing would be a request into nothing - and the
    /// window can sit here for minutes while a system-wide search runs or the user is asked to
    /// point at the game.
    /// </summary>
    private bool _ready;


    public ShaderModManagerOverlay()
    {
        this.InitializeComponent();

        _downloadStatuses = new Dictionary<string, DownloadStatus>();
        _downloadQueue = new Queue<DownloadQueueItem>();
        _isProcessingQueue = false;
        _manager.DownloadTrackingReset = ClearDownloadTracking;

        PrepareContent();

        // Dead until this window has finished opening. The strip goes into the titlebar as the
        // module opens, so without this the button is pressable through the whole locate-and-load
        // pass and would race the fetch it is meant to replace. InitializeRefreshButton, at the
        // end of Loaded, is what brings it back.
        RefreshButton.IsEnabled = false;

        ShowBrowseTargets();

        // The Supervanilla RTX button is the bar's one accented control, and so are its two
        // bevels - which, built from code, have to follow both the theme and the button's own
        // enabled state by hand (a # SuspendControls entry is what would disable it).
        ApplySupervanillaButtonBevels(ThemeService.Current);
        ThemeService.ThemeChanged += ApplySupervanillaButtonBevels;
        SupervanillaDownloadsButton.IsEnabledChanged += (_, _) => ApplySupervanillaButtonBevels(ThemeService.Current);

        this.Loaded += ShaderModManagerOverlay_Loaded;
    }

    /// <summary>The cache-refresh button, which lives in MainWindow's titlebar while this is open.</summary>
    protected internal override FrameworkElement? TitleBarStrip => TitleBarActions;

    private async void ShaderModManagerOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= ShaderModManagerOverlay_Loaded;

            if (_isClosing) return;

            // bedrock.graphics builds against stable Minecraft, so on Preview the notice at
            // the top of the list is the whole of what makes BetterRTX supported there rather
            // than refused. Everything below it behaves identically either way.
            ApplyNotices();

            await InitializeAsync();
            if (_isClosing) return;

            InitializeRefreshButton();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    protected override void OnClosing()
    {
        if (_isClosing) return;
        _isClosing = true;

        this.Loaded -= ShaderModManagerOverlay_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();

        _downloadQueue.Clear();
        lock (_downloadStatusLock) { _downloadStatuses.Clear(); }

        WebImportOverlay.CloseIfOpen();

        _closingCts.Cancel();

        _cooldownTimer?.Stop();
        _cooldownTimer = null;

        ThemeService.ThemeChanged -= ApplySupervanillaButtonBevels;
    }

    /// <summary>
    /// Settles the two notice cards. Both are hand-written Pinned <see cref="TeletextCard"/>
    /// lookalikes rather than fetched ones: neither can fail to apply, and neither may depend
    /// on the announcements fetch having succeeded. Only one of them is pinned to the window:
    ///
    /// <list type="bullet">
    /// <item>The Preview warning scrolls with the announcements it sits above - it is advice
    /// read once, and a fixed card that tall costs the list most of its height.</item>
    /// <item>The no-backup card stays put: nothing in the list below it can be installed
    /// while it is up, so scrolling away from it would leave a greyed-out list with no
    /// explanation on screen.</item>
    /// </list>
    ///
    /// <para>Called twice - from Loaded, when only the Preview half is known, and again once
    /// the backup has been checked.</para>
    /// </summary>
    private void ApplyNotices()
    {
        PreviewWarningCard.Visibility = _isPreview ? Visibility.Visible : Visibility.Collapsed;

        bool showDefaultMissing = DefaultMissingText.Text.Length > 0;
        DefaultMissingCard.Visibility = showDefaultMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Takes (or confirms) this edition's Default backup and turns the outcome into the two
    /// things the window does with it: whether anything may be installed, and what the notice
    /// card says.
    ///
    /// <para>Runs before the list is drawn. Every non-Ready state means something is wrong
    /// with the game folder itself, and a list that implies installing will work is the worst
    /// place for the user to find that out.</para>
    /// </summary>
    private void EvaluateDefaultBackup()
    {
        var state = _manager.EnsureDefaultBackedUp();
        _defaultReady = state == ShaderModManager.DefaultBackupState.Ready;

        DefaultMissingText.Text = state switch
        {
            ShaderModManager.DefaultBackupState.Ready => "",

            ShaderModManager.DefaultBackupState.GameFilesIncomplete =>
                "Your Minecraft installation is missing some of the RTX shader files shader mods replace, so the app can't take a backup of your originals - " +
                "and without one there would be no way back if a preset didn't work out. Installing presets is disabled until that's sorted.\n\n" +
                "Repairing or reinstalling Minecraft from the Xbox app/Microsoft Store usually fixes this. You can still import presets in the meantime; they'll be waiting once the game is whole again.",

            ShaderModManager.DefaultBackupState.BackupUnverifiable =>
                "The app has a partial backup of your original RTX shader files, and it no longer matches what's in your game - which means the game is running shaders that aren't its own. " +
                "Finishing the backup from those files would record somebody else's preset as your defaults permanently, so the app won't, and installing presets is disabled.\n\n" +
                "Repair or reinstall Minecraft from the Xbox app/Microsoft Store to put its original files back, then reopen this module. Importing presets still works.",

            _ =>
                "The app couldn't write a backup of your original RTX shader files, so installing presets is disabled - without a backup there would be no way back from one. " +
                "This is usually free disk space or a permissions problem on the app's own data folder.\n\nImporting presets still works."
        };
    }

    private async Task<bool> ShowDisclaimerDialogAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        if (localSettings.Values.ContainsKey(ShaderModManager.DISCLAIMER_KEY))
            return true;

        var tcs = new TaskCompletionSource<bool>();

        var confirmButton = new Button
        {
            Content = "I understand the risks and wish to continue",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            Margin = new Thickness(0, 20, 0, 0),
            IsTextScaleFactorEnabled = false,
            Padding = new Thickness(16, 10, 16, 10),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24)
        };

        var closeButton = new Button
        {
            Content = "Dismiss",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0),
            IsTextScaleFactorEnabled = false,
            Padding = new Thickness(16, 8, 16, 8),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24)
        };

        var contentPanel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
            new TextBlock
                 {
                Text = "Shader mods replace Minecraft RTX's own shader files. This module installs two of them:\n" +
                    "- Supervanilla RTX, provided by vanillartx.net.\n" +
                    "- BetterRTX, an unofficial third-party mod whose files are provided by https://bedrock.graphics/api\n\n" +
                    "Either can break with Minecraft updates. Vanilla RTX App takes extensive measures to mitigate any issues that may arise, such as giving a way to quickly revert to your defaults." +
                    "\n\nPlease pay attention to the info panels to keep updated and help steer yourself away from potential issues.",
                TextWrapping = TextWrapping.Wrap,
                IsTextScaleFactorEnabled = false
                 },
            confirmButton,
            closeButton
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Shader Mod Notice",
            Content = contentPanel,
            XamlRoot = this.XamlRoot,
            IsTextScaleFactorEnabled = false,
            MinWidth = 0,
            MaxWidth = double.PositiveInfinity,
            Width = this.ActualWidth * 0.55,
            RequestedTheme = this.ActualTheme
        };

        // Block all closes until one of our buttons sets the tcs
        dialog.Closing += (s, e) =>
        {
            if (!tcs.Task.IsCompleted)
                e.Cancel = true;
        };

        // If this module is closed while the dialog is open, resolve gracefully
        void onOverlayClosed(object? s, EventArgs e)
        {
            tcs.TrySetResult(false);
        }
        this.Closed += onOverlayClosed;

        confirmButton.Click += (s, e) =>
        {
            localSettings.Values[ShaderModManager.DISCLAIMER_KEY] = true;
            tcs.TrySetResult(true);
            dialog.Hide();
        };

        closeButton.Click += (s, e) =>
        {
            tcs.TrySetResult(false);
            dialog.Hide();
        };

        await dialog.ShowAsync();
        await tcs.Task; // ensure tcs is always resolved before we return

        this.Closed -= onOverlayClosed; // clean up listener
        return tcs.Task.Result;
    }

    // ======================= Initialization =======================
    private async Task InitializeAsync()
    {
        try
        {
            // Each edition caches its own install path, and the one validated here has to be
            // the one about to be written into - RevalidateCachedPath checks the path against
            // the edition, so a mismatch is an eviction rather than a wrong-game write.
            var cachedPath = _isPreview
                ? Persistent.MinecraftPreviewInstallPath
                : Persistent.MinecraftInstallPath;

            string? minecraftPath = null;

            // Validate cached path
            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath, _isPreview))
            {
                Trace.WriteLine($"[ShaderMods] ✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                // Cache invalid - clear it and search
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine($"[ShaderMods] ⚠ Cache became invalid, clearing");
                    if (_isPreview)
                        Persistent.MinecraftPreviewInstallPath = null;
                    else
                        Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    SetLoadingStatus("Looking for your Minecraft installation...");
                    ManualSelectionPanel.Visibility = Visibility.Visible;
                });

                // Start system-wide search
                Trace.WriteLine("[ShaderMods] Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    _isPreview,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[ShaderMods] System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            // minecraftPath is guaranteed non-null here — both branches either return early or assign a value
            if (minecraftPath != null)
                await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    /// <summary>
    /// Starts the countdown timer for whatever cooldown a previous press left running.
    ///
    /// <para><b>Opening the window never arms it</b>, unlike the reload buttons elsewhere in the
    /// app. Those refresh what you are looking at, so opening does the same thing pressing them
    /// would. This one clears every downloaded and imported preset and rebuilds from scratch -
    /// an action nothing automatic performs, and the one thing to reach for when the API has
    /// shipped new files without saying so. Putting it on cooldown for merely having opened the
    /// window would lock out exactly that.</para>
    /// </summary>
    private void InitializeRefreshButton()
    {
        UpdateRefreshButtonState();
        _cooldownTimer = new DispatcherTimer();
        _cooldownTimer.Interval = TimeSpan.FromSeconds(1);
        _cooldownTimer.Tick += (s, e) => UpdateRefreshButtonState();
        _cooldownTimer.Start();
    }

    private void UpdateRefreshButtonState()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;

            if (settings.Values.TryGetValue(REFRESH_COOLDOWN_KEY, out var storedValue) && storedValue is long ticks)
            {
                var lastClickTime = new DateTime(ticks, DateTimeKind.Utc);
                var elapsed = DateTime.UtcNow - lastClickTime;
                var remainingSeconds = REFRESH_COOLDOWN_SECONDS - (int)elapsed.TotalSeconds;

                if (remainingSeconds > 0)
                {
                    RefreshButton.IsEnabled = false;

                    // Hide icon, show countdown text
                    RefreshIcon.Visibility = Visibility.Collapsed;
                    RefreshCountdownText.Visibility = Visibility.Visible;
                    RefreshCountdownText.Text = remainingSeconds.ToString();

                    return;
                }
            }

            // No cooldown - live again, unless this window is still opening or a refresh is
            // still running.
            RefreshButton.IsEnabled = _ready && !_refreshInProgress;

            // Show icon, hide countdown text
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] Error updating refresh button state: {ex.Message}");

            // On error, default to enabled with icon visible
            RefreshButton.IsEnabled = true;
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshCountdownText.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // A refresh soft-wipes the cache, which would delete the preset folder a running
        // install is copying its .bin files out of. The button's cooldown already stops this
        // being spammed; this stops it colliding with an install.
        if (_applyInProgress)
        {
            Trace.WriteLine("[ShaderMods] A preset is being applied - ignoring refresh");
            return;
        }

        // Same collision from the other end: a download in flight extracts into a folder
        // directly under the cache, and the wipe walks that cache deleting folders.
        // DownloadTrackingReset only stops what hasn't started; an extraction already running
        // keeps writing into a folder being deleted underneath it, and its per-entry failures
        // are caught and logged, leaving a preset with a readable manifest and missing .bin
        // files. Refused rather than queued, same as an install.
        if (_isProcessingQueue)
        {
            Trace.WriteLine("[ShaderMods] A preset download is in progress - ignoring refresh");
            return;
        }

        // Set before the first await, so a second click in the same instant is refused rather
        // than racing the first through.
        if (!_ready || _refreshInProgress) return;
        _refreshInProgress = true;

        try
        {
            Trace.WriteLine("[ShaderMods] === REFRESH BUTTON CLICKED ===");

            // The loading panel is shared, and the manual-pick offer inside it belongs to the
            // locate phase alone - left visible it would read as an instruction for this refresh.
            ManualSelectionPanel.Visibility = Visibility.Collapsed;
            SetLoadingStatus("Checking vanillartx.net and bedrock.graphics for the latest presets...");
            LoadingPanel.Visibility = Visibility.Visible;
            PresetSelectionPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = false;
            await Task.Delay(100);

            // Fetch first, per provider: each clears only its own presets, and only if its own
            // source answered, so a refresh with no connection costs nothing and one with half a
            // connection costs only the half that can be rebuilt.
            var supervanillaRebuilt = await _manager.RebuildSupervanillaAsync();
            var betterRtxRebuilt = await _manager.RebuildFromApiAsync();

            if (!supervanillaRebuilt || !betterRtxRebuilt)
            {
                SetLoadingStatus((supervanillaRebuilt, betterRtxRebuilt) switch
                {
                    (false, false) => "Couldn't reach vanillartx.net or bedrock.graphics. Nothing was cleared - check your connection and try again.",
                    (false, true) => "Couldn't reach vanillartx.net - your Supervanilla RTX presets were left as they were.",
                    _ => "Couldn't reach bedrock.graphics - your BetterRTX presets were left as they were."
                });
                await Task.Delay(2200);
            }

            if (!supervanillaRebuilt && !betterRtxRebuilt)
            {
                _manager.ForgetLoadedPresets();
                await _manager.LoadApiDataAsync();
                await _manager.LoadLocalPresetsAsync();
                await DisplayPresetsAsync();

                LoadingPanel.Visibility = Visibility.Collapsed;
                PresetSelectionPanel.Visibility = Visibility.Visible;
                return;
            }

            // Armed only now: a failed refresh must not spend the user's next attempt.
            ApplicationData.Current.LocalSettings.Values[REFRESH_COOLDOWN_KEY] = DateTime.UtcNow.Ticks;

            SetLoadingStatus("Reading the presets you already have...");
            _manager.ForgetLoadedPresets();

            // Reads the index RebuildFromApiAsync just stored, so this makes no second request.
            await _manager.LoadApiDataAsync();
            await _manager.LoadLocalPresetsAsync();
            await DisplayPresetsAsync();

            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;

            Trace.WriteLine($"[ShaderMods] ✓ Refresh complete — {_manager.DefaultFolderName} preserved");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] ✗ Error during refresh: {ex.Message}");
            LoadingPanel.Visibility = Visibility.Collapsed;
            PresetSelectionPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _refreshInProgress = false;
            UpdateRefreshButtonState();
            _ = Host.BlinkingLamp(true, true, 1.0);
        }
    }

    /// <summary>The line under the loading ring. Safe to call from any thread.</summary>
    private void SetLoadingStatus(string text)
    {
        if (DispatcherQueue.HasThreadAccess) LoadingStatusText.Text = text;
        else _ = DispatcherQueue.TryEnqueue(() => LoadingStatusText.Text = text);
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("[ShaderMods] Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowHandle;
        var pick = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(_isPreview, hWnd);

        if (pick.Path is { } path)
        {
            Trace.WriteLine($"[ShaderMods] ✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Trace.WriteLine("[ShaderMods] ✗ User cancelled or selected invalid path");
            StatusMessage = MinecraftGDKLocator.DescribeUnsetPick(pick, _isPreview);
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        // Locate the materials folder, establish the cache, deal with a game update having
        // happened since last time, and ask both sources whether they have published something
        // new - all of which is the manager's business.
        SetLoadingStatus("Checking for new Supervanilla RTX and BetterRTX builds...");
        switch (await _manager.TryAttachAsync(minecraftPath, _isPreview))
        {
            case ShaderModManager.AttachFailure.MaterialsFolderMissing:
                StatusMessage = "Materials folder not found in Minecraft installation";
                this.Close();
                return;

            case ShaderModManager.AttachFailure.CacheFolderUnavailable:
                StatusMessage = "Could not establish cache folder";
                this.Close();
                return;
        }

        // Before anything is listed: is there a way back? This decides whether the preset
        // rows below are clickable at all, so it has to happen ahead of them being built.
        EvaluateDefaultBackup();
        ApplyNotices();

        SetLoadingStatus("Checking bedrock.graphics for the latest presets...");
        await _manager.LoadApiDataAsync();

        SetLoadingStatus("Reading the presets you already have...");
        await _manager.LoadLocalPresetsAsync();

        await DisplayPresetsAsync();

        _ready = true;
        UpdateRefreshButtonState();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;

        // Announcements. The .md section is still called BetterRTXInfo: it is a wire format
        // older builds read too, and renaming it would take it away from every one of them.
        TeletextCard.Populate(BetterRTXInfoPanel, TeletextContent.BetterRTXInfo);

        // Populate can legitimately add nothing (every announcement dismissed), and an
        // empty-but-visible panel still contributes its margin, which reads as an
        // oversized gap between whatever is above it and the preset list.
        BetterRTXInfoPanel.Visibility = BetterRTXInfoPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Show disclaimer -- background work is done, but try to gate the UI
        try
        {
            var agreed = await ShowDisclaimerDialogAsync();
            if (!agreed)
            {
                StatusMessage = "Dismissed the shader mod notice. You should understand the risks before you can use this feature.\n" +
                    $"If you wish to change up the look of RTX without a shader mod, try out \"RTX LUT manager\" instead.";
                this.Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[ShaderMods]  Something went wrong while trying to show the shader mod disclaimer dialogue:\n" + ex.ToString());
        }
    }


    /// <summary>
    /// Rebuilds the whole list: Default, then Supervanilla RTX, then BetterRTX, each under its own
    /// header. Called after anything that changes what is on disk or what the game is running -
    /// cheap enough that redrawing everything beats tracking which row changed.
    /// </summary>
    private async Task DisplayPresetsAsync()
    {
        try
        {
            // Both should be loaded by the time anything draws. A null here means a load was
            // skipped or failed - worth a line in the log, not worth refusing to draw.
            if (_manager.ApiPresets == null)
                Trace.WriteLine("[ShaderMods] ⚠ WARNING: ApiPresets is null in DisplayPresetsAsync!");

            if (_manager.LocalPresets == null)
                Trace.WriteLine("[ShaderMods] ⚠ WARNING: LocalPresets is null in DisplayPresetsAsync!");

            PresetListContainer.Children.Clear();

            var currentHashes = _manager.GetCurrentlyInstalledHashes();

            // ── Default ──
            // Absent only when there is no backup, in which case the notice card above the list
            // already says why and every row below is disabled.
            var defaultPreset = _manager.CreateDefaultPreset();
            if (defaultPreset != null)
            {
                PresetListContainer.Children.Add(CreateSectionHeader("Default"));
                PresetListContainer.Children.Add(CreatePresetButton(defaultPreset, currentHashes, true));
            }

            // ── Supervanilla RTX ──
            PresetListContainer.Children.Add(CreateSectionHeader("Supervanilla RTX"));
            foreach (var preset in BuildSupervanillaRows())
                PresetListContainer.Children.Add(CreatePresetButton(preset, currentHashes, false));

            // ── BetterRTX ──
            PresetListContainer.Children.Add(CreateSectionHeader("BetterRTX"));
            var betterRtxRows = BuildBetterRTXRows();
            foreach (var preset in betterRtxRows)
                PresetListContainer.Children.Add(CreatePresetButton(preset, currentHashes, false));

            if (betterRtxRows.Count == 0)
                PresetListContainer.Children.Add(CreateSectionNote(DescribeEmptyBetterRTX()));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] Error displaying presets: {ex.Message}");
        }
    }

    /// <summary>
    /// The Supervanilla RTX section, in order: this edition's Get latest row, then (while
    /// <see cref="ShaderModManager.ShowSupervanillaIndexPresets"/> allows) the index's other builds,
    /// then every Supervanilla RTX import.
    ///
    /// <para><b>The Get latest row is always there</b>, whatever the index says or whether it was
    /// ever reached - it asks /latest directly. Until it has downloaded something it offers to;
    /// afterwards it is that build, named by its version, until an update or a newer build in the
    /// index retires it and it goes back to offering.</para>
    ///
    /// <para>Only this edition's downloads are drawn - a Release build installed into Preview is
    /// exactly what the per-edition channels exist to avoid. Imports carry no edition and are drawn
    /// for both, as BetterRTX's are.</para>
    /// </summary>
    private List<DisplayPresetData> BuildSupervanillaRows()
    {
        var rows = new List<DisplayPresetData>();
        var edition = ShaderModManager.EditionLabel(_isPreview);

        var held = _manager.HeldSupervanillaLatest;
        if (held != null)
        {
            rows.Add(FromLocal(held,
                name: SupervanillaBuildName(held),
                subtitle: $"Latest build for Minecraft {edition} - click to install",
                deletable: false));
        }
        else
        {
            var latestKey = ShaderModManager.LatestDownloadKey(_isPreview);
            rows.Add(new DisplayPresetData
            {
                Uuid = latestKey,
                Name = $"Get latest Supervanilla RTX ({edition})",
                IsDownloaded = false,
                DownloadKey = latestKey,
                DownloadAction = _manager.DownloadSupervanillaLatestAsync,
                Subtitle = $"Click to download the newest build made for Minecraft {edition}"
            });
        }

#pragma warning disable CS0162 // ShowSupervanillaIndexPresets is a deliberate compile-time switch
        if (ShaderModManager.ShowSupervanillaIndexPresets && _manager.CurrentSupervanillaIndex is { } index)
        {
            // The index's latest is the Get latest row's build already; listing it twice would
            // offer the same file under two rows that download it into two folders.
            foreach (var entry in index.Files.Where(f =>
                         !string.Equals(f.Name, index.Latest?.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var downloaded = _manager.HeldSupervanillaIndexBuild(entry);
                if (downloaded != null)
                {
                    rows.Add(FromLocal(downloaded, SupervanillaBuildName(downloaded), "Click to install", deletable: false));
                    continue;
                }

                var key = ShaderModManager.IndexDownloadKey(_isPreview, entry.Name);
                rows.Add(new DisplayPresetData
                {
                    Uuid = key,
                    Name = entry.EffectiveVersion is { } v ? $"Supervanilla RTX v{v}" : entry.Name,
                    IsDownloaded = false,
                    DownloadKey = key,
                    DownloadAction = ct => _manager.DownloadSupervanillaIndexEntryAsync(entry, ct),
                    Subtitle = entry.Date is { } date ? $"Released {date} - click to download" : null
                });
            }
        }
#pragma warning restore CS0162

        var imports = (_manager.SupervanillaPresets ?? new List<LocalPresetData>())
            .Where(p => p.Supervanilla is not { Kind: not SupervanillaKind.Import })
            .Select(p => FromLocal(p, p.Name, subtitle: null, deletable: true));
        rows.AddRange(SmartPresetSorter.SafeOrderByName(imports, p => p.Name));

        return rows;
    }

    /// <summary>"Supervanilla RTX v1.26.40.56" when the build's version is known, its manifest name otherwise.</summary>
    private static string SupervanillaBuildName(LocalPresetData preset) =>
        preset.Supervanilla?.Version is { } version ? $"Supervanilla RTX v{version}" : preset.Name ?? "Supervanilla RTX";

    private static DisplayPresetData FromLocal(LocalPresetData preset, string? name, string? subtitle, bool deletable) => new()
    {
        Uuid = preset.Uuid,
        Name = name,
        IsDownloaded = true,
        IsCustomImport = deletable,
        Icon = preset.Icon,
        PresetPath = preset.PresetPath,
        BinFiles = preset.BinFiles,
        FileHashes = preset.FileHashes,
        Subtitle = subtitle
    };

    /// <summary>
    /// The BetterRTX section: every API preset merged with what is on disk, downloaded ones first,
    /// then every BetterRTX import the API doesn't list. Reads <see cref="ShaderModManager.LocalPresets"/>
    /// only, which by construction holds no Supervanilla RTX folder.
    /// </summary>
    private List<DisplayPresetData> BuildBetterRTXRows()
    {
        var apiPresets = _manager.ApiPresets ?? new List<ApiPresetData>();
        var localPresets = _manager.LocalPresets ?? new Dictionary<string, LocalPresetData>();

        var downloadedPresets = new List<DisplayPresetData>();
        var notDownloadedPresets = new List<DisplayPresetData>();
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, process API presets
        foreach (var apiPreset in apiPresets)
        {
            if (string.IsNullOrEmpty(apiPreset.Uuid)) continue;    // skip malformed entries

            seenUuids.Add(apiPreset.Uuid);

            if (localPresets.TryGetValue(apiPreset.Uuid, out var localPreset))
            {
                downloadedPresets.Add(new DisplayPresetData
                {
                    Uuid = apiPreset.Uuid,
                    Name = localPreset.Name,
                    IsDownloaded = true,
                    IsCustomImport = false,
                    Icon = localPreset.Icon,
                    PresetPath = localPreset.PresetPath,
                    BinFiles = localPreset.BinFiles,
                    FileHashes = localPreset.FileHashes
                });
            }
            else
            {
                var uuid = apiPreset.Uuid;
                notDownloadedPresets.Add(new DisplayPresetData
                {
                    Uuid = uuid,
                    Name = apiPreset.Name,
                    IsDownloaded = false,
                    DownloadKey = uuid,
                    DownloadAction = ct => _manager.DownloadPresetAsync(uuid, ct)
                });
            }
        }

        // Second, add any local presets that weren't in the API list
        foreach (var localPreset in localPresets.Values)
        {
            if (string.IsNullOrEmpty(localPreset.Uuid)) continue;   // skip malformed entries

            if (!seenUuids.Contains(localPreset.Uuid))
            {
                downloadedPresets.Add(new DisplayPresetData
                {
                    Uuid = localPreset.Uuid,
                    Name = localPreset.Name,
                    IsDownloaded = true,
                    IsCustomImport = true,
                    Icon = localPreset.Icon,
                    PresetPath = localPreset.PresetPath,
                    BinFiles = localPreset.BinFiles,
                    FileHashes = localPreset.FileHashes
                });
            }
        }

        // Sort each list alphabetically, downloaded first
        return SmartPresetSorter.SafeOrderByName(downloadedPresets, p => p.Name)
            .Concat(SmartPresetSorter.SafeOrderByName(notDownloadedPresets, p => p.Name))
            .ToList();
    }

    /// <summary>What the BetterRTX section says when it has nothing to list.</summary>
    private string DescribeEmptyBetterRTX()
    {
        int apiCount = _manager.ApiPresets?.Count ?? 0;
        int localCount = _manager.LocalPresets?.Count ?? 0;

        Trace.WriteLine($"[ShaderMods] 📊 BetterRTX section empty - API: {apiCount}, Local: {localCount}");

        if (apiCount == 0 && localCount == 0)
        {
            return File.Exists(_manager.ApiCachePath)
                // Cache exists but is empty/corrupt
                ? "No BetterRTX presets available. The preset list may be corrupted, try clicking the Refresh button in the top left corner."
                // No cache, probably offline
                : "No BetterRTX presets available. An internet connection is required to fetch BetterRTX preset information.\n" +
                  "You may still import and install custom presets (.rtpack) even without an internet connection.";
        }

        // We have data but nothing to display (edge case)
        return "No BetterRTX presets could be displayed. Try clicking the Refresh button in the top left corner.";
    }

    /// <summary>
    /// A section's heading: its name centred on a hairline that runs the width of the list, so it
    /// reads as a divider while scrolling past it rather than as one more row. Code-built like the
    /// rows around it, so its colours come from App.xaml's Themed styles (CLAUDE.md §6, "Theme
    /// colours on code-built elements").
    /// </summary>
    private static FrameworkElement CreateSectionHeader(string title)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 2), ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement Rule(int column)
        {
            var rule = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["ThemedDividerRuleStyle"]
            };
            Grid.SetColumn(rule, column);
            return rule;
        }

        var label = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false,
            Style = (Style)Application.Current.Resources["ThemedSecondaryTextStyle"]
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(Rule(0));
        grid.Children.Add(label);
        grid.Children.Add(Rule(2));
        return grid;
    }

    /// <summary>A line of explanation inside a section that has no rows to show.</summary>
    private static FrameworkElement CreateSectionNote(string text) => new TextBlock
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(24, 4, 24, 8),
        IsTextScaleFactorEnabled = false,
        Style = (Style)Application.Current.Resources["ThemedSecondaryTextStyle"]
    };

    private FrameworkElement CreatePresetButton(object presetData, Dictionary<string, string> currentInstalledHashes, bool isDefault)
    {
        bool isDownloaded = true;
        bool isCurrent = false;
        bool isCustomImport = false;
        string name = "";
        string description = "";
        string uuid = "";
        BitmapImage? icon = null;

        if (presetData is LocalPresetData localPreset)
        {
            isDownloaded = true;
            name = localPreset.Name ?? "";
            icon = localPreset.Icon;
            uuid = localPreset.Uuid ?? "";

            // Compare ALL hashes to see if it is current
            if (localPreset.FileHashes != null && ShaderModManager.AreHashesMatching(currentInstalledHashes, localPreset.FileHashes))
            {
                isCurrent = true;
            }
            description = isCurrent
                ? Helpers.SanitizePathForDisplay(localPreset.PresetPath ?? "")
                : isDefault
                    ? "Click to rollback" + (Helpers.RuntimeFlags.Set("Already_Informed_About_How_Default_RTX_Preset_Is_Made") ? ": this preset was automatically created from your game files" : "")
                    : "Click to install";
        }
        else if (presetData is DisplayPresetData displayPreset)
        {
            isDownloaded = displayPreset.IsDownloaded;
            isCustomImport = displayPreset.IsCustomImport;
            name = displayPreset.Name ?? "";
            uuid = displayPreset.DownloadKey ?? displayPreset.Uuid ?? "";
            icon = displayPreset.Icon;

            // Dynamic description based on download status
            if (isDownloaded)
            {
                if (displayPreset.FileHashes != null && ShaderModManager.AreHashesMatching(currentInstalledHashes, displayPreset.FileHashes))
                {
                    isCurrent = true;
                    description = Helpers.SanitizePathForDisplay(displayPreset.PresetPath ?? "");
                }
                else
                {
                    isCurrent = false;
                    description = displayPreset.Subtitle ?? "Click to install";
                }
            }
            else
            {
                // Check download status with lock
                DownloadStatus status;
                lock (_downloadStatusLock)
                {
                    _downloadStatuses.TryGetValue(uuid, out status);
                }

                description = status switch
                {
                    DownloadStatus.Queued => "In queue",
                    DownloadStatus.Downloading => "Download in progress...",
                    _ => displayPreset.Subtitle ?? "Click to download"
                };
            }
        }

        if (isCurrent)
        {
            name += " (Currently Installed)";
        }

        // Decided up front so the button's own corner radius, margin and shadow can be set
        // correctly the first time rather than mutated after the fact - see the split-button
        // wrapping at the bottom of this method for why those three all change together.
        bool showDeleteButton = !isDefault && !isCurrent && isCustomImport && presetData is DisplayPresetData;

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 40, 0),
            BorderThickness = new Thickness(0),
            Margin = showDeleteButton ? new Thickness(0) : new Thickness(0, 0, 0, 4),
            MinHeight = 96,
            // Squared off on the right where the delete button will sit flush against it -
            // squared off below where the delete button sits flush against it.
            CornerRadius = showDeleteButton ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(5),
            Tag = presetData,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };
        if (isCurrent)
        {
            button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        }

        // A delete-button row gets one shared shadow cast from a backing element behind the
        // whole composite (see the bottom of this method) - as if it were still one card, even
        // though its visible surface is cut in two. Without this, this button's own shadow and
        // the delete button's would double up into two overlapping drop shadows for what reads
        // as a single row.
        if (!showDeleteButton)
        {
            var buttonShadow = new ThemeShadow();
            button.Shadow = buttonShadow;
            button.Loaded += (s, e) =>
            {
                if (ShadowReceiverGrid != null)
                {
                    buttonShadow.Receivers.Add(ShadowReceiverGrid);
                }
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon
        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        if (icon != null)
        {
            iconBorder.Child = new Image
            {
                Source = icon,
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            bool showDownloadGlyph = !isDefault && !isDownloaded;
            iconBorder.Child = new FontIcon
            {
                Glyph = showDownloadGlyph ? "\uE896" : "\uEABC",
                FontSize = 44, // scaled up to match larger icon container
                FontWeight = FontWeights.ExtraLight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false
            };
        }

        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // Info panel
        var infoPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            Text = name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var descText = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(nameText);
        infoPanel.Children.Add(descText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        // Download button or progress indicator
        if (!isDefault && !isDownloaded)
        {
            DownloadStatus status;
            lock (_downloadStatusLock)
            {
                status = _downloadStatuses.TryGetValue(uuid, out var s) ? s : DownloadStatus.NotDownloaded;
            }

            if (status == DownloadStatus.Downloading || status == DownloadStatus.Queued)
            {
                var progressRing = new ProgressRing
                {
                    Width = 50,
                    Height = 50,
                    IsActive = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                };

                Grid.SetColumn(progressRing, 4);
                grid.Children.Add(progressRing);
            }
        }


        button.Content = grid;

        // Only attach if it isn't current
        if (!isCurrent)
        {
            button.Click += PresetButton_Click;
        }

        // No usable Default backup means no installing and no downloading. The rows still
        // draw - a greyed-out list beside the notice card reads as blocked, where an empty
        // one reads as broken - but none of them leads anywhere. The delete button below
        // stays live, as do the bottom bar's import buttons: neither touches the game.
        if (!_defaultReady)
            button.IsEnabled = false;

        // Delete button: only for imported presets - either provider's - that aren't __DEFAULT
        // and aren't the one currently installed. Built as a sibling "fake split button" next to the main
        // button rather than floating on top of it - see CreateSplitSeamPiece below.
        if (!showDeleteButton || presetData is not DisplayPresetData deletablePreset)
            return button;

        var deleteButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0, 5, 5, 0),
            IsTextScaleFactorEnabled = false,
            Tag = deletablePreset,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 20, Margin = new Thickness(-5, 0, 0, 0), IsTextScaleFactorEnabled = false }
        };
        deleteButton.Click += DeletePresetButton_Click;

        // Split-button wrapper: main button (Star) + 6px seam column + delete button,
        // touching corners squared off above. See CreateSplitSeamPiece for the two 3px bevel
        // strips that fill the seam.
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        // One shared shadow for the whole composite row, cast from an invisible backdrop behind
        // both pieces - see the comment where buttonShadow is conditionally skipped above.
        var rowShadowBackdrop = new Border
        {
            CornerRadius = new CornerRadius(5),
            Translation = new System.Numerics.Vector3(0, 0, 32),
            IsHitTestVisible = false
        };
        var rowShadow = new ThemeShadow();
        rowShadowBackdrop.Shadow = rowShadow;
        rowShadowBackdrop.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                rowShadow.Receivers.Add(ShadowReceiverGrid);
        };
        Grid.SetColumnSpan(rowShadowBackdrop, 3);
        row.Children.Add(rowShadowBackdrop);

        Grid.SetColumn(button, 0);
        row.Children.Add(button);

        Grid.SetColumn(deleteButton, 2);
        row.Children.Add(deleteButton);

        var darkSeam = CreateSplitSeamPiece(dark: true);
        Grid.SetColumn(darkSeam, 0);
        row.Children.Add(darkSeam);

        var brightSeam = CreateSplitSeamPiece(dark: false);
        Grid.SetColumn(brightSeam, 2);
        row.Children.Add(brightSeam);

        return row;
    }

    /// <summary>
    /// One 3px half of the "fake split button" seam between the preset button and its delete
    /// button - the same hand-written pattern this module's own "Create your own preset" /
    /// "Add customized preset" pair uses directly in XAML via
    /// <c>{ThemeResource FakeSplitButtonDarkBorderColor}</c>. That binding auto-updates on theme
    /// change for free; built from code (these rows are assembled at runtime, one per preset) it
    /// needs the same live-theme handling <c>ApplyCloseButtonBevel</c>-style code elsewhere in
    /// this app already does: resolve once, then follow <see cref="ThemeService.ThemeChanged"/>
    /// and drop the subscription on Unloaded.
    /// <para>
    /// Pass <c>dark: true</c> for the piece added to the LEFT (preset) button's own Grid.Column -
    /// it right-aligns itself and bleeds 3px into the gap via a negative margin. Pass
    /// <c>dark: false</c> for the piece added to the RIGHT (delete) button's column - it
    /// left-aligns and bleeds the other way. Together the two 3px strips exactly fill the 6px
    /// gap column, dark meeting bright at the seam.
    /// </para>
    /// </summary>
    private FrameworkElement CreateSplitSeamPiece(bool dark)
    {
        var strip = new Grid
        {
            HorizontalAlignment = dark ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Width = 3,
            Margin = dark ? new Thickness(0, 0, -3, 0) : new Thickness(-3, 0, 0, 0),
            IsHitTestVisible = false
        };
        strip.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ThemedSeamFillStyle"] });

        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0) };
        strip.Children.Add(border);

        void Apply(ElementTheme theme) =>
            border.BorderBrush = new SolidColorBrush(ThemeService.GetBevelColor(
                theme, dark ? ThemeService.BevelEdge.Right : ThemeService.BevelEdge.Left, accented: false));

        Apply(ThemeService.Current);
        ThemeService.ThemeChanged += Apply;
        strip.Unloaded += (_, _) => ThemeService.ThemeChanged -= Apply;

        return strip;
    }


    #region custom preset handlers

    /// <summary>
    /// Names the address each in-app-browser button opens, in its own label (and for Create, its
    /// tooltip). Read once at construction from the same source the click uses, so the two cannot
    /// say different things - which is exactly what they did while the label was a literal in XAML
    /// and the address was a setting.
    /// </summary>
    private void ShowBrowseTargets()
    {
        CreatePresetTargetText.Text = $"Visit {EnvironmentVariables.LinkLabel(Links.BetterRtxCreator, includePath: true)}";
        ToolTipService.SetToolTip(CreatePresetLink,
            $"Browse {EnvironmentVariables.LinkLabel(Links.BetterRtxCreator, includePath: true)} right here - " +
            "build a BetterRTX preset and it will be imported automatically when you close it.");

        var supervanillaPage = EnvironmentVariables.LinkLabel(ShaderModManager.SupervanillaDownloadsPageUrl, includePath: false);
        SupervanillaDownloadsTargetText.Text = $"Visit {supervanillaPage}";
        ToolTipService.SetToolTip(SupervanillaDownloadsButton,
            $"Browse every Supervanilla RTX build on {supervanillaPage}, for any game version, right here - " +
            "whatever you download is imported automatically when you click Done.");
    }

    /// <summary>
    /// The way to every Supervanilla RTX build the Get latest row doesn't offer - older ones for an
    /// older game, mostly. Same flow as Create your own preset, onto the site's minimal
    /// <c>?app</c> layout; the difference is that everything downloaded there is Supervanilla RTX
    /// by construction, so the import is told so rather than left to guess from the file name.
    /// </summary>
    private void SupervanillaDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        WebImportOverlay.Show(
            url: ShaderModManager.SupervanillaDownloadsPageUrl,
            title: "Supervanilla RTX downloads",
            glyph: "\uE81C",
            guideText: "Download the Supervanilla RTX build for the game version you play. Once it downloads, click Done; It'll auto-import & you can install it.",
            stagingTag: "Supervanilla",
            watchedExtensions: ShaderModManager.SupportedCustomPresetExtensions,
            onFilesReady: files => ImportCustomPresetsAsync(files, forceSupervanilla: true));
    }

    /// <summary>
    /// Paints the two bevels the Supervanilla RTX button owns: accented while it is enabled, and the
    /// dimmed resting colour while it isn't - the same rule MainWindow's Preview toggle follows, so
    /// the seams never claim a state the button between them isn't in. Its neighbours' own edges
    /// are plain ThemeResources in the XAML and need nothing from here.
    /// </summary>
    private void ApplySupervanillaButtonBevels(ElementTheme theme)
    {
        var enabled = SupervanillaDownloadsButton.IsEnabled;
        SupervanillaLeftBevel.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: true, isEnabled: enabled));
        SupervanillaRightBevel.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right, accented: true, isEnabled: enabled));
    }

    /// <summary>
    /// bedrock.graphics/creator has no API worth scraping - it's a build-your-own-preset tool,
    /// not a static file list. Rather than send the user out to their real browser and leave
    /// them to find their way back with a .rtpack in hand, they build it right here; whatever
    /// they download gets imported automatically once they close the overlay. See
    /// <see cref="WebImportOverlay"/> for the mechanism, which knows nothing about BetterRTX
    /// specifically - it just hands back whatever matched and lets this reuse the exact same
    /// <see cref="ImportCustomPresetsAsync"/> a manual drag-and-drop already goes through.
    ///
    /// <para>Which page that is comes from <see cref="Core.EnvironmentVariables.Links"/> rather
    /// than being written here: the settings panel lets it be pointed somewhere else, and that
    /// class is what falls back to the built-in address when the stored one stops being
    /// usable.</para>
    /// </summary>
    private void CreatePresetButton_Click(object sender, RoutedEventArgs e)
    {
        WebImportOverlay.Show(
            url: Links.BetterRtxCreator,
            title: "Create your own preset",
            glyph: "",
            guideText: "Once you've customized your preset, click Export, build and export as .rtpack. Once it downloads, click Done; It'll auto-import & you can install it.",
            stagingTag: "BetterRTX",
            watchedExtensions: ShaderModManager.SupportedCustomPresetExtensions,
            onFilesReady: files => ImportCustomPresetsAsync(files));
    }

    private async void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hWnd = WindowHandle;
            InitializeWithWindow.Initialize(picker, hWnd);

            foreach (var ext in ShaderModManager.SupportedCustomPresetExtensions)
                picker.FileTypeFilter.Add(ext);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            await ImportCustomPresetsAsync(files.Select(f => f.Path));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [CustomImport] Error browsing for preset file(s): {ex.Message}");
        }
    }
    private async void AddPresetButton_DragOver(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            bool hasSupportedFile = items.OfType<StorageFile>()
                .Any(f => ShaderModManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

            if (hasSupportedFile)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Add as shader preset(s)";
                e.DragUIOverride.IsGlyphVisible = true;
            }
            else
            {
                // Something's being dragged, but none of it is a preset file we accept —
                // explicitly refuse so the OS shows the "not allowed" cursor instead of a copy cursor.
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [CustomImport] Error evaluating drag-over content: {ex.Message}");
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }
    // DragEnter dims the button and DragLeave/Drop undo it - but DragEnter has to await the
    // data view before it knows whether to dim at all, and a quick drag across the button
    // delivers DragLeave before that await returns. Left alone, the dim would then land
    // *after* the restore and stick. This token is "is the pointer still over the button?":
    // every enter takes a new one, every leave and drop invalidate it.
    private int _addPresetDragToken;

    private async void AddPresetButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var token = ++_addPresetDragToken;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            bool hasSupportedFile = items.OfType<StorageFile>()
                .Any(f => ShaderModManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase));

            if (hasSupportedFile && token == _addPresetDragToken)
            {
                button.Opacity = 0.7;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [CustomImport] Error on drag enter: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
    private void AddPresetButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Button button) return;

        // Undo exactly what DragEnter did, and only that. Clearing Background/BorderBrush/
        // BorderThickness here instead leaves the dim in place for the rest of the window's
        // life (DragEnter never touched those) and throws away the BorderThickness="0" set
        // in XAML, handing the button the theme's 1px border.
        _addPresetDragToken++;
        button.Opacity = 1.0;
    }
    private async void AddPresetButton_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            // Undo DragEnter's dim regardless of what happens next (see DragLeave).
            _addPresetDragToken++;
            if (sender is Button button)
                button.Opacity = 1.0;

            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                Trace.WriteLine("[ShaderMods] [CustomImport] Drop event carried no storage items");
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var filePaths = items.OfType<StorageFile>()
                .Select(f => f.Path)
                .ToList();

            if (filePaths.Count == 0)
            {
                Trace.WriteLine("[ShaderMods] [CustomImport] Drop contained no usable files");
                return;
            }

            // ImportCustomPresetsAsync already filters down to supported extensions,
            // toggles LoadingPanel/PresetSelectionPanel, and refreshes the list when done.
            await ImportCustomPresetsAsync(filePaths);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [CustomImport] Error handling dropped file(s): {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
    private async void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DisplayPresetData preset }) return;

        if (string.IsNullOrEmpty(preset.PresetPath) || !Directory.Exists(preset.PresetPath))
        {
            Trace.WriteLine($"[ShaderMods] [Delete] ✗ Preset path missing or already gone: {preset.PresetPath}");
            return;
        }

        try
        {
            if (!await _manager.DeletePresetAsync(preset.PresetPath!, preset.Name))
                return;

            lock (_downloadStatusLock)
            {
                var key = preset.DownloadKey ?? preset.Uuid;
                if (!string.IsNullOrEmpty(key))
                    _downloadStatuses.Remove(key);
            }

            await _manager.LoadLocalPresetsAsync();
            await DisplayPresetsAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [Delete] ✗ Error refreshing after delete of \"{preset.Name}\": {ex.Message}");
        }
    }
    #endregion


    private void EnqueueDownload(string key, string name, Func<CancellationToken, Task<bool>> download)
    {
        // Check if already in queue or downloading
        lock (_downloadStatusLock)
        {
            if (_downloadStatuses.ContainsKey(key))
            {
                Trace.WriteLine($"[ShaderMods] ⚠ Already queued or downloading: {name}");
                return;
            }

            Trace.WriteLine($"[ShaderMods] ➕ Queued download: {name}");

            // Mark as queued
            _downloadStatuses[key] = DownloadStatus.Queued;
        }

        // Add to queue
        _downloadQueue.Enqueue(new DownloadQueueItem { Key = key, Name = name, Download = download });

        // Refresh UI to show "Queued" status
        _ = this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

        // Start processing queue if not already running
        if (!_isProcessingQueue)
        {
            _ = ProcessDownloadQueueAsync();
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        if (_isProcessingQueue)
            return;

        _isProcessingQueue = true;
        Trace.WriteLine("[ShaderMods] ▶ Starting download queue processor");

        try
        {
            while (_downloadQueue.Count > 0)
            {
                var item = _downloadQueue.Dequeue();
                Trace.WriteLine($"[ShaderMods] 🔽 Processing download: {item.Name} (Queue: {_downloadQueue.Count} remaining)");

                // Update status to downloading
                lock (_downloadStatusLock)
                {
                    if (item.Key != null)
                        _downloadStatuses[item.Key] = DownloadStatus.Downloading;
                }
                await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());

                // Wait a moment before starting (helps with slow API)
                await Task.Delay(2000);

                // Download
                var success = item.Key != null && item.Download != null && await item.Download(_closingCts.Token);

                if (success)
                {
                    Trace.WriteLine($"[ShaderMods] ✓ Download complete: {item.Name}");

                    // Mark as downloaded and remove from status tracking
                    lock (_downloadStatusLock)
                    {
                        if (item.Key != null)
                            _downloadStatuses.Remove(item.Key);
                    }

                    // Reload local presets and refresh UI
                    await _manager.LoadLocalPresetsAsync();
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }
                else
                {
                    Trace.WriteLine($"[ShaderMods] ✗ Download failed: {item.Name}");

                    // Remove from status (will show download button again)
                    lock (_downloadStatusLock)
                    {
                        if (item.Key != null)
                            _downloadStatuses.Remove(item.Key);
                    }
                    await this.DispatcherQueue.EnqueueAsync(async () => await DisplayPresetsAsync());
                }

                // Wait a bit between downloads to be nice to the api
                if (_downloadQueue.Count > 0)
                {
                    Trace.WriteLine("[ShaderMods] ⏱ Waiting 3 seconds before next download...");
                    await Task.Delay(1500);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] ✗ Error in download queue processor: {ex.Message}");
        }
        finally
        {
            _isProcessingQueue = false;
            Trace.WriteLine("[ShaderMods] ⏹ Download queue processor stopped");
        }
    }

    /// <summary>
    /// Reloads and redisplays the local presets list - the same pair of calls
    /// ImportCustomPresetsAsync makes after its own import loop. Exposed so MainWindow can
    /// call it on an already-open manager once a headless .rtpack file-activation
    /// import lands (see MainWindow.ImportShaderModPresetFilesAsync,
    /// ImportPresetFilesHeadlessAsync above) - otherwise this module's list would keep
    /// showing what was installed before that import until closed and reopened.
    /// </summary>
    internal async Task RefreshLocalPresetsAsync()
    {
        await _manager.LoadLocalPresetsAsync();
        await DisplayPresetsAsync();
    }

    /// <summary>
    /// Handed to <see cref="ShaderModManager.DownloadTrackingReset"/>: a soft wipe has just
    /// deleted the folders anything queued was headed for.
    /// </summary>
    private void ClearDownloadTracking()
    {
        lock (_downloadStatusLock)
        {
            _downloadStatuses.Clear();
        }
        _downloadQueue.Clear();
    }

    /// <summary>
    /// Imports presets handed over by a double-click in Explorer, through this module rather
    /// than MainWindow - see <see cref="Core.FileActivation.IFileActivationTarget"/>.
    ///
    /// <para>Deliberately the same call drag-and-drop and the Add button make, so an
    /// activated .rtpack and a dropped one are the same operation: same loading panel, same
    /// extension filtering, same list reload afterwards.</para>
    /// </summary>
    public Task ImportActivatedFilesAsync(IReadOnlyList<string> paths) =>
        ImportCustomPresetsAsync(paths);

    /// <summary>
    /// Bulk wrapper around <see cref="ShaderModManager.ImportCustomPresetAsync"/> for every route a
    /// file comes in by. <paramref name="forceSupervanilla"/> is for the one route that knows its
    /// files are Supervanilla RTX - the downloads page - rather than leaving it to their names.
    /// </summary>
    private async Task ImportCustomPresetsAsync(IEnumerable<string> filePaths, bool forceSupervanilla = false)
    {
        var candidates = filePaths
            .Where(p => ShaderModManager.SupportedCustomPresetExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            Trace.WriteLine($"[ShaderMods] [CustomImport] No supported preset files ({string.Join("/", ShaderModManager.SupportedCustomPresetExtensions)}) in selection");
            return;
        }

        ManualSelectionPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        PresetSelectionPanel.Visibility = Visibility.Collapsed;

        int successCount = 0;
        foreach (var path in candidates)
        {
            var (ok, _) = await _manager.ImportCustomPresetAsync(path, forceSupervanilla);
            if (ok) successCount++;
        }

        Trace.WriteLine($"[ShaderMods] [CustomImport] Imported {successCount}/{candidates.Count} preset(s) successfully");

        await _manager.LoadLocalPresetsAsync();
        await DisplayPresetsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        PresetSelectionPanel.Visibility = Visibility.Visible;
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag != null)
        {
            try
            {
                LocalPresetData? presetToApply = null;
                if (button.Tag is LocalPresetData localPreset)
                {
                    presetToApply = localPreset;
                }
                else if (button.Tag is DisplayPresetData displayPreset)
                {
                    if (!displayPreset.IsDownloaded)
                    {
                        // Trigger download by adding to queue
                        if (displayPreset.DownloadKey is { } key && displayPreset.DownloadAction is { } download)
                            EnqueueDownload(key, displayPreset.Name ?? "", download);
                        return;
                    }
                    else
                    {
                        presetToApply = new LocalPresetData
                        {
                            Uuid = displayPreset.Uuid,
                            Name = displayPreset.Name,
                            PresetPath = displayPreset.PresetPath,
                            Icon = displayPreset.Icon,
                            BinFiles = displayPreset.BinFiles,
                            FileHashes = displayPreset.FileHashes
                        };
                    }
                }
                if (presetToApply != null)
                {
                    // Applying a preset is an elevated file copy: it writes a batch script,
                    // raises a UAC prompt and waits, with the UI thread free for most of it.
                    // Without this a second click starts a second script and the user gets a
                    // queue of prompts (Helpers.ReplaceFilesWithElevation refuses that outright
                    // as a backstop). Ignoring the click rather than disabling the button is
                    // deliberate - there is no state here that can be left stuck.
                    if (_applyInProgress)
                    {
                        Trace.WriteLine("[ShaderMods] A preset is already being applied - ignoring this click");
                        return;
                    }

                    // Nothing between setting the flag and entering the try, so there is no
                    // statement that could throw its way past the finally and leave the window
                    // permanently refusing installs.
                    _applyInProgress = true;
                    try
                    {
                        SetPresetListBusy(true);

                        var success = await _manager.ApplyPresetAsync(presetToApply);

                        // The elevated replace has been through either way by here, and the
                        // user has just dismissed a UAC prompt - so whichever way it went is
                        // worth saying. The DLSS and LUT installs say it in the same place.
                        _ = Host.BlinkingLamp(true, true, success ? 1.0 : 0.0, 1.0);

                        if (success)
                        {
                            OperationSuccessful = true;
                            StatusMessage = $"Installed {presetToApply.Name} successfully";
                            Trace.WriteLine(StatusMessage);
                            await DisplayPresetsAsync();
                        }
                    }
                    finally
                    {
                        SetPresetListBusy(false);
                        _applyInProgress = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ShaderMods] Error applying preset: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Greys the preset list and stops it taking clicks while an install runs - delete buttons
    /// included, since they live inside it and deleting the folder being copied from is the one
    /// way to break a running install.
    ///
    /// <para>Both properties are set on <c>PresetListContainer</c> itself rather than on the
    /// buttons, which <see cref="DisplayPresetsAsync"/> replaces wholesale. The container is
    /// declared in XAML and outlives every redraw, so the restoring call in the finally always
    /// lands on the same element the disabling call touched. <c>Panel</c> has no
    /// <c>IsEnabled</c> - that lives on <c>Control</c> - so this is the UIElement-level
    /// equivalent.</para>
    /// </summary>
    private void SetPresetListBusy(bool busy)
    {
        PresetListContainer.IsHitTestVisible = !busy;
        PresetListContainer.Opacity = busy ? 0.5 : 1.0;
    }


}


/// <summary>
/// Extension methods for DispatcherQueue to support async operations
/// </summary>
public static class DispatcherQueueExtensions
{
    /// <summary>
    /// Enqueues an async callback on the dispatcher queue and awaits its completion
    /// </summary>
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Func<Task> callback, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource<bool>();

        bool enqueued = dispatcher.TryEnqueue(priority, async () =>
        {
            try
            {
                await callback();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue operation on dispatcher"));
        }

        return tcs.Task;
    }

    /// <summary>
    /// Enqueues a synchronous callback on the dispatcher queue and awaits its completion
    /// </summary>
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Action callback, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        var tcs = new TaskCompletionSource<bool>();

        bool enqueued = dispatcher.TryEnqueue(priority, () =>
        {
            try
            {
                callback();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue operation on dispatcher"));
        }

        return tcs.Task;
    }
}
