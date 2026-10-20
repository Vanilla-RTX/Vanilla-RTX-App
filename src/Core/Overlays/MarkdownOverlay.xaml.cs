using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Vanilla_RTX_App.Modules;
using Windows.System;
using Windows.UI.Core;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>
/// A full-window overlay that fetches a GitHub markdown file's raw content and renders it as
/// plain WinUI elements - the read-only sibling of <see cref="WebImportOverlay"/>. Shares that
/// control's <see cref="OverlayHeaderBar"/> chrome (icon, hyperlinked title, Reload, Close) but
/// never spins up a WebView2 and never hands anything back to the caller: there is nothing to
/// import from a document, so <c>Show</c> takes just a URL, a title and a glyph.
///
/// <para><b>Why this exists instead of just pointing WebImportOverlay at the GitHub page.</b>
/// The Help and Bug-tracker buttons only ever showed a single README - paying for a full
/// Chromium instance (and GitHub's own page chrome, ads-adjacent trackers, and layout shift) to
/// display text that fits in a <see cref="Markdig.MarkdownPipeline"/> parse and a handful of
/// <see cref="TextBlock"/>s was the wrong tool for a job this small. Rendering the markdown
/// directly is also what makes the cache below possible - there's a single file to
/// cache, not a browser profile.</para>
///
/// <para><b>Pages are cached on disk and fetched at most once an hour</b>, the same model as
/// <see cref="Teletext"/> and through the same machinery - see <see cref="DocumentAsset"/>
/// and <see cref="AssetUpdater"/>. raw.githubusercontent.com is effectively this app's CDN, and
/// a fetch on every launch, times every user, is requests spent on documents that change a few
/// times a year. Inside the cooldown the cached copy is shown without touching the network;
/// after it the page is fetched, and if that fails the cached copy is shown anyway, however old,
/// because a stale document beats an error card. Only a page that has never been fetched can
/// show the error.</para>
///
/// <para><b>Reload is rate-limited, deliberately, and it always goes to the network.</b>
/// A WebView2 "reload" is a browser doing what browsers already do constantly; this is a direct,
/// unconditional request to a git host's raw-content server, and a caller mashing the button has
/// no throttle standing between them and that server. <see cref="_reloadCooldownUntil"/> is a
/// static dictionary, keyed by page URL and storing *next allowed reload time* rather
/// than *last reload time* (the pattern <c>AssetUpdater</c> already uses) - that's what makes the
/// cooldown survive closing and reopening the overlay: it isn't instance state at all.</para>
///
/// <para><b>Clicking Help/Bug again is a toggle, and switching documents closes the one that was
/// open first.</b> <see cref="Show"/> re-clicked with the same URL while already open just closes
/// it (see <see cref="Close"/>). A *different* URL while one is open plays the same close
/// animation and only then opens the new document - not a silent content swap under a panel
/// that's still visibly there, and not two overlays stacking.</para>
///
/// <para><b>Rendering never throws</b> - see <see cref="MarkdownRenderer"/>'s own doc. Every
/// other failure mode (no internet, a 404 from a renamed branch, a malformed URL) is caught here
/// and shown as the same friendly <c>ErrorState</c> panel <see cref="WebImportOverlay"/> uses,
/// with a Try Again button that re-fetches without ever leaving the user looking at nothing.
/// </para>
/// </summary>
public sealed partial class MarkdownOverlay : UserControl
{
    /// <summary>
    /// A document as <see cref="AssetUpdater"/> sees it, built per address because the two pages
    /// are settings a user can repoint (<c>Links</c>) rather than fixed endpoints. The cached
    /// copy, the cooldown, the conditional request and the retries are all its business; what
    /// stays here is rendering and the Reload button.
    ///
    /// <para>No packaged copy: these documents live in a repository, and one baked into a build
    /// would be stale the day it shipped. So a page nobody has ever opened fetches whatever the
    /// schedule says, and only a page that has been read once can be read offline.</para>
    ///
    /// <para>The two deadlines are the interesting part. With nothing cached the overlay has an
    /// error card as its only alternative, so it waits 15s; with a copy in hand a slow network
    /// isn't worth waiting out and 5s is plenty.</para>
    /// </summary>
    private static ManagedAsset DocumentAsset(string rawUrl) => new(
        rawUrl,
        TimeSpan.FromHours(1),
        CacheFolderName: "MarkdownCache",
        FileName: AssetUpdater.DefaultFileName(rawUrl) + ".md",
        FailureBackoff: TimeSpan.FromMinutes(10),
        Timeout: TimeSpan.FromSeconds(15),
        TimeoutWhenCached: TimeSpan.FromSeconds(5));

    /// <summary>Next allowed Reload time per page URL - see the class doc. Process-lifetime, independent of any single overlay instance's open/closed state.</summary>
    private static readonly Dictionary<string, DateTime> _reloadCooldownUntil = new(StringComparer.Ordinal);
    private const int ReloadCooldownSeconds = 60;

    /// <summary>
    /// How far down each page was last left, so reopening it lands where the reader was rather
    /// than at the top. Kept here rather than in the visual tree because closing releases that
    /// tree outright (see <see cref="CloseInternal"/>), and for the session only - a page's
    /// position is worth restoring five minutes later, not next week.
    /// </summary>
    private static readonly Dictionary<string, double> _scrollOffsets = new(StringComparer.Ordinal);

    /// <summary>
    /// True from an open until the page it opened has been rendered. Rendering also happens on
    /// Reload and Try Again, where the reader has not gone anywhere and being moved would be the
    /// bug rather than the fix.
    /// </summary>
    private bool _restorePositionOnRender;

    /// <summary>
    /// Set by any wheel, pointer or key input on the page, and by the back-to-top button, so a
    /// pending <see cref="RestoreScrollOffset"/> knows the reader has taken over. Input rather
    /// than the offset is the signal because the offset moves on its own: the viewer keeps its
    /// last value while collapsed, and a re-rendered page clamps it as its images arrive.
    /// </summary>
    private bool _readerScrolled;

    /// <summary>
    /// True while a fetch is running. The Reload cooldown is armed by the answer rather than by
    /// the click, so this is what stops a second click going out behind the first.
    /// </summary>
    private bool _loadInFlight;

    private bool _isOpen;
    private string _pageUrl = string.Empty;
    private string _rawUrl = string.Empty;
    private string _blobBaseUrl = string.Empty;
    private string _rawBaseUrl = string.Empty;
    private string? _pendingFragment;
    private IReadOnlyDictionary<string, FrameworkElement>? _anchors;
    private IReadOnlyList<MarkdownSearchEntry> _searchEntries = Array.Empty<MarkdownSearchEntry>();
    private readonly List<MarkdownSearchEntry> _searchMatches = new();
    private int _searchMatchIndex = -1;
    private CancellationTokenSource? _fetchCts;
    private DispatcherTimer? _cooldownTimer;
    private Storyboard? _fadeStoryboard;

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;
    /// <summary>
    /// Fade duration for the whole overlay, bounded at both ends: below ~100ms a 60Hz display
    /// has too few frames left for the ease to read as anything but a snap, and much above it
    /// the overlay feels slow to open on a high-refresh display. 100ms is 6 frames at 60Hz
    /// and 14 at 144Hz.
    /// </summary>
    private const double FADE_MS = 125;

    public MarkdownOverlay()
    {
        InitializeComponent();

        Header.SetNavButtonsVisible(false);

        // Opens whatever we actually fetched, not the human-readable blob page - the title link
        // is supposed to reflect the real request this overlay made, and that request is always
        // to raw.githubusercontent.com. See ResolveGithubUrls.
        Header.TitleClick += (_, _) =>
        {
            if (Uri.TryCreate(_rawUrl, UriKind.Absolute, out var uri))
                _ = Launcher.LaunchUriAsync(uri);
        };

        Header.ReloadClick += (_, _) =>
        {
            // The button should already be disabled for the duration, but a click that slips in
            // right on the boundary (or one queued before SetReloadCooldown took effect) must
            // still be refused - the cooldown is the invariant, not the button's IsEnabled.
            if (_loadInFlight) return;
            if (_reloadCooldownUntil.TryGetValue(_pageUrl, out var until) && until > DateTime.UtcNow)
                return;

            // Re-rendering replaces the content and takes the scroll position with it, and
            // someone who asked for a fresh copy of the page they are reading meant to stay on it.
            RememberScrollOffset();
            _restorePositionOnRender = true;

            // The cooldown is armed by the load itself, and only if the remote answered - a
            // reload that failed must not spend the attempt the user would make next.
            _ = LoadAsync(bypassCache: true);
        };

        Header.ScrollTopClick += (_, _) =>
        {
            _readerScrolled = true;
            ContentScrollViewer.ChangeView(null, 0, null, disableAnimation: AnimationsSuspended);
        };

        // handledEventsToo: the ScrollViewer marks the very input that scrolls it as handled.
        ContentScrollViewer.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => _readerScrolled = true), true);
        ContentScrollViewer.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _readerScrolled = true), true);
        ContentScrollViewer.AddHandler(KeyDownEvent, new KeyEventHandler((_, _) => _readerScrolled = true), true);

        Header.CloseClick += (_, _) => Close();
    }

    /// <summary>
    /// Opens the overlay and shows <paramref name="url"/> - a normal <c>github.com/.../blob/...</c>
    /// page URL (the same kind you'd paste into a browser address bar), not a raw content URL.
    /// The raw file, and the base URLs relative links/images resolve against, are derived from it -
    /// see <see cref="ResolveGithubUrls"/>. A URL fragment (<c>#some-heading</c>) is honored:
    /// once rendered, the overlay scrolls to the matching heading the same way GitHub itself would.
    ///
    /// <para>Calling this again with the URL currently showing closes the overlay (a toggle).
    /// Calling it with a different URL while one is open closes the current document first, then
    /// opens the new one - see the class doc.</para>
    /// </summary>
    public void Show(string url, string title, string glyph)
    {
        if (_isOpen && string.Equals(_pageUrl, url, StringComparison.Ordinal))
        {
            Close();
            return;
        }

        if (_isOpen)
        {
            CloseInternal(() => OpenAndLoad(url, title, glyph));
            return;
        }

        OpenAndLoad(url, title, glyph);
    }

    private void OpenAndLoad(string url, string title, string glyph)
    {
        _isOpen = true;

        Header.SetTitleText(title);
        Header.SetIcon(glyph);
        Header.SetCloseButton("", "Return", "Close this page");

        _pageUrl = url;
        _anchors = null;
        _searchEntries = Array.Empty<MarkdownSearchEntry>();
        _restorePositionOnRender = true;

        try
        {
            (_rawUrl, _blobBaseUrl, _rawBaseUrl, _pendingFragment) = ResolveGithubUrls(url);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownOverlay] Couldn't parse URL '{url}': {ex.Message}");
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            AnimateOpacity(1.0, null);
            ShowError("This page's address couldn't be understood.");
            return;
        }

        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        AnimateOpacity(1.0, null);
        UpdateLayout();

        RefreshReloadCooldownUI();
        _ = LoadAsync(bypassCache: false);
    }

    /// <summary>
    /// True while a document is showing. MainWindow reads it to decide whether the settings
    /// panel has to wait for this to close first - the two overlays occupy the same space and
    /// are mutually exclusive.
    /// </summary>
    public bool IsOpen => _isOpen;

    /// <summary>
    /// The page URL currently showing, as passed to <see cref="Show"/>, or null while closed.
    /// Lets a caller that wants a page open - rather than toggled - leave it alone when it
    /// already is.
    /// </summary>
    public string? ShowingUrl => _isOpen ? _pageUrl : null;

    /// <summary>
    /// Closes the overlay, optionally continuing into <paramref name="onClosed"/> once the
    /// fade-out has actually finished. Callers that open something else in that continuation
    /// get the same real animation a Close click gets, rather than a swap under a panel that
    /// is still visibly there.
    /// </summary>
    public void Close(Action? onClosed = null) => CloseInternal(onClosed);

    /// <summary>The guts of closing - optionally continues into <paramref name="onClosed"/> once the fade-out finishes, which is how <see cref="Show"/> chains "close this document, then open the next one" through the same real animation a Close click gets.</summary>
    private void CloseInternal(Action? onClosed)
    {
        if (!_isOpen) return;

        RememberScrollOffset();
        _isOpen = false;
        _fetchCts?.Cancel();
        _cooldownTimer?.Stop();
        CloseSearchBar();

        IsHitTestVisible = false;
        AnimateOpacity(0.0, () =>
        {
            Visibility = Visibility.Collapsed;
            // Release the rendered visual tree (and everything it's holding - Image sources,
            // Hyperlink closures) rather than letting a large README sit alive in memory
            // between visits. The cached copy on disk is untouched; re-opening just re-renders it.
            MarkdownHost.Content = null;
            onClosed?.Invoke();
        });
    }

    /// <summary>The host window's own Closed handler should call this - see WebImportOverlay.CloseIfOpen for why. A markdown fetch has no staging folder to clean up, only an in-flight request and a countdown timer worth stopping.</summary>
    public void CloseIfOpen()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _fetchCts?.Cancel();
        _cooldownTimer?.Stop();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(bypassCache: false);

    // =========================================================================
    // Loading and rendering
    // =========================================================================

    /// <summary>
    /// Shows the page from the disk cache when it is fresh, otherwise fetches it - falling back
    /// to the cached copy, however old, if the fetch fails. <paramref name="bypassCache"/>
    /// (Reload) always goes to the network, with the same fallback. See the class doc.
    /// </summary>
    private async Task LoadAsync(bool bypassCache)
    {
        ShowLoading("Loading...");

        _loadInFlight = true;
        Header.SetReloadBusy(true);
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _fetchCts = cts;

        try
        {
            var asset = DocumentAsset(_rawUrl);
            var read = await AssetUpdater.ResolveFreshOrCachedAsync(asset, force: bypassCache, cancellationToken: cts.Token);

            // Reaching the remote arms Reload's cooldown, so the button reads as "this page has
            // just been checked" - which includes a 304, where the server confirmed the cached
            // copy is current and pressing Reload would learn nothing. A page served without
            // asking anyone, or an attempt that failed, leaves the button live.
            if (read.CheckedRemote)
            {
                _reloadCooldownUntil[_pageUrl] = DateTime.UtcNow.AddSeconds(ReloadCooldownSeconds);
                RefreshReloadCooldownUI();
            }

            if (read.Path is null || !File.Exists(read.Path))
            {
                if (cts.Token.IsCancellationRequested) return;
                ShowError("Couldn't load this page. Check your internet connection and try again.");
                return;
            }

            var markdown = await File.ReadAllTextAsync(read.Path, cts.Token);

            if (!_isOpen || cts.Token.IsCancellationRequested || string.IsNullOrWhiteSpace(markdown)) return;
            RenderMarkdown(markdown);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Show()/Reload/Retry, or the overlay closed mid-fetch.
        }
        finally
        {
            _loadInFlight = false;
            Header.SetReloadBusy(false);
            RefreshReloadCooldownUI();
        }
    }

    private void RenderMarkdown(string markdown)
    {
        try
        {
            var result = MarkdownRenderer.Render(markdown, _blobBaseUrl, _rawBaseUrl, OnLinkActivated);
            _anchors = result.Anchors;
            _searchEntries = result.SearchEntries;
            MarkdownHost.Content = result.Content;
            ShowContent();

            var restore = _restorePositionOnRender;
            _restorePositionOnRender = false;

            if (!string.IsNullOrEmpty(_pendingFragment))
            {
                var slug = _pendingFragment;
                _pendingFragment = null;
                // The content isn't laid out yet on this same tick - give it one dispatcher
                // pass before asking a heading to scroll itself into view.
                DispatcherQueue.TryEnqueue(() => ScrollToAnchor(slug));
            }
            else if (restore)
            {
                RestoreScrollOffset();
            }
        }
        catch (Exception ex)
        {
            // MarkdownRenderer.Render is documented to never throw, but this is a full-window
            // overlay with nothing else to fall back to - a defensive backstop costs nothing.
            Trace.WriteLine($"[MarkdownOverlay] Render failed: {ex}");
            ShowError("This page couldn't be displayed.");
        }
    }

    /// <summary>Keeps where this page was left, for the next time it is opened.</summary>
    private void RememberScrollOffset()
    {
        if (string.IsNullOrEmpty(_pageUrl) || MarkdownHost.Content is null) return;

        var offset = ContentScrollViewer.VerticalOffset;
        if (offset > 0.5) _scrollOffsets[_pageUrl] = offset;
        else _scrollOffsets.Remove(_pageUrl);
    }

    /// <summary>
    /// Scrolls back to where this page was left, once there is enough page to scroll.
    ///
    /// <para><b>The wait is the whole problem.</b> Freshly rendered markdown is shorter than its
    /// final height: inline images have no dimensions until they load, so asking for an offset on
    /// the tick the content is set clamps it to whatever the page is at that moment and lands the
    /// reader part-way up. Each layout pass is a chance to try again, and the attempt and time
    /// caps are what stop a page that never grows tall enough - a document edited down since it
    /// was last read - from leaving a handler subscribed for the session.</para>
    ///
    /// <para>A reader who scrolls while that is going on has said where they want to be, so the
    /// restore gives up rather than yanking them somewhere else - judged by their input
    /// (<see cref="_readerScrolled"/>), never by the offset, which moves on its own here.</para>
    ///
    /// <para>When the deadline passes first, it goes as far down as the page allows by then
    /// rather than nowhere: a page whose images are slow is still worth landing near the
    /// place.</para>
    /// </summary>
    private void RestoreScrollOffset()
    {
        if (!_scrollOffsets.TryGetValue(_pageUrl, out var target) || target <= 0.5) return;

        _readerScrolled = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var page = _pageUrl;

        void OnLayoutUpdated(object? sender, object e)
        {
            // A different document is on screen when the overlay was switched while this waited -
            // its position is not this one's to set.
            if (!_isOpen || _readerScrolled || !string.Equals(_pageUrl, page, StringComparison.Ordinal))
            {
                ContentScrollViewer.LayoutUpdated -= OnLayoutUpdated;
                return;
            }

            var reachable = Math.Max(0, ContentScrollViewer.ExtentHeight - ContentScrollViewer.ViewportHeight);
            if (reachable + 0.5 < target && DateTime.UtcNow < deadline) return;

            ContentScrollViewer.LayoutUpdated -= OnLayoutUpdated;
            ContentScrollViewer.ChangeView(null, Math.Min(target, reachable), null, disableAnimation: true);
        }

        ContentScrollViewer.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLinkActivated(string target)
    {
        if (target.StartsWith('#'))
        {
            ScrollToAnchor(target.TrimStart('#'));
            return;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }

    private void ScrollToAnchor(string slug)
    {
        if (_anchors is null || string.IsNullOrEmpty(slug)) return;

        var decoded = Uri.UnescapeDataString(slug);
        if (_anchors.TryGetValue(decoded, out var element) || _anchors.TryGetValue(slug, out element))
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
        else
            Trace.WriteLine($"[MarkdownOverlay] No heading matched anchor '#{slug}' - see MarkdownRenderer's anchor-slug caveat.");
    }

    // =========================================================================
    // Reload cooldown
    // =========================================================================

    /// <summary>
    /// Paints the header's Reload button for the current cooldown state and, while one is
    /// active, keeps a one-second timer running to count it down - called from
    /// <see cref="OpenAndLoad"/> (so reopening mid-cooldown shows it immediately) and by the
    /// timer itself each tick.
    /// </summary>
    private void RefreshReloadCooldownUI()
    {
        if (_reloadCooldownUntil.TryGetValue(_pageUrl, out var until))
        {
            var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            if (remaining > 0)
            {
                Header.SetReloadCooldown(remaining);

                // Kept running rather than merely kept alive. Closing stops this timer, and this
                // control is one instance shared by every document - switching from Help to Bugs
                // closes and reopens it - so a check for the field being null would find a timer
                // that exists and is stopped, and the countdown would freeze on screen while the
                // cooldown itself carried on expiring.
                _cooldownTimer ??= CreateCooldownTimer();
                if (!_cooldownTimer.IsEnabled) _cooldownTimer.Start();
                return;
            }
            _reloadCooldownUntil.Remove(_pageUrl);
        }

        _cooldownTimer?.Stop();
        _cooldownTimer = null;
        Header.SetReloadCooldown(null);
    }

    private DispatcherTimer CreateCooldownTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshReloadCooldownUI();
        return timer;
    }

    // =========================================================================
    // Cache
    // =========================================================================

    // =========================================================================
    // GitHub URL resolution
    // =========================================================================

    /// <summary>
    /// Given a normal <c>github.com/{owner}/{repo}/blob/{branch}/{path}[#fragment]</c> page URL,
    /// returns the equivalent <c>raw.githubusercontent.com</c> URL to fetch, the directory-level
    /// blob and raw base URLs relative links/images resolve against (see
    /// <see cref="MarkdownRenderer"/>), and the fragment (if any) as a plain anchor slug.
    /// <para>
    /// A URL that isn't a recognizable github.com blob link (already a raw URL, or something
    /// else entirely) is treated as the raw content itself, fetched as-is, with its own
    /// directory as both base URL - there's no separate "human view" URL to derive a different
    /// one from.
    /// </para>
    /// </summary>
    private static (string RawUrl, string BlobBaseUrl, string RawBaseUrl, string? Fragment) ResolveGithubUrls(string pageUrl)
    {
        var uri = new Uri(pageUrl);
        var fragment = string.IsNullOrEmpty(uri.Fragment) ? null : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
            {
                var owner = segments[0];
                var repo = segments[1];
                var branch = segments[3];
                var pathSegments = segments.Skip(4).ToArray();
                var path = string.Join('/', pathSegments);
                var dir = pathSegments.Length > 1 ? string.Join('/', pathSegments[..^1]) + "/" : "";

                var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
                var blobBase = $"https://github.com/{owner}/{repo}/blob/{branch}/{dir}";
                var rawBase = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{dir}";
                return (rawUrl, blobBase, rawBase, fragment);
            }
        }

        var directUrl = new UriBuilder(uri) { Fragment = "" }.Uri.ToString();
        var lastSlash = directUrl.LastIndexOf('/');
        var directoryUrl = lastSlash >= 0 ? directUrl[..(lastSlash + 1)] : directUrl;
        return (directUrl, directoryUrl, directoryUrl, fragment);
    }

    // =========================================================================
    // Search
    // =========================================================================

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e) => OpenSearchBar();

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isOpen || ContentScrollViewer.Visibility != Visibility.Visible) return;
        OpenSearchBar();
        args.Handled = true;
    }

    private void OpenSearchBar()
    {
        SearchToggleButton.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void SearchCloseButton_Click(object sender, RoutedEventArgs e) => CloseSearchBar();

    private void CloseSearchBar()
    {
        ClearCurrentHighlight();
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        SearchBox.Text = string.Empty;
        SearchMatchCountText.Text = string.Empty;
        SearchBar.Visibility = Visibility.Collapsed;
        if (ContentScrollViewer.Visibility == Visibility.Visible)
            SearchToggleButton.Visibility = Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(CoreVirtualKeyStates.Down);
                MoveToMatch(shiftDown ? -1 : 1);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                CloseSearchBar();
                e.Handled = true;
                break;
        }
    }

    private void SearchNextButton_Click(object sender, RoutedEventArgs e) => MoveToMatch(1);
    private void SearchPreviousButton_Click(object sender, RoutedEventArgs e) => MoveToMatch(-1);

    private void ApplySearchFilter(string query)
    {
        ClearCurrentHighlight();
        _searchMatches.Clear();
        _searchMatchIndex = -1;

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var entry in _searchEntries)
                if (entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    _searchMatches.Add(entry);
        }

        if (_searchMatches.Count > 0)
        {
            _searchMatchIndex = 0;
            HighlightCurrentMatch(scrollTo: true);
        }

        SearchMatchCountText.Text = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : _searchMatches.Count == 0
                ? "No results"
                : $"{_searchMatchIndex + 1}/{_searchMatches.Count}";
    }

    private void MoveToMatch(int delta)
    {
        if (_searchMatches.Count == 0) return;

        ClearCurrentHighlight();
        _searchMatchIndex = ((_searchMatchIndex + delta) % _searchMatches.Count + _searchMatches.Count) % _searchMatches.Count;
        HighlightCurrentMatch(scrollTo: true);
        SearchMatchCountText.Text = $"{_searchMatchIndex + 1}/{_searchMatches.Count}";
    }

    private void ClearCurrentHighlight()
    {
        if (_searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Count)
            MarkdownRenderer.SetSearchHighlight(_searchMatches[_searchMatchIndex].Element, highlighted: false);
    }

    private void HighlightCurrentMatch(bool scrollTo)
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count) return;

        var element = _searchMatches[_searchMatchIndex].Element;
        MarkdownRenderer.SetSearchHighlight(element, highlighted: true);
        if (scrollTo)
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
    }

    // =========================================================================
    // Loading/Error/Content state
    // =========================================================================

    private void ShowLoading(string text)
    {
        CloseSearchBar();
        LoadingText.Text = text;
        LoadingState.Visibility = Visibility.Visible;
        ErrorState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        SearchToggleButton.Visibility = Visibility.Collapsed;
        Header.SetScrollTopButtonVisible(false);
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorState.Visibility = Visibility.Visible;
        LoadingState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Collapsed;
        SearchToggleButton.Visibility = Visibility.Collapsed;
        Header.SetScrollTopButtonVisible(false);
    }

    private void ShowContent()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        ContentScrollViewer.Visibility = Visibility.Visible;
        SearchToggleButton.Visibility = Visibility.Visible;
        Header.SetScrollTopButtonVisible(true);
    }

    // =========================================================================
    // Fade animation - identical to WebImportOverlay's; see that class for the reasoning.
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

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
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
