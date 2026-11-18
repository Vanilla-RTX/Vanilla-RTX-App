using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Modules;
using Vanilla_RTX_App.Modules.PackUpdater;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The settings panel's "Activate packs on launch" list - the editor for
/// <see cref="Persistent.PacksToActivate"/>. The work happens in <see cref="PackActivation"/> at
/// launch time; this only edits the ordered list and shows it against what is installed.
///
/// <para><b>The list is one setting for both editions</b>, and each launch resolves it against the
/// edition it starts. So a row reads against the targeted edition's installs - a pack installed
/// only for the other one shows as not installed, and is still activated when that one launches.</para>
///
/// <para><b>Every edit commits immediately and rebuilds every row</b>, the same "the UI is the
/// model" rule the launch options follow. The list is a handful of rows, so a rebuild is cheaper
/// than keeping row state in step with reorders.</para>
/// </summary>
public sealed partial class SettingsOverlay
{
    private List<ActivationEntry> _activationEntries = new();
    private List<InstalledPack> _installedPacks = new();

    /// <summary>Re-reads the stored list and what is installed, then rebuilds. Called on every open.</summary>
    private void RefreshActivationList()
    {
        _activationEntries = PackActivation.Parse(Persistent.PacksToActivate).ToList();
        try { _installedPacks = PackActivation.FindInstalledPacks(Persistent.IsTargetingPreview); }
        catch { _installedPacks = new(); }
        BuildActivationRows();
    }

    private InstalledPack? Installed(string uuid)
        => _installedPacks.FirstOrDefault(p => p.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pack's name when it isn't installed here: a base pack's own, an extra's from the cached Extras index, or its UUID.</summary>
    private static string NameOfMissing(string uuid)
        => PackActivation.BasePackName(uuid)
        ?? PackUpdater.ReadCachedExtrasIndex()?.Packs.FirstOrDefault(p => p.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))?.Name
        ?? uuid;

    private void BuildActivationRows()
    {
        ActivationRows.Children.Clear();

        for (var i = 0; i < _activationEntries.Count; i++)
            ActivationRows.Children.Add(BuildActivationRow(i));

        ActivationEmptyText.Visibility = _activationEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid BuildActivationRow(int index)
    {
        var entry = _activationEntries[index];
        var pack = Installed(entry.Uuid);

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = pack?.Name ?? NameOfMissing(entry.Uuid),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = false,
        });
        text.Children.Add(new TextBlock
        {
            Text = pack is null
                ? $"Not installed for {MinecraftUserDataLocator.GetVersionDisplayName(Persistent.IsTargetingPreview)}"
                : pack.VersionDisplay,
            Style = (Style)Resources["SettingsHintTextStyle"],
        });
        grid.Children.Add(text);

        if (pack is { Subpacks.Count: > 0 })
        {
            var options = new List<(string Value, string Label)> { (PackActivation.PackDefault, "Pack's default") };
            if (PackActivation.HasBasePackSubpacks(pack))
                options.Add((PackActivation.MatchBasePack, "Match the Vanilla RTX pack"));
            options.AddRange(pack.Subpacks.Select(s => (s.FolderName, s.Name)));

            var combo = new ComboBox
            {
                MaxWidth = 180,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false,
                ItemsSource = options.Select(o => o.Label).ToList(),
                SelectedIndex = Math.Max(0, options.FindIndex(o => o.Value == entry.Subpack)),
            };
            ToolTipService.SetToolTip(combo, "Which of this pack's subpacks to activate. \"Match the Vanilla RTX pack\" picks the one made for whichever Vanilla RTX pack gets activated with it.");
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                _activationEntries[index] = entry with { Subpack = options[combo.SelectedIndex].Value };
                CommitActivation(rebuild: false);
            };
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
        }

        AddRowButton(grid, 2, "", "Move up - a higher pack takes priority over the ones below it.", index > 0, () => Move(index, -1));
        AddRowButton(grid, 3, "", "Move down.", index < _activationEntries.Count - 1, () => Move(index, +1));
        AddRowButton(grid, 4, "", "Remove from the list. Launching then leaves this pack as the game has it.", true, () =>
        {
            _activationEntries.RemoveAt(index);
            CommitActivation(rebuild: true);
        });

        return grid;
    }

    private static void AddRowButton(Grid grid, int column, string glyph, string tooltip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtle) && subtle is Style style)
            button.Style = style;
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _activationEntries.Count) return;
        (_activationEntries[index], _activationEntries[target]) = (_activationEntries[target], _activationEntries[index]);
        CommitActivation(rebuild: true);
    }

    private void CommitActivation(bool rebuild)
    {
        Persistent.PacksToActivate = PackActivation.Serialize(_activationEntries);
        if (rebuild) BuildActivationRows();
    }

    private void ActivatePacksSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks) return;
        Persistent.ActivatePacksOnLaunch = ActivatePacksSwitch.IsOn;
    }

    /// <summary>
    /// Fills the Add pack menu with every installed pack not already listed - the Vanilla RTX packs
    /// first, then the rest by name - built on open so it always reflects what is installed now.
    /// </summary>
    private void AddActivationPackFlyout_Opening(object sender, object e)
    {
        AddActivationPackFlyout.Items.Clear();

        var candidates = _installedPacks
            .Where(p => !_activationEntries.Any(a => a.Uuid.Equals(p.Uuid, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => PackActivation.IsBasePack(p.Uuid) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            AddActivationPackFlyout.Items.Add(new MenuFlyoutItem { Text = "Every installed pack is already on the list", IsEnabled = false });
            return;
        }

        foreach (var pack in candidates)
        {
            var item = new MenuFlyoutItem { Text = $"{pack.Name}  ({pack.VersionDisplay})" };
            item.Click += (_, _) =>
            {
                var subpack = PackActivation.HasBasePackSubpacks(pack) ? PackActivation.MatchBasePack : PackActivation.PackDefault;
                _activationEntries.Add(new ActivationEntry(pack.Uuid, subpack));
                CommitActivation(rebuild: true);
            };
            AddActivationPackFlyout.Items.Add(item);
        }
    }

    private void ResetActivationPacksButton_Click(object sender, RoutedEventArgs e)
    {
        _activationEntries = PackActivation.DefaultEntries.ToList();
        CommitActivation(rebuild: true);
        MainWindow.Log("Packs to activate on launch restored to defaults: Vanilla RTX, then Normals, then Opus - whichever is installed first.", MainWindow.LogLevel.Reset);
        _ = _host?.BlinkingLamp(true, true, 0.0);
    }
}
