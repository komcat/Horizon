namespace Horizon.Process.Models;

public sealed class ProcessProgramStep
{
    public string PluginId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string Uid { get; set; } = "";
    public string CustomName { get; set; } = "";
}

public sealed class ProcessProgram
{
    public string Name { get; set; } = "Untitled";
    public List<ProcessProgramStep> Steps { get; set; } = new();
}
