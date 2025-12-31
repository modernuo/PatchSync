using System.Runtime.InteropServices;
using Spectre.Console;

namespace PatchSync.CLI.Prompts;

/// <summary>
/// Interactive file and folder browser using Spectre.Console.
/// Supports navigation, drive selection (Windows), and file filtering.
/// </summary>
public class FileBrowser
{
    private const int PageSize = 15;

    private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Spectre.Console emoji codes - see https://spectreconsole.net/console/reference/emoji-reference
    private static class Icons
    {
        public const string Folder = ":file_folder:";                // 📁
        public const string FolderOpen = ":open_file_folder:";       // 📂
        public const string File = ":page_facing_up:";               // 📄
        public const string Drive = ":optical_disk:";                // 💿
        public const string Up = ":upwards_button:";                 // 🔼
        public const string Check = ":check_mark_button:";           // ✅
        public const string Plus = ":plus:";                         // ➕
        public const string Lock = ":locked:";                       // 🔒
        public const string HammerAndWrench = ":hammer_and_wrench:"; // 🛠
        public const string Package = ":package:";                   // 📦
        public const string Image = ":framed_picture:";              // 🖼
        public const string Music = ":musical_note:";                // 🎵
        public const string Video = ":clapper_board:";               // 🎬
        public const string Code = ":scroll:";                       // 📜
        public const string Note = ":memo:";                         // 📝
        public const string Ok = ":ok_button:";                      // 🆗
    }

    private bool _selectFile;
    private bool _mustExist = true;

    /// <summary>
    /// Whether to display icons (emojis) next to files and folders.
    /// </summary>
    public bool DisplayIcons { get; set; } = true;

    /// <summary>
    /// Starting directory for browsing.
    /// </summary>
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Title shown above the selection prompt.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// File search pattern (e.g., "*.json", "*.sig").
    /// </summary>
    public string SearchPattern { get; set; } = "*";

    /// <summary>
    /// Configure to select a file.
    /// </summary>
    public FileBrowser SelectFile()
    {
        _selectFile = true;
        return this;
    }

    /// <summary>
    /// Configure to select a directory.
    /// </summary>
    public FileBrowser SelectDirectory()
    {
        _selectFile = false;
        return this;
    }

    /// <summary>
    /// Allow selecting a path that doesn't exist yet (for output paths).
    /// </summary>
    public FileBrowser AllowNew()
    {
        _mustExist = false;
        return this;
    }

    /// <summary>
    /// Set the file filter pattern.
    /// </summary>
    public FileBrowser WithPattern(string pattern)
    {
        SearchPattern = pattern;
        return this;
    }

    /// <summary>
    /// Set the starting directory.
    /// </summary>
    public FileBrowser StartingFrom(string path)
    {
        if (Directory.Exists(path))
            WorkingDirectory = path;
        return this;
    }

    /// <summary>
    /// Set the title.
    /// </summary>
    public FileBrowser WithTitle(string title)
    {
        Title = title;
        return this;
    }

    /// <summary>
    /// Show the browser and get the selected path.
    /// </summary>
    public string Browse()
    {
        var currentDir = Directory.Exists(WorkingDirectory) ? WorkingDirectory : UserProfilePath;
        var lastValidDir = currentDir;

        while (true)
        {
            var choices = new List<(string Display, string Path, ChoiceType Type)>();

            // Get directories safely
            string[] directories;
            try
            {
                Directory.SetCurrentDirectory(currentDir);
                directories = Directory.GetDirectories(currentDir);
                lastValidDir = currentDir;
            }
            catch (UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine("[red]Access denied to this folder[/]");
                currentDir = lastValidDir;
                continue;
            }
            catch
            {
                currentDir = lastValidDir;
                continue;
            }

            // Drive selection (Windows only)
            if (IsWindows)
            {
                choices.Add((
                    FormatChoice(Icons.Drive, "Change Drive", Color.Green),
                    "::DRIVE::",
                    ChoiceType.Drive
                ));
            }

            // Parent directory
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir != null)
            {
                choices.Add((
                    FormatChoice(Icons.Up, "..", Color.Green),
                    parentDir.FullName,
                    ChoiceType.Parent
                ));
            }

            // Select current folder (when selecting directories)
            if (!_selectFile)
            {
                choices.Add((
                    FormatChoice(Icons.Ok, "Select This Folder", Color.Blue),
                    currentDir,
                    ChoiceType.Select
                ));

                // Create new folder option
                if (!_mustExist)
                {
                    choices.Add((
                        FormatChoice(Icons.Plus, "Create New Folder Here...", Color.Yellow),
                        "::NEW::",
                        ChoiceType.New
                    ));
                }
            }

            // Subdirectories
            foreach (var dir in directories.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                choices.Add((
                    FormatChoice(Icons.Folder, name, Color.Default),
                    dir,
                    ChoiceType.Directory
                ));
            }

            // Files (when selecting files)
            if (_selectFile)
            {
                try
                {
                    var files = Directory.GetFiles(currentDir, SearchPattern)
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

                    foreach (var file in files)
                    {
                        var name = Path.GetFileName(file);
                        var icon = GetFileIcon(file);
                        choices.Add((
                            FormatChoice(icon, name, Color.Default),
                            file,
                            ChoiceType.File
                        ));
                    }
                }
                catch
                {
                    // Ignore file access errors
                }
            }

            // Build title
            var title = Title ?? (_selectFile ? "[green]Select File[/]" : "[green]Select Folder[/]");
            // Use console width minus some padding for "Current: " prefix, or minimum 80
            var maxPathLength = Math.Max(80, Console.WindowWidth - 15);
            var currentDisplay = TruncatePath(currentDir, maxPathLength);

            var prompt = new SelectionPrompt<string>()
                .Title($"{title}\n[grey]Current:[/] [blue]{currentDisplay}[/]")
                .PageSize(PageSize)
                .HighlightStyle(new Style(Color.Cyan1))
                .MoreChoicesText("[grey]↑↓ to navigate, Enter to select[/]")
                .AddChoices(choices.Select(c => c.Display));

            var selected = AnsiConsole.Prompt(prompt);
            var choice = choices.First(c => c.Display == selected);

            switch (choice.Type)
            {
                case ChoiceType.Drive:
                    currentDir = SelectDrive();
                    break;

                case ChoiceType.Parent:
                case ChoiceType.Directory:
                    currentDir = choice.Path;
                    break;

                case ChoiceType.Select:
                    return choice.Path;

                case ChoiceType.File:
                    return choice.Path;

                case ChoiceType.New:
                    var newName = AnsiConsole.Prompt(
                        new TextPrompt<string>("[green]New folder name:[/]")
                            .Validate(name =>
                            {
                                if (string.IsNullOrWhiteSpace(name))
                                    return ValidationResult.Error("Name cannot be empty");
                                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                                    return ValidationResult.Error("Name contains invalid characters");
                                return ValidationResult.Success();
                            }));

                    var newPath = Path.Combine(currentDir, newName);
                    try
                    {
                        Directory.CreateDirectory(newPath);
                        AnsiConsole.MarkupLine($"[green]Created:[/] {newPath}");
                        return newPath;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to create folder:[/] {ex.Message}");
                    }
                    break;
            }
        }
    }

    private string SelectDrive()
    {
        var drives = Directory.GetLogicalDrives();

        var driveInfos = drives.Select(d =>
        {
            try
            {
                var info = new DriveInfo(d);
                var label = info.IsReady ? Markup.Escape(info.VolumeLabel) : "Not Ready";
                var size = info.IsReady ? FormatBytes(info.TotalSize) : "";
                var free = info.IsReady ? FormatBytes(info.AvailableFreeSpace) : "";
                return (
                    Drive: d,
                    Display: DisplayIcons
                        ? $"{Icons.Drive} {d.TrimEnd('\\')} [[{label}]] {size} ({free} free)"
                        : $"{d.TrimEnd('\\')} [[{label}]] {size} ({free} free)",
                    info.IsReady
                );
            }
            catch
            {
                return (Drive: d, Display: DisplayIcons ? $"{Icons.Drive} {d}" : d, IsReady: false);
            }
        }).ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select Drive[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(driveInfos.Select(d => d.Display)));

        var drive = driveInfos.First(d => d.Display == selected);
        return drive.Drive;
    }

    private string FormatChoice(string icon, string text, Color color)
    {
        var coloredText = color == Color.Default ? text : $"[{color.ToMarkup()}]{text}[/]";
        return DisplayIcons && !string.IsNullOrEmpty(icon) ? $"{icon} {coloredText}" : coloredText;
    }

    private static string GetFileIcon(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json"                                                             => Icons.File,
            ".sig"                                                              => Icons.Lock,
            ".exe" or ".dll"                                                    => Icons.HammerAndWrench,
            ".zip" or ".gz" or ".zst" or ".7z" or ".rar" or ".pak" or ".bundle" => Icons.Package,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"          => Icons.Image,
            ".mp3" or ".ogg" or ".wav" or ".flac"                               => Icons.Music,
            ".mp4" or ".webm" or ".avi" or ".mkv"                               => Icons.Video,
            ".txt" or ".md" or ".log"                                           => Icons.Note,
            ".cs" or ".js" or ".ts" or ".py" or ".go" or ".rs"                  => Icons.Code,
            ".xml" or ".yaml" or ".yml" or ".toml"                              => Icons.HammerAndWrench,
            _                                                                   => Icons.File
        };
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength)
            return path;

        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length <= 2)
            return path[..(maxLength - 3)] + "...";

        // Keep the root (e.g., "C:") and as many parts from the end as fit
        var root = parts[0] + Path.DirectorySeparatorChar;
        var ellipsis = "...";
        var availableLength = maxLength - root.Length - ellipsis.Length - 1; // -1 for separator after ellipsis

        // Build from the end, adding as many path segments as fit
        var endParts = new List<string>();
        var currentLength = 0;

        for (int i = parts.Length - 1; i > 0; i--)
        {
            var partLength = parts[i].Length + 1; // +1 for separator
            if (currentLength + partLength > availableLength)
                break;
            endParts.Insert(0, parts[i]);
            currentLength += partLength;
        }

        if (endParts.Count == 0)
        {
            // Can't fit any parts, just truncate the last part
            return root + ellipsis + Path.DirectorySeparatorChar + parts[^1][..Math.Min(parts[^1].Length, availableLength)];
        }

        return root + ellipsis + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, endParts);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} GB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        };
    }

    private enum ChoiceType
    {
        Drive,
        Parent,
        Directory,
        File,
        Select,
        New
    }
}

/// <summary>
/// Quick access methods for file/folder browsing.
/// </summary>
public static class Browse
{
    /// <summary>
    /// Browse for a folder.
    /// </summary>
    public static string ForFolder(string? title = null, string? startPath = null, bool allowNew = false)
    {
        var browser = new FileBrowser().SelectDirectory();

        if (title != null)
            browser.WithTitle(title);
        if (startPath != null)
            browser.StartingFrom(startPath);
        if (allowNew)
            browser.AllowNew();

        return browser.Browse();
    }

    /// <summary>
    /// Browse for a file.
    /// </summary>
    public static string ForFile(string? title = null, string? startPath = null, string pattern = "*")
    {
        var browser = new FileBrowser()
            .SelectFile()
            .WithPattern(pattern);

        if (title != null)
            browser.WithTitle(title);
        if (startPath != null)
            browser.StartingFrom(startPath);

        return browser.Browse();
    }
}
