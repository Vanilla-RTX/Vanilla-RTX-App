using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using Vanilla_RTX_App.Modules.Json;
using WinRT.Interop;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Modules.PackBrowser;

public sealed partial class PackBrowserOverlay : ModuleOverlay, Core.FileActivation.IFileActivationTarget
{
    private bool _isClosing;

    /// <summary>
    /// Guards the refresh button against itself. An import disables that button for its own
    /// duration instead, since it finishes with a reload of its own either way.
    /// </summary>
    private bool _reloadInProgress;

    /// <summary>
    /// Pack path -> size badge text, for every pack measured since this module opened. A pack
    /// that is not in here has never been measured and gets no badge; see
    /// <see cref="ResolvePackSizeTextAsync"/> for why only some loads fill it.
    /// </summary>
    private readonly Dictionary<string, string> _packSizes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Button> _packButtonMap = new();
    private readonly HashSet<string> _selectedPaths = new();
    private readonly List<string> _knownTags = new();

    public static string gameTitleText => Persistent.IsTargetingPreview
        ? "Minecraft Preview" : "Minecraft";

    public const string AlchitexCandidateTag = "RTX Reactor Candidate";

    /// <summary>
    /// Purely cosmetic capability tags, and deliberately confined to this module: they get a
    /// badge and a VFX, and nothing else in the app ever learns a pack declared them.
    ///
    /// They are NOT candidates for PackType, and the reason is worth writing down, because
    /// they look like they should be. PackType is not a bucket for whatever a manifest
    /// declares — it is a scale of how close a pack is to being ray traced: RTX at the top,
    /// Vibrant Visuals as the step in between (some PBR, but not the whole thing), and
    /// Incompatible as the absence of both. A pack declaring "raytraced" and "pbr" is still
    /// simply an RTX pack, which is why the two are a priority pick rather than a set.
    /// Chemistry and an unrecognised capability say nothing about that scale, so they cannot
    /// be points on it — and since Incompatible is the only slot they could ever win,
    /// admitting them would turn a pack Tuner currently skips into one it tries to tune.
    ///
    /// Nor do they reach the SelectedPacks tuple. IsAlchitexCandidate is a bool there because
    /// it has no root in the capabilities at all — it is decided by AlchitexSuitabilityScanner
    /// scanning the pack's own contents — whereas every other fact in that tuple is derived
    /// from the tags. These two are derived from the tags and still don't belong, because
    /// nothing outside this module has any use for them.
    ///
    /// Internal rather than public because only BuildTagBadge, TagDisplayRank and
    /// PackBrowserBadgeVFX ever name them.
    /// </summary>
    internal const string ChemistryTag = "Education";
    internal const string UnknownCapabilityTag = "Unknown Capability";

    private static readonly string VibrantVisualsPoopJoke =
        $"Vibrant Visuals{(Random.Shared.Next(100) == 49 ? " 💩" : "")}";

    private static readonly Regex StrictSemVerRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>The refresh button, shown in MainWindow's titlebar while this module is open.</summary>
    protected internal override FrameworkElement? TitleBarStrip => TitleBarActions;

    public PackBrowserOverlay()
    {
        this.InitializeComponent();
        PrepareContent();

        ExpImpDel.ImportStatusChanged += OnImportStatusChanged;
        ExpImpDel.ConfirmOverwrite = (packName, existingPath) => ImportDialogs.ShowOverwriteDialogAsync(this, packName, existingPath);
        ExpImpDel.ConfirmNonResourceImport = packName => ImportDialogs.ShowNonResourceDialogAsync(this, packName);
        ExpImpDel.ConfirmBehaviorImport = packName => ImportDialogs.ShowBehaviorDialogAsync(this, packName);

        this.Loaded += PackBrowserOverlay_Loaded;
    }
    private async void PackBrowserOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Loaded -= PackBrowserOverlay_Loaded;

            if (_isClosing) return;

            AddPackDescriptionText.Text =
                $"Select or drag & drop resource pack files to import to {gameTitleText} (.mcpack, .zip, .mcaddon)";

            TeletextCard.Populate(PackBrowserInfoPanel, TeletextContent.PackBrowserInfo);
            PackBrowserInfoPanel.Visibility = PackBrowserInfoPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            AllowDrop = true;
            DragOver += ContentRoot_DragOver;
            Drop += ContentRoot_Drop;

            await LoadPacksAsync();
            if (_isClosing) return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBrowser] The _Loaded Event Crashed: {ex.Message}");
            return;
        }
    }

    protected override void OnClosing()
    {
        if (_isClosing) return;
        _isClosing = true;

        this.Loaded -= PackBrowserOverlay_Loaded;

        ExpImpDel.ImportStatusChanged -= OnImportStatusChanged;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Drag-and-drop
    // ════════════════════════════════════════════════════════════════════════

    private void ContentRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Import pack";
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void ContentRoot_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items == null || items.Count == 0) return;

        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder folder)
                paths.Add(folder.Path);
            else if (item is Windows.Storage.StorageFile file)
                paths.Add(file.Path);
        }

        if (paths.Count == 0) return;
        await RunImportAsync(() => ExpImpDel.ImportFromPathsAsync(paths));
    }

    // Confirmation dialogs (Pack already installed / Not a resource pack) now live in
    // ExpImpDel.ImportDialogs, shared with MainWindow's .mcpack file-activation path - see
    // the constructor's ConfirmOverwrite/ConfirmNonResourceImport wiring above.

    // ════════════════════════════════════════════════════════════════════════
    //  Version string resolution
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a display version string from a parsed manifest. Accepts a three-element
    /// non-negative int array [1,26,15], or a strict X.Y.Z string - which is where a legacy
    /// manifest's header.packs_version lands. Anything else (a two-element array, a float
    /// component, "v1.2", a missing field) reads as "Unknown" rather than showing the user a
    /// half-right number.
    ///
    /// Reading the manifest itself - tolerant of comments, trailing commas and duplicate keys,
    /// and of both the modern and legacy field layouts - is PackManifest's job now.
    /// </summary>
    private string ResolveVersion(PackManifest manifest)
    {
        if (manifest.VersionTriplet is int[] triplet)
            return string.Join(".", triplet);

        if (manifest.VersionString is string raw && !string.IsNullOrWhiteSpace(raw))
            return StrictSemVerRegex.IsMatch(raw) ? raw : "Unknown";

        return "Unknown";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack list loading
    // ════════════════════════════════════════════════════════════════════════

    // Internal rather than private so MainWindow can re-run it on an already-open browser
    // after a headless .mcpack file-activation import lands (see
    // MainWindow.ImportPackFilesAsync) - otherwise the list would keep showing what was
    // installed before that import until the user closed and reopened this module.
    /// <param name="measureSizes">
    /// Walk every pack's directory to put a size on its badge row. Off by default because the
    /// list is what the user is waiting on - see <see cref="MeasurePackSizesAsync"/>.
    /// </param>
    internal async Task LoadPacksAsync(bool measureSizes = false)
    {
        // Every button is rebuilt, so the ticks have to be carried across by path: a selection
        // survives a rescan for as long as the pack it names is still installed. Empty on the
        // first load, which is why this costs nothing there.
        var wasSelected = new HashSet<string>(_selectedPaths);

        PackListContainer.Children.Clear();
        _packButtonMap.Clear();
        _selectedPaths.Clear();
        _knownTags.Clear();
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        try
        {
            Trace.WriteLine("[PackBrowser] Starting pack scan...");
            var packs = await ScanForCompatiblePacksAsync();
            if (measureSizes) await MeasurePackSizesAsync(packs);
            Trace.WriteLine($"[PackBrowser] Found {packs.Count} packs");

            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;

            if (packs.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = EnvironmentVariables.Persistent.IsTargetingPreview
                    ? "No packs found in Minecraft Preview data directory."
                    : "No packs found in Minecraft data directory.";
                RebuildSelectAllDropdown();
                return;
            }

            var sortedPacks = packs
                .OrderBy(p => p switch
                {
                    { PackType: "RTX" } => 0,
                    { PackType: "Vibrant Visuals" } => 1,
                    _ => 2
                })
                .ThenBy(p => p.PackName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pack in sortedPacks)
                foreach (var tag in pack.CapabilityTags)
                    if (!_knownTags.Contains(tag))
                        _knownTags.Add(tag);

            foreach (var pack in sortedPacks)
            {
                // Empty for a pack nothing has measured yet, which drops its badge downstream.
                pack.PackSizeText = _packSizes.TryGetValue(pack.PackPath, out var size) ? size : string.Empty;

                // Before the button is built - CreatePackButton reads this to decide whether
                // its selection overlay starts visible, which is the only way to restore a tick
                // without walking a visual tree that has not been realised yet.
                if (wasSelected.Contains(pack.PackPath)) _selectedPaths.Add(pack.PackPath);

                var btn = CreatePackButton(pack);
                PackListContainer.Children.Add(btn);
                _packButtonMap[pack.PackPath] = btn;
            }

            RebuildSelectAllDropdown();
            Trace.WriteLine("[PackBrowser] Pack loading complete");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBrowser] EXCEPTION in LoadPacksAsync: {ex}");
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = $"Error: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SelectAll dropdown
    // ════════════════════════════════════════════════════════════════════════

    private void RebuildSelectAllDropdown()
    {
        var flyout = new MenuFlyout();

        if (_knownTags.Count > 0)
        {
            foreach (var tag in _knownTags)
            {
                var capturedTag = tag;
                var item = new MenuFlyoutItem
                {
                    Text = $"Include all with \"{capturedTag}\" tag",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                item.Click += (_, _) => SelectPacksByTag(capturedTag);
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var selectAll = new MenuFlyoutItem
        {
            Text = "Select all",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Medium
        };
        selectAll.Click += (_, _) => SetAllPacksSelected(true);
        flyout.Items.Add(selectAll);

        var deselectAll = new MenuFlyoutItem
        {
            Text = "Deselect all",
            HorizontalAlignment =
            HorizontalAlignment.Stretch,
            FontWeight = FontWeights.Medium
        };
        deselectAll.Click += (_, _) => SetAllPacksSelected(false);
        flyout.Items.Add(deselectAll);

        SelectAll_Button.Flyout = flyout;
    }

    private void SetAllPacksSelected(bool selected)
    {
        foreach (var (path, button) in _packButtonMap)
        {
            var overlay = FindSelectionOverlay(button);
            if (overlay == null) continue;

            if (selected) { _selectedPaths.Add(path); overlay.Visibility = Visibility.Visible; }
            else { _selectedPaths.Remove(path); overlay.Visibility = Visibility.Collapsed; }
        }

        _ = Host.BlinkingLamp(true, true, selected ? 1.0 : 0.0);
    }

    private void SelectPacksByTag(string tag)
    {
        foreach (var (path, button) in _packButtonMap)
        {
            if (button.Tag is not PackData pack) continue;
            if (!pack.CapabilityTags.Contains(tag)) continue;

            var overlay = FindSelectionOverlay(button);
            if (overlay == null) continue;

            _selectedPaths.Add(path);
            overlay.Visibility = Visibility.Visible;
        }

        // Some packs go on, the rest are left as they were, so neither direction is the truth.
        _ = Host.BlinkingLamp(true, true, 0.5);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack button factory
    // ════════════════════════════════════════════════════════════════════════

    private Button CreatePackButton(PackData pack)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 12, 12, 12),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 5, 0, 5),
            CornerRadius = new CornerRadius(5),
            Tag = pack,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            MinHeight = 96
        };

        var buttonShadow = new ThemeShadow();
        button.Shadow = buttonShadow;
        button.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                buttonShadow.Receivers.Add(ShadowReceiverGrid);
        };

        // Columns: [icon 96] [gap 15] [info *] [gap 15] [right panel Auto]
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Icon + selection overlay ─────────────────────────────────────────
        var iconContainer = new Grid { Width = 96, Height = 96 };

        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 96, 96, 96)),
            Translation = new System.Numerics.Vector3(0, 0, 12)
        };

        var iconShadow = new ThemeShadow();
        iconBorder.Shadow = iconShadow;
        iconBorder.Loaded += (s, e) =>
        {
            if (ShadowReceiverGrid != null)
                iconShadow.Receivers.Add(ShadowReceiverGrid);
        };

        if (pack.Icon != null)
        {
            iconBorder.Child = new Microsoft.UI.Xaml.Controls.Image
            { Source = pack.Icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            try
            {
                iconBorder.Child = new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/missing.png")),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                iconBorder.Child = new FontIcon
                {
                    Glyph = "\uE7B8",
                    FontSize = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        // Selection overlay
        var selectionOverlay = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(ColorHelper.FromArgb(200, 0, 0, 0)),
            Visibility = _selectedPaths.Contains(pack.PackPath) ? Visibility.Visible : Visibility.Collapsed,
            Tag = "SelectionOverlay"
        };
        selectionOverlay.Child = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 72,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        iconContainer.Children.Add(iconBorder);
        iconContainer.Children.Add(selectionOverlay);
        Grid.SetColumn(iconContainer, 0);
        grid.Children.Add(iconContainer);

        // ── Pack name + description ──────────────────────────────────────────
        var nameBlock = new TextBlock
        {
            Text = pack.PackName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var descBlock = new TextBlock
        {
            Text = pack.PackDescription,
            FontSize = 12,
            Opacity = 0.75,
            Margin = new Thickness(6, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(nameBlock);
        infoPanel.Children.Add(descBlock);
        Grid.SetColumn(infoPanel, 2);
        grid.Children.Add(infoPanel);

        // ── Right panel: [size | version] top-right, tags bottom-right ───────
        //
        // Row 0 holds a horizontal StackPanel with size badge on the left and
        // version badge on the right. Row 2 holds capability tags.
        var rightPanel = new Grid();
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top row: size badge + version badge side by side, right-aligned
        var topBadgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6
        };
        // Empty means nobody has measured this pack yet - see ResolvePackSizeTextAsync.
        if (!string.IsNullOrEmpty(pack.PackSizeText))
            topBadgeRow.Children.Add(BuildSizeBadge(pack.PackSizeText));
        topBadgeRow.Children.Add(BuildVersionBadge(pack.Version));
        Grid.SetRow(topBadgeRow, 0);
        rightPanel.Children.Add(topBadgeRow);

        // Bottom row: capability tags
        var tagsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Spacing = 6
        };
        foreach (var tag in pack.CapabilityTags.OrderBy(TagDisplayRank))
            tagsPanel.Children.Add(BuildTagBadge(tag));
        Grid.SetRow(tagsPanel, 2);
        rightPanel.Children.Add(tagsPanel);

        Grid.SetColumn(rightPanel, 4);
        grid.Children.Add(rightPanel);

        button.Content = grid;
        button.Click += PackButton_Click;
        return button;
    }

    private static Border BuildSizeBadge(string sizeText) => BuildPlainBadge(sizeText);

    private static Border BuildVersionBadge(string version) => BuildPlainBadge($"Version: {version}");

    /// <summary>
    /// The neutral badge both of the top-row badges are. They sit side by side on the same row
    /// and say the same kind of thing about a pack, so they are one builder rather than two -
    /// which is how they came to be drawn on two different greys in the first place.
    /// (<see cref="BuildTagBadge"/> is deliberately not one of these: a tag's colour is what
    /// the tag means.)
    /// </summary>
    private static Border BuildPlainBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 4, 8, 4),
        Background = new SolidColorBrush(ColorHelper.FromArgb(155, 32, 32, 32)),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        }
    };

    /// <summary>
    /// Where a tag sits in the badge row: least important leftmost, most important hard up
    /// against the right edge of the card, which is where the eye lands first. So the two
    /// cosmetic tags lead, the pack's own type closes, and the Alchitex offer sits just
    /// inside it.
    ///
    /// This is display order only — CapabilityTags stays in the order the parser built it,
    /// which is what the "Include all with X tag" menu is listed in. OrderBy is stable, so
    /// anything unranked keeps the order it arrived in.
    /// </summary>
    private static int TagDisplayRank(string tag) => tag switch
    {
        UnknownCapabilityTag => 0,
        ChemistryTag => 1,
        AlchitexCandidateTag => 2,
        "Incompatible" => 3,
        "Vibrant Visuals" => 4,
        "RTX" => 5,
        _ => 0
    };

    private static Border BuildTagBadge(string tag)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4)
        };
        var text = new TextBlock
        {
            Text = tag,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        switch (tag)
        {
            case "Incompatible":
                text.Text = "Incompatible with Tuner";
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 192, 33, 0));
                break;
            case "RTX":
                text.Text = "Ray Traced";
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 111, 177, 0));
                break;
            case "Vibrant Visuals":
                text.Text = VibrantVisualsPoopJoke;
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 200, 132, 0));
                break;
            case AlchitexCandidateTag:
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 0, 72, 138));
                break;
            case ChemistryTag:
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 0, 165, 143));
                break;
            case UnknownCapabilityTag:
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));
                badge.Background = new SolidColorBrush(ColorHelper.FromArgb(244, 43, 43, 43));
                break;
            default:
                text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                badge.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                break;
        }

        badge.Child = text;
        if (!Persistent.SuspendUIAnimations)
        {
            PackBrowserBadgeVFX.Apply(badge, tag);
        }
        return badge;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Click handlers
    // ════════════════════════════════════════════════════════════════════════

    private void PackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PackData pack) return;

        var overlay = FindSelectionOverlay(button);
        if (overlay == null) return;

        bool isNowSelected = !_selectedPaths.Contains(pack.PackPath);

        if (isNowSelected)
        {
            _selectedPaths.Add(pack.PackPath);
            overlay.Visibility = Visibility.Visible;
        }
        else
        {
            _selectedPaths.Remove(pack.PackPath);
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        EnvironmentVariables.SelectedPacks.Clear();

        foreach (var path in _selectedPaths.Where(p => _packButtonMap.ContainsKey(p)))
        {
            var pack = (PackData)_packButtonMap[path].Tag;
            EnvironmentVariables.SelectedPacks.Add(
                (pack.PackPath, pack.PackName, pack.PackType, pack.PotentiallySuitableForPBRGen));
        }

        this.Close();
    }

    private async void AddPackButton_Click(object sender, RoutedEventArgs e)
    {
        await RunImportAsync(() => ExpImpDel.ImportPackAsync(WindowHandle));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Import orchestration
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Imports files handed over by a double-click in Explorer, through this module rather
    /// than MainWindow - see <see cref="Core.FileActivation.IFileActivationTarget"/>.
    ///
    /// <para>Deliberately the same call drag-and-drop makes, so an activated file and a
    /// dropped one are the same operation: same busy state, same confirmation dialogs (this
    /// window's constructor already points ExpImpDel's at itself), same list reload.</para>
    /// </summary>
    public Task ImportActivatedFilesAsync(IReadOnlyList<string> paths) =>
        RunImportAsync(() => ExpImpDel.ImportFromPathsAsync(paths));

    /// <summary>
    /// Rescans the packs folder and rebuilds the list. The list is built when this module
    /// opens and nothing tells it when the folder changes underneath it - RTX Reactor
    /// promoting a generated pack is the case that prompted this - so a manual rescan is what
    /// stands in for the refresh that reopening the module would have given.
    ///
    /// <para>It is also the only load that measures pack sizes, for the reason on
    /// <see cref="MeasurePackSizesAsync"/>: this is the one the user asked for, so it is the
    /// one that can afford to be slow.</para>
    /// </summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reloadInProgress) return;
        _reloadInProgress = true;

        try
        {
            RefreshButton.IsEnabled = false;
            AddPackButton.IsEnabled = false;

            LoadingPanel.Visibility = Visibility.Visible;
            PackSelectionPanel.Visibility = Visibility.Collapsed;
            await LoadPacksAsync(measureSizes: true);
        }
        finally
        {
            _reloadInProgress = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            AddPackButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            _ = Host.BlinkingLamp(true, true, 0.5, 1.0);
        }
    }

    private async Task RunImportAsync(Func<Task<bool>> importWork)
    {
        AddPackButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        var imported = false;

        // An import is a download, an unzip, or both, over as many files as the user dropped.
        // Nothing else ever turns the continuous blink off, so the finally below is the only
        // place it stops and every path out of here has to go through it.
        _ = Host.BlinkingLamp(true);

        try
        {
            imported = await importWork();
        }
        finally
        {
            _ = Host.BlinkingLamp(false);

            LoadingPanel.Visibility = Visibility.Visible;
            PackSelectionPanel.Visibility = Visibility.Collapsed;
            await LoadPacksAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;
            PackSelectionPanel.Visibility = Visibility.Visible;
            AddPackButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            SetImportBusy(false);
        }
    }

    /// <summary>
    /// ExpImpDel narrates each file as it extracts. The message itself is dropped - the
    /// titlebar it used to be written into belongs to MainWindow now - and what is kept is the
    /// one bit of it the user was actually reading at that speed: that an import is running.
    /// It is also written to Trace, so the detail is still there when something goes wrong.
    /// </summary>
    private void OnImportStatusChanged(string message)
    {
        Trace.WriteLine($"[PackBrowser] {message}");
        DispatcherQueue.TryEnqueue(() => SetImportBusy(true));
    }

    private void SetImportBusy(bool busy) =>
        ImportProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    // ════════════════════════════════════════════════════════════════════════
    //  Pack scanning
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<PackData>> ScanForCompatiblePacksAsync()
    {
        var packs = new List<PackData>();
        var isTargetingPreview = EnvironmentVariables.Persistent.IsTargetingPreview;

        if (!MinecraftUserDataLocator.IsDataValid(isTargetingPreview))
        {
            Trace.WriteLine($"[PackBrowser] {MinecraftUserDataLocator.GetVersionDisplayName(isTargetingPreview)} data root not found.");
            return packs;
        }

        // Two passes per scan path: manifest.json first so modern always wins over legacy
        // in the same directory. .Concat() ordering was filesystem-dependent and unsafe.
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scanPath in MinecraftUserDataLocator.GetExistingResourcePackScanPaths(
                     EnvironmentVariables.Persistent.IsTargetingPreview))
        {
            // Pass 1: modern manifest.json
            foreach (var manifestPath in Helpers.FindFilesAtDepth(scanPath, PackManifest.ModernFileName, minDepth: 1, maxDepth: 2))
            {
                var packDir = Path.GetDirectoryName(manifestPath);
                if (packDir == null || !seenDirs.Add(packDir)) continue;

                try
                {
                    var packData = await ParsePackAsync(packDir, manifestPath);
                    if (packData != null) packs.Add(packData);
                }
                catch (Exception ex) { Trace.WriteLine($"[PackBrowser] Error parsing pack {packDir}: {ex.Message}"); }
            }

            // Pass 2: legacy pack_manifest.json (seenDirs skips dirs already handled above)
            foreach (var manifestPath in Helpers.FindFilesAtDepth(scanPath, PackManifest.LegacyFileName, minDepth: 1, maxDepth: 2))
            {
                var packDir = Path.GetDirectoryName(manifestPath);
                if (packDir == null || !seenDirs.Add(packDir)) continue;

                try
                {
                    var packData = await ParsePackAsync(packDir, manifestPath);
                    if (packData != null) packs.Add(packData);
                }
                catch (Exception ex) { Trace.WriteLine($"[PackBrowser] Error parsing pack {packDir}: {ex.Message}"); }
            }
        }

        return packs;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Manifest parsing — handles both manifest.json and pack_manifest.json
    // ════════════════════════════════════════════════════════════════════════

    private async Task<PackData?> ParsePackAsync(string packDir, string manifestPath)
    {
        var manifest = await PackManifest.FromFileAsync(manifestPath);
        if (manifest == null)
        {
            Trace.WriteLine($"[PackBrowser] Unreadable manifest, skipping pack: {manifestPath}");
            return null;
        }

        return manifest.IsLegacy
            ? await ParseLegacyPackManifestAsync(packDir, manifest)
            : await ParseModernManifestAsync(packDir, manifest);
    }

    /// <summary>
    /// Parses the old pack_manifest.json format (pre-1.16 era).
    /// Always Incompatible — no capabilities field exists. Eligible for the Alchitex candidate
    /// tag like any modern pack: RTX Reactor promotes the manifest to format_version 2 on the
    /// way out (PostProcess.PromoteLegacyManifest).
    /// </summary>
    private async Task<PackData> ParseLegacyPackManifestAsync(string packDir, PackManifest manifest)
    {
        string packName = Helpers.StripMinecraftFormatting(manifest.HeaderName ?? string.Empty);
        string packDesc = Helpers.StripMinecraftFormatting(manifest.HeaderDescription ?? string.Empty);

        if (string.IsNullOrWhiteSpace(packName)) packName = Path.GetFileName(packDir);
        if (string.IsNullOrWhiteSpace(packDesc)) packDesc = Helpers.SanitizePathForDisplay(packDir);

        string version = ResolveVersion(manifest);

        var capabilityTags = new List<string>();
        bool potentiallySuitable = false;

        if (AlchitexSuitabilityScanner.IsPotentiallySuitable(packDir))
        {
            potentiallySuitable = true;
            capabilityTags.Add(AlchitexCandidateTag);
        }

        capabilityTags.Add("Incompatible");

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = "Incompatible",
            Version = version,
            IsLegacyFormat = true,
            PotentiallySuitableForPBRGen = potentiallySuitable
        };
    }

    /// <summary>
    /// Parses the modern manifest.json format.
    /// Version must be a three-element int array or a strict X.Y.Z string.
    /// </summary>
    private async Task<PackData> ParseModernManifestAsync(string packDir, PackManifest manifest)
    {
        var capabilityTags = new List<string>();
        var packType = "Incompatible";

        // Hoisted out of the capabilities block below so the cosmetic tags can be appended
        // after the functional ones, without reordering anything that already works.
        bool hasChemistry = false, hasUnknownCapability = false;

        if (manifest.Capabilities.Count > 0)
        {
            bool hasRaytraced = false, hasPbr = false;

            foreach (var cap in manifest.Capabilities)
            {
                var capLower = cap.Trim().ToLowerInvariant();
                if (capLower.Length == 0) continue;

                if (capLower == "raytraced") hasRaytraced = true;
                else if (capLower == "pbr") hasPbr = true;
                else if (capLower == "chemistry") hasChemistry = true;
                // Anything else is a capability we genuinely don't model — experimental_custom_ui
                // today, whatever Mojang adds next. Say so rather than render it as nothing.
                else hasUnknownCapability = true;
            }
            if (hasRaytraced)
            {
                capabilityTags.Add("RTX"); packType = "RTX";
            }
            if (hasPbr)
            {
                capabilityTags.Add("Vibrant Visuals");
                if (packType == "Incompatible") packType = "Vibrant Visuals";
            }
        }


        bool potentiallySuitable = false;
        if (packType == "Incompatible" || packType == "Vibrant Visuals")
        {
            if (AlchitexSuitabilityScanner.IsPotentiallySuitable(packDir))
            {
                potentiallySuitable = true;
                capabilityTags.Add(AlchitexCandidateTag);
            }
            if (packType == "Incompatible" && packType != "Vibrant Visuals")
            {
                capabilityTags.Add("Incompatible");
            }
        }

        // Last, deliberately: these never touch packType, so every functional tag keeps both
        // its meaning and its position in the badge row.
        if (hasChemistry) capabilityTags.Add(ChemistryTag);
        if (hasUnknownCapability) capabilityTags.Add(UnknownCapabilityTag);

        string version = ResolveVersion(manifest);

        string packName = manifest.HeaderName ?? "pack.name";
        string packDesc = manifest.HeaderDescription ?? "pack.description";

        if (packName == "pack.name" || packDesc == "pack.description")
        {
            var langFolder = Path.Combine(packDir, "texts");
            if (Directory.Exists(langFolder))
            {
                var langData = await TryLoadLangFileAsync(langFolder);
                if (langData != null)
                {
                    if (packName == "pack.name" && langData.ContainsKey("pack.name"))
                        packName = langData["pack.name"];
                    if (packDesc == "pack.description" && langData.ContainsKey("pack.description"))
                        packDesc = langData["pack.description"];
                }
            }
        }

        packName = Helpers.StripMinecraftFormatting(packName);
        packDesc = Helpers.StripMinecraftFormatting(packDesc);

        if (packName == "pack.name" || string.IsNullOrWhiteSpace(packName))
            packName = Path.GetFileName(packDir);
        if (packDesc == "pack.description" || string.IsNullOrWhiteSpace(packDesc))
            packDesc = Helpers.SanitizePathForDisplay(packDir);

        return new PackData
        {
            PackName = packName,
            PackDescription = packDesc,
            PackPath = packDir,
            Icon = await LoadIconAsync(packDir),
            CapabilityTags = capabilityTags,
            PackType = packType,
            Version = version,
            IsLegacyFormat = false,
            PotentiallySuitableForPBRGen = potentiallySuitable
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Lang file loading — en_US first, en_GB fallback, then any other en_*
    // ════════════════════════════════════════════════════════════════════════

    private async Task<Dictionary<string, string>?> TryLoadLangFileAsync(string langFolder)
    {
        if (!Directory.Exists(langFolder)) return null;

        var langFiles = Directory.GetFiles(langFolder, "*.lang")
            .Where(f => Path.GetFileName(f).StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                return name switch
                {
                    "en_us" => 0,
                    "en_gb" => 1,
                    _ => 2
                };
            })
            .ToArray();

        foreach (var langPath in langFiles)
        {
            try
            {
                var langData = new Dictionary<string, string>();
                var lines = await File.ReadAllLinesAsync(langPath);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    langData[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                if (langData.ContainsKey("pack.name") || langData.ContainsKey("pack.description"))
                    return langData;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PackBrowser] Error loading lang file {langPath}: {ex.Message}");
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Icon loading
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads a pack icon from disk, supports common types, but not Targa.
    /// </summary>
    private Task<BitmapImage?> LoadIconAsync(string packDir) => LoadPackIconAsync(packDir);

    /// <summary>
    /// Loads a pack's pack_icon.* as a BitmapImage, or null if it has none / none of them
    /// load. No manifest reading involved - just the icon file.
    ///
    /// Public and static because the Alchitex overlay shows the same icons for the packs
    /// queued for generation, and that's the same question with the same answer.
    /// </summary>
    public static async Task<BitmapImage?> LoadPackIconAsync(string packDir)
    {
        // The list draws these at 96x96; 192 is 2x that for 200% scale. A decoded image costs
        // width x height x 4 bytes of graphics memory regardless of file size, and a pack's
        // icon is whatever resolution its author chose.
        const int iconDecodeWidth = 192;

        if (string.IsNullOrEmpty(packDir) || !Directory.Exists(packDir)) return null;

        var iconFiles = Directory.GetFiles(packDir, "pack_icon.*")
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
            .ToArray();

        foreach (var iconPath in iconFiles)
        {
            try
            {
                // DecodePixelWidth is ignored unless it is set before the source is handed over.
                var bitmap = new BitmapImage { DecodePixelWidth = iconDecodeWidth };
                using var fs = File.OpenRead(iconPath);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                ms.Position = 0;
                await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                return bitmap;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PackBrowser] Error loading icon {iconPath}: {ex.Message}");
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pack size calculation
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Measures every pack in <paramref name="packs"/> and remembers the result in
    /// <see cref="_packSizes"/>.
    ///
    /// <para><b>Only a refresh the user clicked for calls this.</b> A size is a full recursive
    /// enumeration of one pack's files, and over a real library that is seconds of loading ring
    /// in front of the list. Every load the user did not ask for - opening the module, an import
    /// landing, a file activation - is one they are waiting through to get somewhere else, so
    /// none of them pays for it. The refresh button is the one they pressed on purpose.</para>
    ///
    /// <para><b>The results outlive the load that produced them</b>, so the badges do not vanish
    /// the moment an import rebuilds the list. What that costs is that a badge reads as of the
    /// last refresh rather than as of now, which is why refreshing re-measures everything rather
    /// than keeping what it already has.</para>
    ///
    /// <para><b>Packs are walked in parallel</b>, which is most of why this is usable at all:
    /// measured over 56 installed packs / 121k files, one at a time takes 5.3s against 1.9s at
    /// eight at once. The cap is eight because sixteen measured no faster - past that point the
    /// disk is the limit rather than the thread count - and an unbounded fan-out over a large
    /// library on a spinning disk is all seek.</para>
    /// </summary>
    private async Task MeasurePackSizesAsync(IReadOnlyList<PackData> packs)
    {
        var dirs = packs.Select(p => p.PackPath).ToArray();

        var measured = await Task.Run(() =>
        {
            var sizes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                             dir => sizes[dir] = MeasurePackSizeText(dir));
            return sizes;
        });

        foreach (var (dir, text) in measured)
            _packSizes[dir] = text;
    }

    /// <summary>
    /// Formats one pack directory's total size, e.g. "12.34 MB". Answers "? MB" on any failure -
    /// a pack whose size cannot be read is still a pack the user can select - and counts an
    /// unreadable file as nothing rather than losing the whole pack's total with it.
    /// </summary>
    private static string MeasurePackSizeText(string packDir)
    {
        try
        {
            var totalBytes = Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories)
                                      .Sum(f =>
                                      {
                                          try { return new FileInfo(f).Length; }
                                          catch { return 0L; }
                                      });

            // Under a megabyte the two-decimal MB form reads "0.00 MB", which is the one
            // size a badge must never claim: a stub pack of a few files is not an empty one.
            double kb = totalBytes / 1024.0;
            return kb < 1024
                ? kb.ToString("F0") + " KB"
                : (kb / 1024.0).ToString("F2") + " MB";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBrowser] Error calculating size for {packDir}: {ex.Message}");
            return "? MB";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Visual-tree helper
    // ════════════════════════════════════════════════════════════════════════

    private static Border? FindSelectionOverlay(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Tag is string s && s == "SelectionOverlay")
                return b;
            var result = FindSelectionOverlay(child);
            if (result != null) return result;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Alchitex suitability scanner
    // ════════════════════════════════════════════════════════════════════════

    // TODO: The definition of what makes a texture pack truly and concretely suitable for Alchitex can evolve over time
    // You'll figure it out when you get there, for now, 20 textures in all block dirs gives good confidence, combined with the not-being-legacy checks
    private static class AlchitexSuitabilityScanner
    {
        private const int MinimumQualifyingImageCount = 32;

        private static readonly HashSet<string> QualifyingExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tga" };

        public static bool IsPotentiallySuitable(string packDir)
        {
            int matchCount = 0;
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories))
                {
                    if (!QualifyingExtensions.Contains(Path.GetExtension(filePath))) continue;
                    if (!IsUnderTexturesBlocksPath(filePath)) continue;
                    if (++matchCount >= MinimumQualifyingImageCount) return true;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PackBrowser] Error scanning {packDir} for Alchitex suitability: {ex.Message}");
            }
            return false;
        }

        private static bool IsUnderTexturesBlocksPath(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return false;
            var segments = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("textures", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 1].Equals("blocks", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Private data model
    // ════════════════════════════════════════════════════════════════════════

    private class PackData
    {
        public required string PackName { get; set; }
        public required string PackDescription { get; set; }
        public required string PackPath { get; set; }
        public BitmapImage? Icon { get; set; }
        public required List<string> CapabilityTags { get; set; }
        public required string PackType { get; set; }
        public required string Version { get; set; }
        /// <summary>Pre-formatted pack folder size, e.g. "12.34 MB".</summary>
        /// <summary>Filled by the size pass, not by manifest parsing - empty until something measures it.</summary>
        public string PackSizeText { get; set; } = string.Empty;
        public bool IsLegacyFormat { get; set; } = false;
        public bool PotentiallySuitableForPBRGen { get; set; } = false;
    }
}
