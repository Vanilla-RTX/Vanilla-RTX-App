using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.System;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// A full-window WebView2 overlay that lets the user browse a real, external site (TechPowerUp
/// for DLSS DLLs, bedrock.graphics/creator for BetterRTX presets) from inside the app, and hands
/// whatever they downloaded there back to the caller in one batch the moment they close it.
///
/// <para><b>Why this exists.</b> Neither TechPowerUp nor bedrock.graphics has an API worth
/// scraping - one is a download mirror, the other a build-your-own-preset tool - so there is no
/// way to fetch these files directly. The reliable middle ground is to let the user do exactly
/// what they would do in a real browser without leaving the app, and watch the one folder their
/// downloads land in, so the file comes back on its own instead of being hunted for.</para>
///
/// <para><b>Deliberately detachable.</b> This control knows nothing about DLSS or BetterRTX - it
/// takes a URL, a set of file extensions to watch for, static instruction text, and a callback,
/// and that's the entire contract. Passing an empty extension set and a no-op callback turns it
/// into a plain in-app page viewer with nothing watched at all - though for a page that never
/// hands anything back at all, <see cref="MarkdownOverlay"/> is almost always the better fit; it
/// shares this control's <see cref="OverlayHeaderBar"/> chrome but renders GitHub markdown
/// directly instead of paying for a full WebView2 to show what's usually just a README.</para>
///
/// <para><b>Only watched extensions ever get intercepted.</b> A download whose extension isn't in
/// the watched set is left completely alone - it downloads to the user's real Downloads folder
/// through WebView2's own normal UI, exactly like it would in an actual browser. Silently
/// swallowing an unrelated download into a staging folder that gets deleted on close would mean a
/// user occasionally loses a file with no idea why; this way nothing is ever hidden from them.</para>
///
/// <para><b>A download only ever gets imported if it actually finished.</b> The downloads shelf
/// tracks every watched download by its own <see cref="CoreWebView2DownloadOperation"/>, not by
/// scanning the staging folder for files that merely have the right extension - Chromium's
/// download manager can leave a same-named file sitting there mid-transfer, and a half-written
/// zip handed to <c>ImportZipAsync</c> is worse than not importing it at all. Closing the overlay
/// while something is still in flight cancels it outright (the same thing closing a browser tab
/// does to its downloads) rather than racing the staging-folder cleanup against a write still in
/// progress.</para>
/// </summary>
public sealed partial class WebImportOverlay : UserControl
{
    private bool _isOpen;
    private bool _webViewReady;

    /// <summary>Set when <see cref="EnsureWebViewAsync"/> fails specifically because the shared Evergreen WebView2 Runtime isn't installed on this machine, rather than some other init failure - see <see cref="GetRuntimeButton_Click"/>.</summary>
    private bool _runtimeMissing;

    private Storyboard? _fadeStoryboard;

    private string _stagingFolder = string.Empty;
    private string _lastUrl = string.Empty;
    private string _lastStagingTag = string.Empty;
    private IReadOnlyList<string> _watchedExtensions = Array.Empty<string>();
    private Func<IReadOnlyList<string>, Task>? _onFilesReady;

    /// <summary>Every download this session has redirected, so a still-InProgress one can be cancelled on close instead of racing the staging-folder cleanup.</summary>
    private readonly List<CoreWebView2DownloadOperation> _liveDownloads = new();

    /// <summary>Paths WebView2 itself has confirmed Completed - the only things FinalizeImportsAsync will ever hand to the caller.</summary>
    private readonly HashSet<string> _completedPaths = new(StringComparer.OrdinalIgnoreCase);

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;
    /// <summary>
    /// Fade duration for the whole overlay, bounded at both ends: below ~100ms a 60Hz display
    /// has too few frames left for the ease to read as anything but a snap, and much above it
    /// the overlay feels slow to open on a high-refresh display. 100ms is 6 frames at 60Hz
    /// and 14 at 144Hz.
    /// </summary>
    private const double FADE_MS = 125;

    public WebImportOverlay()
    {
        InitializeComponent();

        Header.TitleClick += HeaderTitleLink_Click;
        Header.BackClick += BackButton_Click;
        Header.ForwardClick += ForwardButton_Click;
        Header.ReloadClick += ReloadButton_Click;
        Header.CloseClick += CloseButton_Click;
        Header.SetNavButtonsVisible(true);
    }

    /// <summary>
    /// Opens the overlay and navigates to <paramref name="url"/>. Any download whose extension is
    /// in <paramref name="watchedExtensions"/> is staged in a folder keyed by
    /// <paramref name="stagingTag"/> (so two overlay instances - e.g. the DLSS and BetterRTX
    /// windows both open at once - can never collide) and shown live in the downloads shelf; the
    /// full set of completed matches is handed to <paramref name="onFilesReady"/> in one call once
    /// the user closes the overlay. If nothing completed, <paramref name="onFilesReady"/> is never
    /// called at all - a session where the user just looked around and downloaded nothing (or a
    /// plain-viewer call that watches nothing at all) behaves exactly as if this feature didn't
    /// exist. <paramref name="guideText"/> is a short, static sentence telling the user what
    /// "done" means for this particular site (e.g. when to click Done) - pass an empty string for
    /// a plain page viewer with nothing to guide.
    /// </summary>
    /// <summary>True while the page is up. Its host reads this to get out of its way.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Raised whenever this stops being on screen, by either close path. A host that dressed
    /// itself differently while this was up - the feature modules hide their own close button,
    /// which would otherwise sit on top of this one's - uses it to put itself back.
    /// </summary>
    public event EventHandler? Dismissed;

    public void Show(
        string url,
        string title,
        string glyph,
        string guideText,
        string stagingTag,
        IReadOnlyList<string> watchedExtensions,
        Func<IReadOnlyList<string>, Task> onFilesReady)
    {
        if (_isOpen) return;
        _isOpen = true;

        Header.SetTitleText(title);
        Header.SetIcon(glyph);
        Header.SetGuideText(guideText);
        _watchedExtensions = watchedExtensions;
        _onFilesReady = onFilesReady;
        _lastUrl = url;
        _lastStagingTag = stagingTag;

        // Nothing watched means there's nothing this page could hand back - a plain viewer
        // (the bug tracker, a README) rather than an import flow, so the button reads
        // "Return" instead of "Done".
        var isPlainViewer = watchedExtensions.Count == 0;
        Header.SetCloseButton(
            isPlainViewer ? "" : "",
            isPlainViewer ? "Return" : "Done",
            isPlainViewer
                ? "Close this page"
                : "Close - anything you downloaded here will be imported automatically");

        _liveDownloads.Clear();
        _completedPaths.Clear();
        DownloadsListPanel.Children.Clear();
        DownloadsShelf.Visibility = Visibility.Collapsed;

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);

        // Forces a layout pass before WebView2 initialization gets anywhere near it - the
        // cache-hit path can resolve synchronously with no intervening yield back to the
        // dispatcher, and a WebView2 with no real bounds can fail to initialize natively.
        UpdateLayout();

        _ = LoadAsync(url, stagingTag);
    }

    private void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;
        Dismissed?.Invoke(this, EventArgs.Empty);

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () =>
        {
            Visibility = Visibility.Collapsed;
            _ = FinalizeImportsAsync();
        });
    }

    /// <summary>
    /// The host module's own teardown should call this. The Done button is the normal close
    /// path, but nothing stops a user from closing the app, or the module, while still
    /// mid-browse. Without this, a download that landed
    /// seconds before that would sit in the staging folder forever, never imported and never
    /// cleaned up - silently losing exactly the file the user just fetched. No fade-out here; the
    /// host is already on its way down. The overlay still goes back to its hidden state, because a
    /// host that is only hidden rather than torn down (the settings panel) shows it again on its
    /// next open otherwise.
    /// </summary>
    public void CloseIfOpen()
    {
        if (!_isOpen) return;
        _isOpen = false;
        Dismissed?.Invoke(this, EventArgs.Empty);

        _fadeStoryboard?.Stop();
        Opacity = 0;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        _ = FinalizeImportsAsync();
    }

    // =========================================================================
    // Loading the page
    // =========================================================================

    private async Task LoadAsync(string url, string stagingTag)
    {
        ShowLoading("Loading...");
        PrepareStagingFolder(stagingTag);

        await EnsureWebViewAsync();
        if (!_isOpen) return;

        if (!_webViewReady)
        {
            // Neither failure is the user's fault and neither is the end of the road: this
            // overlay only ever existed to save a round trip through the browser, so when it
            // can't run, the browser is still right there and both modules that use it have a
            // manual import button. Say that, rather than leaving them at a dead end.
            GetRuntimeButton.Content = _runtimeMissing ? "Install WebView2" : "Repair WebView2";

            // A plain viewer has nothing to import, so there is no Add button to point at.
            var alternative = _watchedExtensions.Count == 0
                ? "open the page in your normal browser."
                : "open the page in your normal browser, download what you need there, and bring it in with this window's own Add button.";

            ShowError(_runtimeMissing
                ? "This page is shown using the Microsoft Edge WebView2 Runtime, which doesn't seem to be installed on this PC.\n\n" +
                  "Install it and click Try Again - or " + alternative
                : "The embedded browser couldn't start. Repairing or reinstalling the Microsoft Edge WebView2 Runtime usually fixes this.\n\n" +
                  "Click Try Again once you have - or " + alternative,
                offerRuntime: true);
            return;
        }

        await NavigateAndWaitFirstAsync(url);
    }

    /// <summary>
    /// Waits for the very first navigation only, so the loading spinner covers the initial page
    /// load but never reappears over ordinary in-site browsing afterward - a link click on
    /// TechPowerUp shouldn't cover the page with our own spinner while the browser handles it
    /// exactly like it always does.
    /// </summary>
    private async Task NavigateAndWaitFirstAsync(string url)
    {
        var navigationDone = new TaskCompletionSource<bool>();
        void OnNavigationCompleted(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs a) =>
            navigationDone.TrySetResult(a.IsSuccess);

        ImportWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        try
        {
            ImportWebView.CoreWebView2.Navigate(url);
            var success = await navigationDone.Task;

            if (!_isOpen) return;

            if (success) ShowContent();
            else ShowError("Couldn't load the page. Check your internet connection and try again.");
        }
        finally
        {
            ImportWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingState.Visibility = Visibility.Visible;
        ErrorState.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// <paramref name="offerRuntime"/> is for the two failures a WebView2 install can fix; the
    /// way out through the real browser is offered whatever went wrong, since by this point the
    /// URL is always known and a page this can't show is one the user's own browser still can.
    /// </summary>
    private void ShowError(string text, bool offerRuntime = false)
    {
        ErrorText.Text = text;
        GetRuntimeButton.Visibility = offerRuntime ? Visibility.Visible : Visibility.Collapsed;
        OpenInBrowserButton.Visibility = string.IsNullOrEmpty(_lastUrl) ? Visibility.Collapsed : Visibility.Visible;
        ErrorState.Visibility = Visibility.Visible;
        LoadingState.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-runs the whole load sequence, not just navigation - if the very first attempt failed
    /// because <see cref="EnsureWebViewAsync"/> itself never succeeded (no WebView2 to navigate
    /// with at all), retrying only the navigation would just throw on a null CoreWebView2.
    /// </summary>
    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_lastUrl, _lastStagingTag);

    private void GetRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"));

    /// <summary>
    /// Hands the page to the user's real browser and gets out of the way. Closing is the point
    /// rather than a side effect: what they do next - download the file, then import it - needs
    /// the window underneath this one, and an overlay stuck on an error screen is in the way of
    /// it. The close path is the ordinary one, so a session that did manage to download
    /// something before failing still has it imported.
    /// </summary>
    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastUrl))
            _ = Launcher.LaunchUriAsync(new Uri(_lastUrl));

        Close();
    }

    /// <summary>Opens wherever the embedded browser currently is - not necessarily the entry URL - in the user's real browser.</summary>
    private void HeaderTitleLink_Click(object sender, RoutedEventArgs e)
    {
        var url = _webViewReady ? ImportWebView.CoreWebView2.Source : _lastUrl;
        if (!string.IsNullOrEmpty(url))
            _ = Launcher.LaunchUriAsync(new Uri(url));
    }

    // =========================================================================
    // WebView2
    // =========================================================================

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        try
        {
            // A dedicated profile folder under LocalState - this overlay can be open on a
            // module window at the same time another top-level window is open elsewhere, and
            // two CoreWebView2Environments pointed at the same user data folder from the same
            // process is not a combination worth risking.
            var userDataFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "WebView2_WebImport");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, userDataFolder, options);
            await ImportWebView.EnsureCoreWebView2Async(env);

            ImportWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ImportWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            ImportWebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
            ImportWebView.CoreWebView2.HistoryChanged += (_, _) => UpdateNavButtons();

            _webViewReady = true;
        }
        catch (Exception ex)
        {
            // 0x80070002 (ERROR_FILE_NOT_FOUND) is what CreateCoreWebView2EnvironmentWithOptions
            // returns when the shared Evergreen WebView2 Runtime isn't installed at all - checked
            // by HRESULT rather than catching WebView2RuntimeNotFoundException by type, since that
            // keeps working regardless of which WebView2 SDK version ends up referenced.
            _runtimeMissing = ex.HResult == unchecked((int)0x80070002);

            Trace.WriteLine($"[WebImportOverlay] WebView2 init failed: {ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}");
            Trace.WriteLine(ex.ToString());
        }
    }

    /// <summary>
    /// Popups (target="_blank" links - social share buttons, "Sign in", etc.) open in the user's
    /// real browser rather than spawning a second in-app tab this overlay has no chrome for.
    /// </summary>
    private void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _ = Launcher.LaunchUriAsync(new Uri(e.Uri));
    }

    /// <summary>
    /// Only a download whose extension is in the watched set gets redirected into the staging
    /// folder and tracked in the shelf. Everything else is left completely alone - no
    /// <see cref="CoreWebView2DownloadStartingEventArgs.Handled"/>, no touched
    /// <see cref="CoreWebView2DownloadStartingEventArgs.ResultFilePath"/> - so it downloads to the
    /// user's real Downloads folder through WebView2's own default UI exactly like it would in an
    /// actual browser. The alternative - capturing every download unconditionally - means an
    /// unrelated file (an export, a screenshot, anything not a DLSS/BetterRTX file) would vanish
    /// into a folder that gets deleted on close with no trace and no way back.
    /// </summary>
    private void CoreWebView2_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var suggestedName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggestedName))
                return;

            var ext = Path.GetExtension(suggestedName);
            if (!_watchedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(_stagingFolder);

            var destPath = Path.Combine(_stagingFolder, suggestedName);
            var nameNoExt = Path.GetFileNameWithoutExtension(destPath);
            var counter = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(_stagingFolder, $"{nameNoExt}-{counter}{ext}");
                counter++;
            }

            e.ResultFilePath = destPath;
            e.Handled = true;

            Trace.WriteLine($"[WebImportOverlay] Download starting -> {destPath}");

            var op = e.DownloadOperation;
            _liveDownloads.Add(op);

            var row = AddDownloadRow(suggestedName);

            void UpdateProgress()
            {
                if (op.TotalBytesToReceive > 0)
                {
                    var percent = (int)(op.BytesReceived * 100 / op.TotalBytesToReceive);
                    row.ProgressBar.IsIndeterminate = false;
                    row.ProgressBar.Value = percent;
                    row.ProgressText.Text = $"{percent}%";
                }
                else
                {
                    row.ProgressText.Text = $"{op.BytesReceived / 1024.0 / 1024.0:0.0} MB";
                }
            }

            op.BytesReceivedChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateProgress);
            op.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => OnDownloadStateChanged(op, row, destPath));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Error redirecting download: {ex.Message}");
        }
    }

    private void OnDownloadStateChanged(CoreWebView2DownloadOperation op, DownloadRow row, string path)
    {
        switch (op.State)
        {
            case CoreWebView2DownloadState.Completed:
                _liveDownloads.Remove(op);
                _completedPaths.Add(path);

                row.ProgressBar.IsIndeterminate = false;
                row.ProgressBar.Value = 100;
                row.StatusHost.Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 84, 178, 96))
                };
                row.ProgressText.Text = "Downloaded";
                Trace.WriteLine($"[WebImportOverlay] ✓ Download completed: {row.FileName}");
                break;

            case CoreWebView2DownloadState.Interrupted:
                _liveDownloads.Remove(op);

                row.ProgressBar.Visibility = Visibility.Collapsed;
                row.StatusHost.Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 87, 76))
                };
                row.ProgressText.Text = op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                    ? "Cancelled"
                    : "Failed";
                Trace.WriteLine($"[WebImportOverlay] ✗ Download interrupted: {row.FileName} ({op.InterruptReason})");
                break;
        }
    }

    /// <summary>Cancels anything still transferring rather than letting it race the staging-folder cleanup that follows - the same thing closing a browser tab does to its own downloads.</summary>
    private void CancelLiveDownloads()
    {
        foreach (var op in _liveDownloads)
        {
            try
            {
                if (op.State == CoreWebView2DownloadState.InProgress)
                    op.Cancel();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WebImportOverlay] Error cancelling in-flight download: {ex.Message}");
            }
        }
        _liveDownloads.Clear();
    }

    // =========================================================================
    // Downloads shelf
    // =========================================================================

    private readonly struct DownloadRow
    {
        public required string FileName { get; init; }
        public required ContentControl StatusHost { get; init; }
        public required ProgressBar ProgressBar { get; init; }
        public required TextBlock ProgressText { get; init; }
    }

    private DownloadRow AddDownloadRow(string fileName)
    {
        DownloadsShelf.Visibility = Visibility.Visible;

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statusHost = new ContentControl
        {
            Width = 22,
            Height = 22,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new ProgressRing { IsActive = true, Width = 16, Height = 16 }
        };
        Grid.SetColumn(statusHost, 0);
        row.Children.Add(statusHost);

        var textPanel = new StackPanel { Spacing = 4 };
        textPanel.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsTextScaleFactorEnabled = false
        });

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Height = 4
        };
        textPanel.Children.Add(progressBar);

        var progressText = new TextBlock
        {
            Text = "Downloading...",
            FontSize = 11,
            Opacity = 0.65,
            IsTextScaleFactorEnabled = false
        };
        textPanel.Children.Add(progressText);

        Grid.SetColumn(textPanel, 1);
        row.Children.Add(textPanel);

        DownloadsListPanel.Children.Add(row);

        return new DownloadRow { FileName = fileName, StatusHost = statusHost, ProgressBar = progressBar, ProgressText = progressText };
    }

    // =========================================================================
    // Navigation chrome
    // =========================================================================

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady && ImportWebView.CoreWebView2.CanGoBack)
            ImportWebView.CoreWebView2.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady && ImportWebView.CoreWebView2.CanGoForward)
            ImportWebView.CoreWebView2.GoForward();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewReady)
            ImportWebView.CoreWebView2.Reload();
    }

    private void UpdateNavButtons()
    {
        if (!_webViewReady) return;
        Header.SetNavButtonsEnabled(ImportWebView.CoreWebView2.CanGoBack, ImportWebView.CoreWebView2.CanGoForward);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // Staging folder and the close-time import pass
    // =========================================================================

    private void PrepareStagingFolder(string stagingTag)
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            _stagingFolder = Path.Combine(localFolder, "WebImportStaging", stagingTag);

            // A folder still here on entry can only be debris from a crash or a force-close
            // between a prior session's downloads and its close-time sweep - never something
            // live, since a normal Close() always empties this out itself.
            if (Directory.Exists(_stagingFolder))
                Directory.Delete(_stagingFolder, true);

            Directory.CreateDirectory(_stagingFolder);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Couldn't prepare staging folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs once the fade-out finishes (or immediately, from <see cref="CloseIfOpen"/>). Cancels
    /// anything still downloading, hands every genuinely-completed staged file to the caller in
    /// one batch, then always cleans the staging folder regardless of whether the callback
    /// succeeded, threw, or there was nothing to hand it at all - every file that went through the
    /// downloads shelf is gone from disk by the time this returns, imported or not, so nothing
    /// lingers in LocalState across sessions.
    /// </summary>
    private async Task FinalizeImportsAsync()
    {
        CancelLiveDownloads();

        var callback = _onFilesReady;
        var stagingFolder = _stagingFolder;
        var matches = _completedPaths.Where(File.Exists).ToList();
        _onFilesReady = null;
        _completedPaths.Clear();

        try
        {
            if (callback == null || matches.Count == 0)
            {
                Trace.WriteLine("[WebImportOverlay] Closed with nothing completed to import");
                return;
            }

            Trace.WriteLine($"[WebImportOverlay] Handing {matches.Count} downloaded file(s) to the importer");
            await callback(matches);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebImportOverlay] Error finalizing downloads: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(stagingFolder) && Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, true);
                    Trace.WriteLine("[WebImportOverlay] Staging folder cleared");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WebImportOverlay] Couldn't clean staging folder: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // Fade animation
    // =========================================================================

    private void AnimateOpacity(double to, Action? onCompleted)
    {
        if (AnimationsSuspended)
        {
            _fadeStoryboard?.Stop();
            Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        if (_fadeStoryboard is not null)
        {
            var current = Opacity;
            _fadeStoryboard.Stop();
            Opacity = current;
        }

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FADE_MS)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, this);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (onCompleted is not null)
            sb.Completed += (_, _) => onCompleted();

        _fadeStoryboard = sb;
        sb.Begin();
    }
}
