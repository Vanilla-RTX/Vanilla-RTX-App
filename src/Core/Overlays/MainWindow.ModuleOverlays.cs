using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.Overlays;
using System.Diagnostics;

namespace Vanilla_RTX_App;

/// <summary>
/// Opening and closing the feature modules, which are overlays over this window rather than
/// windows of their own.
///
/// <para><b>One module at a time, and the host is empty in between.</b> Every button that opens
/// one lives in the body a module covers, and covering it is enforced for the pointer by the
/// host's hit-testing and for the keyboard by MainWindow.OverlayFocus.cs. <see cref="OpenModule"/>
/// still refuses a second one outright: a stacked module is one the return button can't reach,
/// whose control locks are never released, so the invariant is held here too rather than
/// trusted to every path that might ever call this.</para>
///
/// <para><b>The lifecycle is deliberately a window's lifecycle</b> (see
/// <see cref="ModuleOverlay"/> for the full reasoning): constructed on open, torn down on
/// close. The visible gain over the windows these replace is not that state survives a close -
/// it doesn't, and shouldn't - it is that the log, the lamp, the progress bar and the titlebar
/// stay on screen the whole time a module is open, and that Help and Bugs can be read over a
/// module without disturbing it.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The open module, if any. A list rather than a single field because the file-activation
    /// router asks "is a window of this type open" (<see cref="FindOpenModule"/>) and reads
    /// better against a collection, and because one is exactly as cheap as the other here.
    /// </summary>
    private readonly List<ModuleOverlay> _openModules = new();

    private Storyboard? _moduleHostFade;
    private Action? _pendingHostCleanup;

    /// <summary>Matches <see cref="MarkdownOverlay"/>'s fade, so switching surfaces feels like one app rather than several.</summary>
    private const double MODULE_FADE_MS = 125;

    /// <summary>
    /// This window's content as a <see cref="FrameworkElement"/> - a XamlRoot, a dispatcher
    /// and a theme, which is all anything that used to take the whole Window actually wanted
    /// from it. <see cref="Modules.ImportDialogs"/> is the case that made it worth naming: a
    /// module is a control now and cannot hand one a window.
    /// </summary>
    internal FrameworkElement RootElement => (FrameworkElement)Content;

    /// <summary>
    /// Builds a module with <paramref name="create"/> and puts it on screen - unless one is
    /// already open, in which case nothing happens at all. Locks <paramref name="toDisable"/> for as
    /// long as it is up, and runs <paramref name="onClosed"/> once it goes away.
    ///
    /// <para><b>The lock is taken here rather than at the call site</b> so its release cannot
    /// be forgotten: the two halves are the same pair of lines in one method, and a module that
    /// closes itself, is closed by its button, or throws on the way up all reach the same
    /// release. <see cref="LockControls"/> adds <c>SettingsButton</c> on top of the named
    /// controls, for the reason given on it.</para>
    ///
    /// <para><paramref name="onClosed"/> runs before the fade-out finishes, which is what keeps
    /// a module's closing log line and its re-enabled buttons in the same moment the user let
    /// go of it.</para>
    /// </summary>
    internal void OpenModule(Func<ModuleOverlay> create, string[] toDisable, Action<ModuleOverlay>? onClosed = null)
    {
        // Refused before the second module is even constructed - hence a factory rather than an
        // instance. A constructed module cannot simply be closed again: several teardowns act
        // on shared state (WebImportOverlay.CloseIfOpen is static and would close the open
        // module's browser; RTX Reactor saves its options from its controls, which on an
        // instance nobody saw are XAML defaults), so the only safe second module is none.
        if (_openModules.Count > 0)
        {
            Trace.WriteLine($"[OpenModule] Refused: {_openModules[^1].GetType().Name} is already open.");
            return;
        }

        var overlay = create();
        LockControls(false, toDisable);

        try
        {
            _ = BlinkingLamp(false, true, 0.66, 0.33);
        }
        catch
        {
            Trace.WriteLine("[OpenModule] Something went wrong calling the lamp animation.");
        }
        overlay.Closed += (_, _) =>
        {
            _openModules.Remove(overlay);
            ReleaseModuleTitleBarStrip(overlay);
            ModuleTitleBar.ShowReturn(false);

            // Anything the module was still holding on its own controls goes with it. Those
            // controls are about to leave the visual tree, and a count left on one of them is
            // a count nothing will ever release.
            WindowControlsManager.ClearStates(overlay);

            LockControls(true, toDisable);

            onClosed?.Invoke(overlay);

            ModuleOverlayHost.IsHitTestVisible = false;
            FadeModuleHost(0.0, () =>
            {
                ModuleOverlayHost.Children.Remove(overlay);
                ModuleOverlayHost.Visibility = Visibility.Collapsed;
            });
        };

        // Not before Loaded: until then the module's controls aren't in the visual tree to be
        // found. Walks the whole window so a module's titlebar strip, which lives outside the
        // overlay once adopted, is covered too.
        overlay.Loaded += (_, _) =>
        {
            WindowControlsManager.ApplySuspensions(Content);

            // Focus follows the user into the module. Opened from the keyboard, focus is still
            // on the body button that did it; opened by a link, it can be anywhere in the body.
            // Either way it is on something now covered.
            if (FocusManager.FindFirstFocusableElement(overlay) is UIElement first)
                _ = FocusManager.TryFocusAsync(first, FocusState.Programmatic);
        };

        _openModules.Add(overlay);
        AdoptModuleTitleBarStrip(overlay);
        ModuleTitleBar.ShowReturn(true);

        ModuleOverlayHost.Children.Add(overlay);
        ModuleOverlayHost.Visibility = Visibility.Visible;
        ModuleOverlayHost.IsHitTestVisible = true;
        FadeModuleHost(1.0, null);
    }

    /// <summary>
    /// Logs a module's result the way every one of these has reported since they were windows:
    /// its own log level on success, Error on a failure that had something to say, and silence
    /// when the message is empty.
    /// </summary>
    internal void LogModuleResult(ModuleOverlay overlay, LogLevel level)
    {
        if (overlay.OperationSuccessful)
        {
            if (!string.IsNullOrEmpty(overlay.StatusMessage))
                Log(overlay.StatusMessage, level);
        }
        else if (!string.IsNullOrEmpty(overlay.StatusMessage))
        {
            Log(overlay.StatusMessage, LogLevel.Error);
        }
    }

    /// <summary>
    /// The titlebar's return button closes whichever module is open. It is one button shared by
    /// all six rather than one per module, which is the whole reason it can sit in the titlebar
    /// and look like a caption button.
    /// </summary>
    private void ModuleTitleBar_ReturnRequested(object? sender, EventArgs e) =>
        _openModules.LastOrDefault()?.Close();

    /// <summary>
    /// Escape is the return button's shortcut: it closes the open module, and nothing else.
    ///
    /// <para><b>Registered on the window's root without <c>handledEventsToo</c></b>, so
    /// anything that already gives Escape a meaning of its own keeps it - the document search
    /// box closes its bar and marks the key handled, and that has to be the end of it rather
    /// than also tearing down the module behind it. Flyouts and dialogs live in popups outside
    /// this tree and never reach here at all, which is how Escape dismissing a dialog stays a
    /// dialog's business.</para>
    ///
    /// <para><b>Ignored while a document is open over the module.</b> Help and Bugs draw on
    /// top of one (§2i), so the module is not what the user is looking at - closing it from
    /// under the page they are reading would lose its state out of sight.</para>
    /// </summary>
    private void ModuleEscape_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (_openModules.Count == 0 || DocsOverlay.IsOpen || ChessViewer.IsOpen) return;

        e.Handled = true;
        _openModules.LastOrDefault()?.Close();
    }

    /// <summary>
    /// Hands a module's titlebar buttons to the shared titlebar control for as long as it is
    /// open.
    ///
    /// <para><b>The module's own element, not a copy</b>, so its <c>x:Name</c> field and every
    /// <c>Click</c> handler on it keep working untouched - which is the whole reason those
    /// buttons are still declared in the module's own XAML. It arrives already detached from
    /// that XAML; see <see cref="ModuleOverlay.PrepareContent"/> for why that has to happen
    /// there rather than here.</para>
    /// </summary>
    private void AdoptModuleTitleBarStrip(ModuleOverlay overlay)
    {
        if (overlay.TitleBarStrip is not { } strip) return;
        ModuleTitleBar.Strip = strip;
    }

    /// <summary>
    /// Empties the titlebar strip again. The buttons are not put back where they came from -
    /// the module they belong to is being torn down, so there is nothing to put them back
    /// into; dropping the reference is what lets both go.
    /// </summary>
    private void ReleaseModuleTitleBarStrip(ModuleOverlay overlay)
    {
        if (overlay.TitleBarStrip is null) return;
        ModuleTitleBar.Strip = null;
    }

    /// <summary>
    /// Fades the module frame - <c>ModuleOverlayHost</c>, the acrylic panel a module is shown
    /// inside - in or out.
    ///
    /// <para><b>One fade for one frame, rather than one per module.</b> Only one module is
    /// ever open, and the frame and the module inside it are a single surface: fading the
    /// module on its own would slide its content in over an acrylic panel that had already
    /// snapped into place.</para>
    ///
    /// <para>The end value is written back when the storyboard completes, because a storyboard
    /// holds its end value without ever assigning it and <c>Stop</c> - the first thing the next
    /// fade does - reverts the target to whatever base it still has.</para>
    ///
    /// <para><b>A superseded fade still runs its continuation, and that is load-bearing.</b>
    /// <c>Stop</c> does not raise <c>Completed</c>, so a fade-out cut short by a fade-in would
    /// otherwise never reach the callback that takes the closed module out of the tree - and
    /// that is reachable, not theoretical: the window's body is clickable the instant a module
    /// starts closing, so opening another one within the fade leaves the old one parented and
    /// on screen underneath it.</para>
    /// </summary>
    private void FadeModuleHost(double to, Action? onCompleted)
    {
        _moduleHostFade?.Stop();
        _moduleHostFade = null;

        var superseded = _pendingHostCleanup;
        _pendingHostCleanup = onCompleted;
        superseded?.Invoke();

        if (EnvironmentVariables.Persistent.SuspendUIAnimations)
        {
            _pendingHostCleanup = null;
            ModuleOverlayHost.Opacity = to;
            onCompleted?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(MODULE_FADE_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, ModuleOverlayHost);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            ModuleOverlayHost.Opacity = to;

            if (!ReferenceEquals(_pendingHostCleanup, onCompleted)) return;
            _pendingHostCleanup = null;
            onCompleted?.Invoke();
        };

        _moduleHostFade = storyboard;
        storyboard.Begin();
    }

    /// <summary>Closes every open module, for the window shutting down underneath them.</summary>
    private void CloseAllModules()
    {
        foreach (var overlay in _openModules.ToList())
        {
            try { overlay.Close(); } catch { /* shutting down; a module's teardown is not worth a crash */ }
        }
    }
}
