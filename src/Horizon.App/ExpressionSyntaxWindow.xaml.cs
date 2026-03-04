using System.Windows;

namespace Horizon.App;

public partial class ExpressionSyntaxWindow : Window
{
    public ExpressionSyntaxWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
