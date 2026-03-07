using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Horizon.App;

public enum FilePickerMode { Open, Save }

public partial class FilePickerDialog : Window
{
    private readonly FilePickerMode _mode;
    private readonly string _extension;
    private readonly string _filterLabel;
    private string _currentDir;

    /// <summary>Selected full file path after dialog closes with OK.</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>For Save mode with multi-select off, or Open multi-select.</summary>
    public string[] SelectedPaths { get; private set; } = Array.Empty<string>();

    /// <param name="mode">Open or Save</param>
    /// <param name="initialDir">Starting directory</param>
    /// <param name="extension">File extension including dot, e.g. ".hproc"</param>
    /// <param name="filterLabel">Display label, e.g. "Horizon Process"</param>
    /// <param name="defaultFileName">Pre-filled file name for Save mode</param>
    public FilePickerDialog(
        FilePickerMode mode,
        string initialDir,
        string extension,
        string filterLabel,
        string defaultFileName = "")
    {
        _mode = mode;
        _extension = extension;
        _filterLabel = filterLabel;
        _currentDir = initialDir;

        InitializeComponent();

        Title = mode == FilePickerMode.Save ? $"Save {filterLabel}" : $"Open {filterLabel}";
        ActionBtn.Content = mode == FilePickerMode.Save ? "Save" : "Open";
        FilterText.Text = $"{filterLabel} (*{extension})";

        if (mode == FilePickerMode.Save && !string.IsNullOrEmpty(defaultFileName))
            FileNameBox.Text = defaultFileName;

        if (mode == FilePickerMode.Open)
            FileList.SelectionMode = SelectionMode.Extended;

        Directory.CreateDirectory(initialDir);
        NavigateTo(initialDir);

        Loaded += (_, _) =>
        {
            if (mode == FilePickerMode.Save)
            {
                FileNameBox.Focus();
                FileNameBox.SelectAll();
            }
            else
            {
                FileList.Focus();
            }
        };
    }

    private void NavigateTo(string dir)
    {
        if (!Directory.Exists(dir)) return;
        _currentDir = dir;
        PathText.Text = dir;

        var items = new List<FilePickerItem>();

        // Directories first
        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new FilePickerItem
                {
                    Icon = "\uE8B7",
                    IconColor = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0x8B)),
                    Name = Path.GetFileName(d),
                    Modified = Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm"),
                    Size = "",
                    FullPath = d,
                    IsDirectory = true
                });
            }
        }
        catch { }

        // Files matching extension
        try
        {
            foreach (var f in Directory.GetFiles(dir, $"*{_extension}").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(f);
                items.Add(new FilePickerItem
                {
                    Icon = "\uE8A5",
                    IconColor = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5)),
                    Name = Path.GetFileName(f),
                    Modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    Size = FormatSize(info.Length),
                    FullPath = f,
                    IsDirectory = false
                });
            }
        }
        catch { }

        FileList.ItemsSource = items;
    }

    private void NavigateUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentDir);
        if (parent != null)
            NavigateTo(parent.FullName);
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FilePickerItem item) return;

        if (item.IsDirectory)
        {
            NavigateTo(item.FullPath);
            return;
        }

        // Double-click on file = select and confirm
        SelectedPath = item.FullPath;
        SelectedPaths = new[] { item.FullPath };
        DialogResult = true;
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == FilePickerMode.Open)
        {
            var selected = FileList.SelectedItems.Cast<FilePickerItem>()
                .Where(i => !i.IsDirectory)
                .Select(i => i.FullPath)
                .ToArray();

            if (selected.Length == 0)
            {
                // Try the text box as a filename
                var typed = FileNameBox.Text.Trim();
                if (!string.IsNullOrEmpty(typed))
                {
                    var full = ResolveFileName(typed);
                    if (File.Exists(full))
                        selected = new[] { full };
                }
            }

            if (selected.Length == 0) return;

            SelectedPath = selected[0];
            SelectedPaths = selected;
            DialogResult = true;
        }
        else // Save
        {
            var name = FileNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;

            var full = ResolveFileName(name);
            SelectedPath = full;
            SelectedPaths = new[] { full };
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Action_Click(sender, e);
    }

    private string ResolveFileName(string name)
    {
        if (!name.EndsWith(_extension, StringComparison.OrdinalIgnoreCase))
            name += _extension;
        if (Path.IsPathRooted(name))
            return name;
        return Path.Combine(_currentDir, name);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public class FilePickerItem
{
    public string Icon { get; set; } = "";
    public SolidColorBrush IconColor { get; set; } = Brushes.White;
    public string Name { get; set; } = "";
    public string Modified { get; set; } = "";
    public string Size { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
}
