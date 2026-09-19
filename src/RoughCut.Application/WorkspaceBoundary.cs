namespace RoughCut.Application;

public sealed class WorkspaceBoundary
{
    public WorkspaceBoundary(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A workspace root is required.");
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException("A linked workspace root is not supported.");
    }

    public string Root { get; }

    public string Resolve(string relativePath, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("Paths must be workspace-relative.");
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Paths must use non-empty workspace-relative components.");
        var path = Root;
        foreach (var component in normalized.Split('/'))
        {
            path = Path.Combine(path, component);
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Linked paths are not supported inside the workspace.");
        }
        path = Path.GetFullPath(path);
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path escapes the configured workspace.");
        if (mustExist && !File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Workspace path does not exist.");
        return path;
    }
}
