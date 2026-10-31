using Horizon.Common;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Ends a needle calibration or dispense procedure with a message (FireDog's Lua <c>error()</c>):
/// thrown inside the procedure's linear body, caught at its top and returned as a failed
/// <see cref="Result"/> after the needle has been taken off the part. Never escapes Layer 2.
/// </summary>
public sealed class NeedleProcedureException : Exception
{
    public NeedleProcedureException(string message, ErrorCode code = ErrorCode.OperationFailed) : base(message) => Code = code;

    public ErrorCode Code { get; }
}
