using Shottr.Update;

namespace Shottr.Tests;

public class CompareVersionsTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]   // numeric, not text, ordering
    [InlineData("1.9.0", "1.10.0", -1)]
    [InlineData("10.0.0", "9.0.0", 1)]
    [InlineData("1.0.2", "1.0.10", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void ComparesPartsAsIntegers(string a, string b, int expected) =>
        Assert.Equal(expected, UpdateCore.CompareVersions(a, b));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "V1.2.3")]
    [InlineData(" v1.2.3 ", "1.2.3")]
    public void IgnoresLeadingV(string a, string b) =>
        Assert.Equal(0, UpdateCore.CompareVersions(a, b));

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1", "1.0.0")]
    [InlineData("1.2.0.0", "1.2")]
    public void MissingPartsCountAsZero(string a, string b)
    {
        Assert.Equal(0, UpdateCore.CompareVersions(a, b));
        Assert.Equal(0, UpdateCore.CompareVersions(b, a));
    }

    [Theory]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2.3-rc1", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    public void IgnoresSuffix(string a, string b) =>
        Assert.Equal(0, UpdateCore.CompareVersions(a, b));

    [Fact]
    public void NewerWithPrefixAndShortForm() =>
        Assert.Equal(1, UpdateCore.CompareVersions("v1.3", "1.2.9"));
}

public class ParseLatestReleaseTests
{
    private const string Sample = """
        {
          "url": "https://api.github.com/repos/ctorx/Shottr/releases/1",
          "html_url": "https://github.com/ctorx/Shottr/releases/tag/v1.2.0",
          "tag_name": "v1.2.0",
          "name": "Shottr 1.2.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "Shottr.exe",
              "size": 73400320,
              "browser_download_url": "https://github.com/ctorx/Shottr/releases/download/v1.2.0/Shottr.exe"
            },
            {
              "name": "SHA256SUMS",
              "size": 77,
              "browser_download_url": "https://github.com/ctorx/Shottr/releases/download/v1.2.0/SHA256SUMS"
            }
          ]
        }
        """;

    [Fact]
    public void ReadsTagPageAndAssets()
    {
        Assert.True(UpdateCore.TryParseLatestRelease(Sample, out ReleaseInfo? r, out string? error));
        Assert.Null(error);
        Assert.NotNull(r);
        Assert.Equal("v1.2.0", r.Tag);
        Assert.Equal("1.2.0", r.Version);
        Assert.Equal("https://github.com/ctorx/Shottr/releases/tag/v1.2.0", r.PageUrl);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal(new ReleaseAsset("Shottr.exe",
            "https://github.com/ctorx/Shottr/releases/download/v1.2.0/Shottr.exe", 73400320), r.Assets[0]);
    }

    [Fact]
    public void RejectsNotFoundBody()
    {
        const string body = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest","status":"404"}""";
        Assert.False(UpdateCore.TryParseLatestRelease(body, out ReleaseInfo? r, out string? error));
        Assert.Null(r);
        Assert.Contains("Not Found", error);
    }

    [Theory]
    [InlineData("""{"assets":[]}""")]
    [InlineData("""{"tag_name":null}""")]
    [InlineData("""{"tag_name":12}""")]
    [InlineData("""{"tag_name":"   "}""")]
    [InlineData("""[{"tag_name":"v1.0.0"}]""")]
    [InlineData("\"v1.0.0\"")]
    public void RejectsResponsesWithoutATagName(string body)
    {
        Assert.False(UpdateCore.TryParseLatestRelease(body, out ReleaseInfo? r, out string? error));
        Assert.Null(r);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"tag_name":"v1.0.0",""")]
    public void RejectsInvalidJson(string body)
    {
        Assert.False(UpdateCore.TryParseLatestRelease(body, out _, out string? error));
        Assert.Contains("JSON", error);
    }

    [Fact]
    public void DecodesEscapedAndUnicodeStrings()
    {
        const string body = """
            {
              "tag_name": "v1.0.0",
              "html_url": "https:\/\/github.com\/ctorx\/Shottr\/releases\/tag\/v1.0.0",
              "assets": [
                { "name": "Café \"quoted\".exe", "size": 1, "browser_download_url": "https://example.com/a" },
                { "name": "日本語-ünïcødé.exe", "size": 2, "browser_download_url": "https://example.com/b\\c" },
                { "name": "emoji-📸.exe", "size": 3, "browser_download_url": "https://example.com/c" }
              ]
            }
            """;
        Assert.True(UpdateCore.TryParseLatestRelease(body, out ReleaseInfo? r, out _));
        Assert.Equal("https://github.com/ctorx/Shottr/releases/tag/v1.0.0", r!.PageUrl);
        Assert.Equal("Café \"quoted\".exe", r.Assets[0].Name);
        Assert.Equal("日本語-ünïcødé.exe", r.Assets[1].Name);
        Assert.Equal("https://example.com/b\\c", r.Assets[1].DownloadUrl);
        Assert.Equal("emoji-📸.exe", r.Assets[2].Name);
    }

    [Fact]
    public void SkipsMalformedAssetEntries()
    {
        const string body = """
            {
              "tag_name": "v1.0.0",
              "assets": [
                { "name": "NoUrl.exe", "size": 1 },
                { "name": "NoSize.exe", "browser_download_url": "https://example.com/a" },
                { "name": "BadSize.exe", "size": "big", "browser_download_url": "https://example.com/a" },
                "not an object",
                { "name": "Shottr.exe", "size": 5, "browser_download_url": "https://example.com/ok" }
              ]
            }
            """;
        Assert.True(UpdateCore.TryParseLatestRelease(body, out ReleaseInfo? r, out _));
        ReleaseAsset only = Assert.Single(r!.Assets);
        Assert.Equal("Shottr.exe", only.Name);
        Assert.Equal("", r.PageUrl);
    }
}

public class SelectAssetTests
{
    private static ReleaseInfo Release(params string[] names) =>
        new("v2.0.0", "https://example.com", names.Select(n => new ReleaseAsset(n, "https://example.com/" + n, 10)).ToList());

    [Fact]
    public void PicksTheWindowsExe()
    {
        ReleaseAsset? a = UpdateCore.SelectAsset(Release("SHA256SUMS", "Shottr.exe"), "windows", "x64");
        Assert.Equal("Shottr.exe", a?.Name);
    }

    [Fact]
    public void MatchesNameWithoutRegardToCase() =>
        Assert.NotNull(UpdateCore.SelectAsset(Release("shottr.EXE"), "Windows", "X64"));

    [Fact]
    public void ReturnsNullWhenNoAssetMatches()
    {
        Assert.Null(UpdateCore.SelectAsset(Release("SHA256SUMS", "Shottr-source.zip", "Shottr.exe.sig"), "windows", "x64"));
        Assert.Null(UpdateCore.SelectAsset(Release(), "windows", "x64"));
    }

    [Theory]
    [InlineData("windows", "arm64")]
    [InlineData("linux", "x64")]
    [InlineData("macos", "arm64")]
    public void ReturnsNullForPlatformsWithoutABuild(string platform, string arch) =>
        Assert.Null(UpdateCore.SelectAsset(Release("Shottr.exe"), platform, arch));
}

public class ParseChecksumsTests
{
    private const string HashA = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";
    private const string HashB = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void FindsTheFilesHash()
    {
        string text = $"{HashA}  Shottr.exe\n{HashB}  Other.zip\n";
        Assert.Equal(HashA, UpdateCore.ParseChecksums(text, "Shottr.exe"));
        Assert.Equal(HashB.ToLowerInvariant(), UpdateCore.ParseChecksums(text, "Other.zip"));
    }

    [Fact]
    public void AcceptsWindowsLineEndingsAndBinaryMarker()
    {
        string text = $"{HashB} *Other.zip\r\n{HashA} *Shottr.exe\r\n";
        Assert.Equal(HashA, UpdateCore.ParseChecksums(text, "Shottr.exe"));
    }

    [Fact]
    public void HandlesFileNamesWithSpaces() =>
        Assert.Equal(HashA, UpdateCore.ParseChecksums($"{HashA}  My App Setup.exe", "My App Setup.exe"));

    [Fact]
    public void ReturnsNullWhenTheFileIsNotListed() =>
        Assert.Null(UpdateCore.ParseChecksums($"{HashA}  Other.exe\n", "Shottr.exe"));

    [Fact]
    public void DoesNotMatchAPartialName() =>
        Assert.Null(UpdateCore.ParseChecksums($"{HashA}  Shottr.exe.old\n{HashA}  MyShottr.exe\n", "Shottr.exe"));

    [Theory]
    [InlineData("3a7bd3  Shottr.exe")]
    [InlineData("zz7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b  Shottr.exe")]
    [InlineData("Shottr.exe")]
    [InlineData("")]
    public void ReturnsNullForMalformedLines(string text) =>
        Assert.Null(UpdateCore.ParseChecksums(text, "Shottr.exe"));
}
