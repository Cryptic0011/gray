namespace Gmux.Core.Models;

public record ToolStatus(string Command, bool IsAvailable, string? ResolvedPath, string Message);
