using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Vanilla_RTX_App.Core;
using Vanilla_RTX_App.Core.FileActivation;
using static Vanilla_RTX_App.Core.EnvironmentVariables;

// Same arrangement as MainWindow.ImportRouters.cs: filed with the router that calls it, and a
// partial of MainWindow because pressing its buttons needs its private fields.
namespace Vanilla_RTX_App;

/// <summary>
/// What a <c>vanillartx://</c> link does once
/// <see cref="Core.FileActivation.FileActivationRouter"/> has received it: opens one screen
/// of the app, e.g. <c>vanillartx://packbrowser</c>.
///
/// <para><b>A link here only ever navigates - it never acts.</b> Any web page can put one in
/// front of a user, so nothing this file can reach may change a file, a setting or the game: it
/// opens a module, the settings panel or a document, and anything from there on is the user's
/// own click. That is why Tune, Delete and every install have no command, and none should be
/// added. The one exception is <see cref="Core.FileActivation.SilentLinks"/> - launching the
/// game with the user's own launch options - and it lives in its own table with its own,
/// narrower rules for what may join it; <see cref="RunSilentLinkAsync"/> is its only foothold
/// here.</para>
///
/// <para><b>Modules open by pressing their real button</b>, through its automation peer - the
/// same path a screen reader takes. So a link gets exactly what a click gets: the "locate your
/// user data first" check, RTX Reactor's selection check, and above all the disabled state. A
/// button locked because the app is busy, or remotely suspended (<c># SuspendControls</c>),
/// can't be pressed by a link either, and nothing here has to know those rules exist.</para>
///
/// <para><b>Every command means "be here", never "toggle".</b> The buttons behind Settings,
/// Help and Bugs close what they opened when pressed twice; a link opened twice must not. And
/// only one module can be open, so a link to a second one while the first is up is refused with
/// a log line rather than stacking - closing the first could abandon whatever it was doing.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The URI scheme registered in Package.appxmanifest. Lowercase, as <see cref="Uri.Scheme"/> reports it.</summary>
    internal const string LinkScheme = "vanillartx";

    private readonly record struct ModuleLink(string Label, Func<MainWindow, Button> Button, Type Module);

    /// <summary>
    /// The modules a link can open, by command name. Names are matched case-insensitively,
    /// with or without a leading "open" (<c>vanillartx://OpenPackBrowser</c> works too).
    /// The README's link list has to agree with this table.
    /// </summary>
    private static readonly Dictionary<string, ModuleLink> ModuleLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["packupdater"] = new("Get latest RTX packs", w => w.LaunchPackUpdateButton, typeof(Modules.PackUpdater.PackUpdaterOverlay)),
        ["packbrowser"] = new("Select other packs", w => w.BrowsePacksButton, typeof(Modules.PackBrowser.PackBrowserOverlay)),
        ["rtxreactor"] = new("RTX Reactor", w => w.LaunchAlchitexButton, typeof(Modules.Alchitex.Alchitex)),
        ["shadermods"] = new("Shader mod manager", w => w.LaunchShaderModManagerButton, typeof(Modules.ShaderMods.ShaderModManagerOverlay)),
        // The module's name before it took a second provider. Kept because links already
        // published on web pages and in announcements still say it.
        ["betterrtx"] = new("Shader mod manager", w => w.LaunchShaderModManagerButton, typeof(Modules.ShaderMods.ShaderModManagerOverlay)),
        ["dlss"] = new("DLSS swapper", w => w.LaunchDLSSSwapperButton, typeof(Modules.DLSS.DLSSSwapperOverlay)),
        ["lut"] = new("RTX LUT manager", w => w.LaunchLUTManagerButton, typeof(Modules.LUT.LUTManagerOverlay)),
    };

    /// <summary>
    /// Opens what <paramref name="uri"/> names. Waits for startup to finish first, since a link
    /// can be what launched the app and a module opened before the game locations are resolved
    /// would find nothing. Never throws; a link that can't be followed is a log line.
    /// </summary>
    internal async Task OpenLinkAsync(Uri uri)
    {
        await WaitUntilInitializedAsync();

        try
        {
            var (command, section) = FileActivationRouter.ParseLink(uri);
            Trace.WriteLine($"[Links] Opening '{uri.OriginalString}' as command '{command}'{(section is null ? "" : $", section '{section}'")}");

            switch (command)
            {
                case "":
                    return; // vanillartx:// on its own - bringing the app forward was the whole ask.
                case "settings":
                    OpenSettingsFromLink();
                    return;
                case "help":
                    OpenDocumentFromLink(Links.Documentation, section, HelpButton);
                    return;
                case "bugs":
                    OpenDocumentFromLink(Links.BugTracker, section, BugButton);
                    return;
            }

            if (ModuleLinks.TryGetValue(command, out var module))
            {
                OpenModuleFromLink(module);
                return;
            }

            Log($"This app link doesn't lead anywhere: {uri.OriginalString}", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Links] Couldn't follow '{uri.OriginalString}': {ex}");
        }
    }

    /// <summary>
    /// Runs a <see cref="SilentLinks"/> command in this window, without raising it. Gated on
    /// the command's control exactly as <see cref="Press"/> gates a button - disabled because
    /// the app is busy, hidden, or remotely suspended all refuse it - but the command's own
    /// method is called rather than the button pressed, because a command may ask for something
    /// the button wouldn't (launching Preview while the app targets Release). Returns false when
    /// it was refused or failed; the router raises the window then, so the log line is seen.
    /// </summary>
    internal async Task<bool> RunSilentLinkAsync(SilentLinks.SilentLink command)
    {
        await WaitUntilInitializedAsync();

        try
        {
            if ((Content as FrameworkElement)?.FindName(command.ControlName) is not Control control
                || !control.IsEnabled || control.Visibility != Visibility.Visible)
            {
                Log($"A link asked for {command.Label}, but it can't be done right now.", LogLevel.Warning);
                return false;
            }

            Trace.WriteLine($"[Links] Running '{command.Label}' without raising the window");
            return await command.InWindow(this);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Links] Couldn't run '{command.Label}': {ex}");
            return false;
        }
    }

    private void OpenModuleFromLink(ModuleLink module)
    {
        if (_openModules.LastOrDefault() is { } open)
        {
            if (open.GetType() != module.Module)
                Log($"A link asked for {module.Label}, but another feature is open. Close it first, then follow the link again.", LogLevel.Warning);
            return;
        }

        // Neither covers a module in normal use - the settings scrim makes the module buttons
        // unreachable, and a document drawn over a freshly opened module hides it - so both
        // make way first, as they would for the user's own click.
        if (SettingsPanel.IsOpen) SettingsPanel.Hide();
        if (DocsOverlay.IsOpen) DocsOverlay.Close();

        Press(module.Button(this), module.Label);
    }

    private void OpenSettingsFromLink()
    {
        if (!SettingsPanel.IsOpen)
            Press(SettingsButton, "Settings");
    }

    /// <summary>
    /// Shows <paramref name="pageUrl"/>, at <paramref name="section"/> when there is one.
    /// Leaves the page alone if it is already showing - Show on the same URL closes it.
    /// </summary>
    private void OpenDocumentFromLink(string pageUrl, string? section, Button button)
    {
        if (!button.IsEnabled) { Press(button, "That page"); return; }

        var url = section is null ? pageUrl : $"{pageUrl.Split('#')[0]}#{section}";
        if (string.Equals(DocsOverlay.ShowingUrl, url, StringComparison.Ordinal)) return;

        if (SettingsPanel.IsOpen) SettingsPanel.Hide();
        DocsOverlay.Show(url: url, title: button == BugButton ? BugTrackerTitle : DocumentationTitle, glyph: "", landingAnchor: DocumentLandingAnchor);
    }

    /// <summary>
    /// Clicks <paramref name="button"/> the way a user would, or says why it can't. A disabled
    /// button stays unpressed - see the class doc for why that is the point.
    /// </summary>
    private void Press(Button button, string label)
    {
        if (!button.IsEnabled || button.Visibility != Visibility.Visible)
        {
            Log($"A link asked for {label}, but it can't be opened right now.", LogLevel.Warning);
            return;
        }

        new ButtonAutomationPeer(button).Invoke();
    }
}
