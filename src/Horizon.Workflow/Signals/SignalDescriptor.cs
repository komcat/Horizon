namespace Horizon.Workflow.Signals;

/// <summary>
/// One readable scalar signal the engineer can pull into a workflow variable via
/// the <c>signal.read</c> step. A signal is just a named number with a unit — the
/// <see cref="Key"/> is opaque to the generic layer; the owning <see cref="ISignalSource"/>
/// interprets it. The editor uses the rest of the fields to render the read-out
/// block's drop-down.
/// </summary>
/// <param name="Key">
/// Stable lookup key persisted on the step (e.g. <c>"smu.PIC.ch1.current"</c> or the
/// product key <c>"siphog.sagPwrV"</c>). The source that <see cref="ISignalSource.Owns"/>s
/// it resolves it at run time.
/// </param>
/// <param name="Display">Human label for the drop-down, e.g. <c>"SMU PIC — current"</c>.</param>
/// <param name="Unit">Engineering unit shown beside the value, e.g. <c>"A"</c>, <c>"V"</c>, <c>"°C"</c>.</param>
/// <param name="Category">Grouping for the drop-down, e.g. <c>"SMU"</c>, <c>"SiPhOG"</c>.</param>
public sealed record SignalDescriptor(string Key, string Display, string Unit, string Category);
