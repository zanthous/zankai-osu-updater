namespace RulesetUpdater;

public static class ExecutablePath
{
    public static bool IsExpected(string? processPath, string expectedPath)
    {
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath) || !File.Exists(expectedPath)) return false;
        // Parent directory aliases are legitimate (macOS /var -> /private/var), but the
        // executable itself must still be the regular file at the expected install location.
        UpdateStorage.RequireRegularPath(processPath);
        UpdateStorage.RequireRegularPath(expectedPath);
        return string.Equals(resolveParentAliases(processPath), resolveParentAliases(expectedPath),
                             OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string resolveParentAliases(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath)!;
        string root = Path.GetPathRoot(parent)!;
        string resolved = root;
        foreach (string part in parent[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            // ResolveLinkTarget on the executable alone would miss links in its ancestors.
            resolved = new DirectoryInfo(resolved).ResolveLinkTarget(true)?.FullName ?? resolved;
        }
        return Path.Combine(resolved, Path.GetFileName(fullPath));
    }
}
