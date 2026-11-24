using System.Windows;
using System.Windows.Controls;

namespace Horizon.Station.UI.Views;

/// <summary>A small modal "type a name" dialog (OK / Cancel).</summary>
public static class NamePrompt
{
    /// <summary>Shows the dialog over <paramref name="owner"/>; returns the typed text, or null on Cancel.</summary>
    public static string? Show(Window? owner, string title, string message, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 280 };
        var ok = new Button { Content = "OK", Width = 80, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return dlg.ShowDialog() == true ? box.Text : null;
    }
}
