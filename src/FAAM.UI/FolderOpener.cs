using System.Diagnostics;
using System.IO;

namespace FAAM.UI;

/// <summary>
/// Opens a data folder in Windows Explorer for the operator (DUT Data "Open folder" buttons).
/// Creates the folder first when it does not exist yet, so an empty product still opens
/// somewhere sensible instead of failing. Returns an error message, or null on success.
/// </summary>
internal static class FolderOpener
{
    public static string? Open(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "No product is active.";
        try
        {
            Directory.CreateDirectory(directory);
            using var _ = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + directory + "\"",
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }
}
