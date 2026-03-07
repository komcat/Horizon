using System.IO;
using System.Windows;
using System.Windows.Controls;
using Horizon.Core;

namespace Horizon.App;

public partial class DatasetDialog : Window
{
    private readonly TrainerStore _store;
    private readonly List<string> _names;

    public string? SelectedName { get; private set; }

    public DatasetDialog(TrainerStore store, string? currentName = null)
    {
        _store = store;
        InitializeComponent();

        _names = store.GetTrainingSetNames();
        DatasetList.ItemsSource = _names;

        if (currentName != null)
        {
            var idx = _names.IndexOf(currentName);
            if (idx >= 0)
                DatasetList.SelectedIndex = idx;
        }
    }

    private void DatasetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = DatasetList.SelectedItem as string;
        LoadBtn.IsEnabled = name != null;

        if (name == null)
        {
            InfoNameText.Text = "";
            InfoPathText.Text = "";
            InfoDetailText.Text = "";
            return;
        }

        var path = _store.GetFilePath(name);
        InfoNameText.Text = $"Name: {name}";
        InfoPathText.Text = path;

        var set = _store.Load(name);
        if (set != null)
        {
            var bonds = set.BondingPoints.Count;
            var refs = set.ReferencePoints.Count;
            var status = set.Valid ? "Frame OK" : "Frame not computed";
            InfoDetailText.Text = $"{bonds} bonding pts, {refs} ref pts — {status}";
        }
        else
        {
            InfoDetailText.Text = "Error loading dataset";
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        SelectedName = DatasetList.SelectedItem as string;
        if (SelectedName != null)
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _store.DataDirectory;
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
