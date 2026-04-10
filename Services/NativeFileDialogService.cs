using System.Diagnostics;

namespace PiecrustAnalyser.CSharp.Services;

public sealed class NativeFileDialogService
{
    public async Task<string[]> PickFilesAsync()
    {
        if (!OperatingSystem.IsMacOS()) return Array.Empty<string>();

        var script = string.Join('\n',
            "set AppleScript's text item delimiters to linefeed",
            "set chosenFiles to choose file with prompt \"Select TIFF/SPM files\" with multiple selections allowed",
            "set output to {}",
            "repeat with f in chosenFiles",
            "set end of output to POSIX path of f",
            "end repeat",
            "return output as text");

        var start = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);

        using var process = Process.Start(start);
        if (process is null) return Array.Empty<string>();

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
            return Array.Empty<string>();
        }

        return stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(File.Exists)
            .ToArray();
    }

    public async Task<string?> PickSavePathAsync(string prompt, string suggestedFileName)
    {
        if (!OperatingSystem.IsMacOS()) return null;

        var script = string.Join('\n',
            $"set chosenFile to choose file name with prompt \"{EscapeAppleScript(prompt)}\" default name \"{EscapeAppleScript(suggestedFileName)}\"",
            "return POSIX path of chosenFile");

        var start = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);

        using var process = Process.Start(start);
        if (process is null) return null;

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase)) return null;
            return null;
        }

        var path = stdout.Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string EscapeAppleScript(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
