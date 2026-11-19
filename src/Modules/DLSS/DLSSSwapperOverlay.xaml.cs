using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.DLSS;

/// <summary>
/// The DLSS swapper's window: the cached version list, the builds NVIDIA's update server offers
/// beyond it, drag/drop and the file pickers. Everything it does to actual files - where the
/// cache is, what's in it, how a .dll or .zip gets into it, the elevated swap, and the download
/// from NVIDIA - lives in <see cref="DLSSSwapper"/>. This holds one and renders it.
///
/// <para>The NVIDIA list only ever adds rows: a server that stops answering leaves exactly the
/// swapper that existed without it.</para>
/// </summary>
public sealed partial class DLSSSwapperOverlay : ModuleOverlay
{
    private bool _isClosing;

    private readonly DLSSSwapper _swapper = new();
    private CancellationTokenSource? _scanCancellationTokenSource;

    /// <summary>
    /// True from the moment a swap is started until it has finished and the list has been
    /// redrawn. Read and written only on the UI thread, and always cleared in a finally -
    /// see <see cref="DllButton_Click"/>.
    /// </summary>
    private bool _swapInProgress;

    /// <summary>Cancelled when the module closes: the NVIDIA listing and any download from it end with it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The last listing from NVIDIA's server: null until it answers, or when it couldn't be reached.</summary>
    private IReadOnlyList<NvidiaDll>? _nvidiaBuilds;
    private bool _nvidiaListed;
    private bool _nvidiaDownloadInProgress;

    /// <summary>Everything done while open, so the report on close covers swaps and downloads alike.</summary>
    private readonly List<string> _done = new();


    public DLSSSwapperOverlay()
    {
        this.InitializeComponent();
        PrepareContent();

        ShowBrowseTarget();

        this.Loaded += DLSSSwapperOverlay_Loaded;
    }
    private async void DLSSSwapperOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= DLSSSwapperOverlay_Loaded;

            if (_isClosing) return;

            await InitializeAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSSSwapper] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    protected override void OnClosing()
    {
        if (_isClosing) return;
        _isClosing = true;

        this.Loaded -= DLSSSwapperOverlay_Loaded;

        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource?.Dispose();
        _lifetime.Cancel();

        WebImportOverlay.CloseIfOpen();
    }

    private void Report(string done)
    {
        _done.Add(done);
        OperationSuccessful = true;
        var said = string.Join(", ", _done);
        StatusMessage = char.ToUpperInvariant(said[0]) + said[1..];
    }

    // ======================= Initialization =======================
    private async Task InitializeAsync()
    {
        try
        {
            var isPreview = Persistent.IsTargetingPreview;
            var cachedPath = isPreview
                ? Persistent.MinecraftPreviewInstallPath
                : Persistent.MinecraftInstallPath;

            string? minecraftPath = null;

            if (MinecraftGDKLocator.RevalidateCachedPath(cachedPath, Persistent.IsTargetingPreview))
            {
                Trace.WriteLine($"[DLSS] ✓ Using cached path: {cachedPath}");
                minecraftPath = cachedPath;
            }
            else
            {
                if (!string.IsNullOrEmpty(cachedPath))
                {
                    Trace.WriteLine($"[DLSS] ⚠ Cache became invalid, clearing");
                    if (isPreview)
                        Persistent.MinecraftPreviewInstallPath = null;
                    else
                        Persistent.MinecraftInstallPath = null;
                }

                _ = this.DispatcherQueue.TryEnqueue(() =>
                {
                    ManualSelectionPanel.Visibility = Visibility.Visible;
                });

                Trace.WriteLine("[DLSS] Starting system-wide search...");
                _scanCancellationTokenSource = new CancellationTokenSource();

                minecraftPath = await MinecraftGDKLocator.SearchForMinecraftAsync(
                    isPreview,
                    _scanCancellationTokenSource.Token
                );

                if (minecraftPath == null)
                {
                    Trace.WriteLine("[DLSS] System search cancelled or failed - waiting for manual selection");
                    return;
                }
            }

            if (minecraftPath != null)
                await ContinueInitializationWithPath(minecraftPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] EXCEPTION in InitializeAsync: {ex}");
            StatusMessage = $"Initialization error: {ex.Message}";
            this.Close();
        }
    }

    private async Task ContinueInitializationWithPath(string minecraftPath)
    {
        if (!_swapper.TryAttach(minecraftPath))
        {
            StatusMessage = "Could not establish cache folder";
            this.Close();
            return;
        }

        if (!_swapper.GameDllExists)
        {
            Trace.WriteLine($"[DLSS] ⚠ DLSS file not found at: {_swapper.GameDllPath}");

            var cachedDlls = _swapper.CachedDllPathsByNewest();

            if (cachedDlls.Count > 0)
            {
                var repairDll = cachedDlls[0];
                Trace.WriteLine($"[DLSS] 🔧 Attempting to repair with: {repairDll}");

                var repairSuccess = await _swapper.InstallAsync(repairDll);

                if (repairSuccess)
                {
                    Trace.WriteLine("[DLSS] ✓ Game repaired successfully");
                    await _swapper.CacheInstalledDllAsync();
                }
                else
                {
                    Trace.WriteLine("[DLSS] ⚠ User cancelled UAC or repair failed - continuing anyway");
                }
            }
            else
            {
                Trace.WriteLine("[DLSS] ⚠ No cached DLLs available - user must import one");
            }
        }
        else
        {
            await _swapper.CacheInstalledDllAsync();
        }

        await LoadDllsAsync();

        LoadingPanel.Visibility = Visibility.Collapsed;
        DllSelectionPanel.Visibility = Visibility.Visible;
        TeletextCard.Populate(DLSSInfoPanel, TeletextContent.DLSSInfo);
        DLSSInfoPanel.Visibility = DLSSInfoPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _ = LoadNvidiaBuildsAsync();
    }

    private async void ManualSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        Trace.WriteLine("[DLSS] Manual selection button clicked - cancelling system search");

        _scanCancellationTokenSource?.Cancel();

        var hWnd = WindowHandle;
        var isPreview = EnvironmentVariables.Persistent.IsTargetingPreview;
        var pick = await MinecraftGDKLocator.LocateMinecraftManuallyAsync(isPreview, hWnd);

        if (pick.Path is { } path)
        {
            Trace.WriteLine($"[DLSS] ✓ User selected valid path: {path}");
            await ContinueInitializationWithPath(path);
        }
        else
        {
            Trace.WriteLine("[DLSS] ✗ User cancelled or selected invalid path");
            StatusMessage = MinecraftGDKLocator.DescribeUnsetPick(pick, isPreview);
            this.Close();
        }
    }

    /// <param name="IsCalledByAddDLSSVersion">
    /// Skips the cleanup sweep. A version the user has just imported gets to show up in the
    /// list as unusable rather than silently vanishing between the picker closing and the
    /// list redrawing.
    /// </param>
    private async Task LoadDllsAsync(bool IsCalledByAddDLSSVersion = false)
    {
        try
        {
            if (!IsCalledByAddDLSSVersion)
            {
                await _swapper.RemoveUnsupportedFromCacheAsync();
            }

            DllListContainer.Children.Clear();

            var dlls = _swapper.ListCachedVersions();

            if (dlls.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = "No DLSS versions yet. Get one from NVIDIA below, or download nvngx_dlss.dll files and import them here.";
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                foreach (var dll in dlls)
                    DllListContainer.Children.Add(CreateDllButton(dll));
            }

            Trace.WriteLine("[DLSS] DLL loading complete");
            ShowNvidiaBuilds();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] EXCEPTION in LoadDllsAsync: {ex}");
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error loading DLLs: {ex.Message}";
        }
    }

    private FrameworkElement CreateDllButton(DllData dll)
    {
        bool isCurrentVersion = dll.Version == _swapper.InstalledVersion;
        bool isTooOld = !DLSSSwapper.IsSupportedVersion(dll.Version);

        // Decided up front so the button's own corner radius, margin and shadow can be set
        // correctly the first time - see the split-button wrapping at the bottom of this method.
        bool showDeleteButton = !isCurrentVersion;

        var button = new Button
        {
            IsEnabled = !isTooOld,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 40, 0),
            Margin = showDeleteButton ? new Thickness(0) : new Thickness(0, 0, 0, 4),
            MinHeight = 96,
            // Squared off on the right where the delete button will sit flush against it -
            // squared off below where the delete button sits flush against it.
            CornerRadius = showDeleteButton ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(5),
            Tag = dll,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };

        if (isCurrentVersion)
        {
            button.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        }

        // A delete-button row gets one shared shadow cast from a backing element behind the
        // whole composite (see the bottom of this method) rather than two overlapping drop
        // shadows for what reads as a single row.
        if (!showDeleteButton)
        {
            var buttonShadow = new ThemeShadow();
            button.Shadow = buttonShadow;
            button.Loaded += (s, e) =>
            {
                if (ShadowReceiverGrid != null)
                    buttonShadow.Receivers.Add(ShadowReceiverGrid);
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var icon = new FontIcon
        {
            Glyph = "\uF156",
            FontSize = 44,
            FontWeight = FontWeights.ExtraLight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };

        iconBorder.Child = icon;
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var displayVersion = dll.DisplayVersion;
        if (isCurrentVersion)
            displayVersion += " (Currently Installed)";

        var versionText = new TextBlock
        {
            Text = $"DLSS {displayVersion}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        var pathText = new TextBlock
        {
            Text = isCurrentVersion
                ? Helpers.SanitizePathForDisplay(dll.FilePath)
                : isTooOld
                    ? "Incompatible DLSS version. Only import 2.0.0.0 or higher!\nThis version will be removed automatically."
                    : "Click to swap to this version",
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        };

        infoPanel.Children.Add(versionText);
        infoPanel.Children.Add(pathText);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        button.Content = grid;
        button.Click += DllButton_Click;

        // Delete button: every version except the one currently installed. Built as a sibling
        // "fake split button" next to the main button rather than floating on top of it - see
        // CreateSplitSeamPiece below.
        if (!showDeleteButton)
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
            Tag = dll,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 20, Margin = new Thickness(-5, 0, 0, 0), IsTextScaleFactorEnabled = false }
        };
        deleteButton.Click += DeleteDllButton_Click;

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
    /// One 3px half of the "fake split button" seam between the version button and its delete
    /// button - the same hand-written pattern this module's own "Create your own preset"-style
    /// button pairs use directly in XAML via <c>{ThemeResource FakeSplitButtonDarkBorderColor}</c>.
    /// That binding auto-updates on theme change for free; built from code (these rows are
    /// assembled at runtime, one per DLL) it needs the same live-theme handling
    /// <c>ApplyCloseButtonBevel</c>-style code elsewhere in this app already does: resolve once,
    /// then follow <see cref="ThemeService.ThemeChanged"/> and drop the subscription on Unloaded.
    /// <para>
    /// Pass <c>dark: true</c> for the piece added to the LEFT (version) button's own Grid.Column -
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

    private async void DeleteDllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DllData dllData)
        {
            if (_swapper.DeleteCachedVersion(dllData))
                await LoadDllsAsync();
        }
    }

    private void AddDllButton_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 0.7;
    }

    private void AddDllButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 1.0;
    }

    private void AddDllButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to add DLSS files";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void AddDllButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            button.Opacity = 1.0;

        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();

                if (paths.Count > 0)
                    await ImportDllFilesAsync(paths);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error processing dropped files: {ex.Message}");
        }
    }

    private async void AddDllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".dll");
            picker.FileTypeFilter.Add(".zip");

            var hWnd = WindowHandle;
            InitializeWithWindow.Initialize(picker, hWnd);

            var files = await picker.PickMultipleFilesAsync();

            if (files != null && files.Count > 0)
                await ImportDllFilesAsync(files.Select(f => f.Path));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error adding file: {ex.Message}");
        }
    }

    /// <summary>
    /// Names the address the Download button opens, in its own label and its tooltip. Read
    /// once at construction from the same <see cref="Links"/> accessor the click uses, so the
    /// two cannot say different things - which is exactly what they did while the label was a
    /// literal in XAML and the address was a setting.
    /// </summary>
    private void ShowBrowseTarget()
    {
        DownloadDllsTargetText.Text = $"Visit {EnvironmentVariables.LinkLabel(Links.DlssProvider)}";
        ToolTipService.SetToolTip(DownloadDllsButton,
            $"Browse {EnvironmentVariables.LinkLabel(Links.DlssProvider, includePath: true)} right here - " +
            "anything you download will be imported automatically when you close it.");
    }

    // ======================= Browse for DLSS files (WebImportOverlay) =======================

    /// <summary>
    /// TechPowerUp has no API worth scraping and no source is as consistently up to date -
    /// so instead of leaving the user to a real browser and a manual re-import, they browse it
    /// right here. Whatever they download that looks like a DLSS runtime gets imported
    /// automatically once they close the overlay - see <see cref="WebImportOverlay"/> for the
    /// mechanism, which knows nothing about DLSS specifically.
    ///
    /// <para>Which page that is comes from <see cref="Core.EnvironmentVariables.Links"/> rather
    /// than being written here: the settings panel lets it be pointed somewhere else, and that
    /// class is what falls back to the built-in address when the stored one stops being
    /// usable.</para>
    /// </summary>
    private void DownloadDllsButton_Click(object sender, RoutedEventArgs e)
    {
        WebImportOverlay.Show(
            url: Links.DlssProvider,
            title: "Download DLSS files",
            glyph: "",
            guideText: "Once your desired DLSS .dll files have finished downloading, click 'Done' and they'll automatically import.",
            stagingTag: "DLSS",
            watchedExtensions: new[] { ".dll", ".zip" },
            onFilesReady: ImportDllFilesAsync);
    }

    /// <summary>
    /// The one place any DLSS file - manually browsed for, dragged in, or downloaded through
    /// <see cref="WebImportOverlay"/> - actually gets imported. Filtering, the import loop, the
    /// list refresh and the transient titlebar result message all live here exactly once, so
    /// every entry point reports the same way rather than only the newest one remembering to.
    /// </summary>
    private async Task ImportDllFilesAsync(IEnumerable<string> filePaths)
    {
        var candidates = filePaths
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            Trace.WriteLine("[DLSS] No supported DLSS files (.dll/.zip) in selection");
            return;
        }

        foreach (var path in candidates)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await _swapper.ImportZipAsync(path);
            else
                await _swapper.ImportDllAsync(path);
        }

        await LoadDllsAsync(true);
    }

    private async void DllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DllData dllData)
            return;

        if (dllData.Version == _swapper.InstalledVersion)
            return;

        // A swap is an elevated file copy: it writes a batch script, raises a UAC prompt and
        // waits. The UI thread is free for most of that, so without this a second click
        // starts a second script and the user gets a queue of prompts (see
        // Helpers.ReplaceFilesWithElevation, which refuses that outright as a backstop).
        // Ignoring the click rather than disabling the button is deliberate: there is no
        // state here that can be left stuck.
        if (_swapInProgress)
        {
            Trace.WriteLine("[DLSS] A swap is already in progress - ignoring this click");
            return;
        }

        // Nothing between setting the flag and entering the try, so there is no statement that
        // could throw its way past the finally and leave the window permanently refusing swaps.
        _swapInProgress = true;
        try
        {
            SetVersionListBusy(true);

            var success = await _swapper.InstallAsync(dllData.FilePath);

            // The elevated replace has been through either way by here - see the same call in
            // the shader mod and LUT installs.
            _ = Host.BlinkingLamp(true, true, success ? 1.0 : 0.0, 1.0);

            if (success)
            {
                Report($"Swapped to DLSS {dllData.DisplayVersion}");

                await _swapper.CacheInstalledDllAsync();
                await LoadDllsAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] Error replacing DLL: {ex.Message}");
        }
        finally
        {
            SetVersionListBusy(false);
            _swapInProgress = false;
        }
    }

    /// <summary>
    /// Greys the version list and stops it taking clicks while a swap runs - delete buttons
    /// included, since they live inside it and deleting the file being copied from is the one
    /// way to break a running swap.
    ///
    /// <para>Both properties are set on <c>DllListContainer</c> itself rather than on the
    /// buttons, which the list rebuild replaces wholesale. The container is declared in XAML
    /// and outlives every rebuild, so the restoring call in the finally always lands on the
    /// same element the disabling call touched. <c>Panel</c> has no <c>IsEnabled</c> - that
    /// lives on <c>Control</c> - so this is the UIElement-level equivalent.</para>
    /// </summary>
    private void SetVersionListBusy(bool busy)
    {
        DllListContainer.IsHitTestVisible = !busy;
        DllListContainer.Opacity = busy ? 0.5 : 1.0;
    }

    // ======================= Builds from NVIDIA =======================

    private async Task LoadNvidiaBuildsAsync()
    {
        NvidiaSection.Visibility = Visibility.Visible;
        try
        {
            _nvidiaBuilds = await DLSSSwapper.ListNvidiaVersionsAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] [NVIDIA] Listing failed: {ex.Message}");
        }

        if (_isClosing) return;
        _nvidiaListed = true;
        ShowNvidiaBuilds();
    }

    /// <summary>
    /// Redraws the NVIDIA rows from the last listing against what the cache holds now - so a
    /// version imported, downloaded or deleted any other way appears or disappears here too.
    /// No network: the listing is only asked for once per open.
    /// </summary>
    private void ShowNvidiaBuilds()
    {
        if (!_nvidiaListed || _isClosing) return;

        NvidiaListContainer.Children.Clear();
        NvidiaLoading.IsActive = false;
        NvidiaLoading.Visibility = Visibility.Collapsed;

        if (_nvidiaBuilds is null)
        {
            NvidiaStatusPanel.Visibility = Visibility.Visible;
            NvidiaStatusText.Text = "Couldn't reach NVIDIA's update server. Download DLSS files still works.";
            return;
        }

        var missing = _swapper.NotYetCached(_nvidiaBuilds);
        if (missing.Count == 0)
        {
            NvidiaStatusPanel.Visibility = Visibility.Visible;
            NvidiaStatusText.Text = "You have every version NVIDIA's update server offers.";
            return;
        }

        NvidiaStatusPanel.Visibility = Visibility.Collapsed;
        foreach (var dll in missing)
            NvidiaListContainer.Children.Add(CreateNvidiaRow(dll));
    }

    /// <summary>
    /// Built like a cached version's row - same shadow, same icon column - only shorter, with a
    /// size where the path would be. Secondary by its place under the divider, not by its look.
    /// </summary>
    private FrameworkElement CreateNvidiaRow(NvidiaDll dll)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE896",
            FontSize = 32,
            FontWeight = FontWeights.ExtraLight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"DLSS {dll.DisplayVersion}",
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false
        });
        var subtitle = new TextBlock
        {
            Text = "Click to download from NVIDIA",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            IsTextScaleFactorEnabled = false
        };
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var size = new TextBlock
        {
            Text = FormatSize(dll.Size),
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };
        Grid.SetColumn(size, 3);
        grid.Children.Add(size);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 40, 0),
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 76,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(0),
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32)
        };
        var shadow = new ThemeShadow();
        button.Shadow = shadow;
        button.Loaded += (_, _) => shadow.Receivers.Add(ShadowReceiverGrid);
        ToolTipService.SetToolTip(button, $"Downloads DLSS {dll.DisplayVersion} from NVIDIA's own update server into your collection. It isn't installed until you pick it above.");
        button.Click += async (_, _) => await DownloadNvidiaBuildAsync(dll, subtitle);
        return button;
    }

    private async Task DownloadNvidiaBuildAsync(NvidiaDll dll, TextBlock subtitle)
    {
        if (_nvidiaDownloadInProgress) return;

        _nvidiaDownloadInProgress = true;
        try
        {
            NvidiaListContainer.IsHitTestVisible = false;
            subtitle.Text = "Downloading from NVIDIA...";
            var progress = new Progress<double>(p => subtitle.Text = $"Downloading from NVIDIA... {p:P0}");

            var problem = await _swapper.DownloadNvidiaVersionAsync(dll, progress, _lifetime.Token);
            if (_isClosing) return;

            _ = Host.BlinkingLamp(true, true, problem is null ? 1.0 : 0.0, 1.0);
            if (problem is null)
            {
                Report($"added DLSS {dll.DisplayVersion} from NVIDIA");
                await LoadDllsAsync(true);
            }
            else
            {
                subtitle.Text = $"Couldn't add it: {problem}. Click to try again.";
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DLSS] [NVIDIA] Download of {dll.DisplayVersion} failed: {ex.Message}");
            if (!_isClosing) subtitle.Text = "Couldn't add it. Click to try again.";
        }
        finally
        {
            _nvidiaDownloadInProgress = false;
            NvidiaListContainer.IsHitTestVisible = true;
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
