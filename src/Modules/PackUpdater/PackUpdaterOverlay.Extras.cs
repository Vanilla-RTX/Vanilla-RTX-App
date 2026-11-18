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
/// The Vanilla RTX Extras card: one row per pack the Extras repository's latest release lists
/// (see <see cref="PackUpdater"/>'s Extras partial for where that list comes from), each with
/// what is installed, what is available and an Install / Update / Reinstall button, plus one
/// "Get all" for everything missing or out of date.
///
/// <para><b>Rows are built in code</b> because the list is the release's, not this window's -
/// a pack added to the Extras repository appears here without an app update. Secondary text
/// takes <c>Opacity</c> rather than a themed brush, the same as the Vanilla RTX cards above it,
/// so nothing here has to follow a theme change by hand.</para>
///
/// <para><b>One install at a time, shared with the Vanilla RTX packs.</b> While any install runs,
/// every button in the window is disabled; the pack being installed shows "Installing..." on its
/// own row. The batch runs on <see cref="PackUpdater"/>, which outlives this window, so closing
/// the module mid-batch lets it finish and a reopened module picks the state back up.</para>
/// </summary>
public sealed partial class PackUpdaterOverlay
{
    private ExtrasIndex? _extrasIndex;
    private Dictionary<string, InstalledExtra> _installedExtras = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _extraButtons = new();

    /// <summary>
    /// Reads the index (fresh when due or forced), re-scans what is installed, and rebuilds the
    /// rows. Returns true when the repository answered just now, for the refresh cooldown.
    /// </summary>
    private async Task<bool> LoadExtrasAsync(bool force)
    {
        ExtrasIndex? index = null;
        var checkedRemote = false;
        try
        {
            (index, checkedRemote) = await PackUpdater.GetExtrasIndexAsync(force);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Extras index failed: {ex.Message}");
        }
        if (_isClosing) return checkedRemote;

        _extrasIndex = index;
        _installedExtras = index is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : await Task.Run(() => PackUpdater.FindInstalledExtras(index.Packs));
        if (_isClosing) return checkedRemote;

        BuildExtrasRows();
        return checkedRemote;
    }

    /// <summary>
    /// The packs in the order they are listed: the index's own, except that Everwinter leads
    /// during the Christmas season, which is when the app promotes it.
    /// </summary>
    private IEnumerable<ExtraPack> OrderedExtras()
    {
        if (_extrasIndex is null) return Array.Empty<ExtraPack>();

        var packs = _extrasIndex.Packs.AsEnumerable();
        if (Helpers.GetSpecialOccasionName() == "christmas")
            packs = packs.OrderBy(p => p.Id.Contains("Everwinter", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        return packs;
    }

    private InstalledExtra? InstalledCopy(ExtraPack pack)
        => _installedExtras.TryGetValue(pack.Uuid, out var installed) ? installed : null;

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
        ExtrasBuildText.Text = $"Add-ons and extensions from the latest automated build, {built}. {_extrasIndex.Packs.Count} packs.";

        var first = true;
        foreach (var pack in OrderedExtras())
        {
            if (!first)
                ExtrasList.Children.Add(new Border { Height = 1, Style = (Style)Application.Current.Resources["ThemedDividerRuleStyle"] });
            first = false;

            ExtrasList.Children.Add(BuildExtraRow(pack));
        }

        UpdateGetAllButton();
        ApplyExtrasInstallState();
    }

    private Grid BuildExtraRow(ExtraPack pack)
    {
        var installed = InstalledCopy(pack);

        var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
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

        var versions = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        versions.Children.Add(VersionLine("Installed:", installed?.VersionDisplay ?? "Not installed"));
        var upToDate = installed is not null && !PackUpdater.IsExtraOutdated(pack, installed);
        versions.Children.Add(VersionLine("Available:", upToDate ? $"{pack.VersionDisplay} (You're up-to-date)" : pack.VersionDisplay));
        Grid.SetColumn(versions, 1);
        row.Children.Add(versions);

        var outdated = PackUpdater.IsExtraOutdated(pack, installed);
        var button = new Button
        {
            Content = installed is null ? "Install" : outdated ? "Update" : "Reinstall",
            Style = (Style)Application.Current.Resources[installed is null || outdated ? "AccentButtonStyle" : "DefaultButtonStyle"],
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false,
        };
        ToolTipService.SetToolTip(button, $"Downloads {FormatSize(pack.Size)} from the Vanilla RTX Extras repository.");
        button.Click += (_, _) => StartExtrasInstall(new[] { pack });
        Grid.SetColumn(button, 2);
        row.Children.Add(button);

        _extraButtons[pack.Id] = button;
        return row;
    }

    /// <summary>The loading state replaces the headings and rows, so the card never shows an empty table.</summary>
    private void ShowExtrasLoading(bool loading)
    {
        ExtrasLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ExtrasColumnHeadings.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        ExtrasList.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private static StackPanel VersionLine(string label, string value)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = 0.75, IsTextScaleFactorEnabled = false });
        line.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false,
        });
        return line;
    }

    /// <summary>Everything "Get all" would install: each pack not installed, and each one out of date.</summary>
    private List<ExtraPack> ExtrasToGet()
        => OrderedExtras().Where(p => InstalledCopy(p) is not { } i || PackUpdater.IsExtraOutdated(p, i)).ToList();

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

        var size = FormatSize(toGet.Sum(p => p.Size));
        ExtrasGetAllButton.Content = missing > 0 ? $"Get all ({size})" : $"Update all ({size})";
        ExtrasGetAllButton.IsEnabled = !_updater.IsInstallationInProgress();
    }

    private void SetExtrasButtonsEnabled(bool enabled)
    {
        foreach (var button in _extraButtons.Values) button.IsEnabled = enabled;
        ExtrasGetAllButton.IsEnabled = enabled && ExtrasToGet().Count > 0;
    }

    /// <summary>Disables everything while any install runs, and labels the extra being installed, if it is one.</summary>
    private void ApplyExtrasInstallState()
    {
        if (!_updater.IsInstallationInProgress()) return;

        SetExtrasButtonsEnabled(false);
        if (_updater.GetCurrentlyInstallingExtra() is { } id && _extraButtons.TryGetValue(id, out var button))
            button.Content = "Installing...";
    }

    private void ExtrasGetAllButton_Click(object sender, RoutedEventArgs e) => StartExtrasInstall(ExtrasToGet());

    private async void StartExtrasInstall(IReadOnlyList<ExtraPack> packs)
    {
        if (_extrasIndex is not { } index || packs.Count == 0) return;
        if (_updater.IsInstallationInProgress())
        {
            Trace.WriteLine("Installation already in progress - ignoring button click");
            return;
        }

        DisableAllInstallButtons();

        // Same as the Vanilla RTX packs: the lamp blinks for as long as anything installs, and
        // the finally is the only place it stops.
        _ = Host.BlinkingLamp(true);

        try
        {
            var (installed, failed) = await _updater.InstallExtrasAsync(index, packs, pack =>
            {
                if (_isClosing) return;
                if (_extraButtons.TryGetValue(pack.Id, out var button)) button.Content = "Installing...";
            });

            if (installed.Count > 0)
                MainWindow.Log($"Installed {string.Join(", ", installed.Select(p => $"{p.Name} {p.VersionDisplay}"))}.", MainWindow.LogLevel.Package);
            if (failed.Count > 0)
                MainWindow.Log($"Couldn't install {string.Join(", ", failed.Select(p => p.Name))}. Check your connection and try again.", MainWindow.LogLevel.Warning);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackUpdaterOverlay] Extras install failed: {ex.Message}");
        }
        finally
        {
            _ = Host.BlinkingLamp(false);

            if (!_isClosing)
            {
                await RefreshInstalledVersions();
                await FetchAndDisplayRemoteVersions();
                await LoadExtrasAsync(force: false);
            }
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
