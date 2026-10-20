using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Vanilla_RTX_App;

/// <summary>
/// Keeps keyboard focus out of whatever an overlay is covering.
///
/// <para><b>An overlay blocks the pointer and nothing else.</b> Settings, Help/Bugs and the
/// module host are hit-test-visible layers over the window's body, so a click can't reach what
/// is underneath - but XAML's focus navigation walks the visual tree without regard to
/// z-order or occlusion, so Tab walks straight into the body's buttons behind the acrylic and
/// Space presses them. That is how a second module used to open over the first, and a third over
/// that: the return button closes only the newest, and every module left underneath kept its
/// controls locked. Everything below makes "covered" mean covered for the keyboard too.</para>
///
/// <para><b>One rule, applied at the window's root, for every overlay at once:</b> while one is
/// open, focus may be inside the topmost open overlay, in the titlebar, or outside this window's
/// tree entirely - and nowhere else. The titlebar stays reachable because it is drawn above
/// every overlay and deliberately stays live over them (Help, Bugs, the return button, a
/// module's own titlebar buttons). "Outside the tree" is dialogs and flyouts, which live in
/// popups; redirecting their focus would make every ContentDialog unusable.</para>
///
/// <para><b>Two halves, and both are needed.</b> <see cref="OverlayFocus_GettingFocus"/> stops
/// focus <i>moving</i> into covered content, which is what Tab does. It cannot help with an
/// element that already <i>had</i> focus when the overlay came up - a slider in the body is
/// still focused after a <c>vanillartx://</c> link opens a module over it, and its arrow keys
/// still work. <see cref="OverlayFocus_PreviewKeyDown"/> catches that case: a key aimed at
/// covered content is swallowed and focus is moved into the overlay instead.</para>
///
/// <para>Nothing per overlay. A new overlay only has to be named in
/// <see cref="TopmostOpenOverlay"/>, in the same order their z-indices stack.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// A key-down that was swallowed because it was aimed at covered content. Its key-up has to
    /// go too: focus has moved into the overlay by then, and a key-up landing on a control there
    /// with no matching key-down is not something every control can be trusted to ignore.
    /// </summary>
    private VirtualKey? _swallowedKey;

    private void AttachOverlayFocusTrap()
    {
        RootElement.GettingFocus += OverlayFocus_GettingFocus;
        RootElement.PreviewKeyDown += OverlayFocus_PreviewKeyDown;
        RootElement.PreviewKeyUp += OverlayFocus_PreviewKeyUp;
    }

    /// <summary>
    /// The open overlay the user is looking at, or null when none is. Settings is checked first
    /// and the module host last because that is how they stack: Settings and the documents draw
    /// over a module (§2i), so while one of them is up, the module under it is covered content
    /// like the body is - the same reason Escape leaves a module alone while a document is open.
    /// </summary>
    private UIElement? TopmostOpenOverlay()
    {
        if (ChessViewer.IsOpen) return ChessViewer;
        if (SettingsPanel.IsOpen) return SettingsPanel;
        if (DocsOverlay.IsOpen) return DocsOverlay;
        if (_openModules.Count > 0) return ModuleOverlayHost;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="element"/> is content the topmost overlay is covering. False for
    /// anything inside that overlay, anything in the titlebar, and anything not in this window's
    /// tree at all (a dialog or flyout, in its own popup).
    /// </summary>
    private bool IsCoveredByOverlay(DependencyObject? element, UIElement overlay)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, overlay) || ReferenceEquals(node, TitleBarLayer)) return false;
            if (ReferenceEquals(node, RootElement)) return true;
        }
        return false;
    }

    private void OverlayFocus_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (TopmostOpenOverlay() is not { } overlay) return;
        if (!IsCoveredByOverlay(args.NewFocusedElement, overlay)) return;

        // Into the overlay rather than nowhere, so Tab keeps moving through something the user
        // can see. Backwards navigation lands on the overlay's last element, which is where
        // Shift+Tab out of its first one would naturally expect to arrive.
        var redirect = args.Direction == FocusNavigationDirection.Previous
            ? FocusManager.FindLastFocusableElement(overlay)
            : FocusManager.FindFirstFocusableElement(overlay);

        if (redirect is null || !args.TrySetNewFocusedElement(redirect))
            args.TryCancel();

        args.Handled = true;
    }

    private void OverlayFocus_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TopmostOpenOverlay() is not { } overlay) return;
        if (!IsCoveredByOverlay(e.OriginalSource as DependencyObject, overlay)) return;

        // Escape goes through untouched: its handler closes the open module, and a covered
        // element has no meaning of its own for it that could get in the way.
        if (e.Key != VirtualKey.Escape)
        {
            e.Handled = true;
            _swallowedKey = e.Key;
        }

        if (FocusManager.FindFirstFocusableElement(overlay) is UIElement target)
            _ = FocusManager.TryFocusAsync(target, FocusState.Keyboard);
    }

    private void OverlayFocus_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_swallowedKey != e.Key) return;
        _swallowedKey = null;
        e.Handled = true;
    }
}
