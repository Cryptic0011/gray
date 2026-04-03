using System.Reflection;
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public static class AppInfoService
{
    public static string ProductName =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? "gray";

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "0.0.0";

    public static string? RepositoryUrl =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;

    public static ToolStatus GetToolStatus(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new ToolStatus(command, false, null, "Command is empty");

        string executable = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim('"');
        string? resolved = ResolveOnPath(executable);
        return resolved == null
            ? new ToolStatus(command, false, null, $"{executable} was not found on PATH")
            : new ToolStatus(command, true, resolved, $"{executable} found");
    }

    private static string? ResolveOnPath(string executable)
    {
        if (Path.IsPathRooted(executable) && File.Exists(executable))
            return executable;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        var candidates = executable.Contains('.')
            ? [executable]
            : new[] { executable, $"{executable}.exe", $"{executable}.cmd", $"{executable}.bat" };

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(dir.Trim(), candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }
}
