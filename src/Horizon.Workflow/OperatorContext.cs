namespace Horizon.Workflow;

/// <summary>
/// The operator currently at the station, supplied by the front-end after login and
/// stamped onto every DUT run for traceability. Set via
/// <see cref="IProcessService.SetOperator"/>. This is identity-for-traceability only —
/// not an access-control principal.
/// </summary>
/// <param name="UserId">Stable operator id recorded on the DUT run (badge / login).</param>
/// <param name="DisplayName">Human-readable operator name.</param>
/// <param name="StationId">Machine / station identity the run executed on.</param>
public sealed record OperatorContext(string UserId, string DisplayName, string StationId);
