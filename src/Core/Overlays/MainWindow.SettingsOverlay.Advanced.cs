using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// The settings panel's Advanced section: the content source addresses and every value in
/// <see cref="Tunables"/>, behind a button that takes three clicks.
///
/// <para><b>The three clicks are the resistance the section is meant to have</b>: nothing in here
/// is needed to use the app, and most of it can make generated packs worse. The count lives for the
/// session - once someone has asked for it they aren't made to ask again until the app restarts.</para>
///
/// <para><b>Rows are built from <see cref="Tunables.All"/></b>, grouped by section in registry
/// order, so a value added there appears here with no XAML. Each edit writes straight through
/// <see cref="Tunables.Set"/>, which clamps and saves, so there is nothing to apply. A changed
/// value says so beside its label, and its reset button is live only while it differs.</para>
/// </summary>
public sealed partial class SettingsOverlay
{
    private const int ClicksToRevealAdvanced = 3;
    private static int _advancedClicks;
    private static bool AdvancedRevealed => _advancedClicks >= ClicksToRevealAdvanced;
    private static bool _advancedHidden;

    private bool _suppressTunableEvents;

    private void ShowAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedRevealed)
            _advancedHidden = !_advancedHidden;
        else
            _advancedClicks++;

        ApplyAdvancedState();
    }

    /// <summary>Paints the button's label and the panel for the current reveal state. Called on every open.</summary>
    private void ApplyAdvancedState()
    {
        var remaining = ClicksToRevealAdvanced - _advancedClicks;
        ShowAdvancedButtonText.Text = AdvancedRevealed
            ? (_advancedHidden ? "Show advanced settings" : "Hide advanced settings")
            : remaining == ClicksToRevealAdvanced ? "Show advanced settings"
            : remaining == 1 ? "Click once more to show advanced settings"
            : $"Click {remaining} more times to show advanced settings";

        var visible = AdvancedRevealed && !_advancedHidden;
        AdvancedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) BuildTunableRows();
    }

    private void BuildTunableRows()
    {
        _suppressTunableEvents = true;
        try
        {
            TunableRows.Children.Clear();
            foreach (var group in Tunables.All.GroupBy(t => t.Section))
            {
                var section = new StackPanel { Spacing = 10 };
                section.Children.Add(new TextBlock { Text = group.Key, Style = (Style)Resources["SettingsItemLabelStyle"] });
                foreach (var tunable in group)
                    section.Children.Add(BuildTunableRow(tunable));
                TunableRows.Children.Add(section);
            }
        }
        finally
        {
            _suppressTunableEvents = false;
        }
    }

    private Grid BuildTunableRow(Tunable tunable)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var changed = new TextBlock
        {
            Text = "changed",
            Style = (Style)Resources["SettingsHintTextStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = tunable.IsModified ? Visibility.Visible : Visibility.Collapsed,
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = tunable.Label, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextScaleFactorEnabled = false });
        title.Children.Add(changed);

        var description = tunable.Description
            + $" Built-in: {Tunables.Format(tunable, tunable.Default)}."
            + (tunable.NeedsRestart ? " Takes effect after the app restarts." : "");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = description, Style = (Style)Resources["SettingsHintTextStyle"] });
        grid.Children.Add(text);

        var box = new NumberBox
        {
            Minimum = tunable.Min,
            Maximum = tunable.Max,
            Value = tunable.Value,
            SmallChange = tunable.IsInteger ? 1 : StepFor(tunable),
            LargeChange = tunable.IsInteger ? Math.Max(1, Math.Round((tunable.Max - tunable.Min) / 10)) : StepFor(tunable) * 10,
            Width = 112,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false,
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow(),
        };
        ToolTipService.SetToolTip(box, $"{Tunables.Format(tunable, tunable.Min)} to {Tunables.Format(tunable, tunable.Max)}");
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var reset = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = tunable.IsModified,
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtle) && subtle is Style style)
            reset.Style = style;
        ToolTipService.SetToolTip(reset, $"Put back the built-in {Tunables.Format(tunable, tunable.Default)}.");
        Grid.SetColumn(reset, 2);
        grid.Children.Add(reset);

        void Reflect()
        {
            changed.Visibility = tunable.IsModified ? Visibility.Visible : Visibility.Collapsed;
            reset.IsEnabled = tunable.IsModified;
        }

        box.ValueChanged += (_, args) =>
        {
            if (_suppressTunableEvents) return;
            // NaN is what a NumberBox holds while its text is empty; that is no value at all, not zero.
            if (double.IsNaN(args.NewValue)) return;
            Tunables.Set(tunable, args.NewValue);
            Reflect();
        };
        reset.Click += (_, _) =>
        {
            Tunables.Reset(tunable);
            _suppressTunableEvents = true;
            box.Value = tunable.Value;
            _suppressTunableEvents = false;
            Reflect();
        };

        return grid;
    }

    /// <summary>A spin step that moves a value visibly without taking dozens of clicks across its range.</summary>
    private static double StepFor(Tunable t)
    {
        var span = t.Max - t.Min;
        return span <= 1 ? 0.01 : span <= 10 ? 0.05 : span <= 50 ? 0.5 : 1;
    }

    private void ResetTunablesButton_Click(object sender, RoutedEventArgs e)
    {
        Tunables.ResetAll();
        BuildTunableRows();
        MainWindow.Log("Every internal value is back to its built-in setting.", MainWindow.LogLevel.Reset);
        _ = _host?.BlinkingLamp(true, true, 0.0);
    }
}
