using System.Windows;
using System.Windows.Controls;

namespace FAAM.UI.ViewModels;

/// <summary>
/// Picks the editor template for a <see cref="BlockParamViewModel"/> from its
/// <see cref="ParamKind"/>: number / bool / combo (position, axis, set-clear) /
/// text. The four templates are supplied by the view's resources.
/// </summary>
public sealed class ParamTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Number { get; set; }
    public DataTemplate? Bool { get; set; }
    public DataTemplate? Combo { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is BlockParamViewModel p
            ? p.Kind switch
            {
                ParamKind.Number => Number,
                ParamKind.Bool or ParamKind.SetClear => Bool,
                ParamKind.PositionName or ParamKind.AxisName => Combo,
                _ => Text,
            }
            : Text;
}
