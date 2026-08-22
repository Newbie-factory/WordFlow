using System.Drawing;
using System.Security.Cryptography;

namespace WordFlow.App.Tests.Bootstrap;

public sealed class BrandingArtifactTests
{
    private static readonly int[] RequiredSizes = [16, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void GeneratedIconsContainTheRequiredVisibleSquareFramesAndDiffer()
    {
        string iconsDirectory = Path.Combine(FindRepositoryRoot(), "src", "WordFlow.App", "Assets", "Icons");
        string photoPath = Path.Combine(iconsDirectory, "wordflow-photo.ico");
        string classicPath = Path.Combine(iconsDirectory, "wordflow-classic.ico");

        Assert.True(File.Exists(photoPath), $"Branding artifact is missing: {photoPath}");
        Assert.True(File.Exists(classicPath), $"Branding artifact is missing: {classicPath}");

        AssertIcon(photoPath);
        AssertIcon(classicPath);
        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(photoPath))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(classicPath))));
    }

    [Fact]
    public void ProjectAllowsTheApplicationIconToBeSelectedAtBuildTime()
    {
        string project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "WordFlow.App", "WordFlow.App.csproj"));

        Assert.Contains("<WordFlowApplicationIcon Condition=\"'$(WordFlowApplicationIcon)' == ''\">", project);
        Assert.Contains("<ApplicationIcon>$(WordFlowApplicationIcon)</ApplicationIcon>", project);
    }

    [Fact]
    public void ProjectKeepsAllSharedDataFilesOutsideTheSingleFileBundle()
    {
        string project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "WordFlow.App", "WordFlow.App.csproj"));

        Assert.Equal(4, project.Split("ExcludeFromSingleFile=\"true\"").Length - 1);
        Assert.Equal(4, project.Split("CopyToPublishDirectory=\"PreserveNewest\"").Length - 1);
    }

    [Fact]
    public void TrayIconUsesBrandingFromTheRunningExecutable()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "WordFlow.Infrastructure", "Windows", "TrayIconService.cs"));

        Assert.Contains("Environment.ProcessPath", source);
        Assert.Contains("Icon.ExtractAssociatedIcon", source);
    }

    [Fact]
    public void DualPublishCannotReuseTheFirstVariantsAppHost()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "build-second-iteration.ps1"));

        Assert.Contains("& dotnet 'clean'", script);
        Assert.Contains("Published executables are identical", script);
    }

    [Fact]
    public void DualPublishRequiresBothDatabasesAndBothManifests()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "build-second-iteration.ps1"));

        Assert.Contains("Data\\ielts\\vocabulary.sqlite3", script);
        Assert.Contains("Data\\ielts\\relations.sqlite3", script);
        Assert.Contains("Data\\ielts\\manifest.json", script);
        Assert.Contains("Data\\ielts\\relations-manifest.json", script);
    }

    [Fact]
    public void DualPublishUsesAnEncodingSupportedByWindowsPowerShell()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "build-second-iteration.ps1"));

        Assert.DoesNotContain("utf8NoBOM", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new-object System.Text.UTF8Encoding($false)", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecondIterationVerifierTreatsCleanupAsARequiredFinalGate()
    {
        string script = ReadVerificationScript();

        Assert.Contains("function Complete-CleanupGate", script);
        Assert.Contains("cleanupSucceeded", script);
        Assert.Contains("remainingPids", script);
        Assert.Contains("tempRootRemoved", script);
    }

    [Fact]
    public void SecondIterationVerifierParsesTestTotalsAndSkipsFromActualOutput()
    {
        string script = ReadVerificationScript();

        Assert.Contains("function Read-TestOutputSummary", script);
        Assert.Contains("Could not parse per-project test totals, failures, and skips", script);
        Assert.Contains("Expected zero skipped tests", script);
        Assert.Contains("skipped", script);
        Assert.DoesNotContain("$evidence.tests = [pscustomobject][ordered]@{ total = $testTotal; failed = 0; skipped = 0 }", script);
    }

    [Fact]
    public void SecondIterationVerifierCrossChecksEveryManifestExecutableAndHead()
    {
        string script = ReadVerificationScript();

        Assert.Contains("function Assert-ReleaseManifestMatchesFiles", script);
        Assert.Contains("git rev-parse HEAD", script);
        Assert.Contains("Manifest filename mismatch", script);
        Assert.Contains("Manifest size mismatch", script);
        Assert.Contains("Manifest SHA256 mismatch", script);
    }

    [Fact]
    public void UiOnlyVerificationUsesPartialEvidenceAndCannotOverwriteTheFullEvidence()
    {
        string script = ReadVerificationScript();

        Assert.Contains("second-iteration-ui-evidence.json", script);
        Assert.Contains("partial-ui-only", script);
        Assert.Contains("Refusing to overwrite full evidence", script);
    }

    private static string ReadVerificationScript() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "scripts", "verify-second-iteration.ps1"));

    private static void AssertIcon(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        IReadOnlyList<IconEntry> entries = ReadDirectory(bytes);

        Assert.Equal(RequiredSizes, entries.Select(entry => entry.Width).Order().ToArray());
        Assert.All(entries, entry => Assert.Equal(entry.Width, entry.Height));

        foreach (IconEntry entry in entries)
        {
            using var frameStream = new MemoryStream(BuildSingleFrameIcon(bytes, entry));
            using var icon = new Icon(frameStream);
            using Bitmap bitmap = icon.ToBitmap();
            Assert.Equal(entry.Width, bitmap.Width);
            Assert.Equal(entry.Height, bitmap.Height);

            Color[] pixels = Enumerable.Range(0, bitmap.Height)
                .SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y)))
                .ToArray();
            Assert.Contains(pixels, pixel => pixel.A > 0);
            Assert.True(pixels.Select(pixel => pixel.ToArgb()).Distinct().Skip(1).Any(),
                $"Icon frame {entry.Width}x{entry.Height} in {path} has no visual content.");
        }
    }

    private static IReadOnlyList<IconEntry> ReadDirectory(byte[] bytes)
    {
        Assert.True(bytes.Length >= 6, "ICO header is truncated.");
        Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 2));
        int count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(bytes.Length >= 6 + (count * 16), "ICO directory is truncated.");

        var entries = new List<IconEntry>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 6 + (index * 16);
            int width = bytes[offset] == 0 ? 256 : bytes[offset];
            int height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
            int length = checked((int)BitConverter.ToUInt32(bytes, offset + 8));
            int imageOffset = checked((int)BitConverter.ToUInt32(bytes, offset + 12));
            Assert.InRange(imageOffset, 6 + (count * 16), bytes.Length);
            Assert.InRange(length, 1, bytes.Length - imageOffset);
            entries.Add(new IconEntry(width, height, length, imageOffset));
        }

        return entries;
    }

    private static byte[] BuildSingleFrameIcon(byte[] source, IconEntry entry)
    {
        byte[] result = new byte[22 + entry.Length];
        result[2] = 1;
        result[4] = 1;
        result[6] = entry.Width == 256 ? (byte)0 : (byte)entry.Width;
        result[7] = entry.Height == 256 ? (byte)0 : (byte)entry.Height;
        result[10] = 1;
        result[12] = 32;
        BitConverter.GetBytes((uint)entry.Length).CopyTo(result, 14);
        BitConverter.GetBytes((uint)22).CopyTo(result, 18);
        source.AsSpan(entry.Offset, entry.Length).CopyTo(result.AsSpan(22));
        return result;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WordFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private sealed record IconEntry(int Width, int Height, int Length, int Offset);
}
