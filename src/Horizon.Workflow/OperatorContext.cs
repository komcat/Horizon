namespace Horizon.Workflow;

/// <summary>
/// The operator currently at the station, supplied by the front-end after login and
/// stamped onto every DUT run for traceability. Set via
/// <see cref="IProcessService.SetOperator"/>. This is identity-for-traceability only —
/// not an access-control principal.
/// </summary>
/// <param name="UserId">Employee number recorded on the DUT run.</param>
/// <param name="DisplayName">Human-readable operator name.</param>
/// <param name="StationId">Machine / station number the run executed on.</param>
/// <param name="Level">Access level the operator signed in at (e.g. "Engineer"). Empty
///   when the head has no level concept — it is a label for the record, not a check.</param>
/// <param name="MachineCredential">The station's credential string (its signature),
///   stamped alongside <paramref name="StationId"/> so a DUT record says under what
///   standing the machine ran the unit. Empty when the station is unsigned.</param>
public sealed record OperatorContext(
    string UserId,
    string DisplayName,
    string StationId,
    string Level = "",
    string MachineCredential = "");
