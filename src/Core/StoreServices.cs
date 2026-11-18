using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Services.Store;
using WinRT.Interop;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Everything the app asks of the Microsoft Store: the in-app rating dialog and update checks.
/// The Store is the only place the app ships (no sideloaded or GitHub builds exist), so every
/// install is one these APIs can answer for - except a Debug deploy from Visual Studio, which has
/// no Store license, and gets empty answers and failed requests. Every method here treats that as
/// "nothing to do" and logs to Trace rather than throwing.
///
/// <para><b>A desktop app's <see cref="StoreContext"/> has to be given a window</b>
/// (<see cref="GetContext"/>), or the Store's own dialogs have no owner and the request fails.
/// One context is created per call rather than cached: they are cheap, and a cached one would
/// outlive the window it was initialised with.</para>
/// </summary>
public static class StoreServices
{
    public const string ProductId = "9N6PCRZ5V9DJ";

    /// <summary>
    /// Build switch: true makes this build treat every available update as mandatory, whatever
    /// Partner Center says. Off, only a submission marked "Make this update mandatory" there
    /// raises the update prompt.
    /// </summary>
    public const bool TreatEveryUpdateAsMandatory = false;

    private static StoreContext GetContext()
    {
        var context = StoreContext.GetDefault();
        InitializeWithWindow.Initialize(context, WindowNative.GetWindowHandle(MainWindow.Instance));
        return context;
    }

    public enum ReviewOutcome { Submitted, Canceled, Unavailable }

    /// <summary>
    /// Shows the Store's rating and review dialog over the app. <see cref="ReviewOutcome.Unavailable"/>
    /// covers every way it can fail - offline, no Store license, the API throwing - so the caller
    /// can fall back to opening the Store's review page.
    /// </summary>
    public static async Task<ReviewOutcome> RequestReviewAsync()
    {
        try
        {
            var result = await GetContext().RequestRateAndReviewAppAsync();
            Trace.WriteLine($"[Store] Review dialog: {result.Status} {result.ExtendedError?.Message}");
            return result.Status switch
            {
                StoreRateAndReviewStatus.Succeeded => ReviewOutcome.Submitted,
                StoreRateAndReviewStatus.CanceledByUser => ReviewOutcome.Canceled,
                _ => ReviewOutcome.Unavailable
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Store] Review dialog failed: {ex.Message}");
            return ReviewOutcome.Unavailable;
        }
    }

    /// <summary>
    /// The updates the user should be pushed toward: those Partner Center marks mandatory, or all
    /// of them under <see cref="TreatEveryUpdateAsMandatory"/>. Empty when there are none, and when
    /// the Store can't be asked.
    /// </summary>
    public static async Task<IReadOnlyList<StorePackageUpdate>> FindRequiredUpdatesAsync()
    {
        try
        {
            var updates = await GetContext().GetAppAndOptionalStorePackageUpdatesAsync();
            var required = updates.Where(u => TreatEveryUpdateAsMandatory || u.Mandatory).ToList();
            Trace.WriteLine($"[Store] Updates available: {updates.Count}, required: {required.Count}");
            return required;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Store] Update check failed: {ex.Message}");
            return Array.Empty<StorePackageUpdate>();
        }
    }

    /// <summary>
    /// Downloads and installs through the Store's own progress UI; on success Windows closes the
    /// app to swap the package in, so nothing after a successful await is guaranteed to run.
    /// Returns false when the install did not complete, including when the user cancelled it.
    /// </summary>
    public static async Task<bool> InstallUpdatesAsync(IReadOnlyList<StorePackageUpdate> updates)
    {
        try
        {
            var result = await GetContext().RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            Trace.WriteLine($"[Store] Update install: {result.OverallState}");
            return result.OverallState == StorePackageUpdateState.Completed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Store] Update install failed: {ex.Message}");
            return false;
        }
    }

    public static Task OpenStorePageAsync()
        => Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?ProductId={ProductId}")).AsTask();

    public static Task OpenReviewPageAsync()
        => Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://review/?ProductId={ProductId}")).AsTask();
}
