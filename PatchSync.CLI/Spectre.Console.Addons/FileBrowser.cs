using System.Runtime.InteropServices;
using Spectre.Console;

namespace PatchSync.CLI;

public class FileBrowser
{
    private const int PageSize = 15;
    private const string LevelUpText = "..";
    private const string CurrentFolder = "Current Folder";
    private const string MoreChoicesText = "Use up and down arrows to select";
    private const string SelectFileText = "[b][green]Select File[/][/]";
    private const string SelectFolderText = "[b][green]Select Folder[/][/]";
    private const string SelectDriveText = "Change Drives";
    private const string SelectActualText = "Select Folder";

    private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private bool _selectFile;

    public bool DisplayIcons { get; set; } = true;
    public string WorkingDirectory { get; set; } = UserProfilePath;
    public string? Title { get; set; }

    public string SearchPattern { get; set; } = "*";

    public string? PromptMessage { get; set; }

    public FileBrowser SelectFile()
    {
        _selectFile = true;
        return this;
    }

    public FileBrowser SelectDirectory()
    {
        _selectFile = false;
        return this;
    }

    public string GetPath()
    {
        var selectedPath = InternalGetPath();
        if (!string.IsNullOrEmpty(PromptMessage))
        {
            // Can't use interpolated cause it doesn't cascade and we may have markup in the prompt message
            AnsiConsole.MarkupLine($"{PromptMessage}: [blue]{selectedPath}[/]");
        }

        return selectedPath;
    }

    private string InternalGetPath()
    {
        var actualFolder = WorkingDirectory;

        var lastFolder = actualFolder;
        while (true)
        {
            string[] directoriesInFolder;
            Directory.SetCurrentDirectory(actualFolder);

            var folders = new Dictionary<string, string>();

            try
            {
                directoriesInFolder = Directory.GetDirectories(Directory.GetCurrentDirectory());
                lastFolder = actualFolder;
            }
            catch
            {
                actualFolder = actualFolder == lastFolder ? UserProfilePath : lastFolder;
                Directory.SetCurrentDirectory(actualFolder);
                directoriesInFolder = Directory.GetDirectories(Directory.GetCurrentDirectory());
            }

            if (IsWindows)
            {
                folders.Add(
                    DisplayIcons ? $":computer_disk: [green]{SelectDriveText}[/]" : $"[green]{SelectDriveText}[/]",
                    "/////"
                );
            }

            try
            {
                if (new DirectoryInfo(actualFolder).Parent != null)
                {
                    folders.Add(
                        DisplayIcons ? $":upwards_button: [green]{LevelUpText}[/]" : $"[green]{LevelUpText}[/]",
                        new DirectoryInfo(actualFolder).Parent?.FullName!
                    );
                }
            }
            catch
            {
            }

            if (!_selectFile)
            {
                folders.Add(
                    DisplayIcons ? $":ok_button: [green]{SelectActualText}[/]" : $"[green]{SelectActualText}[/]",
                    Directory.GetCurrentDirectory()
                );
            }

            foreach (var d in directoriesInFolder)
            {
                var folderName = d[(actualFolder.Length + (new DirectoryInfo(actualFolder).Parent != null ? 1 : 0))..];
                folders.Add(DisplayIcons ? $":file_folder: {folderName}" : folderName, d);
            }

            if (_selectFile)
            {
                foreach (var file in Directory.EnumerateFiles(actualFolder, SearchPattern))
                {
                    var result = Path.GetFileName(file);
                    folders.Add(DisplayIcons ? $":page_facing_up: {result}" : result, file);
                }
            }

            var title = Title ?? (_selectFile ? SelectFileText : SelectFolderText);

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"{title}: ({CurrentFolder}: [orange3]{actualFolder}[/])")
                    .PageSize(PageSize)
                    .MoreChoicesText($"[grey]{MoreChoicesText}[/]")
                    .AddChoices(folders.Keys)
            );

            lastFolder = actualFolder;
            var record = folders[selected];

            if (record == "/////")
            {
                record = SelectDrive();
                actualFolder = record;
            }

            if (record == Directory.GetCurrentDirectory())
            {
                return actualFolder;
            }

            try
            {
                if (Directory.Exists(record))
                {
                    actualFolder = record;
                }
                else if (File.Exists(record))
                {
                    return record;
                }
            }
            catch
            {
                AnsiConsole.WriteLine("[red]You have no access to this folder[/]");
            }
        }
    }

    private string SelectDrive() =>
        AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[green]{SelectDriveText}:[/]")
                .PageSize(PageSize)
                .MoreChoicesText($"[grey]{MoreChoicesText}[/]")
                .AddChoices(Directory.GetLogicalDrives())
                .UseConverter(drive => DisplayIcons ? $":computer_disk: {drive}" : drive)
        );
}
