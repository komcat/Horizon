using Horizon.Common;

namespace Horizon.Workflow;

/// <summary>
/// A Layer 3 image panel that a script or a step can push a still picture into —
/// the FAAM Run page's snapshot box (where the SiPhOG chart used to sit).
/// Registered via <see cref="IProcessService.SetImageDisplaySink"/>. Same
/// inverted-dependency seam as <see cref="IUtilityAppHost"/> and
/// <see cref="ISiphogSheetSink"/>: Horizon.Workflow defines the contract, a head
/// implements it over its own WPF surface. With no head registered,
/// <c>machine.ui.showImage</c> fails with a clear message rather than crash.
///
/// The path is a file the caller has already written — typically the return value
/// of <c>machine.camera.snap(camera)</c>. The implementation reads it and releases
/// the handle immediately (the capture directory is recycled), so the file may be
/// deleted right after this returns.
/// </summary>
public interface IImageDisplaySink
{
    /// <summary>
    /// Shows the image file at <paramref name="imagePath"/>, with an optional
    /// caption naming what the operator is looking at. Fails if the file is
    /// missing or not a readable image.
    /// </summary>
    Result Show(string imagePath, string? caption);

    /// <summary>Clears the panel back to its empty state.</summary>
    Result Clear();
}
