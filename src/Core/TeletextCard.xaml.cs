using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vanilla_RTX_App.Core;

public sealed partial class TeletextCard : UserControl
{
    private readonly string _text;
    private readonly TeletextKind _kind;
    private readonly int? _cooldownMinutes;

    // When animations are suspended each animated property is assigned outright rather than
    // animated over a very short duration - see AnimateOpacity and AnimateCollapse. A
    // storyboard still has to be built, begun and stepped by the compositor whatever its
    // duration, and a duration under one frame is not a faster animation, it is a snap with
    // overhead. Same shape as the overlays in Core\Overlays.
    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;

    private const double FADE_IN_MS = 50;
    private const double FADE_OUT_MS = 50;
    private const double COLLAPSE_MS = 160;

    public double CardFontSize
    {
        get => ContentText.FontSize;
        set => ContentText.FontSize = value;
    }

    /// <summary>
    /// Shown in place of a module's announcements when no copy of the .md, fresh or cached, could
    /// be read. Pinned, so it
    /// carries no dismiss button - there is nothing for the user to dismiss, it goes away on
    /// its own the next time the fetch works.
    /// </summary>
    private static readonly TeletextItem RetrievalFailedNotice = new(
        "An error occurred when trying to retrieve text contents for this module, please relaunch the app " +
        "and make sure it has internet access. Some features will be limited without internet.",
        TeletextKind.Pinned,
        Glyph: "EB5E");

    /// <summary>
    /// Shown in place of a module's announcements when the .md was read but has no section for
    /// this module. Either this build is older than the file (a section it reads was renamed or
    /// retired) or the section was never written - updating fixes the first, a report the second.
    /// </summary>
    private static readonly TeletextItem SectionMissingNotice = new(
        "No announcements were found for this module. Updating the app from the Microsoft Store " +
        "should fix this; if it doesn't, please report it to the developer.",
        TeletextKind.Pinned,
        Glyph: "EB5E");

    /// <summary>
    /// Fills a module's announcement panel, falling back to a single notice card when there
    /// was nothing to fill it with.
    ///
    /// Several windows carve out a fixed space for these, and an empty one reads as a broken
    /// layout rather than as "no news" - which is what the user would see whenever the .md
    /// fetch fails outright.
    ///
    /// The fallback keys off the **source** array, not the filtered result, and that
    /// distinction is the whole rule:
    ///
    ///   - source null, no .md read - <see cref="RetrievalFailedNotice"/>.
    ///   - source null, .md read    - the file has no section for this module:
    ///                                <see cref="SectionMissingNotice"/>.
    ///   - source empty             - the section is there with nothing in it, or every item in it
    ///   - or GetFiltered gives null  is dismissed or outside this version. Nothing to say, and an
    ///                                empty panel is the correct answer.
    ///
    /// Deliberately lives here rather than on Teletext: this is a card, and the log-facing
    /// Teletext feed reads Teletext directly, so it can't pick this up by accident.
    /// </summary>
    public static void Populate(Panel host, TeletextItem[]? source, double? cardFontSize = null, bool sharpCorners = false)
    {
        if (host is null) return;

        host.Children.Clear();

        var items = source is null
            ? new[] { Teletext.HasContent ? SectionMissingNotice : RetrievalFailedNotice }
            : Teletext.GetFiltered(source);

        if (items is null) return;

        foreach (var item in items)
        {
            var card = new TeletextCard(item, sharpenCorners: sharpCorners);
            if (cardFontSize is { } size) card.CardFontSize = size;

            host.Children.Add(card);
        }
    }

    public TeletextCard(TeletextItem item, bool sharpenCorners = false)
    {
        InitializeComponent();
        _text = item.Text;
        _kind = item.Kind;
        _cooldownMinutes = item.CooldownMinutes;
        Overlays.MarkdownRenderer.RenderInlines(ContentText.Inlines, item.Text, OpenLink);

        // ── Glyph override ────────────────────────────────────────────────────
        // Value is a 4–5 char hex string e.g. "E946". Convert to the char the FontIcon expects.
        if (!string.IsNullOrEmpty(item.Glyph))
        {
            try
            {
                var codepoint = Convert.ToInt32(item.Glyph, 16);
                GlyphIcon.Glyph = char.ConvertFromUtf32(codepoint);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TeletextCard] Failed to apply glyph '{item.Glyph}' — using default. {ex.Message}");
            }
        }

        // ── Per-kind background, opacity, dismiss setup ───────────────────────
        switch (item.Kind)
        {
            case TeletextKind.Pinned:
                DismissButton.Visibility = Visibility.Collapsed;
                ContentText.Opacity = 0.95;
                break;

            case TeletextKind.Timed:
                ToolTipService.SetToolTip(DismissButton, FormatCooldownTooltip(_cooldownMinutes)); // tooltip
                ContentText.Opacity = 0.9;
                break;

            case TeletextKind.Permanent:
                CardBorder.Background = new SolidColorBrush(Colors.Transparent);
                CardBorder.Translation = System.Numerics.Vector3.Zero;
                CardBorder.Shadow = null;
                ContentText.Opacity = 0.85;
                break;
        }
        if (sharpenCorners)
        {
            CardBorder.CornerRadius = new CornerRadius(0);
            DismissButton.CornerRadius = new CornerRadius(0);
        }
    }

    /// <summary>
    /// Opens a Teletext link in the default browser or mail client. Anything but an absolute http,
    /// https or mailto address is refused and logged: this text comes off the network, and a
    /// link in it must not be able to launch whatever protocol handler happens to be installed.
    /// </summary>
    private static void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        else
            Trace.WriteLine($"[TeletextCard] Refused to open link '{url}' — only absolute http, https and mailto are allowed");
    }

    private static string FormatCooldownTooltip(int? cooldownMinutes)
    {
        var minutes = cooldownMinutes ?? (int)Teletext.TimedDuration.TotalMinutes;

        if (minutes == 0)
            return "Dismiss for now";

        if (minutes < 60)
            return $"Dismiss for {minutes} minute{(minutes == 1 ? "" : "s")}";

        var hours = (int)Math.Round(minutes / 60.0);
        if (hours < 24)
            return $"Dismiss for {hours} hour{(hours == 1 ? "" : "s")}";

        var days = (int)Math.Round(hours / 24.0);
        return $"Dismiss for {days} day{(days == 1 ? "" : "s")}";
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != TeletextKind.Pinned)
            AnimateOpacity(DismissButton, to: 0.9, durationMs: FADE_IN_MS);
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != TeletextKind.Pinned)
            AnimateOpacity(DismissButton, to: 0.0, durationMs: FADE_OUT_MS);
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_kind)
        {
            case TeletextKind.Permanent:
                Teletext.Dismiss(_text);
                break;
            case TeletextKind.Timed:
                // Pass the per-item cooldown so [cd:""] overrides from the .md are respected.
                // If null, DismissTimed falls back to the global TIMED_DURATION.
                Teletext.DismissTimed(_text, _cooldownMinutes);
                break;
            case TeletextKind.Pinned:
                return; // button is hidden, should never fire
        }
        AnimateCollapse();
    }

    private void AnimateCollapse()
    {
        if (AnimationsSuspended)
        {
            Opacity = 0;
            Visibility = Visibility.Collapsed;
            return;
        }

        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(COLLAPSE_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");

        sb.Children.Add(fade);
        sb.Completed += (_, _) => Visibility = Visibility.Collapsed;
        sb.Begin();
    }

    private static void AnimateOpacity(UIElement target, double to, double durationMs)
    {
        if (AnimationsSuspended)
        {
            target.Opacity = to;
            return;
        }

        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs))
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }
}
