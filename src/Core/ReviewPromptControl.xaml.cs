using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage;
using System.Diagnostics;

namespace Vanilla_RTX_App.Core;

public sealed partial class ReviewPromptControl : UserControl
{
    public event EventHandler? Closed;

    public ReviewPromptControl()
    {
        this.InitializeComponent();

        ReviewButton.Click += ReviewButton_Click;
        LaterButton.Click += LaterButton_Click;
        NeverButton.Click += NeverButton_Click;
        // Make sure the control takes full size of parent
        this.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void RootGrid_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Clicking the backdrop (outside dialog) = "Show later"
        ReviewPromptManager.ResetTimer();
        Hide();
    }

    private void DialogBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // prevent taps on the dialog itself from closing it
        e.Handled = true;
    }

    /// <summary>
    /// The Store's own rating dialog, drawn over the app. The prompt fades out first so the
    /// dialog isn't sitting on top of it. Cancelling the Store dialog counts as "Show later" -
    /// the user said yes here and then changed their mind, which is not "never". Only when the
    /// dialog can't be shown at all does this fall back to the Store's review page.
    /// </summary>
    private async void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        switch (await StoreServices.RequestReviewAsync())
        {
            case StoreServices.ReviewOutcome.Submitted:
                ReviewPromptManager.MarkAsCompleted();
                break;
            case StoreServices.ReviewOutcome.Canceled:
                ReviewPromptManager.ResetTimer();
                break;
            default:
                await StoreServices.OpenReviewPageAsync();
                ReviewPromptManager.MarkAsCompleted();
                break;
        }
    }

    private async void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewPromptManager.ResetTimer();
        Hide();
    }

    private async void NeverButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewPromptManager.NeverShowAgain();
        Hide();
    }

    private const double FADE_MS = 100;

    private Storyboard? _fade;
    private bool _hiding;

    /// <summary>
    /// Fades the prompt in over <see cref="FADE_MS"/>. Opacity is zeroed before the grid goes
    /// Visible, or the first frame draws it at full strength and the fade starts from a flash.
    /// </summary>
    public void Show()
    {
        RootGrid.Opacity = 0;
        RootGrid.Visibility = Visibility.Visible;
        AnimateOpacity(1, onCompleted: null);
    }

    /// <summary>
    /// Fades the prompt out, then collapses it and raises <see cref="Closed"/> - which takes the
    /// control out of the tree, so that has to wait for the fade or there is nothing left to see
    /// fading.
    ///
    /// <para><b>Hit testing goes off at once, and a second call is ignored.</b> The prompt stays on
    /// screen for the length of the fade, and in that window a second click - on another button, or
    /// the backdrop - would otherwise record a second, contradictory answer (Show later after Don't
    /// show again) and raise Closed twice.</para>
    /// </summary>
    public void Hide()
    {
        if (_hiding) return;
        _hiding = true;
        RootGrid.IsHitTestVisible = false;

        AnimateOpacity(0, () =>
        {
            RootGrid.Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Animates <c>RootGrid</c>'s opacity, or assigns it outright when UI animations are
    /// suspended - a storyboard under that setting is a snap with overhead, the same call every
    /// overlay in Core\Overlays makes.
    ///
    /// <para>A fade already running is stopped where it stands, so hiding mid-fade-in fades out
    /// from wherever it had got to. <c>Stop</c> reverts to the base value, hence the read-then-
    /// assign. The end value is written back on completion for the same reason: a storyboard only
    /// holds its end value, and the next <c>Stop</c> would otherwise undo it.</para>
    /// </summary>
    private void AnimateOpacity(double to, Action? onCompleted)
    {
        var current = RootGrid.Opacity;
        _fade?.Stop();
        _fade = null;

        if (EnvironmentVariables.Persistent.SuspendUIAnimations)
        {
            RootGrid.Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        RootGrid.Opacity = current;

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FADE_MS)),
            EasingFunction = new QuadraticEase { EasingMode = to > current ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(anim, RootGrid);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            RootGrid.Opacity = to;
            onCompleted?.Invoke();
        };

        _fade = sb;
        sb.Begin();
    }
}

public static class ReviewPromptManager
{
    private static readonly string FIRST_LAUNCH_KEY = "ReviewPromptFirstLaunchTime";
    private static readonly string DONT_SHOW_KEY = $"ReviewPromptDontShow_{EnvironmentVariables.appVersionMajorMinor}"; // Ask again only with Major or Minor updates (not new builds/revisions)

    private static readonly string LAST_PROMPT_KEY = "ReviewPromptLastPromptTime";
    private const double HOURS_BEFORE_PROMPT = 100; // Hours after first launch before the first ask, and after each "Show later" before the next

    // Launches counted since the same moment the hours are measured from, and both must be met.
    // Hours alone can pass with the app installed and never opened again; launches alone can
    // pile up in one afternoon. Together they mean the app has been come back to over days.
    // Reset with the timer, so a re-ask after "Show later" needs the same evidence again.
    private static readonly string LAUNCH_COUNT_KEY = "ReviewPromptLaunchCount";
    private const int LAUNCHES_BEFORE_PROMPT = 4;
    private const int SHOW_DELAY_Milisecs = 0; // delay to show it after being called

    private static void CleanupOldVersionKeys()
    {
        try
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var keysToRemove = localSettings.Values.Keys
                .Where(k => k.StartsWith("ReviewPromptDontShow_") && k != DONT_SHOW_KEY)
                .ToList();
            foreach (var key in keysToRemove)
                localSettings.Values.Remove(key);
        }
        catch
        {
            Trace.WriteLine("[ReviewPrompt] Failed to clear orhphaned ReviewPromptDontShow_ keys");
        }
    }

    private static ReviewPromptControl? _currentPrompt;
    private static Panel? _rootPanel;

    /// <summary>
    /// Initialize and show the review prompt if conditions are met.
    /// Call this once on app startup.
    /// </summary>
    /// <param name="rootPanel">The root panel of your MainWindow (e.g., the main Grid)</param>
    public static async Task InitializeAsync(Panel rootPanel)
    {
        Trace.WriteLine("=== ReviewPrompt: InitializeAsync called ===");
        _rootPanel = rootPanel;

        if (_rootPanel == null)
        {
            Trace.WriteLine("ERROR: rootPanel is NULL!");
            return;
        }

        Trace.WriteLine($"Root panel type: {_rootPanel.GetType().Name}");

        CleanupOldVersionKeys();
        // Record first launch if not already recorded
        await RecordFirstLaunchIfNeededAsync();

        // Check if we should show the prompt
        bool shouldShow = await ShouldShowPromptAsync();
        Trace.WriteLine($"Should show prompt: {shouldShow}");

        if (shouldShow)
        {
            Trace.WriteLine($"Waiting {SHOW_DELAY_Milisecs} seconds before showing...");
            await Task.Delay(TimeSpan.FromMilliseconds(SHOW_DELAY_Milisecs));
            Trace.WriteLine("Calling ShowPrompt()...");
            ShowPrompt();
        }
    }

    private static async Task RecordFirstLaunchIfNeededAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        if (!localSettings.Values.ContainsKey(FIRST_LAUNCH_KEY))
        {
            var now = DateTime.UtcNow.Ticks;
            localSettings.Values[FIRST_LAUNCH_KEY] = now;
            Trace.WriteLine($"First launch recorded: {now} ticks ({DateTime.UtcNow})");
        }
        else
        {
            Trace.WriteLine($"First launch already recorded: {localSettings.Values[FIRST_LAUNCH_KEY]} ticks");
        }

        var launches = (localSettings.Values[LAUNCH_COUNT_KEY] is int n ? n : 0) + 1;
        localSettings.Values[LAUNCH_COUNT_KEY] = launches;
        Trace.WriteLine($"Launches counted toward the review prompt: {launches}");
    }

    private static async Task<bool> ShouldShowPromptAsync()
    {
        var localSettings = ApplicationData.Current.LocalSettings;

        // Check if user said "Don't show again"
        if (localSettings.Values.ContainsKey(DONT_SHOW_KEY))
        {
            Trace.WriteLine("Don't show key exists - returning false");
            return false;
        }

        // Get first launch time
        if (!localSettings.Values.ContainsKey(FIRST_LAUNCH_KEY))
        {
            Trace.WriteLine("No first launch key - returning false");
            return false;
        }

        var firstLaunchTicks = localSettings.Values[FIRST_LAUNCH_KEY];
        if (firstLaunchTicks == null || !(firstLaunchTicks is long))
        {
            Trace.WriteLine($"Invalid first launch ticks: {firstLaunchTicks}");
            return false;
        }

        var firstLaunch = new DateTime((long)firstLaunchTicks, DateTimeKind.Utc);
        Trace.WriteLine($"First launch: {firstLaunch} UTC");

        // Check if time has passed since first launch (or last "Show later")
        DateTime checkTime = firstLaunch;

        if (localSettings.Values.ContainsKey(LAST_PROMPT_KEY))
        {
            var lastPromptTicks = localSettings.Values[LAST_PROMPT_KEY];
            if (lastPromptTicks is long)
            {
                var lastPrompt = new DateTime((long)lastPromptTicks, DateTimeKind.Utc);
                checkTime = lastPrompt;
                Trace.WriteLine($"Using last prompt time: {lastPrompt} UTC");
            }
        }

        var hoursSince = (DateTime.UtcNow - checkTime).TotalHours;
        Trace.WriteLine($"Hours since check time: {hoursSince:F1} (need {HOURS_BEFORE_PROMPT})");
        Trace.WriteLine($"Current UTC: {DateTime.UtcNow}");

        var launches = localSettings.Values[LAUNCH_COUNT_KEY] is int n ? n : 0;
        Trace.WriteLine($"Launches since check time: {launches} (need {LAUNCHES_BEFORE_PROMPT})");

        return hoursSince >= HOURS_BEFORE_PROMPT && launches >= LAUNCHES_BEFORE_PROMPT;
    }

    private static void ShowPrompt()
    {
        Trace.WriteLine("=== ShowPrompt() called ===");

        if (_rootPanel == null)
        {
            Trace.WriteLine("ERROR: _rootPanel is NULL in ShowPrompt!");
            return;
        }

        if (_currentPrompt != null)
        {
            Trace.WriteLine("Prompt already showing!");
            return;
        }

        Trace.WriteLine("Creating new ReviewPromptControl...");
        _currentPrompt = new ReviewPromptControl();

        // ensure it appears on top
        Canvas.SetZIndex(_currentPrompt, 9999);

        // span all columns and rows beucase your mainwindow has 2 sections
        if (_rootPanel is Grid)
        {
            Grid.SetColumnSpan(_currentPrompt, int.MaxValue);
            Grid.SetRowSpan(_currentPrompt, int.MaxValue);
            Trace.WriteLine("Set ColumnSpan and RowSpan to cover entire Grid");
        }

        _currentPrompt.Closed += (s, e) =>
        {
            Trace.WriteLine("Prompt closed event fired");
            if (_rootPanel.Children.Contains(_currentPrompt))
            {
                _rootPanel.Children.Remove(_currentPrompt);
            }
            _currentPrompt = null;
        };

        Trace.WriteLine("Adding prompt to root panel...");
        _rootPanel.Children.Add(_currentPrompt);

        Trace.WriteLine($"Current children count: {_rootPanel.Children.Count}");

        Trace.WriteLine("Calling Show() on prompt...");
        _currentPrompt.Show();

        Trace.WriteLine("=== ShowPrompt() complete ===");
    }

    internal static void ResetTimer()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[LAST_PROMPT_KEY] = DateTime.UtcNow.Ticks;
        localSettings.Values[LAUNCH_COUNT_KEY] = 0;
        Trace.WriteLine("Timer reset - will show again after delay");
    }

    internal static void NeverShowAgain()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        Trace.WriteLine("Never show again flag set");
    }

    internal static void MarkAsCompleted()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[DONT_SHOW_KEY] = true;
        Trace.WriteLine("Review completed - will not show again");
    }

    /// <summary>
    /// Debug method to clear all settings and force the prompt to show on next launch
    /// </summary>
    public static void ResetForTesting()
    {
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values.Remove(FIRST_LAUNCH_KEY);
        localSettings.Values.Remove(DONT_SHOW_KEY);
        localSettings.Values.Remove(LAST_PROMPT_KEY);
        localSettings.Values.Remove(LAUNCH_COUNT_KEY);
        Trace.WriteLine("All review prompt settings cleared for testing");
    }
}
