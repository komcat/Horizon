namespace FAAM.UI;

/// <summary>
/// The intermediate publish/subscribe seam for operator actions. Publishers (the jog
/// rail, the correction-sequence orchestrator, the alignment panels) push an
/// <see cref="OperatorActivity"/>; subscribers (the DUT-log mirror, and optionally the
/// log surface) react — without any publisher knowing who is listening.
///
/// This is the layer that lets us mirror operator work into the DUT log <em>without
/// hardwiring</em> DUT-logging into each routine: a routine emits an activity to its
/// natural sink and stays ignorant of where it ends up.
///
/// Publishers may run on any thread (the jog pump is a background task); the single
/// <see cref="Published"/> fan-out is synchronous, so subscribers must marshal to the UI
/// thread themselves if they touch UI.
/// </summary>
public sealed class OperatorActivityHub
{
    /// <summary>Raised for every published activity. May fire on any thread.</summary>
    public event Action<OperatorActivity>? Published;

    /// <summary>Publishes an activity to all subscribers. No-op for a null activity.</summary>
    public void Publish(OperatorActivity activity)
    {
        if (activity == null) return;
        Published?.Invoke(activity);
    }
}
