using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using Horizon.Workflow.Scripting;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace FAAM.UI.Views;

/// <summary>
/// The <c>.moon</c> procedural-script page — a Lua editor (AvalonEdit) that runs
/// against the engine through the Layer 2 <c>RunScriptAsync</c> seam. Syntax
/// highlighting + a searchable <c>machine</c> command panel + <c>.</c>-triggered
/// autocomplete come from <see cref="LuaSyntax"/> / <see cref="MachineApiCatalog"/>.
/// All hardware work runs off the UI thread; output marshals back via <see cref="Ui"/>.
/// </summary>
public partial class ScriptEditorView : UserControl
{
    private const string Template =
        "-- machine.* reaches every facet. Examples:\n" +
        "--   machine.motion.moveTo(\"gantry-main\", \"Load\")\n" +
        "--   machine.io.setNamed(\"IOBottom\", \"Vacuum\", true)\n" +
        "--   local v = machine.signal.read(\"smu.PIC.ch1.current\")\n" +
        "--   if machine.ask(\"Continue?\", \"Proceed with attach?\") then ... end\n\n" +
        "machine.log(\"hello from lua\")\n";

    /// <summary>Canned snippets offered by the Examples dropdown.</summary>
    private static readonly (string Name, string Source)[] Examples =
    {
        ("Template", Template),

        ("IO on/off cycle (by name)", """
            -- Pulse a named output 3 times, 500 ms on / 500 ms off.
            -- Pin names come from the IO page (e.g. UV_Head, Vacuum_Base, L_Gripper).
            local dev = "IOBottom"
            local name = "UV_Head"

            machine.io.connect(dev)
            for i = 1, 3 do
              machine.io.setNamed(dev, name, true)
              machine.log("cycle " .. i .. ": " .. name .. " ON")
              machine.wait(500)

              machine.io.setNamed(dev, name, false)
              machine.log("cycle " .. i .. ": " .. name .. " OFF")
              machine.wait(500)
            end
            machine.log("io test done")
            """),

        ("Motion: gantry X +1 / -1", """
            -- Jog gantry-main +1.0 mm on X, then back -1.0 mm.
            local dev = "gantry-main"
            local axis = "X"          -- must match the configured axis name

            machine.motion.connect(dev)
            machine.motion.setVelocity(dev, 5)        -- mm/s (optional)

            machine.log("start X = " .. machine.motion.position(dev).x)
            machine.motion.moveRel(dev, axis, 1.0)
            machine.log("after +1.0, X = " .. machine.motion.position(dev).x)
            machine.wait(300)
            machine.motion.moveRel(dev, axis, -1.0)
            machine.log("after -1.0, X = " .. machine.motion.position(dev).x)
            machine.log("move test done")
            """),

        ("Read a signal", """
            -- Read one unified signal (engine catalog + product sources).
            local key = "smu.PIC.ch1.current"
            local v = machine.signal.read(key)
            machine.log(key .. " = " .. v)
            """),

        ("Ask operator + branch", """
            -- Block on an operator yes/no, then branch.
            if machine.ask("Confirm", "Proceed with the attach?") then
              machine.log("operator said yes")
            else
              machine.log("operator declined")
            end
            """),
    };

    private readonly EngineHost _host;
    private CancellationTokenSource? _cts;
    private CompletionWindow? _completion;

    public ScriptEditorView(EngineHost host)
    {
        _host = host;
        InitializeComponent();

        using (var reader = XmlReader.Create(new StringReader(LuaSyntax.Xshd)))
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        Editor.Text = Template;
        Editor.TextArea.TextEntered += OnTextEntered;

        ExamplesCombo.ItemsSource = Examples.Select(x => x.Name).ToList();
        CommandsList.ItemsSource = MachineApiCatalog.All;
    }

    /// <summary>Refreshes the saved-script list for the active product.</summary>
    public void Activate() => RefreshScripts();

    /// <summary>Cancels a running script when navigating away.</summary>
    public void Deactivate() => _cts?.Cancel();

    private void RefreshScripts()
    {
        var current = ScriptCombo.SelectedItem as string;
        ScriptCombo.ItemsSource = _host.Process.ListScripts();
        if (current != null) ScriptCombo.SelectedItem = current;
    }

    // --- command help panel ---

    private void OnHelpSearch(object sender, TextChangedEventArgs e)
    {
        var q = HelpSearch.Text?.Trim();
        CommandsList.ItemsSource = string.IsNullOrEmpty(q)
            ? MachineApiCatalog.All
            : MachineApiCatalog.All
                .Where(f => f.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || f.Summary.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
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

        // The token path ending just before the '.' that was typed.
        var m = Regex.Match(lineUpToCaret, @"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\.$");
        if (!m.Success) return result;
        var path = m.Groups[1].Value;

        if (path == "machine")
        {
            // sub-tables (machine.io, machine.motion, …) + the core functions
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

    // --- script load / save / examples ---

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        var name = ScriptCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(name)) { SetStatus("Pick a script to load.", warn: true); return; }

        var source = _host.Process.LoadScript(name);
        if (source == null) { SetStatus($"Script '{name}' not found.", warn: true); return; }

        Editor.Text = source;
        NameBox.Text = name;
        SetStatus($"Loaded '{name}'.");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SetStatus("Enter a name to save.", warn: true); return; }

        var r = _host.Process.SaveScript(name, Editor.Text ?? string.Empty);
        if (!r.IsSuccess) { SetStatus($"Save failed: {r.Message}", warn: true); return; }

        RefreshScripts();
        ScriptCombo.SelectedItem = name;
        SetStatus($"Saved '{name}'.");
    }

    private void OnExampleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExamplesCombo.SelectedItem is not string name) return;   // cleared selection

        var match = Examples.FirstOrDefault(x => x.Name == name);
        if (match.Source == null) return;

        Editor.Text = match.Source;
        NameBox.Text = string.Empty;
        SetStatus("Loaded example: " + name);
        ExamplesCombo.SelectedIndex = -1;   // reset so the same example can be re-picked
    }

    // --- run / stop ---

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;   // already running

        OutputBox.Clear();
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SetStatus("Running…");

        _cts = new CancellationTokenSource();
        var activity = _host.Activity.Begin(ActivityKind.Alignment, "Script");
        try
        {
            var result = await _host.Process.RunScriptAsync(
                Editor.Text ?? string.Empty,
                onOutput: line => Ui(() => AppendOutput(line)),
                ct: _cts.Token);

            if (result.Success) SetStatus("Completed.");
            else if (result.Cancelled) SetStatus("Cancelled.", warn: true);
            else { SetStatus("Error.", warn: true); AppendOutput($"!! {result.Error}"); }
        }
        catch (Exception ex)
        {
            SetStatus("Error.", warn: true);
            AppendOutput($"!! {ex.Message}");
        }
        finally
        {
            activity.Dispose();
            _cts.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void AppendOutput(string line)
    {
        OutputBox.AppendText(line + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private void SetStatus(string text, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(warn ? "Danger" : "Subtle");
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
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
