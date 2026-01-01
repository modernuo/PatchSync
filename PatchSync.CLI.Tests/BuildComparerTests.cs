using PatchSync.CLI.Build;
using PatchSync.Common.Manifest;

namespace PatchSync.CLI.Tests;

/// <summary>
/// Tests for BuildComparer comparing input files against a base manifest.
/// </summary>
public class BuildComparerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;

    public BuildComparerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patchsync_build_compare_test_{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(_inputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompareAsync_EmptyInput_AllFilesDeleted()
    {
        // Arrange
        var baseManifest = CreateManifest("1.0.0",
            ("file1.txt", "abc123", 100, UpdateStrategy.Delta),
            ("file2.txt", "def456", 200, UpdateStrategy.HashCheck));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Empty(result.NewFiles);
        Assert.Empty(result.ModifiedFiles);
        Assert.Empty(result.UnchangedFiles);
        Assert.Equal(2, result.DeletedFiles.Count);
        Assert.All(result.DeletedFiles, f => Assert.Equal(FileChangeType.Deleted, f.ChangeType));
    }

    [Fact]
    public async Task CompareAsync_NewFilesOnly_DetectsAllAsNew()
    {
        // Arrange
        CreateFile("new1.txt", "content1");
        CreateFile("new2.txt", "content2");
        CreateFile("subdir/new3.txt", "content3");

        var baseManifest = CreateManifest("1.0.0"); // Empty base

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Equal(3, result.NewFiles.Count);
        Assert.Empty(result.ModifiedFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.Empty(result.UnchangedFiles);
        Assert.All(result.NewFiles, f => Assert.Equal(FileChangeType.New, f.ChangeType));
    }

    [Fact]
    public async Task CompareAsync_IdenticalFiles_DetectsAsUnchanged()
    {
        // Arrange
        var content = "identical content";
        CreateFile("same.txt", content);
        var hash = ComputeHash(content);

        var baseManifest = CreateManifest("1.0.0",
            ("same.txt", hash, content.Length, UpdateStrategy.Delta));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Empty(result.NewFiles);
        Assert.Empty(result.ModifiedFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.Single(result.UnchangedFiles);
        Assert.Equal(FileChangeType.Unchanged, result.UnchangedFiles[0].ChangeType);
        Assert.Equal(hash, result.UnchangedFiles[0].CurrentHash);
    }

    [Fact]
    public async Task CompareAsync_ModifiedFile_DetectsAsModified()
    {
        // Arrange
        CreateFile("modified.txt", "new content");
        var oldHash = ComputeHash("old content");

        var baseManifest = CreateManifest("1.0.0",
            ("modified.txt", oldHash, "old content".Length, UpdateStrategy.Delta));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Empty(result.NewFiles);
        Assert.Single(result.ModifiedFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.Empty(result.UnchangedFiles);

        var modified = result.ModifiedFiles[0];
        Assert.Equal(FileChangeType.Modified, modified.ChangeType);
        Assert.Equal(oldHash, modified.BaseHash);
        Assert.NotEqual(oldHash, modified.CurrentHash);
    }

    [Fact]
    public async Task CompareAsync_MixedChanges_DetectsAllTypes()
    {
        // Arrange
        var unchangedContent = "unchanged";
        var unchangedHash = ComputeHash(unchangedContent);

        CreateFile("unchanged.txt", unchangedContent);
        CreateFile("modified.txt", "modified new content");
        CreateFile("new.txt", "brand new file");
        // deleted.txt is not created

        var baseManifest = CreateManifest("1.0.0",
            ("unchanged.txt", unchangedHash, unchangedContent.Length, UpdateStrategy.Delta),
            ("modified.txt", ComputeHash("old content"), 11, UpdateStrategy.Delta),
            ("deleted.txt", ComputeHash("deleted"), 7, UpdateStrategy.HashCheck));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.NewFiles);
        Assert.Single(result.ModifiedFiles);
        Assert.Single(result.DeletedFiles);
        Assert.Single(result.UnchangedFiles);
        Assert.Equal(4, result.TotalFiles);
    }

    [Fact]
    public async Task CompareAsync_PreservesCreateOnlyStrategy()
    {
        // Arrange
        CreateFile("config.ini", "modified config");
        var oldHash = ComputeHash("original config");

        var baseManifest = CreateManifest("1.0.0",
            ("config.ini", oldHash, 15, UpdateStrategy.CreateOnly));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.ModifiedFiles);
        var modified = result.ModifiedFiles[0];
        Assert.Equal(UpdateStrategy.CreateOnly, modified.SuggestedStrategy);
    }

    [Fact]
    public async Task CompareAsync_PreservesUpdateIfNotModifiedStrategy()
    {
        // Arrange
        CreateFile("user-config.ini", "user modified config");
        var oldHash = ComputeHash("default config");

        var baseManifest = CreateManifest("1.0.0",
            ("user-config.ini", oldHash, 14, UpdateStrategy.UpdateIfNotModified));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.ModifiedFiles);
        var modified = result.ModifiedFiles[0];
        Assert.Equal(UpdateStrategy.UpdateIfNotModified, modified.SuggestedStrategy);
    }

    [Fact]
    public async Task CompareAsync_DeletedFile_SuggestsDeleteStrategy()
    {
        // Arrange
        // Don't create any input files

        var baseManifest = CreateManifest("1.0.0",
            ("obsolete.dll", "hash123", 1000, UpdateStrategy.Delta));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.DeletedFiles);
        Assert.Equal(UpdateStrategy.Delete, result.DeletedFiles[0].SuggestedStrategy);
    }

    [Fact]
    public async Task CompareAsync_NewCompressedMedia_SuggestsAlwaysCompressed()
    {
        // Arrange
        CreateFile("music.mp3", "fake mp3 content");
        CreateFile("image.png", "fake png content");
        CreateFile("archive.zip", "fake zip content");

        var baseManifest = CreateManifest("1.0.0");

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Equal(3, result.NewFiles.Count);
        Assert.All(result.NewFiles, f => Assert.Equal(UpdateStrategy.AlwaysCompressed, f.SuggestedStrategy));
    }

    [Fact]
    public async Task CompareAsync_NewContainerFormat_SuggestsVirtualDelta()
    {
        // Arrange
        CreateFile("game.uop", "fake uop content for testing");
        CreateFile("data.pak", "fake pak content for testing");

        var baseManifest = CreateManifest("1.0.0");

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Equal(2, result.NewFiles.Count);
        Assert.All(result.NewFiles, f => Assert.Equal(UpdateStrategy.VirtualDelta, f.SuggestedStrategy));
    }

    [Fact]
    public async Task CompareAsync_NewSmallFile_SuggestsHashCheck()
    {
        // Arrange
        CreateFile("small.txt", "tiny"); // Under 64KB threshold

        var baseManifest = CreateManifest("1.0.0");

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.NewFiles);
        Assert.Equal(UpdateStrategy.HashCheck, result.NewFiles[0].SuggestedStrategy);
    }

    [Fact]
    public async Task CompareAsync_NewLargeFile_SuggestsDelta()
    {
        // Arrange
        var largeContent = new string('X', 100_000); // Over 64KB threshold
        CreateFile("large.dat", largeContent);

        var baseManifest = CreateManifest("1.0.0");

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Single(result.NewFiles);
        Assert.Equal(UpdateStrategy.Delta, result.NewFiles[0].SuggestedStrategy);
    }

    [Fact]
    public async Task CompareAsync_CaseInsensitivePaths()
    {
        // Arrange
        var content = "content";
        var hash = ComputeHash(content);
        CreateFile("SomeFile.TXT", content);

        var baseManifest = CreateManifest("1.0.0",
            ("somefile.txt", hash, content.Length, UpdateStrategy.Delta));

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Empty(result.NewFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.Empty(result.ModifiedFiles);
        Assert.Single(result.UnchangedFiles);
    }

    [Fact]
    public async Task CompareAsync_NormalizesPathSeparators()
    {
        // Arrange
        var content = "content";
        var hash = ComputeHash(content);
        Directory.CreateDirectory(Path.Combine(_inputDir, "subdir"));
        CreateFile("subdir/file.txt", content);

        var baseManifest = CreateManifest("1.0.0",
            ("subdir\\file.txt", hash, content.Length, UpdateStrategy.Delta)); // Windows-style path

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Empty(result.NewFiles);
        Assert.Empty(result.DeletedFiles);
        Assert.Single(result.UnchangedFiles);
    }

    [Fact]
    public async Task CompareAsync_ReportsProgress()
    {
        // Arrange
        CreateFile("file1.txt", "content1");
        var progressMessages = new List<string>();
        var progress = new Progress<string>(msg => progressMessages.Add(msg));

        var baseManifest = CreateManifest("1.0.0");

        var comparer = new BuildComparer(progress);

        // Act
        await comparer.CompareAsync(_inputDir, baseManifest);

        // Allow progress callbacks to complete
        await Task.Delay(100);

        // Assert
        Assert.NotEmpty(progressMessages);
    }

    [Fact]
    public async Task CompareAsync_SupportsCanellation()
    {
        // Arrange
        CreateFile("file1.txt", "content1");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var baseManifest = CreateManifest("1.0.0");
        var comparer = new BuildComparer();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            comparer.CompareAsync(_inputDir, baseManifest, cts.Token));
    }

    [Fact]
    public async Task CompareAsync_SetsBaseVersionInfo()
    {
        // Arrange
        var buildDate = DateTime.UtcNow.AddDays(-1);
        var baseManifest = new GameManifest
        {
            Version = "1.2.3",
            BuildDate = buildDate,
            SupportedAlgorithms = ["fastcdc-v1"],
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "https://cdn.example.com/",
            Files = []
        };

        var comparer = new BuildComparer();

        // Act
        var result = await comparer.CompareAsync(_inputDir, baseManifest);

        // Assert
        Assert.Equal("1.2.3", result.BaseVersion);
        Assert.Equal(buildDate, result.BaseBuiltAt);
    }

    private void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_inputDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    private static GameManifest CreateManifest(string version, params (string path, string hash, long size, UpdateStrategy strategy)[] files)
    {
        return new GameManifest
        {
            Version = version,
            BuildDate = DateTime.UtcNow,
            SupportedAlgorithms = ["fastcdc-v1"],
            PreferredAlgorithm = "fastcdc-v1",
            BaseUrl = "https://cdn.example.com/",
            Files = files.Select(f => new ManifestFile
            {
                Path = f.path,
                Hash = f.hash,
                Size = f.size,
                Strategy = f.strategy
            }).ToList()
        };
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
