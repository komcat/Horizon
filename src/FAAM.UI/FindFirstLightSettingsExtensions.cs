using Horizon.Common;
using Horizon.Workflow.GridSearch;

namespace FAAM.UI;

/// <summary>
/// Maps the persisted <see cref="FindFirstLightSettings"/> (Layer-3 / Common) onto a
/// Layer-2 <see cref="GridSearchConfig"/> for a run. Starts from
/// <see cref="GridSearchConfig.Default"/> so timing / axis fields not surfaced in the
/// Engineer tab keep their engine defaults, and overlays the operator-edited tunables.
/// </summary>
public static class FindFirstLightSettingsExtensions
{
    public static GridSearchConfig ToGridSearchConfig(this FindFirstLightSettings s)
    {
        var c = GridSearchConfig.Default;
        c.Stage1Enabled = s.Stage1Enabled;
        c.LineJumpUm = s.LineJumpUm;
        c.LineStepUm = s.LineStepUm;
        c.LineSteps = s.LineSteps;
        c.Stage2Enabled = s.Stage2Enabled;
        c.GridExtentUm = s.GridExtentUm;
        c.GridStepUm = s.GridStepUm;
        c.MaxGridNodes = s.MaxGridNodes;
        c.DetectDeltaV = s.DetectDeltaV;
        c.DropFraction = s.DropFraction;
        c.DropConfirmCount = s.DropConfirmCount;
        return c;
    }
}
