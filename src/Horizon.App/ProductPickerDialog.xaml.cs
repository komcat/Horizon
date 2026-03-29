using System.Windows;
using System.Windows.Input;

namespace Horizon.App;

public partial class ProductPickerDialog : Window
{
    public string SelectedProduct { get; private set; } = string.Empty;

    public ProductPickerDialog(List<string> productNames, string? activeProduct = null)
    {
        InitializeComponent();
        ProductList.ItemsSource = productNames;

        if (!string.IsNullOrEmpty(activeProduct))
        {
            var idx = productNames.FindIndex(n =>
                n.Equals(activeProduct, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                ProductList.SelectedIndex = idx;
        }

        Loaded += (_, _) => ProductList.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (ProductList.SelectedItem is string name)
        {
            SelectedProduct = name;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ProductList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProductList.SelectedItem is string name)
        {
            SelectedProduct = name;
            DialogResult = true;
        }
    }
}
