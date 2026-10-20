using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Modules;
using Vanilla_RTX_App.Modules.PackBrowser;
using Vanilla_RTX_App.Modules.PackUpdater;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;
using WinUIEx;
using static Vanilla_RTX_App.Core.EnvironmentVariables;
using static Vanilla_RTX_App.Core.EnvironmentVariables.Persistent;
using static Vanilla_RTX_App.Modules.Helpers;

namespace Vanilla_RTX_App;
/// <summary>
/// For dynamically updating number of other selected packs in the UI (select other packs button)
/// </summary>
public class PackSelectionViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher
        = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    // When set, overrides the count-based label entirely.
    // Set to null to restore normal behavior.
    private string? _labelOverride;

    public PackSelectionViewModel()
    {
        EnvironmentVariables.SelectedPacks.CollectionChanged += OnSelectedPacksChanged;
    }

    private void OnSelectedPacksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowseButtonLabel))));
    }

    public string BrowseButtonLabel
    {
        get
        {
            if (_labelOverride != null)
                return _labelOverride;

            int count = EnvironmentVariables.SelectedPacks.Count;
            return count switch
            {
                0 => "Select other packs",
                1 => "Selected 1 other pack",
                _ => $"Selected {count} other packs"
            };
        }
    }

    public void SetLabelOverride(string? label)
    {
        _labelOverride = label;
        _dispatcher.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowseButtonLabel))));
    }
}
public sealed partial class MainWindow : Window
{
    #region MainWindow Boilerplate
    public static MainWindow? Instance { get; private set; }

    private bool _isClosing = false;
    private bool _isInitializing = true;

    // Set the moment MainWindow_Loaded finishes resolving the Minecraft data location cache
    // (see the _isInitializing = false line below) - anything that depends on that cache
    // being trustworthy, like .mcpack file-activation import, awaits this instead of
    // guessing at how long startup takes.
    private readonly TaskCompletionSource _initializedTcs = new();
    private Task WaitUntilInitializedAsync() => _initializedTcs.Task;

    /// <summary>
    /// Shared with the settings panel, which drives it for the duration of a hard reset -
    /// see MainWindow.SettingsOverlay.xaml.cs. Internal rather than private for that one
    /// reason; nothing outside this assembly touches it.
    /// </summary>
    internal readonly ProgressBarManager _progressManager;

    public readonly PackUpdater _updater = new();

    private LampAnimator? _titlebarLampAnimator;
    private LampAnimator? _splashLampAnimator;
    public PackSelectionViewModel PackVM { get; } = new();

    private async void InitializeLampAnimators()
    {
        // Titlebar lamp
        _titlebarLampAnimator = new LampAnimator(
            LampAnimator.LampContext.Titlebar,
            baseImage: iconImageBox,
            overlayImage: iconOverlayImageBox,
            haloImage: iconHaloImageBox
        );

        // Splash lamp
        _splashLampAnimator = new LampAnimator(
            LampAnimator.LampContext.Splash,
            baseImage: SplashLamp,
            overlayImage: null,
            haloImage: SplashLampHalo,
            superImage: SplashLampOverlay
        );

        // Initialize both immediately to preload and set special occasion images
        await Task.WhenAll(
            _titlebarLampAnimator.InitializeAsync(),
            _splashLampAnimator.InitializeAsync()
        );
    }
    private void InitializePreviewerImages()
    {
        var occasion = GetSpecialOccasionName();
        var (prefix, count) = occasion switch
        {
            "birthday" => ("vrtx.birthday", 3),
            "pumpkin" => ("vrtx.pumpkin", 3),
            "christmas" => ("vrtx.christmas", 5),
            _ => ("vrtx.app", 70)
        };
        var PreviewArt = Enumerable.Range(1, count)
            .Select(i => $"ms-appx:///Assets/previews/{prefix}.{i}.png").ToArray();
        Previewer.Instance.InitializeButton(SettingsButton, PreviewArt);


        Previewer.Instance.InitializeSlider(FogMultiplierSlider,
            "ms-appx:///Assets/previews/fog.default.png",
            "ms-appx:///Assets/previews/fog.min.png",
            "ms-appx:///Assets/previews/fog.max.png",
            Defaults.FogMultiplier
        );

        Previewer.Instance.InitializeSlider(EmissivityMultiplierSlider,
            "ms-appx:///Assets/previews/emissivity.default.png",
            "ms-appx:///Assets/previews/emissivity.min.png",
            "ms-appx:///Assets/previews/emissivity.max.png",
            Defaults.EmissivityMultiplier
        );

        Previewer.Instance.InitializeSlider(NormalIntensitySlider,
            "ms-appx:///Assets/previews/normals.default.png",
            "ms-appx:///Assets/previews/normals.flat.png",
            "ms-appx:///Assets/previews/normals.intense.png",
            Defaults.NormalIntensity
        );

        Previewer.Instance.InitializeSlider(RoughenUpSlider,
            "ms-appx:///Assets/previews/roughenup.default.png",
            "ms-appx:///Assets/previews/roughenup.unrough.png",
            "ms-appx:///Assets/previews/roughenup.rough.png",
            Defaults.RoughnessControlValue
        );

        Previewer.Instance.InitializeSlider(MaterialNoiseSlider,
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/roughenup.default.png",
             "ms-appx:///Assets/previews/materials.grainy.png",
             Defaults.MaterialNoiseOffset
        );

        Previewer.Instance.InitializeSlider(LazifyNormalsSlider,
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.default.png",
            "ms-appx:///Assets/previews/heightmaps.butchered.png",
            Defaults.LazifyNormalAlpha
        );

        Previewer.Instance.InitializeToggleSwitch(EmissivityAmbientLightToggle,
            "ms-appx:///Assets/previews/emissivity.ambient.on.png",
            "ms-appx:///Assets/previews/emissivity.ambient.off.png"
        );

        Previewer.Instance.InitializeToggleButton(TargetPreviewToggle,
            "ms-appx:///Assets/previews/preview.overlay.png",
            "ms-appx:///Assets/previews/preview.png"
        );

        Previewer.Instance.InitializeCheckBox(VanillaRTXCheckBox,
            "ms-appx:///Assets/previews/checkbox.regular.ticked.png",
            "ms-appx:///Assets/previews/checkbox.regular.unticked.png"
        );
        if (GetSpecialOccasionName() == "birthday")
        {
            Previewer.Instance.InitializeCheckBox(NormalsCheckBox,
                "ms-appx:///Assets/previews/checkbox.normals.ticked.birthday.png",
                "ms-appx:///Assets/previews/checkbox.normals.unticked.birthday.png");
        }
        else
        {
            Previewer.Instance.InitializeCheckBox(NormalsCheckBox,
               "ms-appx:///Assets/previews/checkbox.normals.ticked.png",
               "ms-appx:///Assets/previews/checkbox.normals.unticked.png"
            );
        }
        Previewer.Instance.InitializeCheckBox(OpusCheckBox,
            "ms-appx:///Assets/previews/checkbox.opus.ticked.png",
            "ms-appx:///Assets/previews/checkbox.opus.unticked.png"
        );

        Previewer.Instance.InitializeButton(BrowsePacksButton,
            "ms-appx:///Assets/previews/locate.png"
        );

        Previewer.Instance.InitializeButton(ExportButton,
            "ms-appx:///Assets/previews/chest.export.png",
            "ms-appx:///Assets/previews/chest.export.png"
        );
        Previewer.Instance.InitializeButton(DeleteButton,
            "ms-appx:///Assets/previews/chest.delete.png",
            "ms-appx:///Assets/previews/chest.delete.png"
        );

        Previewer.Instance.InitializeButton(LaunchPackUpdateButton,
            GetSpecialOccasionName() == "christmas"
                ? "ms-appx:///Assets/previews/version.checker.christmas.png"
                : "ms-appx:///Assets/previews/version.checker.png"
        );

        Previewer.Instance.InitializeButton(TuneSelectionButton,
            "ms-appx:///Assets/previews/table.tune.png",
            "ms-appx:///Assets/previews/table.tune.overlay.png"
        );

        Previewer.Instance.InitializeButton(LaunchMinecraftButton,
            "ms-appx:///Assets/previews/minecart.launch.png",
            "ms-appx:///Assets/previews/minecart.launch.png"
        );

        Previewer.Instance.InitializeButton(HelpButton,
            "ms-appx:///Assets/previews/cubeir.help.png"
        );

        Previewer.Instance.InitializeButton(BugButton,
            "ms-appx:///Assets/previews/cubeir.bugs.png"
        );

        Previewer.Instance.InitializeButton(ResetButton,
            "ms-appx:///Assets/previews/table.reset.variables.png"
        );

        Previewer.Instance.InitializeButton(ClearButton,
            "ms-appx:///Assets/previews/table.reset.png"
        );

        Previewer.Instance.InitializeButton(LaunchShaderModManagerButton,
            "ms-appx:///Assets/previews/brtx.png"
        );

        Previewer.Instance.InitializeButton(LaunchDLSSSwapperButton,
            "ms-appx:///Assets/previews/dlss.png"
        );

        Previewer.Instance.InitializeButton(LaunchLUTManagerButton,
            "ms-appx:///Assets/previews/lut.png"
        );

        Previewer.Instance.InitializeButton(LaunchAlchitexButton,
            "ms-appx:///Assets/previews/alchitex.png",
            "ms-appx:///Assets/previews/alchitex.overlay.png"
        );

        _ = Previewer.Instance.PreloadAllRegisteredImagesAsync();
    }

    #endregion

    public MainWindow()
    {
        // Set Window-level properties before initializing
        SetMainWindowProperties();
        InitializeComponent();

        InitializeLogTypewriter();
        InitializeLampAnimators();
        SetTitleBar(TitleBarDragArea);
        Instance = this;
        _progressManager = new ProgressBarManager(ProgressBar);

        // Do upon app closure
        this.Closed += (s, e) =>
        {
            _isClosing = true;

            SaveSettings();

            _typewriterTimer?.Stop();

            // Static event, instance handler - it would outlive the window otherwise.
            PackUpdater.DeployableCacheChanged -= OnDeployableCacheChanged;

            // A module is part of this window now, so it has no native side to tear down -
            // but its teardown still has to run, or a cancellation token is never tripped and
            // a temp folder is never swept.
            CloseAllModules();
        };

        // Our own titlebar buttons dim with the window, matching the system's caption
        // buttons beside them. The whole group goes over as one container - see
        // TitleBarFocus for why that beats naming each button here. The centered
        // "Vanilla RTX App" title is deliberately NOT included: it's the app's identity,
        // and it stays at full strength whether the window is focused or not.
        TitleBarFocus.Attach(this, TitleBarActions, ModuleTitleBar);

        ModuleTitleBar.ReturnRequested += ModuleTitleBar_ReturnRequested;
        RootElement.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(ModuleEscape_KeyDown), handledEventsToo: false);
        AttachOverlayFocusTrap();

        // Things to do after mainwindow is initialized...
        if (Content is FrameworkElement root)
            root.Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// Disables whatever the announcements .md lists under <c># SuspendControls</c>, twice: at
    /// once from the cache App already parsed synchronously, then again if the fetch App started
    /// comes back fresh, which is what gets a newly added suspension to everyone within the
    /// fetch cooldown rather than one launch later. Both are additive; see
    /// <see cref="WindowControlsManager.SuspendControls"/>. Modules pick the list up as they
    /// load, in <see cref="OpenModule"/>.
    /// </summary>
    private async Task ApplyRemoteSuspensionsAsync()
    {
        WindowControlsManager.SuspendControls(Content, TeletextContent.SuspendControls);

        if (await Teletext.LatestUpdate && !_isClosing)
            WindowControlsManager.SuspendControls(Content, TeletextContent.SuspendControls);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FrameworkElement? root = Content as FrameworkElement;

            if (root != null)
                root.Loaded -= MainWindow_Loaded;

            // Load variables back in from previous session
            LoadSettings();

            // APPLY THEME - the loaded AppThemeMode, not a change to it
            ApplyThemeMode();

            double speedMultiplier = Persistent.SuspendUIAnimations ? 0.64 : 1.0;

            // Give the window time to render
            await Task.Delay((int)(175 * speedMultiplier));

            // Apply some colors, then continue to watch theme changes and adjust based on theme
            if (root != null)
            {
                ThemeService.ApplyTitleBarColors(this.AppWindow, root.ActualTheme);
                root.FlowDirection = FlowDirection.LeftToRight;

                ApplyTargetPreviewBevelColors(root.ActualTheme);
                ApplyLocateUserDataColors(root.ActualTheme);
                TargetPreviewToggle.IsEnabledChanged += (s, e) =>
                {
                    if (_isClosing) return; // It crashes the app if we try to set it while window is closed, duh!
                    ApplyTargetPreviewBevelColors(((FrameworkElement)Content).ActualTheme);
                };
                BrowsePacksButton.IsEnabledChanged += (s, e) =>
                {
                    if (_isClosing) return;
                    ApplyLocateUserDataColors(((FrameworkElement)Content).ActualTheme);
                };

                // The saved theme was applied above, before this subscription existed, so the
                // ActualThemeChanged it caused has already fired to nobody. Everything painted
                // from code that follows ThemeChanged - the settings panel's seams, the document
                // headers' Return bevel - was built before the setting was even read, and hears
                // what it is drawing in from this call and nowhere else. Without it, launching
                // straight into a theme other than Windows' left them in the wrong one until the
                // user switched away and back.
                ThemeService.Broadcast(root.ActualTheme);
                root.ActualThemeChanged += (_, __) =>
                {
                    if (_isClosing) return;
                    ThemeService.ApplyTitleBarColors(this.AppWindow, root.ActualTheme);
                    ApplyTargetPreviewBevelColors(root.ActualTheme);
                    ApplyLocateUserDataColors(root.ActualTheme);

                    ThemeService.Broadcast(root.ActualTheme);
                };
            }

            // Check for crash logs, might summon a ContentDialogue
            await CheckForCrashLog();

            // Splash Blinking Animation
            _ = AnimateSplash(100 * speedMultiplier);

            // Attach previewer/art vessels
            Previewer.Initialize(PreviewVesselTop, PreviewVesselBottom, PreviewVesselBackground);

            // Subscribe the Get-latest-packs glyph to the cache state once, here, and never think
            // about it again: PackUpdater raises this itself whenever it invalidates or fills the
            // cache, from wherever that happened. Before this, every site that could change the
            // cache owed a copy of the if/else below, which is the kind of debt only ever paid
            // late. Nothing probes the cache here: the glyph also needs the remote's answer
            // before it can say anything (see ApplyVanillaRTXGlyph), and the LocatePacksTask
            // further down gets both in one pass.
            PackUpdater.DeployableCacheChanged += OnDeployableCacheChanged;

            // Update UI to reflect loaded settings
            UpdateUI(1);

            // Calling it last since it might add a bit of delay as it searches a few dirs and files
            MinecraftGDKLocator.ValidateAndUpdateCachedLocations();

            // Assign Previewer images a bit after attaching vessels, just a safety gap
            InitializePreviewerImages();

            // Remote kill switch, applied under the splash so a suspended control is never seen enabled
            _ = ApplyRemoteSuspensionsAsync();

            // Brief delay to ensure everything is fully locked and loaded, then fade out splash screen
            await Task.Delay((int)(700 * speedMultiplier));
            // ================ Do all UI updates you DON'T want to be seen BEFORE here, and for what you want seen, AFTER here =======================

            // Apply Suspend Previewer, but won't toggle it (only the settings panel can)
            ApplySuspendUIAnimations(invokedByUser: false);

            // Startup log
            string startupAppendLog = string.Empty;
#if DEBUG
            startupAppendLog = " [DEBUG BUILD]";
#endif
            string startupLog = $"App Version: {appVersion}" + startupAppendLog + new string('\n', 2) +
                                $"Not affiliated with Mojang or NVIDIA;\nby continuing, you consent to modifications to your Minecraft installations & data.";
            Log(startupLog);
            ToolTipService.SetToolTip(TitleBarText, $"Version: {appVersion + startupAppendLog}");

            // Warning if MC is running
            if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            {
                Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
            }


            _isInitializing = false; // This makes sure ONLY the earlier call from UpdateUI -> TogglePreview_checked is blocked from running similar operations as below, aka, Unblocks these operations from running in regular Preview button toggles
            MinecraftUserDataLocator.ValidateAndUpdateCachedLocations(); // Similar to GDKLocator but faster since it deals with fewer passes, and we want its warning messages
            _initializedTcs.TrySetResult(); // The data-location cache is now trustworthy - see WaitUntilInitializedAsync
            UpdateUserDataDependentUI(IsTargetingPreview); // Updates UI based on location cache status
            SettingsPanel.Initialize(this); // Paint the settings panel now both locators have settled
            Bindings.Update(); // Update bindings cause of a x:Bind gotcha where values come alive after some unrelated property change

            _ = LocatePacksTask(); // Trigger finding packs

            // Same split as Teletext: this only refreshes the cache, and whoever reads an
            // asset takes whatever is there at the time. Nothing waits on it, and the packaged
            // copies mean nothing breaks if it never finishes.
            Modules.AssetUpdater.TriggerUpdate(Modules.Alchitex.Core.AlchitexAssets.All);

            // By the time we get here, on good internet the Teletext fetch is already done (called from App.xaml.cs). On bad internet it may be stale cache, it's ok, we show it anyway
            // The whole idea is, there is separation of concerns, on this side, we only show what's in the cache, the app tries to update the cache sometimes
            // we deal with cache, for showing things, another task deals with updating sometimes it at App start
            _ = Task.Run(async () =>
            {
                await Task.Delay((int)(750 * speedMultiplier));
                var Teletext = Core.Teletext.GetFiltered(TeletextContent.LogAnnouncements);
                if (Teletext is { Length: > 0 })
                {
                    for (int i = Teletext.Length - 1; i >= 0; i--)
                    {
                        Log(Core.Overlays.MarkdownRenderer.ToPlainText(Teletext[i].Text));
                        await Task.Delay((int)(700 * speedMultiplier));
                    }
                }
            });

            // Random previewer image
            var occasion = GetSpecialOccasionName();
            var (prefix, count) = occasion switch
            {
                "birthday" => ("vrtx.birthday", 3),
                "pumpkin" => ("vrtx.pumpkin", 3),
                "christmas" => ("vrtx.christmas", 5),
                _ => ("vrtx.app", 70)
            };
            int rng = Random.Shared.Next(1, count + 1);
            Previewer.Instance.SetStartupImages($"ms-appx:///Assets/previews/{prefix}.{rng}.png");

            // Show Leave a Review prompt
            _ = ReviewPromptManager.InitializeAsync(MainGrid);

            await FadeOutSplashScreen();

            // ============= End
            async Task FadeOutSplashScreen()
            {
                if (SplashOverlay == null) return;

                if (Persistent.SuspendUIAnimations)
                {
                    SplashOverlay.Opacity = 0.0;
                    SplashOverlay.Visibility = Visibility.Collapsed;
                    return;
                }

                var fadeOut = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(100)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                var storyboard = new Storyboard();
                Storyboard.SetTarget(fadeOut, SplashOverlay);
                Storyboard.SetTargetProperty(fadeOut, "Opacity");
                storyboard.Children.Add(fadeOut);

                var tcs = new TaskCompletionSource<bool>();
                storyboard.Completed += (s, e) =>
                {
                    SplashOverlay.Visibility = Visibility.Collapsed;
                    tcs.SetResult(true);
                };

                storyboard.Begin();
                await tcs.Task;
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Mainwindow_Loaded ", ex?.Message ?? "Unknown error", ex!.ToString());

            // A crash here that lands above the TrySetResult in the try block would
            // otherwise leave WaitUntilInitializedAsync awaiting forever - better to let a
            // waiting import go ahead and fail on its own than hang silently.
            _initializedTcs.TrySetResult();
        }
    }

    #region Main Window properties and essential components used throughout the app
    private void SetMainWindowProperties()
    {
        ExtendsContentIntoTitleBar = true;
        this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var manager = WinUIEx.WindowManager.Get(this);
        manager.PersistenceId = "MainWindow";
        manager.Width = WindowSizeX; // WinUIEx scales internally
        manager.Height = WindowSizeY;
        manager.MinWidth = WindowMinSizeX;
        manager.MinHeight = WindowMinSizeY;
        manager.IsResizable = true;
        manager.IsMaximizable = true;

        // Center window if we no saved pos, by quering if "WinUIEx" key container exists or not, which keep saved positions)
        var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (!settings.Containers.ContainsKey("WinUIEx"))
            this.CenterOnScreen();

        this.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.ico"));
    }

    private async Task CheckForCrashLog()
    {
        try
        {
            var logPath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "last_session_crash_log.txt");

            if (!File.Exists(logPath)) return;

            var content = File.ReadAllText(logPath);
            File.Delete(logPath);

            var copyButton = new Button
            {
                Content = "Copy Crash Logs",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var githubLink = new HyperlinkButton
            {
                Content = "Create an issue on GitHub",
                NavigateUri = new Uri("https://github.com/vanilla-rtx/Vanilla-RTX-App/issues"),
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(4, 4, 4, 4)
            };

            var discordLink = new HyperlinkButton
            {
                Content = "Create a post on the Vanilla RTX Discord Server",
                NavigateUri = new Uri("https://discord.gg/A4wv4wwYud"),
                Padding = new Thickness(4, 4, 4, 4)
            };

            var logBox = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = content,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 200,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dismissButton = new Button
            {
                Content = "Continue Using the App (dismisses the report)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var panel = new StackPanel { Spacing = 12 };

            panel.Children.Add(new TextBlock
            {
                Text = "Oh no! Looks like a crash occurred during the previous session, you may continue to use the app, but it would be better if you report it to the developer to see it patched up soon!",
                TextWrapping = TextWrapping.Wrap
            });

            var linksPanel = new StackPanel { Spacing = 2 };
            linksPanel.Children.Add(new TextBlock { Text = "Report using one of the following methods:" });
            linksPanel.Children.Add(githubLink);
            linksPanel.Children.Add(discordLink);
            panel.Children.Add(linksPanel);

            panel.Children.Add(new TextBlock
            {
                Text = "Crash details:",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(logBox);
            panel.Children.Add(copyButton);
            panel.Children.Add(dismissButton);

            var dialog = new ContentDialog
            {
                Title = "Previous Session Crash Report",
                Content = panel,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
            };

            copyButton.Click += async (s, e) =>
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                copyButton.Content = "Copied!";
                await Task.Delay(1500);
                copyButton.Content = "Copy Crash Log";
            };

            dismissButton.Click += (s, e) => dialog.Hide();

            await dialog.ShowAsync();
        }
        catch { }
    }


    public static async Task OpenUrl(string url)
    {
#if DEBUG
        Log("OpenUrl is disabled in debug builds.", LogLevel.Informational);
        return;
#else
        try
        {
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                throw new ArgumentException("Malformed URL.");

            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Log($"Details: {ex.Message}", LogLevel.Informational);
            Log("Failed to open URL. Make sure you have a browser installed and associated with web links.", LogLevel.Warning);
        }
#endif
    }

    /// <summary>
    /// Every long-running operation in this window locks its controls through here rather than
    /// calling <see cref="WindowControlsManager.ToggleSpecificControls"/> directly, and the only
    /// thing this adds is <c>SettingsButton</c>.
    ///
    /// <para><b>It is the one place that rule can be written down.</b> The settings panel is
    /// where the Minecraft install and user data locations are changed; every operation that
    /// disables anything here is using those locations while it runs, so none of them may leave
    /// that panel reachable. Ten call sites each remembering to list the button is ten chances
    /// to forget, and the eleventh - added later, by someone who never read this - would forget
    /// silently.</para>
    ///
    /// <para>Pass the same names to the <c>false</c> and <c>true</c> calls: the manager
    /// reference-counts per control, and an unbalanced pair leaves a control disabled for the
    /// rest of the session.</para>
    /// </summary>
    private void LockControls(bool enable, params string[] names)
        => WindowControlsManager.ToggleSpecificControls(this, enable, [.. names, nameof(SettingsButton)]);

    /// <summary>
    /// The titlebar lamp, and the app's one ambient status light. Called from here, from the
    /// settings panel and from every feature module (through <c>ModuleOverlay.Host</c>).
    ///
    /// <para><b>The parameters are a vocabulary rather than a dial.</b>
    /// <paramref name="singleFlashOnChance"/> 1.0 is the on-flash for something that arrived or
    /// opened and 0.0 the off-flash for something closed or reset; 0.5 is for an event that is
    /// honestly neither, like the Auto theme or selecting every pack carrying a tag.
    /// <paramref name="rapidFlashChance"/> 1.0 is the loud version, for one heavy thing landing
    /// or for an indeterminate event worth noticing. <paramref name="enable"/> with
    /// <paramref name="singleFlash"/> false starts and stops the continuous blink instead -
    /// the animation built for something that runs for a while.
    /// </para>
    ///
    /// <para><b>Nothing else ever stops the continuous blink</b>, so start it fire-and-forget
    /// and stop it in a <c>finally</c>: a path that leaves without stopping leaves the lamp
    /// blinking for the rest of the session.</para>
    ///
    /// <para><b>A single flash that arrives while the lamp is busy is dropped, not queued</b> -
    /// it takes the animation lock with a zero timeout and gives up. So a flash fired next to a
    /// stop that has not finished is simply lost, and awaiting the stop to fix that costs the
    /// caller the blink loop's whole wind-down. Neither is worth holding a caller up for: this
    /// is a light in a titlebar, and every call site fires and forgets.</para>
    ///
    /// <para>Disabling is allowed through even under <c>SuspendUIAnimations</c>, so a lamp left
    /// blinking by a run that started before the setting changed can still be turned off.</para>
    /// </summary>
    public async Task BlinkingLamp(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75, double rapidFlashChance = 0.05)
    {
        if (!SuspendUIAnimations || !enable) // If the intention is to disable, allow it to pass in
        {
            await _titlebarLampAnimator!.Animate(enable, singleFlash, singleFlashOnChance, rotate: GetSpecialOccasionName() != null, rapidFlashChance: rapidFlashChance);
        }
    }
    private async Task AnimateSplash(double splashDurationMs)
    {
        if (!SuspendUIAnimations)
        {
            await _splashLampAnimator!.Animate(false, true, 0.9, duration: splashDurationMs, rotate: GetSpecialOccasionName() != null, rapidFlashChance: 0.01);
        }
    }


    public async void UpdateUI(double animationDurationMilisecs = 100)
    {
        // Only freeze/unfreeze if we aren't already being handled by SuspendUIAnimations
        if (!SuspendUIAnimations) Previewer.Instance.Freeze();

        // 1. Match bool-based UI elements to their current bools
        TargetPreviewToggle.IsChecked = Persistent.IsTargetingPreview;
        EmissivityAmbientLightToggle.IsOn = Persistent.AddEmissivityAmbientLight;
        VanillaRTXCheckBox.IsChecked = EnvironmentVariables.IsVanillaRTXEnabled;
        NormalsCheckBox.IsChecked = EnvironmentVariables.IsNormalsEnabled;
        OpusCheckBox.IsChecked = EnvironmentVariables.IsOpusEnabled;

        // Sliders/texbox pairs
        var sliderConfigs = new[]
        {
        (FogMultiplierSlider, FogMultiplierBox, Persistent.FogMultiplier, false),
        (EmissivityMultiplierSlider, EmissivityMultiplierBox, Persistent.EmissivityMultiplier, false),
        (NormalIntensitySlider, NormalIntensityBox, (double)Persistent.NormalIntensity, true),
        (MaterialNoiseSlider, MaterialNoiseBox, (double)Persistent.MaterialNoiseOffset, true),
        (RoughenUpSlider, RoughenUpBox, (double)Persistent.RoughnessControlValue, true),
        (LazifyNormalsSlider, LazifyNormalsBox, (double)Persistent.LazifyNormalAlpha, true)
        };

        // 2. Animate sliders (intentionally put here, don't move up or down)
        await AnimateSliders(sliderConfigs, animationDurationMilisecs);

        // Resume Previewer Updates
        if (!SuspendUIAnimations) Previewer.Instance.Unfreeze();

        async Task AnimateSliders((Slider slider, TextBox textBox, double targetValue, bool isInteger)[] configs, double durationMiliSecs)
        {
            // Suspended: straight to the final values. Running the loop with the duration
            // scaled down to ~1ms landed in the same place, but only by way of a stopwatch, a
            // frame of interpolation and an await - and it was one rounding away from showing
            // a half-swept slider on a slow frame.
            if (!Persistent.SuspendUIAnimations)
            {
                var startValues = configs.Select(c => c.slider.Value).ToArray();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var totalMs = durationMiliSecs;

                while (stopwatch.ElapsedMilliseconds < totalMs)
                {
                    var progress = stopwatch.ElapsedMilliseconds / totalMs;
                    var easedProgress = 1 - Math.Pow(1 - progress, 3);

                    for (int i = 0; i < configs.Length; i++)
                    {
                        var (slider, textBox, targetValue, isInteger) = configs[i];
                        var currentValue = Lerp(startValues[i], targetValue, easedProgress);
                        SetSliderValue(slider, textBox, currentValue, isInteger);
                    }
                    await Task.Delay(2);
                }
            }

            for (int i = 0; i < configs.Length; i++)
            {
                var (slider, textBox, targetValue, isInteger) = configs[i];
                SetSliderValue(slider, textBox, targetValue, isInteger);
            }
            void SetSliderValue(Slider slider, TextBox textBox, double value, bool isInteger)
            {
                var rounded = isInteger ? Math.Round(value) : Math.Round(value, 2);
                slider.Value = rounded;
                textBox.Text = isInteger ? rounded.ToString() : rounded.ToString("0.00");
            }

            double Lerp(double start, double end, double t) => start + (end - start) * t;
        }
    }


    #endregion -------------------------------


    #region Titlebar Features -------------------------------
    private static int lampSecretMessageCounter = 0;
    private void LampInteraction_Click(object sender, RoutedEventArgs e)
    {
        _ = BlinkingLamp(true, true, 1.0, 0.1);

        lampSecretMessageCounter++;
        if (lampSecretMessageCounter > (DateTime.Now.Year - 2005)) // it amounts to having to click 1 more time every year, starting in 2026, 21 times
        {
            if (RuntimeFlags.Set("Has_said_the_Thing_about_Debug_Logs_something_2"))
            {
                Log("What? you're expecting some kind of hidden message?? Believe me I've crammed enough of those throughout the app already.", LogLevel.VanillaRTX);
                Task.Run(async () =>
                {
                    await Task.Delay(5666);
                    Log("But now that you've found this one in particular, I won't leave you empty-handed. Wait a couple of seconds...", LogLevel.Lengthy);
                    await Task.Delay(6660);
                    _ = OpenUrl("https://youtu.be/1MhB8mF10H4?si=UragVyvGtqUgm4Oi&t=450");
                    await Task.Delay(3014);
                    Log("I just love this piece! That's it. Hope you like it too.", LogLevel.Misc);
                    await Task.Delay(delay: TimeSpan.FromMinutes(11));
                    Log("The secret message you triggered ten minutes ago wasn't done yet... It might do something, several hours from now...", LogLevel.Lengthy);
                    await Task.Delay(delay: TimeSpan.FromHours(7));
                    Log("This was Cubeir, creator of Vanilla RTX, this app, and everything else around it...", LogLevel.VanillaRTX);
                    await Task.Delay(2718);
                    Log("If people knew the amount of love, effort, time I had to put in and the difficulties I had to push through to keep this up, maybe they'd appreciate it.. just a tiny bit more", LogLevel.Error);
                    await Task.Delay(2718);
                    Log("Despite everything, I continued; Out of necessity. Never wavered. That is how good things are made after all!", LogLevel.Warning);

                    int iteration = 0;
                    var rng = Random.Shared;
                    string[] baseMsgs = { "If people knew the amount of love, effort, time I had to put in and the difficulties I had to push through to keep this up, maybe they'd appreciate it.. just a tiny bit more",
                                             "Despite everything, I continued; Out of necessity. Never wavered. That is how good things are made after all!" };
                    LogLevel[] levels = { LogLevel.Warning, LogLevel.Error, LogLevel.Teletext, LogLevel.Lengthy };
                    string[] spookyEmojis = { "👁️" };

                    // Deteriorate the message over time, then make it seem like It's lagging to creep out the user
                    while (true)
                    {
                        iteration++;

                        string baseMsg = baseMsgs[rng.Next(baseMsgs.Length)];
                        char[] chars = baseMsg.ToCharArray();
                        double c = iteration / 50.0;
                        int corruptCount = (int)(chars.Length * c);
                        double t = Math.Max(0, (iteration - 15) / 35.0);
                        int delay = (int)(500 + 9500 * (t * t * t));

                        for (int i = 0; i < corruptCount; i++)
                        {
                            int pos = rng.Next(chars.Length);
                            chars[pos] = (char)rng.Next(33, 126);
                        }

                        // Sprinkle creepy emojis at random positions
                        string msg = new string(chars);
                        int emojiCount = rng.Next(1, 4);
                        for (int i = 0; i < emojiCount; i++)
                        {
                            if (rng.NextDouble() < 0.1)
                            {
                                int pos = rng.Next(msg.Length);
                                msg = msg.Insert(pos, spookyEmojis[rng.Next(spookyEmojis.Length)]);
                            }
                        }

                        await Task.Delay(delay);
                        Log(msg, levels[rng.Next(levels.Length)]);
                    }
                });
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Titlebar buttons
    //
    //  Three of them, and the grouping is deliberate: Settings, Help and Bugs are the only
    //  buttons whose whole job is to put something over the window's main body. All three
    //  open an overlay that starts 36px down - flush under the titlebar's bevelled edge - so
    //  the titlebar keeps reading as the app's frame while the body swaps underneath it.
    //  Discord, Ko-fi, theme and animation suspension used to sit here too; none of them
    //  opens anything, and they live in the settings panel now.
    //
    //  All three are toggles, and they are mutually exclusive: clicking one while another is
    //  open closes that one first. OpenDocument and SettingsButton_Click below are the two
    //  halves of that rule.
    // ═════════════════════════════════════════════════════════════════════════

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.IsOpen)
        {
            SettingsPanel.Hide();
            return;
        }

        // The markdown overlay's own close animation runs to completion before the panel
        // opens, for the same reason MarkdownOverlay chains its document swaps that way: a
        // silent swap under a panel that is still visibly there reads as a glitch.
        if (DocsOverlay.IsOpen)
            DocsOverlay.Close(onClosed: SettingsPanel.Show);
        else
            SettingsPanel.Show();

        _ = BlinkingLamp(true, true, 1.0, 0.0);
    }

    /// <summary>The two documents' header titles - shared with the vanillartx:// links that open them (MainWindow.LinkCommands.cs).</summary>
    private const string DocumentationTitle = "Vanilla RTX App Documentation";
    private const string BugTrackerTitle = "Known Minecraft RTX Bugs & Issues";

    /// <summary>
    /// The settings panel is closed first if it's open,
    /// then the document opens. <see cref="MarkdownOverlay.Show"/> already handles the
    /// document-to-document case (same URL toggles, a different one swaps), so this only owns
    /// the cross-overlay half of the rule.
    /// </summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.IsOpen)
            SettingsPanel.Hide();

        DocsOverlay.Show(url: Links.Documentation, title: DocumentationTitle, glyph: "");

        _ = BlinkingLamp(true, true, 1.0, 0.0);
    }
    private void BugButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.IsOpen)
            SettingsPanel.Hide();

        DocsOverlay.Show(url: Links.BugTracker, title: BugTrackerTitle, glyph: "");

        _ = BlinkingLamp(true, true, 1.0, 1.0);
    }



    /// <summary>
    /// Applies <see cref="Persistent.AppThemeMode"/> to the window root. Called once at
    /// startup and again whenever the settings panel's theme dropdown changes it - the mode
    /// string is the single source of truth, and this only ever reads it.
    ///
    /// <para>Setting <c>RequestedTheme</c> is what eventually fires <c>ActualThemeChanged</c>,
    /// which is where the titlebar colours, the bevels and <see cref="ThemeService.Broadcast"/>
    /// hang - so nothing else needs doing here.</para>
    /// </summary>
    public void ApplyThemeMode()
    {
        var root = Content as FrameworkElement;
        if (root == null) return;

        var targetTheme = Persistent.AppThemeMode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (root.RequestedTheme != targetTheme)
            root.RequestedTheme = targetTheme;
    }


    /// <summary>
    /// Brings the window in line with <see cref="Persistent.SuspendUIAnimations"/>. The
    /// settings panel flips the flag and calls this; startup calls it with
    /// <paramref name="invokedByUser"/> false.
    ///
    /// <para><b>The false branch is deliberately gated on <paramref name="invokedByUser"/>.</b>
    /// At startup the vessels are already visible and the Previewer has never been frozen, so
    /// "unfreezing" there is not a no-op - it unfreezes something mid-initialization that was
    /// never frozen, which is how the Previewer ends up running before its images exist.</para>
    /// </summary>
    public void ApplySuspendUIAnimations(bool invokedByUser)
    {
        if (SuspendUIAnimations)
        {
            PreviewVesselBackground.Visibility = Visibility.Collapsed;
            PreviewVesselBottom.Visibility = Visibility.Collapsed;
            PreviewVesselTop.Visibility = Visibility.Collapsed;
            Previewer.Instance.Freeze();
        }
        else if (invokedByUser)
        {
            PreviewVesselBackground.Visibility = Visibility.Visible;
            PreviewVesselBottom.Visibility = Visibility.Visible;
            PreviewVesselTop.Visibility = Visibility.Visible;
            Previewer.Instance.Unfreeze();
        }
    }


    #endregion ------------------------------- Titlebar Features


    // ---------------- Vanilla RTX status (cache glyph + update notice) ----------------
    //
    // Both of these are PackUpdater's answers, drawn here. This window owns only the wording and
    // the glyphs, because both name things that live in this window's own XAML.

    // The two facts the glyph is built from. The cache half is pushed here by PackUpdater; the
    // update half is pulled once the remote has been asked, and is kept per edition because
    // Release and Preview have different packs installed and so get different answers. An edition
    // missing from the map has not been asked yet, which is deliberately not the same as "no
    // updates": until the remote has answered, the button promises nothing.
    private bool _hasDeployableCache;
    private readonly Dictionary<bool, bool> _vanillaRTXUpdatesAvailable = new();

    /// <summary>
    /// Paints the get-latest-packs glyph from those two facts. UI thread only.
    ///
    /// Syncfolder is a promise that opening the updater installs straight off the disk, and that
    /// needs both halves to agree: a zipball is cached, and nothing is behind the remote. Updates
    /// waiting means that zipball is about to be dropped as stale the moment the updater opens
    /// (<see cref="PackUpdater.InvalidateCacheIfStaleAsync"/>), and knowing an update exists at
    /// all means the remote was reachable - so the download the cloud implies is one that can
    /// actually happen. The cloud is equally right with no cache at all, and before anyone has
    /// asked, which is the state the XAML already starts in.
    /// </summary>
    private void ApplyVanillaRTXGlyph()
    {
        var knownUpToDate = _vanillaRTXUpdatesAvailable.TryGetValue(IsTargetingPreview, out var available)
                            && !available;

        UpdateVanillaRTXGlyph.Glyph = (_hasDeployableCache && knownUpToDate)
            ? "\uE8F7"  // Syncfolder - installing is a copy out of the cached zipball
            : "\uEBD3"; // Cloud - installing has to download first
    }

    /// <summary>
    /// Follows <see cref="PackUpdater.DeployableCacheChanged"/>, which is how a cache change made
    /// while this window is idle - an install finishing inside the updater, a stale zipball being
    /// dropped - reaches the glyph. Raised on whichever thread made the change, so it marshals.
    /// </summary>
    private void OnDeployableCacheChanged(bool hasDeployableCache)
    {
        if (_isClosing) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;

            _hasDeployableCache = hasDeployableCache;
            ApplyVanillaRTXGlyph();
        });
    }

    // The first check of a session waits, so it lands after the startup burst of web calls
    // (Teletext, the Alchitex asset refresh) rather than joining it. Only the remote lookup
    // waits - locating packs, which is all local, is never held up by this.
    private static readonly TimeSpan UpdateNoticeStartupDelay = TimeSpan.FromMilliseconds(2060);
    private bool _updateNoticeStartupDelayPending = true;
    private int _updateNoticeCheckInFlight;
    private int _updateNoticeCheckPending;

    /// <summary>
    /// Serialises <see cref="RunVanillaRTXStatusPassAsync"/>, because <see cref="LocatePacksTask"/>
    /// has nine call sites and several of them fire in bursts.
    ///
    /// A request that arrives mid-pass is remembered rather than dropped. Dropping it is wrong
    /// specifically when it came from the Preview toggle: the running pass snapshotted the old
    /// edition and is about to discard its own answer as stale, so both would end up saying
    /// nothing and the new edition would go unexamined until something else re-located packs.
    /// </summary>
    private async Task RefreshVanillaRTXStatusAsync()
    {
        if (Interlocked.Exchange(ref _updateNoticeCheckInFlight, 1) == 1)
        {
            Interlocked.Exchange(ref _updateNoticeCheckPending, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _updateNoticeCheckPending, 0);
                await RunVanillaRTXStatusPassAsync();
            }
            while (Interlocked.Exchange(ref _updateNoticeCheckPending, 0) == 1);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MainWindow] Vanilla RTX status refresh failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _updateNoticeCheckInFlight, 0);
        }
    }

    /// <summary>
    /// Both halves of "what's going on with Vanilla RTX", run off the back of
    /// <see cref="LocatePacksTask"/> - the one method every path that changes what's installed or
    /// which edition we're targeting already goes through.
    ///
    /// The verdict is fetched on every pass, because <see cref="ApplyVanillaRTXGlyph"/> is drawn
    /// from it and a glyph that stopped updating would be a glyph that lies. That costs nothing
    /// in extra requests: the remote versions come out of the updater's own few-minute cache, the
    /// same one the updater window fills, so a burst of re-locates asks GitHub once at most.
    ///
    /// Only the log line is rationed - at most once per session per edition. Per edition is the
    /// point of the flag key: Release and Preview have different packs installed, so flipping the
    /// toggle asks a genuinely different question and deserves its own answer. The flag is only
    /// spent when something is actually said, so a check that failed offline doesn't silence a
    /// later one that succeeds.
    /// </summary>
    private async Task RunVanillaRTXStatusPassAsync()
    {
        var targetingPreview = IsTargetingPreview;

        // Read off the UI thread while we're still on it, before the first await.
        var menuName = UpdateVanillaRTXButtonText.Text;

        // Probing opens the cached zipball, so keep it off the UI thread. The return value is
        // what's used rather than the event, which by design only fires when the answer changed -
        // this is also the only regularly scheduled moment that catches a cache changed behind
        // PackUpdater's back, by a temp sweep or by hand.
        _hasDeployableCache = await Task.Run(() => _updater.RefreshDeployableCacheState());
        ApplyVanillaRTXGlyph();

        if (_updateNoticeStartupDelayPending)
        {
            _updateNoticeStartupDelayPending = false;
            await Task.Delay(UpdateNoticeStartupDelay);
        }

        // The edition can be toggled while we wait. If it moved, this answer is about the wrong
        // game - drop it, and the queued re-run picks the new one up.
        if (IsTargetingPreview != targetingPreview) return;

        var (notice, outdatedCount) = await _updater.GetUpdateNoticeAsync(
            VanillaRTXVersion, VanillaRTXNormalsVersion, VanillaRTXOpusVersion);

        if (IsTargetingPreview != targetingPreview) return;

        // None covers both "everything is current" and "the remote could not be reached", and the
        // glyph wants the same answer for each: nothing is going to invalidate the cached zipball,
        // so an install comes off the disk.
        _vanillaRTXUpdatesAvailable[targetingPreview] = notice == PackUpdateNotice.UpdatesAvailable;
        ApplyVanillaRTXGlyph();

        if (notice == PackUpdateNotice.None) return;

        var flagKey = targetingPreview ? "VanillaRTX_UpdateNotice_Preview" : "VanillaRTX_UpdateNotice_Release";
        if (!RuntimeFlags.Set(flagKey)) return;

        if (notice == PackUpdateNotice.NothingInstalled)
        {
            Log($"Start by installing a Vanilla RTX resource pack from the '{menuName}' menu!", LogLevel.VanillaRTX);
            return;
        }

        var editionName = MinecraftUserDataLocator.GetVersionDisplayName(targetingPreview);
        var lead = outdatedCount > 1 ? "Vanilla RTX updates are" : "A Vanilla RTX update is";

        Log($"{lead} available for {editionName}, check the '{menuName}' menu.", LogLevel.Package);
    }


    private Dictionary<bool, string?> _previousStatusMessages = new();
    public async Task LocatePacksTask(bool ShowLogs = false)
    {
        _ = BlinkingLamp(true, true, 1.0);

        // Reset controls
        VanillaRTXCheckBox.IsEnabled = false;
        VanillaRTXCheckBox.IsChecked = false;
        IsVanillaRTXEnabled = false;
        NormalsCheckBox.IsEnabled = false;
        NormalsCheckBox.IsChecked = false;
        IsNormalsEnabled = false;
        OpusCheckBox.IsEnabled = false;
        OpusCheckBox.IsChecked = false;
        IsOpusEnabled = false;

        VanillaRTXLocation = string.Empty;
        VanillaRTXNormalsLocation = string.Empty;
        VanillaRTXOpusLocation = string.Empty;

        VanillaRTXVersion = string.Empty;
        VanillaRTXNormalsVersion = string.Empty;
        VanillaRTXOpusVersion = string.Empty;

        // Status message
        var statusMessage = PackLocator.LocatePacks(IsTargetingPreview,
            out VanillaRTXLocation, out VanillaRTXVersion,
            out VanillaRTXNormalsLocation, out VanillaRTXNormalsVersion,
            out VanillaRTXOpusLocation, out VanillaRTXOpusVersion);

        _previousStatusMessages.TryGetValue(IsTargetingPreview, out var previousMessage);
        if (ShowLogs && statusMessage != previousMessage)
        {
            Log(statusMessage);
            _previousStatusMessages[IsTargetingPreview] = statusMessage;
        }

        // Enable checkboxes based on installation statuses
        if (!string.IsNullOrEmpty(VanillaRTXLocation) && Directory.Exists(VanillaRTXLocation))
        {
            VanillaRTXCheckBox.IsEnabled = true;
        }

        if (!string.IsNullOrEmpty(VanillaRTXNormalsLocation) && Directory.Exists(VanillaRTXNormalsLocation))
        {
            NormalsCheckBox.IsEnabled = true;
        }

        if (!string.IsNullOrEmpty(VanillaRTXOpusLocation) && Directory.Exists(VanillaRTXOpusLocation))
        {
            OpusCheckBox.IsEnabled = true;
        }

        // Deliberately not awaited: what's installed is known by now, and whether GitHub agrees
        // is a slower, entirely separate question that nothing above needs the answer to.
        _ = RefreshVanillaRTXStatusAsync();
    }



    private async void BrowsePacksButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
             "LaunchAlchitexButton", "LaunchPackUpdateButton",
              "TuneSelectionButton", "ExportButton", "DeleteButton", "BrowsePacksButton", "ClearButton", "ResetButton"
        ];
        // If user data isn't valid for the current edition, repurpose this click
        // to let the user locate the data folder manually instead.
        if (!MinecraftUserDataLocator.IsDataValid(IsTargetingPreview))
        {
            LockControls(false, ToDisable);
            await HandleManualDataLocationAsync(IsTargetingPreview);
            LockControls(true, ToDisable);
            return;
        }

        // The Usual Pack browser flow ============ Above is repurposed functionality of the button in case user data is missing

        OpenModule(() => new Modules.PackBrowser.PackBrowserOverlay(), ToDisable, _unused =>
        {
            if (EnvironmentVariables.SelectedPacks.Count > 0)
            {
                var names = string.Join(Environment.NewLine, EnvironmentVariables.SelectedPacks.Select(p => p.Name));
                Log($"Selected the following:\n{names}", LogLevel.Selected);
                _ = BlinkingLamp(true, true, 1.0);
            }
            else if (SelectedPacks.Count == 0)
            {
                Log($"Selected:\nNothing{(Random.Shared.Next(10) == 5 ? ", literally!" : ".")}", LogLevel.Selected);
            }
            else
            {
                _ = BlinkingLamp(true, true, 0.0);
            }
        });
    }



    /// <summary>
    /// Folder-picks a Minecraft user data root for one edition, validates it through
    /// <see cref="MinecraftUserDataLocator.TrySetCustomDataRoot"/> and caches it on success.
    /// Returns which of accepted, rejected (with the reason) or cancelled it came to - the
    /// settings panel shows a rejection under the row, the Browse-packs flow only needs the log.
    ///
    /// <para><b>The edition is a parameter rather than <c>IsTargetingPreview</c>.</b> The
    /// Browse-packs button only ever locates the edition the app is pointed at, but the
    /// settings panel lists both and lets either be set without switching targets first.</para>
    ///
    /// <para>The parent folder is accepted as a fallback because the picker makes it very easy
    /// to navigate one level too deep - selecting "Users" instead of the folder holding it.
    /// Nothing is cached unless the locator validates it, which is the same check that runs at
    /// every startup: accepting a path here that startup would reject is how a setting appears
    /// to silently revert itself.</para>
    /// </summary>
    public async Task<LocationPick> HandleManualDataLocationAsync(bool isPreview)
    {
        _ = BlinkingLamp(false, true, 0.5, 1.0);

        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isPreview);
        var expectedName = isPreview
            ? MinecraftUserDataLocator.PreviewRootFolderName
            : MinecraftUserDataLocator.StableRootFolderName;

        // Opens on this edition's currently cached data root when there is one - see
        // Helpers.PickFolderAsync for why the picker is the Microsoft.Windows one.
        var selectedPath = await Helpers.PickFolderAsync(
            WindowNative.GetWindowHandle(this),
            MinecraftUserDataLocator.GetDataRoot(isPreview));

        if (selectedPath == null) return LocationPick.Cancelled;

        string? acceptedPath = null;

        // The rejection reported is the selected folder's own, never the parent's: the parent
        // is a leniency the user didn't ask for, and "its parent isn't valid either" explains
        // nothing about what they picked.
        if (MinecraftUserDataLocator.TrySetCustomDataRoot(isPreview, selectedPath, out var rejection))
        {
            acceptedPath = selectedPath;
        }
        else
        {
            var parent = Directory.GetParent(selectedPath)?.FullName;
            if (parent != null && MinecraftUserDataLocator.TrySetCustomDataRoot(isPreview, parent, out _))
                acceptedPath = parent;
        }

        if (acceptedPath == null)
        {
            rejection ??= "That folder isn't a Minecraft user data folder.";
            Log($"That isn't a valid {versionName} data folder: {rejection} " +
                $"Please select the folder named \"{expectedName}\", it should be the one that contains a \"Users\" subfolder.",
                LogLevel.Error);
            return LocationPick.Rejected(rejection);
        }

        Log($"{versionName} data folder set: {acceptedPath}\n\n" +
            $"💾 The app is going to remember this location, you can now continue to use features that relied on user data.\n" +
            $"If you picked the wrong folder, you can change it again from Settings at any time.", LogLevel.Success);

        // Only the targeted edition drives this window's pack list and button states; setting
        // the other one is a valid thing to do and must not disturb what's on screen.
        if (isPreview == IsTargetingPreview)
        {
            UpdateUserDataDependentUI(IsTargetingPreview);
            _ = LocatePacksTask();
        }

        return LocationPick.Accepted(acceptedPath);
    }

    private void UpdateUserDataDependentUI(bool isTargetingPreview)
    {
        var isValid = MinecraftUserDataLocator.IsDataValid(isTargetingPreview);
        var versionName = MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview);

        if (isValid)
        {
            PackVM.SetLabelOverride(null);
            ToolTipService.SetToolTip(BrowsePacksButton,
                "Select resource packs that you'd want to tune, export, or delete, you can also import more packs into Minecraft from this menu.");

            BrowsePacksButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
            ApplyLocateUserDataColors(RightEdgeOfLocateButton.ActualTheme);

            _ = LocatePacksTask();
        }
        else
        {
            var editionLabel = isTargetingPreview ? "Preview" : "Stable";
            var expectedFolderName = isTargetingPreview
                ? MinecraftUserDataLocator.PreviewRootFolderName
                : MinecraftUserDataLocator.StableRootFolderName;

            PackVM.SetLabelOverride($"Locate {editionLabel} user data");
            ToolTipService.SetToolTip(BrowsePacksButton,
                $"The app couldn't find {versionName} data folder automatically - click to locate it manually." +
                $"\n\nYou can also set it, and the Preview one, from the Settings menu under Game user data.");

            BrowsePacksButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            ApplyLocateUserDataColors(RightEdgeOfLocateButton.ActualTheme);

            Log($"Couldn't find {versionName} user data folder automatically. Here's what to do:\n" +
                $"Click \"Locate {editionLabel} user data\" button above, find and select the folder named \"{expectedFolderName}\" " +
                $"- It's the one with a \"Users\" subfolder inside it.\n" +
                $"If you don't have {versionName} installed, you can ignore this warning. Also make sure you've played the game at least once if you've installed or reinstalled recently.\n" +
                $"Both editions' folders also live in the Settings menu, under Game user data - that's where to go if you pick the wrong one and want to change it again.",
                LogLevel.Error);
        }
    }
    private void ApplyLocateUserDataColors(ElementTheme theme)
    {
        bool needsAttention = !MinecraftUserDataLocator.IsDataValid(IsTargetingPreview);
        RightEdgeOfLocateButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right, accented: needsAttention, isEnabled: BrowsePacksButton.IsEnabled));
    }


    private void TargetPreviewToggle_Checked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = true;

        ApplyTargetPreviewBevelColors(LeftEdgeOfTargetPreviewButton.ActualTheme);

        // _Checked runs up until here IF the persistent IsTargetingPreview is True, UpdateUI makes sure this happens...

        if (_isInitializing) return; // return early, so the part below this line doesn't run on Window init-triggered TogglePreview (by UpdateUI method...)
        // Locating user data, updating ui based on it, and locating packs, runs regardless, we're avoiding the dupe operation here basically. so we safely run all there instead where its appropriate

        SelectedPacks.Clear();
        Log("Targeting Minecraft Preview.", LogLevel.MCPreview);

        MinecraftUserDataLocator.ValidateAndUpdateCachedLocations();
        UpdateUserDataDependentUI(IsTargetingPreview);
        _ = LocatePacksTask();
    }
    private void TargetPreviewToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        IsTargetingPreview = false;
        _ = BlinkingLamp(true, true, 0.0);

        ApplyTargetPreviewBevelColors(LeftEdgeOfTargetPreviewButton.ActualTheme);

        if (_isInitializing) return; // same as Checked

        SelectedPacks.Clear();
        Log("Targeting stable Minecraft Release.", LogLevel.MCRelease);

        MinecraftUserDataLocator.ValidateAndUpdateCachedLocations();
        UpdateUserDataDependentUI(IsTargetingPreview);
        _ = LocatePacksTask();
    }
    private void ApplyTargetPreviewBevelColors(ElementTheme theme)
    {
        LeftEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Left, accented: IsTargetingPreview, isEnabled: TargetPreviewToggle.IsEnabled));
        RightEdgeOfTargetPreviewButton.BorderBrush = new SolidColorBrush(
            ThemeService.GetBevelColor(theme, ThemeService.BevelEdge.Right, accented: IsTargetingPreview, isEnabled: TargetPreviewToggle.IsEnabled));
    }


    // Vanilla RTX Checkboxes
    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            switch (checkbox.Name)
            {
                case "VanillaRTXCheckBox":
                    IsVanillaRTXEnabled = true;
                    break;
                case "NormalsCheckBox":
                    IsNormalsEnabled = true;
                    break;
                case "OpusCheckBox":
                    IsOpusEnabled = true;
                    break;
            }
        }
    }
    private void Option_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox)
        {
            switch (checkbox.Name)
            {
                case "VanillaRTXCheckBox":
                    IsVanillaRTXEnabled = false;
                    break;
                case "NormalsCheckBox":
                    IsNormalsEnabled = false;
                    break;
                case "OpusCheckBox":
                    IsOpusEnabled = false;
                    break;
            }
        }
    }



    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Defaults
        FogMultiplier = Defaults.FogMultiplier;
        EmissivityMultiplier = Defaults.EmissivityMultiplier;
        NormalIntensity = Defaults.NormalIntensity;
        MaterialNoiseOffset = Defaults.MaterialNoiseOffset;
        RoughnessControlValue = Defaults.RoughnessControlValue;
        LazifyNormalAlpha = Defaults.LazifyNormalAlpha;
        AddEmissivityAmbientLight = Defaults.AddEmissivityAmbientLight;

        // Manually updates UI based on new values
        UpdateUI();

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);

        if (RuntimeFlags.Set("Said_Extra_Resetting_Information"))
        {
            Log($"Note:\nThis does not restore the packs to their default state!\nTo reset packs back to original you can quickly reinstall the latest versions of Vanilla RTX using the '{UpdateVanillaRTXButtonText.Text}' button. Other packs will require manual reinstallation. Use Export to back them up and quickly reimport them as you need.", LogLevel.Informational);
        }
        Log("Tuning environment reset.", LogLevel.Reset);
    }
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        // Capture previous state
        bool hadVanillaRTX = IsVanillaRTXEnabled || IsNormalsEnabled || IsOpusEnabled;
        bool hadCustomPacks = EnvironmentVariables.SelectedPacks.Count > 0;

        // Vanilla RTX
        IsVanillaRTXEnabled = false;
        IsNormalsEnabled = false;
        IsOpusEnabled = false;

        // Custom packs
        EnvironmentVariables.SelectedPacks.Clear();

        // Manually update UI based on new values
        UpdateUI();

        // Lamp single off flash
        _ = BlinkingLamp(true, true, 0.0);

        if (hadCustomPacks)
            Log("Cleared all pack selections.", LogLevel.Cleaning);
        else if (hadVanillaRTX)
            Log("Deselected all Vanilla RTX packs.", LogLevel.Cleaning);
        else
            Log("You haven't selected any resource pack to clear.", LogLevel.Cleaning);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
         "LaunchMinecraftButton", "TargetPreviewToggle",
         "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
         "TuneSelectionButton", "ExportButton", "DeleteButton",
         "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton"
        ];

        // Build the full list of pack locations to delete:
        // the three Vanilla RTX packs (if enabled) plus every custom selected pack.
        var toDelete = new List<(string location, string displayName)>();

        if (IsVanillaRTXEnabled && Directory.Exists(VanillaRTXLocation))
            toDelete.Add((VanillaRTXLocation, "Vanilla RTX"));
        if (IsNormalsEnabled && Directory.Exists(VanillaRTXNormalsLocation))
            toDelete.Add((VanillaRTXNormalsLocation, "Vanilla RTX Normals"));
        if (IsOpusEnabled && Directory.Exists(VanillaRTXOpusLocation))
            toDelete.Add((VanillaRTXOpusLocation, "Vanilla RTX Opus"));

        foreach (var (location, name, _, _) in EnvironmentVariables.SelectedPacks)
            if (!string.IsNullOrEmpty(location) && Directory.Exists(location))
                toDelete.Add((location, name));

        if (toDelete.Count == 0)
        {
            Log("Select at least one pack to delete.", LogLevel.Warning);
            return;
        }

        // Confirm with the user before nuking anything from disk.
        var dialog = new ContentDialog
        {
            Title = "Delete selected packs?",
            Content = $"This will delete {toDelete.Count} pack{(toDelete.Count == 1 ? "" : "s")} forever! (A very long time!)",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).ActualTheme
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            Log($"Deleting selected pack{(toDelete.Count > 1 ? "s" : string.Empty)} was cancelled by user.", LogLevel.Warning);
            return;
        }


        _progressManager.ShowProgress();
        LockControls(false, ToDisable);

        int deletedCount = 0;

        try
        {
            _ = BlinkingLamp(true, true, 0.0);

            // Deduplicate by normalised path so the same folder isn't deleted twice.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (location, displayName) in toDelete)
            {
                var normalised = Path.GetFullPath(location)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!seenPaths.Add(normalised))
                {
                    Log($"{displayName} was found in the deletion queue more than once, skipped duplicate selection.", LogLevel.Warning);
                    continue;
                }

                var deleted = await ExpImpDel.DeletePackAsync(location);
                if (deleted != null)
                {
                    deletedCount++;
                    Log($"Deleted {displayName}.", LogLevel.Success);
                }
                else
                {
                    Log($"Could not delete {displayName}.\nFor details, use \"Copy debug logs\" in the Settings menu.", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Delete failed: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            if (deletedCount == 0)
                Log("No packs were deleted.", LogLevel.Warning);

            _progressManager.HideProgress();

            LockControls(true, ToDisable);

            _ = LocatePacksTask(); // Controls get enabled, their state was captured before deletion, so we re-locate AFTER they're restored, so it properly disables packs that aren't there anymore
            SelectedPacks.Clear();
            UpdateUI(); // Just in case, truly don't know why, prolly afraid of checkboxes remaining "on" visually while disabled, while the bool being off
        }
    }
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
    "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
    "TuneSelectionButton", "DeleteButton", "ExportButton",
             "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton"
        ];

        _progressManager.ShowProgress();
        LockControls(false, ToDisable);

        int exportedCount = 0;

        try
        {
            var exportQueue = new List<(string path, string name)>();
            var suffix = $"_{appVersion}_App_Export";

            // ── Vanilla RTX packs ──────────────────────────────
            if (IsVanillaRTXEnabled && Directory.Exists(VanillaRTXLocation))
                exportQueue.Add((VanillaRTXLocation, "Vanilla_RTX_" + VanillaRTXVersion + suffix));
            if (IsNormalsEnabled && Directory.Exists(VanillaRTXNormalsLocation))
                exportQueue.Add((VanillaRTXNormalsLocation, "Vanilla_RTX_Normals_" + VanillaRTXNormalsVersion + suffix));
            if (IsOpusEnabled && Directory.Exists(VanillaRTXOpusLocation))
                exportQueue.Add((VanillaRTXOpusLocation, "Vanilla_RTX_Opus_" + VanillaRTXOpusVersion + suffix));

            // ── All selected custom packs ─────────────────────────────────────────
            foreach (var (location, name, _, _) in EnvironmentVariables.SelectedPacks)
            {
                if (!string.IsNullOrEmpty(name) && Directory.Exists(location))
                    exportQueue.Add((location, SanitizeFileName(name) + suffix));
            }

            string SanitizeFileName(string name)
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                var sanitized = new string(name
                    .Select(c => char.IsWhiteSpace(c) || invalidChars.Contains(c) ? '_' : c)
                    .ToArray());
                return Regex.Replace(sanitized.Trim('_'), "_{2,}", "_");
            }

            // ── Eradicate dupes w/ normalised path ────────────────────────────────────
            var seenPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dedupedQueue = new List<(string path, string name)>();

            foreach (var (path, name) in exportQueue)
            {
                var normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (seenPaths.ContainsKey(normalizedPath))
                {
                    Log($"{seenPaths[normalizedPath]} was selected twice, but will only be exported once!", LogLevel.Warning);
                }
                else
                {
                    seenPaths.Add(normalizedPath, name.Replace(suffix, ""));
                    dedupedQueue.Add((path, name));
                }
            }

            foreach (var (path, name) in dedupedQueue)
            {
                var exportedPath = await ExpImpDel.ExportMCPACK(path, name);
                if (exportedPath != null)
                {
                    exportedCount++;
                    Log($"Finished exporting {name} to {exportedPath}", LogLevel.Cache);
                }

                _ = BlinkingLamp(true, true, 1.0);
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), LogLevel.Warning);
        }
        finally
        {
            bool nothingSelected =
                !IsVanillaRTXEnabled &&
                !IsNormalsEnabled &&
                !IsOpusEnabled &&
                EnvironmentVariables.SelectedPacks.Count == 0;

            if (nothingSelected)
                Log("Select at least one pack to export.", LogLevel.Warning);
            else if (exportedCount == 0)
                Log("All exports failed.", LogLevel.Warning);

            _progressManager.HideProgress();
            LockControls(true, ToDisable);
        }
    }

    private CancellationTokenSource? _tuningCts;
    private async void TuneSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tuningCts != null)
        {
            TuneSelectionButton.IsEnabled = false;
            TuneSelectionButtonText.Text = "Stopping...";
            _tuningCts.Cancel();
            return;
        }

        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
        "LaunchAlchitexButton", "LaunchPackUpdateButton", "BrowsePacksButton",
        "ExportButton", "DeleteButton",
        "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox", "ClearButton", "ResetButton",
        "FogMultiplierSlider", "FogMultiplierBox",
        "EmissivityMultiplierSlider", "EmissivityMultiplierBox", "NormalIntensitySlider", "NormalIntensityBox",
        "MaterialNoiseSlider", "MaterialNoiseBox", "RoughenUpSlider", "RoughenUpBox",
        "LazifyNormalsSlider", "LazifyNormalsBox", "EmissivityAmbientLightToggle"
        ];

        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);

        string OriginalTooltip = (string)ToolTipService.GetToolTip(TuneSelectionButton);

        try
        {
            bool hasVanillaPacks = IsVanillaRTXEnabled || IsNormalsEnabled || IsOpusEnabled;
            bool hasCompatibleCustom = EnvironmentVariables.SelectedPacks.Any(p => p.Type != "Incompatible");
            bool hasIncompatibleCustom = EnvironmentVariables.SelectedPacks.Any(p => p.Type == "Incompatible");

            if ((hasVanillaPacks || hasCompatibleCustom) && hasIncompatibleCustom)
                Log("Some of the selected packs are not RTX compatible & will be excluded from the tuning process.", LogLevel.Warning);

            if (!hasVanillaPacks && !hasCompatibleCustom)
            {
                if (hasIncompatibleCustom)
                    Log("None of the selected packs are RTX or Vibrant Visuals compatible. Select at least one compatible pack to tune.", LogLevel.Warning);
                else
                    Log("Select at least one compatible pack to tune.", LogLevel.Warning);
                return;
            }

            _tuningCts = new CancellationTokenSource();
            var progress = new Progress<Tuner.TuningProgress>(p => _progressManager.ReportTuningProgress(p));

            _ = BlinkingLamp(true);
            LockControls(false, ToDisable);

            TuneSelectionButtonIcon.Glyph = "\uE733";
            TuneSelectionButtonText.Text = "Abort tuning operation";
            ToolTipService.SetToolTip(TuneSelectionButton,
                "Stops the tuning operation. Textures already finished keep their changes; anything not yet reached by the Tuner is left untouched.");

            var tuningMessage = await Task.Run(() => Tuner.TuneSelectedPacks(progress, _tuningCts.Token));
            Log(tuningMessage, LogLevel.Success);
            _progressManager.Complete();
        }
        catch (OperationCanceledException)
        {
            Log("Tuning was cancelled.", LogLevel.Warning);
            _progressManager.ReportCancelled();
        }
        catch (Exception ex)
        {
            Log($"Something went wrong during the tuning process: {ex}", LogLevel.Error);
            _progressManager.ReportError();
        }
        finally
        {
            _ = BlinkingLamp(false);
            LockControls(true, ToDisable);

            TuneSelectionButtonIcon.Glyph = "\uE9F5";
            TuneSelectionButtonText.Text = "Tune selection";
            ToolTipService.SetToolTip(TuneSelectionButton, OriginalTooltip);
            TuneSelectionButton.IsEnabled = true;

            _tuningCts?.Dispose();
            _tuningCts = null;
        }
    }




    private async void LaunchPackUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        string[] ToDisable =
        [
            "LaunchMinecraftButton", "TargetPreviewToggle",
    "LaunchAlchitexButton", "BrowsePacksButton",
    "TuneSelectionButton", "ExportButton", "DeleteButton", "LaunchPackUpdateButton",
             "VanillaRTXCheckBox", "NormalsCheckBox", "OpusCheckBox","ClearButton", "ResetButton"
        ];

        // The UI display text relies on this, rerun it just in case, few ms overhead worth it
        await LocatePacksTask();

        if (Helpers.IsMinecraftRunning() && RuntimeFlags.Set("Has_Told_User_To_Close_The_Game"))
        {
            Log($"Please close Minecraft while using the app. Once finished, launch the game using {LaunchButtonText.Text} button.", LogLevel.Warning);
        }

        OpenModule(() => new Modules.PackUpdater.PackUpdaterOverlay(this), ToDisable, _unused =>
        {
            // The cache glyph used to be re-derived by hand here, and at startup, and would have
            // owed a third copy at every future cache-touching site. It now follows
            // PackUpdater.DeployableCacheChanged, which already fired for whatever the updater
            // did while it was open - see OnDeployableCacheChanged.
            _ = LocatePacksTask(true); // Trigger an auto pack location check after, only time we log statuses for user to see what's installed
        });
    }
    private void LaunchShaderModManagerButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchShaderModManagerButton", "ResetButton"];

        OpenModule(() => new Modules.ShaderMods.ShaderModManagerOverlay(), ToDisable, overlay =>
        {
            LogModuleResult(overlay, LogLevel.ShaderMods);
            _ = BlinkingLamp(true, true, overlay.OperationSuccessful ? 1.0 : 0.0);
        });
    }
    private void LaunchDLSSSwapperButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchDLSSSwapperButton", "ResetButton"];

        OpenModule(() => new Modules.DLSS.DLSSSwapperOverlay(), ToDisable, overlay =>
        {
            LogModuleResult(overlay, LogLevel.DLSS);
            _ = BlinkingLamp(true, true, overlay.OperationSuccessful ? 1.0 : 0.0);
        });
    }
    private void LaunchLUTManagerButton_Click(object sender, RoutedEventArgs e)
    {
        string[] ToDisable = ["LaunchMinecraftButton", "TargetPreviewToggle", "LaunchLUTManagerButton", "ResetButton"];

        OpenModule(() => new Modules.LUT.LUTManagerOverlay(), ToDisable, overlay =>
        {
            LogModuleResult(overlay, LogLevel.LUT);
            _ = BlinkingLamp(true, true, overlay.OperationSuccessful ? 1.0 : 0.0);
        });
    }
    private void LaunchAlchitexButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(IsTargetingPreview)) return;

        if (SelectedPacks.Count == 0)
        {
            if (RuntimeFlags.Set("Has Already Said the thing about what RTX Reactor does to packs in the button click menu"))
            {
                Log($"RTX Reactor adds RTX support to regular texture packs, it works best on packs tagged as an `{PackBrowserOverlay.AlchitexCandidateTag}`.", LogLevel.Alchitex);
            }
#if DEBUG
            Log("No packs selected - opening RTX Reactor anyway (Debug build).", LogLevel.Alchitex);
#else
            Log($"You must select at least one texture pack to use RTX Reactor on.", LogLevel.Warning);
            return;
#endif
        }

        string[] ToDisable =
        [
         "LaunchMinecraftButton", "TargetPreviewToggle", "ClearButton", "LaunchPackUpdateButton",
        "BrowsePacksButton", "TuneSelectionButton", "ExportButton", "DeleteButton", "LaunchAlchitexButton"
        ];

        OpenModule(() => new Modules.Alchitex.Alchitex(), ToDisable, overlay =>
        {
            LogModuleResult(overlay, LogLevel.Alchitex);
            _ = BlinkingLamp(true, true, overlay.OperationSuccessful ? 1.0 : 0.0);
        });
    }


    private async void LaunchMinecraftButton_Click(object sender, RoutedEventArgs e) =>
        await LaunchMinecraftAsync(IsTargetingPreview);

    /// <summary>
    /// Everything the Launch button does, for either edition. The button passes the one the app
    /// is pointed at; a <c>vanillartx://launchminecraftrtxpreview</c> link passes Preview whatever
    /// the toggle says, which is why the edition is a parameter here rather than read inside.
    /// Returns whether the game was actually launched.
    /// </summary>
    internal async Task<bool> LaunchMinecraftAsync(bool isPreview)
    {
        if (!MinecraftUserDataLocator.RequireValidUserData(isPreview)) return false;

        if (Helpers.IsMinecraftRunning())
        {
            Log("Minecraft already seems to be open. Please restart the game for options.txt changes to take effect.", LogLevel.Warning);
        }

        try
        {
            // Which options.txt parameters this writes is the settings panel's answer, not
            // this button's - see MinecraftLauncher.LaunchConfiguredMinecraftRTXAsync.
            var outcome = await MinecraftLauncher.LaunchConfiguredMinecraftRTXAsync(isPreview);

            Log(outcome.Log, (isPreview ? LogLevel.MCPreview : LogLevel.MCRelease));
            return outcome.Launched;
        }
        finally
        {
            _ = BlinkingLamp(true, true, 0.0);
        }
    }

    #region =============== SLIDER HANDLERS ===============

    private static void HandleDoubleSliderValueChanged(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
    {
        double roundedValue = Math.Round(slider.Value, decimalPlaces);
        property = roundedValue;
        slider.Value = roundedValue;

        string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
        if (textBox != null && textBox.FocusState == FocusState.Unfocused)
        {
            // Use CurrentCulture so weirdos see "1,50" not "1.50"
            textBox.Text = roundedValue.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    private static void HandleDoubleTextBoxLostFocus(Slider slider, TextBox textBox, ref double property, int decimalPlaces)
    {
        // Try parsing with user's culture first (respects comma vs period)
        bool parsed = double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                       CultureInfo.CurrentCulture, out double val);

        // Fallback to invariant culture if that fails (for copy-paste scenarios)
        if (!parsed)
        {
            parsed = double.TryParse(textBox.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                    CultureInfo.InvariantCulture, out val);
        }

        if (parsed)
        {
            val = Math.Clamp(val, slider.Minimum, slider.Maximum);
            double roundedVal = Math.Round(val, decimalPlaces);
            property = roundedVal;
            slider.Value = roundedVal;

            string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
            // Display with user's culture
            textBox.Text = roundedVal.ToString(format, CultureInfo.CurrentCulture);
        }
        else
        {
            // Restore the last valid value with user's culture
            string format = decimalPlaces == 1 ? "F1" : $"F{decimalPlaces}";
            textBox.Text = property.ToString(format, CultureInfo.CurrentCulture);
        }
    }


    private static void HandleIntSliderValueChanged(Slider slider, TextBox textBox, ref int property)
    {
        property = (int)Math.Round(slider.Value);
        if (textBox != null && textBox.FocusState == FocusState.Unfocused)
            textBox.Text = property.ToString(CultureInfo.InvariantCulture);
    }

    private static void HandleIntTextBoxLostFocus(Slider slider, TextBox textBox, ref int property)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            val = Math.Clamp(val, (int)slider.Minimum, (int)slider.Maximum);
            property = val;
            slider.Value = val;
            textBox.Text = val.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            textBox.Text = property.ToString(CultureInfo.InvariantCulture);
        }
    }

    // =============== SLIDER EVENT HANDLERS ===============
    private void FogMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleDoubleSliderValueChanged(FogMultiplierSlider, FogMultiplierBox, ref FogMultiplier, 2);

    private void FogMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
        => HandleDoubleTextBoxLostFocus(FogMultiplierSlider, FogMultiplierBox, ref FogMultiplier, 2);


    private void EmissivityMultiplierSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleDoubleSliderValueChanged(EmissivityMultiplierSlider, EmissivityMultiplierBox, ref EmissivityMultiplier, 1);

    private void EmissivityMultiplierBox_LostFocus(object sender, RoutedEventArgs e)
        => HandleDoubleTextBoxLostFocus(EmissivityMultiplierSlider, EmissivityMultiplierBox, ref EmissivityMultiplier, 1);


    private void NormalIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(NormalIntensitySlider, NormalIntensityBox, ref NormalIntensity);

    private void NormalIntensity_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(NormalIntensitySlider, NormalIntensityBox, ref NormalIntensity);


    private void MaterialNoise_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(MaterialNoiseSlider, MaterialNoiseBox, ref MaterialNoiseOffset);

    private void MaterialNoise_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(MaterialNoiseSlider, MaterialNoiseBox, ref MaterialNoiseOffset);


    private void RoughenUp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(RoughenUpSlider, RoughenUpBox, ref RoughnessControlValue);

    private void RoughenUp_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(RoughenUpSlider, RoughenUpBox, ref RoughnessControlValue);


    private void LazifyNormals_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        => HandleIntSliderValueChanged(LazifyNormalsSlider, LazifyNormalsBox, ref LazifyNormalAlpha);

    private void LazifyNormals_LostFocus(object sender, RoutedEventArgs e)
        => HandleIntTextBoxLostFocus(LazifyNormalsSlider, LazifyNormalsBox, ref LazifyNormalAlpha);


    private void EmissivityAmbientLightToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = sender as ToggleSwitch;
        if (toggle == null) { return; }
        AddEmissivityAmbientLight = toggle.IsOn;

        // Show/hide the warning icon
        EmissivityWarningIcon.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }
    #endregion


    #region =============== UI LOGGER ===============

    // add more types, specifically, let feature windows use their own unique emojis!
    public enum LogLevel
    {
        Success, Informational, Warning, Error, Network, Lengthy, Misc, Teletext, Alchitex, Cache, Package,
        DLSS, ShaderMods, LUT, VanillaRTX, Selected, MCPreview, MCRelease, Cleaning, Reset, Import
    }

    // The single source of truth, Log() only ever writes here
    internal static string LogText = "";
    internal static readonly Lock _logGate = new();

    // Typewriter state, only ever touched on the UI thread, inside TypewriterTick()
    // Logger writes fast; typewriter reveals it to the UI on its own schedule – always the
    // oldest not-yet-shown entry first, left-to-right within it – so chronology holds up
    // AND each message types start-to-finish instead of finish-to-start.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _typewriterTimer;
    private ScrollViewer? _logScrollViewer;
    private int _settledLength = 0;   // trailing chars of LogText already fully shown & final
    private int _activeRevealed = 0;  // chars revealed so far of the current (oldest-pending) entry
    private string? _lastRenderedText;
    private static string? _lastSeenLogText;

    internal const int MaxLogChars = 4000;

    private const double BaselineCharsPerTick = 2.0; // relaxed pace for small/no backlog
    private const double CatchUpFraction = 0.10;      // reveal % of the backlog each tick
    private static readonly int TickIntervalMs = ((Func<int>)(() => // speed based on corecount, since this really does affect cpu usage! it's the main lever
    {
        try
        {
            if (Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On)
                return 64;

            return Environment.ProcessorCount switch
            {
                >= 24 => 4,
                >= 16 => 8,
                >= 8 => 16,
                >= 5 => 32,
                _ => 64,
            };
        }
        catch { return 16; }
    }))();


    // Structural marker ONLY – never rendered, never typed character-by-character, never
    internal const string EntrySentinel = "\uE000\uE001";

    // Idle/typing cursor – sits at the current write-head
    private const bool ShowTypingCursor = true;
    private const int CursorBlinkMs = 750;
    private const string CursorOnGlyph = " |";
    private const string CursorOffGlyph = "  ";

    public static void Log(string message, LogLevel? level = null)
    {
        string prefix = level switch
        {
            LogLevel.Success => "✅ ",
            LogLevel.Informational => "ℹ️ ",
            LogLevel.Warning => "⚠️ ",
            LogLevel.Error => "❌ ",
            LogLevel.Selected => "📍 ",
            LogLevel.MCPreview => "🚧 ",
            LogLevel.MCRelease => "🟩 ",
            LogLevel.Cleaning => "🧹 ",
            LogLevel.Reset => "🔄️ ",
            LogLevel.Lengthy => "⏳ ",
            LogLevel.Teletext => "📢 ",
            LogLevel.Network => "🛜 ",
            LogLevel.Misc => "🛸 ",
            LogLevel.Alchitex => "🟦 ",
            LogLevel.Cache => "💾 ",
            LogLevel.DLSS => "🫧 ",
            LogLevel.ShaderMods => "🧈 ",
            LogLevel.LUT => "🎨 ",
            LogLevel.VanillaRTX => "⛏️ ",
            LogLevel.Package => "📦 ",
            LogLevel.Import => "📥 ",
            null => "",
            _ => "💩 "
        };

        string entry = $"{prefix}{message}";

        lock (_logGate)
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                int firstSentinel = LogText.IndexOf(EntrySentinel, StringComparison.Ordinal);
                string lastEntry = firstSentinel >= 0 ? LogText[..firstSentinel] : LogText;

                if (lastEntry == entry) // identical to previous entry? drop it
                    return;
            }

            LogText = string.IsNullOrEmpty(LogText) ? entry : $"{entry}{EntrySentinel}{LogText}";
        }
    }

    private void InitializeLogTypewriter()
    {
        SidebarLog.Loaded += (_, _) => _logScrollViewer ??= GetScrollViewer(SidebarLog);
        if (SidebarLog.IsLoaded) _logScrollViewer ??= GetScrollViewer(SidebarLog);

        _typewriterTimer = DispatcherQueue.CreateTimer();
        _typewriterTimer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _typewriterTimer.Tick += (_, _) => TypewriterTick();
        _typewriterTimer.Start();
    }

    private void TypewriterTick()
    {
        if (SuspendUIAnimations && ReferenceEquals(LogText, _lastSeenLogText))
            return;

        string current;
        lock (_logGate)
        {
            current = LogText;

            if (current.Length > MaxLogChars)
            {
                // Cut on a sentinel boundary so we drop whole oldest entries, never mid-message.
                int cut = current.LastIndexOf(EntrySentinel, MaxLogChars - 1, MaxLogChars, StringComparison.Ordinal);
                if (cut > 0)
                {
                    int trimmedAmount = current.Length - cut;
                    current = current[..cut];
                    LogText = current;

                    // Trimmed content came off the tail – exactly where _settledLength measures
                    // from – so shrink it by the same amount. If the cut reached into content that
                    // wasn't fully settled yet (only possible under an extreme backlog like a stress
                    // test), just reset both – the next tick starts clean against the trimmed text.
                    if (trimmedAmount > _settledLength)
                    {
                        _settledLength = 0;
                        _activeRevealed = 0;
                    }
                    else
                    {
                        _settledLength -= trimmedAmount;
                    }
                }
            }
        }

        _lastSeenLogText = current;

        // Short circuit the animation
        if (SuspendUIAnimations)
        {
            _settledLength = current.Length; // Mark everything as already "typed"
            _activeRevealed = 0;
            RenderFrame(current, 0, 0);      // Render instantly
            return;
        }

        int unshownLength = current.Length - _settledLength;

        if (unshownLength > 0)
        {
            // The oldest not-yet-shown entry sits adjacent to the settled region. Its own
            // trailing sentinel (connecting it to whatever follows) isn't a real boundary
            // between two DIFFERENT pending entries, so exclude it before searching.
            int trailingConnector = _settledLength > 0 ? EntrySentinel.Length : 0;
            int searchLength = Math.Max(0, unshownLength - trailingConnector);

            int sepIndex = searchLength > 0
                ? current.LastIndexOf(EntrySentinel, searchLength - 1, searchLength, StringComparison.Ordinal)
                : -1;

            int activeStart = sepIndex >= 0 ? sepIndex + EntrySentinel.Length : 0;
            int activeTextLength = searchLength - activeStart; // entry's OWN text only, sentinel excluded

            int remaining = unshownLength - _activeRevealed; // whole backlog left – drives speed-up
            int charsThisTick = (int)Math.Max(BaselineCharsPerTick, Math.Ceiling(remaining * CatchUpFraction));

            _activeRevealed = Math.Min(activeTextLength, _activeRevealed + charsThisTick);
            _activeRevealed = SnapForward(current, activeStart, _activeRevealed);

            if (_activeRevealed >= activeTextLength)
            {
                // Entry fully typed – fold it (and its sentinel, converted to a real blank
                // line) into settled INSTANTLY. The separator is never itself "typed."
                _settledLength = current.Length - activeStart;
                _activeRevealed = 0;

                SidebarLog.UpdateLayout();
                _logScrollViewer?.ChangeView(null, 0, null, true); // once, per finished entry
            }
            else
            {
                RenderFrame(current, activeStart, _activeRevealed);
                return;
            }
        }

        RenderFrame(current, 0, 0);
    }

    private void RenderFrame(string current, int activeStart, int activeRevealed)
    {
        string revealedPrefix = activeRevealed > 0 ? current.Substring(activeStart, activeRevealed) : "";
        string settledDisplay = _settledLength > 0
            ? current.Substring(current.Length - _settledLength).Replace(EntrySentinel, "\n\n")
            : "";

        string headText, tailText;
        if (revealedPrefix.Length > 0)
        {
            headText = revealedPrefix;
            tailText = settledDisplay.Length > 0 ? "\n\n" + settledDisplay : "";
        }
        else
        {
            int firstBoundary = settledDisplay.IndexOf("\n\n", StringComparison.Ordinal);
            headText = firstBoundary >= 0 ? settledDisplay[..firstBoundary] : settledDisplay;
            tailText = firstBoundary >= 0 ? settledDisplay[firstBoundary..] : "";
        }

        string cursor = (ShowTypingCursor && !SuspendUIAnimations)
            ? ((Environment.TickCount64 / CursorBlinkMs) % 2 == 0 ? CursorOnGlyph : CursorOffGlyph)
            : "";

        string newText = headText + cursor + tailText;
        if (newText == _lastRenderedText) return; // nothing visually changed, skip the relayout entirely

        _lastRenderedText = newText;
        SidebarLog.Text = newText;
    }

    // Never reveal a cut that splits a surrogate pair or strands an emoji's
    // variation-selector/combining mark – grows past them instead of stopping mid-glyph.
    private static int SnapForward(string s, int rangeStart, int localIndex)
    {
        int i = rangeStart + localIndex;
        if (i <= rangeStart || i >= s.Length) return localIndex;

        if (char.IsHighSurrogate(s[i - 1]) && char.IsLowSurrogate(s[i]))
            i++;

        while (i < s.Length && IsJoiningMark(s[i]))
            i++;

        return i - rangeStart;
    }
    private static bool IsJoiningMark(char c) =>
        c is '\uFE0F' or '\uFE0E' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    public static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
    #endregion UI Logger
}

/* ### BACKLOG/TODO OF HIGHCORTISOLSOFTWARE LLC (STRICTLY CONFIDENTIAL)

- Figure a way to expose an option to auto-activate a Vanilla RTX pack.
Maybe this:
ticking its checkbox before launching the game also edits the thing that holds which packs are active.
this may not be a great idea, but it is doable, and mostly stems from a curiosity to understand what file in user data saves this stuff.

- need revamped store images like the 4.0 trailer thumbnail concept

- Need to make review prompt appear floaty in the app instead of launching the store
Some apps are doing it how?

- Make background of settings menu fade in faster as to make it clickable earlier? i.e. rapidly opening and closing by clicking the background
it feels like it is a little irresponsive if you mash it so, look into it, make it feel better.

- Adding formatting clearer before desc appending in pack desc Alchitex, log those other ideas from the piece of paper later

- remove shadow from lamp on splash screen?! or weaken it so it is more like titlebar lamp.
the thing is the shadow does make sense it is just too strong. It's the lamp casting light on the background, not a Halo as the name suggests

- Add shadows to crash report page's buttons, and make the margins make more sense, it looks ugly.

- Look into
https://github.com/aufkrawall/dlssdl
is there a way to further streamline DLSS Swapper?!

- The launch options setting text boxes don't have shadow like the others

- Make the reset buttons next to several of the text boxes height match the textbox height?
i.e. auto it, let the refresh stretch vertically

- Need better art to convey surface 3d effect intensity

>> Add a BetterRTX-like lut preset, can get the looks 80% there! call it a joke name like ButterRTX -- or have ButterRTX turn the world yellow for fun... so two presets out of this idea.
its just an edit of Gamescom iirc that looked a lot like BetterRTX 1.4.4 default
weaker sun/skylight was a part of it that made the sun texture look a lot more similar
diminished highlights around the sun sprite, etc...

- Changelogs markdown overlay in PackUpdater overlay
a Markdown page similar to the others that contains a table and changelogs of every single Vanilla RTX version, especially post 1.6+ modern era
push refresh button further to the front.
It'd be nice to keep a Concentrated place with a table of updates out for everyone... a clear place to follow updates
May it also serve as a reminder of your 5 years of effort, it is easy for it to be lost and forgotten, those updates didn't get made overnight, to be forgotten overnight.

Once that is implemented, and changelogs no longer clog up a huge vertical space,
- Future plans for PackUpdat menu:
Find a way to scale.
Allow for More packs to appear after extensions/addons section, but in the same style, and derived from a
an listing somewhere.
The idea is, allow more creators to have their packs listed there, but only review and allow genuine content that have
at least a good bit of effort put into them and are actively maintained. A markdown renderer is probably the way to go.

A system for automatically updating ANY pack doesn't seem feasible, there is no repository to take this from
Look into CurseForge api?! will it give you the packs? look into that ticket you sent their support long ago
they said their API allows but, was it loose or did it need auth?

- restructure main window to be less sloppy and more correct whilst looking the same by the end.
the layout works perfectly but is wrong in many spots, with many dumb margins

- Turn the textbox of sidebarlog into a rich textbox, and add the ability to show clickable links
useful down the line, customize its visuals, etc... to make it look like before with layering tricks

- Idea: instead of showing modules as overlays
Hide mainwindow contents and display those instead.
This might allow things to be simplified:
Only .MD and Imports overlays are actually overlays
The rest is ALL painted in MainWindow
it is basically swapping out what is shown in the main window grid...
So much can be simplified, think about it
And it'll allow module overlay backgrounds to be the same acrylic material as main window instead of appearing more of a solid color
because acrylic doesn't render twice through acrylic, can't layer acrylic on acrylic

It may solve some issues and introduce others
like, having to make sure mainwindow didn't keyboard input was a fault of the current design that was "worked around" via MainWindow.Overlayfocus.cs

BUT, this may not be such a great idea after all
The MAIN, BIG benefit of the current design is, you fade back into main window very simply, because mainwindow is already rendered in the background
The complications executing this idea could introduce may not be worth the benefits, after all. the current system WORKS
Don't touch it. unless a greater redesign is planned... (next)

- How would a greater redesign of Mainwindow even work? (for 5.0?)
Where would tuning go. what'd happen to logs
a redesign must be thought thoroughly
maybe navigation view? like every other winui 3.0 app on the planet, or something more clever that keeps the app's visual design
while offloading Tuning elsewhere...
Tuner is no longer "the main dish" its a module like the others but the main window is still designed around it..
the thing is it is such a core part of the app too. figure this out later.

an idea for mainwindow redesign:
moving tuner into a button like Alchitex, with its own unique design themed around Gold and Tuner's lamp.
make 3x2 button grid large all the way to the top, make the shape assymetrical, give them special logos and colors maybe
instead of glyphs, each button is a block to the top, each with their own weird shape, fit 7 in there
we'll see.
prolly not a good idea
tuner is the most... repeatedly-used module, so, having it in main window makes sense
having to continously click in and out to tune packs is annoying
It makes sense both ways
Moving the "Export" and "Delete" buttons to select other packs menu like that one 3.2 concept image also works well

- Expose ALLLLLLLL of app's internal variables and constants, let them be adjustable
in a "see more" section of the settings, of course, hidden away from the casual user.
All of alchitex, tuner, and more, all variables that don't change during runtime, constants.
Especially focus on the useful ones
More urls
more magic numbers

- Should settings overlay save on EVERY hide or... instead of saving everything, save only changed values?!
the latter introduces more risk if not done correctly, but is it worth it? assess.

- i18n, once all english texts are definitely settled and you're not making any more changes, right?!

 */
