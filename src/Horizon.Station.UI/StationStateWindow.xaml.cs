using System.Windows;
using System.Windows.Controls;
using Horizon.Workflow.Oee;

namespace Horizon.Station.UI;

/// <summary>
/// Confirms a station DOWN / UP change — the two halves of a maintenance-log entry.
/// <para><b>Down</b> asks for the engineer / technician's name, a category and the reason
/// (what is wrong, as seen now). <b>Up</b> shows what was recorded at down time and asks for
/// the name, the real root cause and the troubleshooting action taken. Every starred field
/// is required; the dialog stays open until they are filled.</para>
/// </summary>
public partial class StationStateWindow : Window
{
    /// <summary>Preset categories for the Pareto of downtime; the box is editable for anything else.</summary>
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "Mechanical", "Electrical", "Pneumatic", "Motion / stage", "Optics / vision",
        "Software / controls", "Material / parts", "Calibration", "Preventive maintenance",
        "Utilities (air / power)", "Other",
    };

    private readonly bool _goingDown;

    /// <param name="open">The open downtime when bringing the station UP; null when declaring DOWN.</param>
    public StationStateWindow(OeeDowntime? open)
    {
        InitializeComponent();
        _goingDown = open == null;
        CategoryBox.ItemsSource = Categories;

        if (open == null)
        {
            PromptTitle.Text = "Declare station DOWN";
            PromptMessage.Text = "Down time stops counting as up time until someone brings the station back UP " +
                                 "with the root cause and the action taken.";
            OkButton.Content = "Confirm DOWN";
            OkButton.SetResourceReference(BackgroundProperty, "Danger");
            FrameBorder.SetResourceReference(Border.BorderBrushProperty, "Danger");
        }
        else
        {
            PromptTitle.Text = "Bring station UP";
            PromptMessage.Text = "Record what was really wrong and what was done — it goes into the maintenance history.";
            DownInfo.Visibility = Visibility.Visible;
            DownInfoHeader.Text =
                $"Down since {open.Start.ToLocalTime():ddd yyyy-MM-dd HH:mm} " +
                $"({FormatDuration(DateTime.UtcNow - open.Start)}) · declared by {open.DownBy}" +
                (open.Category != null ? $" · {open.Category}" : "");
            DownInfoReason.Text = "Reason: " + (open.Reason ?? "(none recorded)");
            DownFields.Visibility = Visibility.Collapsed;
            UpFields.Visibility = Visibility.Visible;
            OkButton.Content = "Confirm UP";
            OkButton.SetResourceReference(BackgroundProperty, "Ok");
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    public string PersonName => Trim(NameBox.Text);
    public string? Category => string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim();
    public string Reason => Trim(ReasonBox.Text);
    public string RootCause => Trim(RootCauseBox.Text);
    public string ActionTaken => Trim(ActionBox.Text);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var missing = new List<string>();
        if (PersonName.Length == 0) missing.Add("your name");
        if (_goingDown && Reason.Length == 0) missing.Add("the reason");
        if (!_goingDown && RootCause.Length == 0) missing.Add("the root cause");
        if (!_goingDown && ActionTaken.Length == 0) missing.Add("the action taken");

        if (missing.Count > 0)
        {
            ErrorText.Text = "Enter " + string.Join(", ", missing) + " to confirm.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private static string Trim(string? s) => s?.Trim() ?? string.Empty;

    private static string FormatDuration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
}
