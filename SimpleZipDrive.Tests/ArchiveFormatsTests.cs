using SimpleZipDrive.Core;

namespace SimpleZipDrive.Tests;

/// <summary>
///     Tests for the shared <see cref="ArchiveFormats" /> helper used by both the Dokan
///     and WinFsp mount services.
/// </summary>
public class ArchiveFormatsTests
{
    [Theory]
    [InlineData("wrong.7z", "526172211A0700", "rar")]
    [InlineData("wrong.7z", "526172211A070100", "rar")]
    [InlineData("wrong.rar", "377ABCAF271C", "7z")]
    [InlineData("wrong.rar", "504B0304", "zip")]
    [InlineData("wrong.7z", "504B0506", "zip")]
    [InlineData("archive.bin", "377ABCAF271C", "7z")]
    [InlineData("archive", "526172211A070100", "rar")]
    public void GetArchiveType_SignatureOverridesExtension(string name, string hex, string expected)
    {
        // Header-only fixtures test identification, not archive validity.
        var directory = System.IO.Directory.CreateTempSubdirectory("szd-format-");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, name);
            System.IO.File.WriteAllBytes(path, Convert.FromHexString(hex));
            Assert.Equal(expected, ArchiveFormats.GetArchiveType(path));
            Assert.True(ArchiveFormats.IsSupportedArchive(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("526172211A070100", "rar")]
    [InlineData("377ABCAF271C", "7z")]
    [InlineData("504B0506", "zip")]
    [InlineData("526172211A0701", null)]
    [InlineData("1F8B", null)]
    [InlineData("", null)]
    public void DetectArchiveType_PreservesStreamPosition(string hex, string? expected)
    {
        using var stream = new System.IO.MemoryStream(Convert.FromHexString(hex));
        stream.Position = stream.Length > 0 ? 1 : 0;
        var position = stream.Position;
        Assert.Equal(expected, ArchiveFormats.DetectArchiveType(stream));
        Assert.Equal(position, stream.Position);
    }

    [Theory]
    [InlineData("comic.cbz", "zip")]
    [InlineData("comic.CBZ", "zip")]
    [InlineData("comic.cbr", "rar")]
    [InlineData("comic.cb7", "7z")]
    [InlineData("book.zip", "zip")]
    [InlineData("book.7z", "7z")]
    [InlineData("book.rar", "rar")]
    [InlineData("backup.tar.gz", "tar")]
    [InlineData("backup.tgz", "tar")]
    [InlineData("game.zar", "zar")]
    [InlineData("game.ZAR", "zar")]
    [InlineData("game.iso", "xiso")]
    [InlineData("game.ISO", "xiso")]
    [InlineData("game.xiso", "xiso")]
    [InlineData("game.cso", "xiso")]
    [InlineData("game.1.cso", "xiso")]
    public void GetArchiveType_ComicAndKnownExtensions_MapsCorrectly(string filePath, string expected)
    {
        Assert.Equal(expected, ArchiveFormats.GetArchiveType(filePath));
    }

    [Theory]
    [InlineData("comic.cbz")]
    [InlineData("comic.CBZ")]
    [InlineData("comic.cbr")]
    [InlineData("comic.cb7")]
    [InlineData("book.zip")]
    [InlineData("book.tar.xz")]
    [InlineData("game.zar")]
    [InlineData("game.ZAR")]
    [InlineData("game.iso")]
    [InlineData("game.xiso")]
    [InlineData("game.cso")]
    public void IsSupportedArchive_ComicAndKnownExtensions_ReturnsTrue(string filePath)
    {
        Assert.True(ArchiveFormats.IsSupportedArchive(filePath));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("image.png")]
    [InlineData("document.pdf")]
    [InlineData("noextension")]
    public void IsSupportedArchive_UnsupportedExtensions_ReturnsFalse(string filePath)
    {
        Assert.False(ArchiveFormats.IsSupportedArchive(filePath));
    }

    [Fact]
    public void DialogFilter_IncludesComicExtensions()
    {
        Assert.Contains("*.cbz", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.cbr", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.cb7", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.zar", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.iso", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.xiso", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.cso", ArchiveFormats.DialogFilter, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportedExtensionsDescription_IncludesComicExtensions()
    {
        Assert.Contains(".cbz", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cbr", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cb7", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".zar", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".iso", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".xiso", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cso", ArchiveFormats.SupportedExtensionsDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportedFormatsDescription_IncludesAllFormatFamilies()
    {
        Assert.Contains("ZIP", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7Z", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RAR", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TAR", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cb7", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".zar", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".xiso", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cso", ArchiveFormats.SupportedFormatsDescription, StringComparison.OrdinalIgnoreCase);
    }
}