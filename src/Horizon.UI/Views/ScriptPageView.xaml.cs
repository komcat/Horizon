using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using Horizon.UI.ViewModels;
using Horizon.Workflow.Scripting;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Horizon.UI.Views;

/// <summary>
/// View for the <c>.moon</c> script page. The editor text + run logic live in
/// <see cref="ScriptPageViewModel"/> (which owns the bound <c>TextDocument</c>);
/// this code-behind adds what is intrinsically view-level: Lua syntax
/// highlighting, <c>.</c>-triggered autocomplete, command-list insertion, and
/// output auto-scroll.
/// </summary>
public partial class ScriptPageView : UserControl
{
    private CompletionWindow? _completion;
    private ScriptPageViewModel? _vm;

    public ScriptPageView()
    {
        InitializeComponent();

        using (var reader = XmlReader.Create(new StringReader(LuaSyntax.Xshd)))
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        Editor.TextArea.TextEntered += OnTextEntered;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (DataContext is ScriptPageViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void Detach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptPageViewModel.Output))
            OutputBox.ScrollToEnd();
    }

    private void OnCommandInsert(object sender, MouseButtonEventArgs e)
    {
        if (CommandsList.SelectedItem is not MachineFn f) return;
        Editor.Document.Insert(Editor.TextArea.Caret.Offset, f.FullName);
        Editor.Focus();
    }

    // --- autocomplete (on '.') ---

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != ".") return;

        var items = CompletionsFor(LineTextUpToCaret());
        if (items.Count == 0) return;

        _completion = new CompletionWindow(Editor.TextArea);
        foreach (var it in items) _completion.CompletionList.CompletionData.Add(it);
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    private string LineTextUpToCaret()
    {
        int caret = Editor.TextArea.Caret.Offset;
        var line = Editor.Document.GetLineByOffset(caret);
        return Editor.Document.GetText(line.Offset, caret - line.Offset);
    }

    private static List<MachineCompletion> CompletionsFor(string lineUpToCaret)
    {
        var result = new List<MachineCompletion>();

        var m = Regex.Match(lineUpToCaret, @"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\.$");
        if (!m.Success) return result;
        var path = m.Groups[1].Value;

        if (path == "machine")
        {
            foreach (var table in MachineApiCatalog.All.Where(f => f.Table.Length > 0)
                         .Select(f => f.Table).Distinct())
                result.Add(new MachineCompletion(table, $"machine.{table}", "(sub-table)"));
            foreach (var f in MachineApiCatalog.All.Where(f => f.Table.Length == 0))
                result.Add(new MachineCompletion(f.Name, f.Signature, f.Summary));
            return result;
        }

        if (path.StartsWith("machine.", StringComparison.Ordinal))
        {
            var table = path["machine.".Length..];
            foreach (var f in MachineApiCatalog.All.Where(f => f.Table == table))
                result.Add(new MachineCompletion(f.Name, f.Signature, f.Summary));
        }

        return result;
    }
}

/// <summary>One autocomplete entry — inserts the function/table name at the caret.</summary>
internal sealed class MachineCompletion : ICompletionData
{
    private readonly string _description;

    public MachineCompletion(string text, string header, string summary)
    {
        Text = text;
        _description = header + "\n" + summary;
    }

    public System.Windows.Media.ImageSource? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description => _description;
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}
