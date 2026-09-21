using System.IO.Abstractions.TestingHelpers;
using Lip.Core.Infrastructure;

namespace Lip.Core.Tests.Infrastructure;

public class DiagnosticLogTests {
  private static string GetLogDirectoryPath() =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lip", "logs");

  private static string GetConfigPath() =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lip", "liprc.json");

  [Fact]
  public void Constructor_KeepsOnlyTheMostRecentLogFiles() {
    // Arrange
    string logDirectoryPath = GetLogDirectoryPath();
    Dictionary<string, MockFileData> files = [];

    for (int index = 1; index <= 12; index++) {
      files[Path.Combine(logDirectoryPath, $"lip-2026010{index:D2}-000000-1.log")] = new MockFileData("old");
    }

    MockFileSystem fileSystem = new(files);

    // Act
    _ = new DiagnosticLog(fileSystem);

    // Assert
    IEnumerable<string> remaining = fileSystem.Directory
        .GetFiles(logDirectoryPath, "lip-*.log")
        .Select(Path.GetFileName)!;

    Assert.Equal(9, remaining.Count());
    Assert.DoesNotContain("lip-202601001-000000-1.log", remaining);
    Assert.DoesNotContain("lip-202601002-000000-1.log", remaining);
    Assert.DoesNotContain("lip-202601003-000000-1.log", remaining);
    Assert.Contains("lip-202601012-000000-1.log", remaining);
  }

  [Fact]
  public void Write_AppendsMessagesToTheRunLog() {
    // Arrange
    MockFileSystem fileSystem = new();
    DiagnosticLog log = new(fileSystem);

    // Act
    log.Write("first");
    log.Write("second");

    // Assert
    string content = fileSystem.File.ReadAllText(log.Path);
    Assert.Contains("first", content);
    Assert.Contains("second", content);
  }

  [Fact]
  public void WriteException_IncludesEveryInnerException() {
    // Arrange
    MockFileSystem fileSystem = new();
    DiagnosticLog log = new(fileSystem);

    AggregateException exception = new("Failed to retrieve the package.", [
        new InvalidOperationException("first inner"),
        new HttpRequestException("second inner")
    ]);

    // Act
    log.WriteException(exception);

    // Assert
    string content = fileSystem.File.ReadAllText(log.Path);
    Assert.Contains("Failed to retrieve the package.", content);
    Assert.Contains("first inner", content);
    Assert.Contains("second inner", content);
    Assert.Contains("HttpRequestException", content);
  }

  [Fact]
  public void WriteHeader_RedactsCredentialsFromTheConfiguration() {
    // Arrange
    MockFileSystem fileSystem = new(new Dictionary<string, MockFileData>
    {
      {
        GetConfigPath(),
        new MockFileData("""
            {
              "github_proxy": "http://alice:supersecret@proxy.example.com",
              "go_module_proxy": "https://goproxy.cn"
            }
            """)
      }
    });

    DiagnosticLog log = new(fileSystem);

    // Act
    log.WriteHeader("lip install example");

    // Assert
    string content = fileSystem.File.ReadAllText(log.Path);
    Assert.Contains("lip install example", content);
    Assert.Contains("https://goproxy.cn", content);
    Assert.DoesNotContain("supersecret", content);
    Assert.DoesNotContain("alice", content);
    Assert.Contains("http://***@proxy.example.com", content);
  }

  [Fact]
  public void WriteHeader_WithoutConfigurationFile_DoesNotThrow() {
    // Arrange
    MockFileSystem fileSystem = new();
    DiagnosticLog log = new(fileSystem);

    // Act
    log.WriteHeader("lip list");

    // Assert
    string content = fileSystem.File.ReadAllText(log.Path);
    Assert.Contains("(no configuration file)", content);
  }

  [Theory]
  [InlineData("http://alice:supersecret@proxy.example.com", "http://***@proxy.example.com")]
  [InlineData("https://alice@proxy.example.com:8080", "https://***@proxy.example.com:8080")]
  [InlineData("https://goproxy.cn", "https://goproxy.cn")]
  [InlineData("https://proxy.example.com/", "https://proxy.example.com/")]
  [InlineData("https://proxy.example.com/a@b", "https://proxy.example.com/a@b")]
  [InlineData("not a url", "not a url")]
  public void RedactCredentials_MasksUserInfoOnly(string input, string expected) {
    // Act
    string actual = DiagnosticLog.RedactCredentials(input);

    // Assert
    Assert.Equal(expected, actual);
  }
}
