using RoughCut.Core;

namespace RoughCut.Media;

public static class ProjectFiles
{
    public static string Resolve(string projectPath, string relative)
    {
        if (!ProjectValidator.IsPortablePath(relative)) throw new InvalidDataException("Invalid project-relative asset path.");
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var path = root;
        foreach (var component in relative.Split('/'))
        {
            path = Path.Combine(path, component);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Linked asset paths are not supported; use files within the project directory.");
        }
        return path;
    }

    public static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Input file exceeds the configured size limit.");
        using var output = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(block, cancellationToken)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Input file exceeds the configured size limit.");
            output.Write(block, 0, count);
        }
        return output.ToArray();
    }
}
