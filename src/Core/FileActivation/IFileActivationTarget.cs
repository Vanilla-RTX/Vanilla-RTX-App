using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Core.FileActivation;

/// <summary>
/// A feature window that can take a file activation itself, instead of it being imported
/// through MainWindow.
///
/// <para><b>What it is for:</b> a user who opens the shader mod manager or the pack browser
/// and <i>then</i> double-clicks a file has told us where they expect the result to appear.
/// Importing through MainWindow answers by pulling MainWindow in front of the window they
/// were just looking at, and putting the confirmation dialogs there too. When a window
/// implementing this is open, <see cref="FileActivationRouter"/> hands it the files and
/// brings it forward instead - MainWindow is never raised at all.</para>
///
/// <para>Implementations are expected to do the whole job the way their own UI already does
/// it: their busy state, their dialogs, their list refresh. They are not a second import
/// path - both current implementations forward to the method their own drag-and-drop
/// already calls.</para>
/// </summary>
internal interface IFileActivationTarget
{
    /// <summary>
    /// Imports paths delivered by a file activation. Already de-duplicated and filtered to
    /// this window's route by the router, but an implementation should still ignore anything
    /// it does not recognise rather than assume.
    ///
    /// <para>Called on the UI thread, and awaited - the router does not raise the window or
    /// move on until this completes.</para>
    /// </summary>
    Task ImportActivatedFilesAsync(IReadOnlyList<string> paths);
}
