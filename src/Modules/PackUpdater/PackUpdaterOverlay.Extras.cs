using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules.PackUpdater;

/// <summary>
/// The Vanilla RTX Extras card: one row per pack the Extras repository's latest release lists,
/// each with what is installed, what is available and an Install / Update / Reinstall button, plus
/// one "Get all" for everything missing or out of date. Installs go through the same
/// <see cref="StartInstall"/> as the Vanilla RTX cards.
///
/// <para><b>Rows are built in code</b> because the list is the release's, not this window's -
/// a pack added to the Extras repository appears here without an app update. Secondary text
/// takes <c>Opacity</c> rather than a themed brush, the same as the Vanilla RTX cards above it,
/// so nothing here has to follow a theme change by hand. The column headings in the XAML share
/// the rows' three column widths.</para>
/// </summary>
public sealed partial class PackUpdaterOverlay
{
    private ReleaseIndex? _extrasIndex;
    private Dictionary<string, InstalledPackCopy> _installedExtras = new(StringComparer.OrdinalIgnoreCase);

    // Keyed by header UUID, the identity installs report back with.
    private readonly Dictionary<string, Button> _extraButtons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the index (fresh when due or forced), re-scans what is installed, and rebuilds the
    /// rows. Returns true when the repository answered just now, for the refresh cooldown.
    /// </summary>
    private async Task<bool> LoadExtrasAsync(bool force)
    {
        ReleaseIndex? index = null;
        var checkedRemote = false;
        try
        {
            (index, checkedRemote) = await PackUpdater.GetIndexAsync(PackUpdater.Extras, force);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Extras index failed: {ex.Message}");
        }
        if (_isClosing) return checkedRemote;

        _extrasIndex = index;
        _installedExtras = index is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : await Task.Run(() => PackUpdater.FindInstalled(index.Packs));
        if (_isClosing) return checkedRemote;

        BuildExtrasRows();
        return checkedRemote;
    }

    /// <summary>
    /// The packs in the order they are listed: the index's own, except that Everwinter leads
    /// during the Christmas season, which is when the app promotes it.
    /// </summary>
    private IEnumerable<ReleasePack> OrderedExtras()
    {
        if (_extrasIndex is null) return Array.Empty<ReleasePack>();

        var packs = _extrasIndex.Packs.AsEnumerable();
        if (Helpers.GetSpecialOccasionName() == "christmas")
            packs = packs.OrderBy(p => p.Id.Contains("Everwinter", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return packs;
    }

    private InstalledPackCopy? InstalledCopy(ReleasePack pack)
        => _installedExtras.TryGetValue(pack.Uuid, out var installed) ? installed : null;

    /// <summary>The loading state replaces the headings and rows, so the card never shows an empty table.</summary>
    private void ShowExtrasLoading(bool loading)
    {
        ExtrasLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ExtrasColumnHeadings.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        ExtrasList.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildExtrasRows()
    {
        ShowExtrasLoading(false);
        ExtrasList.Children.Clear();
        _extraButtons.Clear();

        if (_extrasIndex is null)
        {
            ExtrasBuildText.Text = "Couldn't reach the Vanilla RTX Extras repository. Use the refresh button to try again.";
            ExtrasColumnHeadings.Visibility = Visibility.Collapsed;
            UpdateGetAllButton();
            return;
        }

        var built = _extrasIndex.BuiltUtc?.ToLocalTime().ToString("d MMMM yyyy") ?? _extrasIndex.Tag;
        ExtrasBuildText.Text = $"Add-ons and extensions from the latest build, {built}. {_extrasIndex.Packs.Count} packs.";

        var first = true;
        foreach (var pack in OrderedExtras())
        {
            if (!first)
                ExtrasList.Children.Add(new Border { Height = 1, Style = (Style)Application.Current.Resources["ThemedDividerRuleStyle"] });
            first = false;

            ExtrasList.Children.Add(BuildExtraRow(pack));
        }

        UpdateGetAllButton();
        if (_updater.IsInstallationInProgress()) SetExtrasButtonsEnabled(false);
    }

    private Grid BuildExtraRow(ReleasePack pack)
    {
        var installed = InstalledCopy(pack);

        // Name and status share the width 3:2, so a wider window widens both. Status has a floor,
        // and below what it needs its lines wrap (VersionLine) rather than clip.
        // ExtrasColumnHeadings in the XAML has to say the same.
        var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 150 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = pack.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextScaleFactorEnabled = false,
        });
        if (!string.IsNullOrWhiteSpace(pack.Description))
        {
            var description = new TextBlock
            {
                Text = pack.Description,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextScaleFactorEnabled = false,
            };
            ToolTipService.SetToolTip(description, pack.Description);
            text.Children.Add(description);
        }
        row.Children.Add(text);

        var outdated = PackUpdater.IsOutdated(pack, installed);
        var versions = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        versions.Children.Add(VersionLine("Installed:", installed?.VersionDisplay ?? "Not installed"));
        versions.Children.Add(VersionLine("Available:", installed is not null && !outdated ? $"{pack.VersionDisplay} (You're up-to-date)" : pack.VersionDisplay));
        Grid.SetColumn(versions, 1);
        row.Children.Add(versions);

        var button = new Button
        {
            Content = installed is null ? "Install" : outdated ? "Update" : "Reinstall",
            Style = (Style)Application.Current.Resources[installed is null || outdated ? "AccentButtonStyle" : "DefaultButtonStyle"],
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTextScaleFactorEnabled = false,
        };
        ToolTipService.SetToolTip(button, PackUpdater.IsCached(PackUpdater.Extras, pack)
            ? "Installs from the copy the app already downloaded."
            : $"Downloads {FormatSize(pack.Asset.Size)} from the Vanilla RTX Extras repository.");
        button.Click += (_, _) => StartInstall(PackUpdater.Extras, new[] { pack.Uuid }, button);

        // The same lifted, shadowed frame the Vanilla RTX cards and Get all sit in.
        var lift = new Grid
        {
            MinHeight = 36,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Translation = new System.Numerics.Vector3(0, 0, 12),
            Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow(),
        };
        lift.Children.Add(button);
        Grid.SetColumn(lift, 2);
        row.Children.Add(lift);

        _extraButtons[pack.Uuid] = button;
        return row;
    }

    /// <summary>
    /// A label and its value. A grid rather than a horizontal StackPanel, which measures its
    /// children at infinite width and so can only ever clip a long value, never wrap it.
    /// </summary>
    private static Grid VersionLine(string label, string value)
    {
        var line = new Grid { ColumnSpacing = 6 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = 0.75, IsTextScaleFactorEnabled = false });
        var text = new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextScaleFactorEnabled = false,
        };
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        return line;
    }

    /// <summary>Everything "Get all" would install: each pack not installed, and each one out of date.</summary>
    private List<ReleasePack> ExtrasToGet()
        => OrderedExtras().Where(p => InstalledCopy(p) is not { } i || PackUpdater.IsOutdated(p, i)).ToList();

    private void UpdateGetAllButton()
    {
        var toGet = ExtrasToGet();
        var missing = toGet.Count(p => InstalledCopy(p) is null);

        if (_extrasIndex is null || toGet.Count == 0)
        {
            ExtrasGetAllButton.Content = _extrasIndex is null ? "Get all" : "All up-to-date";
            ExtrasGetAllButton.IsEnabled = false;
            return;
        }

        // What still has to come over the network; cached packs install off the disk.
        var download = toGet.Where(p => !PackUpdater.IsCached(PackUpdater.Extras, p)).Sum(p => p.Asset.Size);
        var size = download > 0 ? $" ({FormatSize(download)})" : "";
        ExtrasGetAllButton.Content = missing > 0 ? $"Get all{size}" : $"Update all{size}";
        ExtrasGetAllButton.IsEnabled = !_updater.IsInstallationInProgress();
    }

    private void SetExtrasButtonsEnabled(bool enabled)
    {
        foreach (var button in _extraButtons.Values) button.IsEnabled = enabled;
        ExtrasGetAllButton.IsEnabled = enabled && ExtrasToGet().Count > 0;
    }

    private void ExtrasGetAllButton_Click(object sender, RoutedEventArgs e)
        => StartInstall(PackUpdater.Extras, ExtrasToGet().Select(p => p.Uuid).ToList(), ExtrasGetAllButton);
}
