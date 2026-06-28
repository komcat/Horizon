using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Horizon.UI.Mvvm;
using Horizon.Workflow.Scripting;
using ICSharpCode.AvalonEdit.Document;

namespace Horizon.UI.ViewModels;

/// <summary>
/// The <c>.moon</c> procedural-script page — a Lua editor that runs against the
/// engine through the Layer 2 <c>RunScriptAsync</c> seam (named <c>moveTo</c>,
/// unified <c>signal.read</c>, operator <c>ask</c> all resolve against the active
/// product). Owns the editor <see cref="TextDocument"/> so the script survives
/// navigation in the shell's data-template page host. Syntax highlighting +
/// completion live in the view; the command list comes from
/// <see cref="MachineApiCatalog"/>.
/// </summary>
public sealed class ScriptPageViewModel : PageViewModel
{
    private const string Template =
        "-- machine.* reaches every facet. Examples:\n" +
        "--   machine.motion.moveTo(\"gantry-main\", \"Load\")\n" +
        "--   machine.io.setNamed(\"IOBottom\", \"Vacuum\", true)\n" +
        "--   local v = machine.signal.read(\"smu.PIC.ch1.current\")\n" +
        "--   if machine.ask(\"Continue?\", \"Proceed with attach?\") then ... end\n\n" +
        "machine.log(\"hello from lua\")\n";

    private static readonly (string Name, string Source)[] ExampleData =
    {
        ("Template", Template),

        ("IO on/off cycle (by name)", """
            -- Pulse a named output 3 times, 500 ms on / 500 ms off.
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
            local dev = "gantry-main"
            local axis = "X"

            machine.motion.connect(dev)
            machine.motion.setVelocity(dev, 5)

            machine.log("start X = " .. machine.motion.position(dev).x)
            machine.motion.moveRel(dev, axis, 1.0)
            machine.log("after +1.0, X = " .. machine.motion.position(dev).x)
            machine.wait(300)
            machine.motion.moveRel(dev, axis, -1.0)
            machine.log("after -1.0, X = " .. machine.motion.position(dev).x)
            machine.log("move test done")
            """),

        ("Read a signal", """
            local key = "smu.PIC.ch1.current"
            local v = machine.signal.read(key)
            machine.log(key .. " = " .. v)
            """),

        ("Ask operator + branch", """
            if machine.ask("Confirm", "Proceed with the attach?") then
              machine.log("operator said yes")
            else
              machine.log("operator declined")
            end
            """),
    };

    private readonly EngineHost _host;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    public ScriptPageViewModel(EngineHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Document = new TextDocument(Template);
        ExampleNames = ExampleData.Select(x => x.Name).ToList();
        foreach (var f in MachineApiCatalog.All) FilteredCommands.Add(f);

        LoadCommand = new RelayCommand(LoadSelected);
        SaveCommand = new RelayCommand(SaveCurrent);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
    }

    public override string Title => "Script";

    /// <summary>Editor text — bound to the view's <c>TextEditor.Document</c>.</summary>
    public TextDocument Document { get; }

    public IReadOnlyList<string> ExampleNames { get; }
    public ObservableCollection<string> ScriptNames { get; } = new();
    public ObservableCollection<MachineFn> FilteredCommands { get; } = new();

    public RelayCommand LoadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    private string? _selectedScript;
    public string? SelectedScript { get => _selectedScript; set => SetField(ref _selectedScript, value); }

    private string _saveName = string.Empty;
    public string SaveName { get => _saveName; set => SetField(ref _saveName, value); }

    private string? _selectedExample;
    public string? SelectedExample
    {
        get => _selectedExample;
        set { if (SetField(ref _selectedExample, value) && value != null) LoadExample(value); }
    }

    private string _commandFilter = string.Empty;
    public string CommandFilter
    {
        get => _commandFilter;
        set { if (SetField(ref _commandFilter, value)) ApplyFilter(); }
    }

    private string _output = string.Empty;
    public string Output { get => _output; private set => SetField(ref _output, value); }

    private string _status = "Ready";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => SetField(ref _statusIsError, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public override void OnActivated() => RefreshScripts();

    public override void OnDeactivated() => _cts?.Cancel();

    private void RefreshScripts()
    {
        var current = SelectedScript;
        ScriptNames.Clear();
        foreach (var n in _host.Process.ListScripts()) ScriptNames.Add(n);
        SelectedScript = current != null && ScriptNames.Contains(current) ? current : null;
    }

    private void ApplyFilter()
    {
        FilteredCommands.Clear();
        var q = _commandFilter?.Trim();
        foreach (var f in MachineApiCatalog.All.Where(f =>
                     string.IsNullOrEmpty(q)
                     || f.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || f.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)))
            FilteredCommands.Add(f);
    }

    private void LoadSelected()
    {
        if (string.IsNullOrWhiteSpace(SelectedScript)) { SetStatus("Pick a script to load.", true); return; }
        var source = _host.Process.LoadScript(SelectedScript);
        if (source == null) { SetStatus($"Script '{SelectedScript}' not found.", true); return; }
        Document.Text = source;
        SaveName = SelectedScript;
        SetStatus($"Loaded '{SelectedScript}'.");
    }

    private void SaveCurrent()
    {
        var name = SaveName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SetStatus("Enter a name to save.", true); return; }
        var r = _host.Process.SaveScript(name, Document.Text);
        if (!r.IsSuccess) { SetStatus($"Save failed: {r.Message}", true); return; }
        RefreshScripts();
        SelectedScript = name;
        SetStatus($"Saved '{name}'.");
    }

    private void LoadExample(string name)
    {
        var match = ExampleData.FirstOrDefault(x => x.Name == name);
        if (match.Source != null)
        {
            Document.Text = match.Source;
            SaveName = string.Empty;
            SetStatus("Loaded example: " + name);
        }
        SelectedExample = null;   // reset so the same example can be re-picked (setter guards null)
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        Output = string.Empty;
        SetStatus("Running…");
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _host.Process.RunScriptAsync(
                Document.Text,
                onOutput: line => _dispatcher.BeginInvoke(() => Append(line)),
                ct: _cts.Token);

            if (result.Success) SetStatus("Completed.");
            else if (result.Cancelled) SetStatus("Cancelled.", true);
            else { SetStatus("Error.", true); Append("!! " + result.Error); }
        }
        catch (Exception ex)
        {
            SetStatus("Error.", true);
            Append("!! " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private void Append(string line) => Output += line + Environment.NewLine;

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }
}
