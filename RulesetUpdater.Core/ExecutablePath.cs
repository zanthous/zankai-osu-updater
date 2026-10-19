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
        int remainingLinks = 64;
        string parent = resolveDirectoryAliases(Path.GetDirectoryName(fullPath)!, ref remainingLinks);
        return Path.Combine(parent, Path.GetFileName(fullPath));
    }

    private static string resolveDirectoryAliases(string directory, ref int remainingLinks)
    {
        string root = Path.GetPathRoot(directory)!;
        string resolved = root;
        foreach (string part in directory[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var target = new DirectoryInfo(resolved).ResolveLinkTarget(false);
            if (target == null) continue;
            if (--remainingLinks < 0) throw new IOException("Too many symbolic links in executable path.");
            // A link's target can itself contain aliased parents, e.g. /var on macOS.
            // Even ResolveLinkTarget(true) doesn't canonicalise those ancestors.
            resolved = resolveDirectoryAliases(target.FullName, ref remainingLinks);
        }
        return resolved;
    }
}
