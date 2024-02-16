namespace PatchSync.Common;

public static class PathUtilities
{
    public static string EnsureDirectory(string baseDir, string dir)
    {
        var path = Path.Combine(baseDir, dir);
        Directory.CreateDirectory(path);

        return path;
    }

    public static string EnsureRandomPath(string basePath) => EnsureDirectory(basePath, Path.GetRandomFileName());

    public static void CopyDirectoryContents(string sourceDir, string destDir, bool recursive = true)
    {
        var dir = new DirectoryInfo(sourceDir);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        Directory.CreateDirectory(destDir);

        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        if (recursive)
        {
            foreach (DirectoryInfo subdir in dir.GetDirectories())
            {
                string destSubDir = Path.Combine(destDir, subdir.Name);
                CopyDirectoryContents(subdir.FullName, destSubDir);
            }
        }
    }

    public static void MoveDirectoryContents(string sourceDir, string destDir, bool recursive = true)
    {
        var dir = new DirectoryInfo(sourceDir);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        Directory.CreateDirectory(destDir);

        foreach (FileInfo file in dir.GetFiles())
        {
            file.MoveTo(Path.Combine(destDir, file.Name));
        }

        if (recursive)
        {
            foreach (DirectoryInfo subdir in dir.GetDirectories())
            {
                string destSubDir = Path.Combine(destDir, subdir.Name);
                MoveDirectoryContents(subdir.FullName, destSubDir);
            }
        }

        try
        {
            dir.Delete(true);
        }
        catch
        {
            // ignored
        }
    }
}
