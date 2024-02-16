using Spectre.Console;

namespace PatchSync.CLI;

public static class CLIText
{
    public static void WriteFilePathText(string path)
    {
        var manifestTextPath = new TextPath(Path.GetFullPath(path));
        manifestTextPath.StemStyle = new Style(Color.Blue);

        var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        manifestTextPath.LeafStyle = new Style(isDirectory ? Color.Blue : Color.Green);

        AnsiConsole.Write(manifestTextPath);
    }
}
