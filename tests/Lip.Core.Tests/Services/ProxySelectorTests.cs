using System.IO.Abstractions.TestingHelpers;
using Flurl;
using Lip.Core.Infrastructure;
using Lip.Core.Services;
using Moq;

namespace Lip.Core.Tests.Services;

public class ProxySelectorTests {
  private static string GetCachePath() => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lip", "proxy-selection.json");

  private static readonly IReadOnlyList<Url> _defaultGithubMirrors = ProxySelector.DefaultGithubMirrors;

  private static Mock<IUserInteraction> CreateMockUserInteraction() {
    Mock<IUserInteraction> mock = new();

    mock.Setup(u => u.PrintInfo(It.IsAny<string>())).Returns(Task.CompletedTask);
    mock.Setup(u => u.PrintVerbose(It.IsAny<string>())).Returns(Task.CompletedTask);

    return mock;
  }

  [Fact]
  public async Task OrderGithubSources_FixedProxySet_SkipsProbingAndPutsFixedFirst() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();
    Url fixedProxy = Url.Parse("https://my-own-mirror.example.com");
    int probeCalls = 0;

    ProxySelector selector = new(fileSystem, userInteraction.Object, new() {
      FixedGithubProxy = fixedProxy,
    }, (url, token) => {
      probeCalls++;
      return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(1));
    });

    // Act
    IReadOnlyList<Url?> sources = await selector.OrderGithubSources();

    // Assert
    Assert.Equal(0, probeCalls);
    Assert.Equal(fixedProxy, sources[0]);
    Assert.Contains(null, sources); // Direct access stays as the final fallback.
  }

  [Fact]
  public async Task OrderGithubSources_OrdersByMeasuredLatency() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();

    Url fastMirror = _defaultGithubMirrors[0];
    Url slowMirror = _defaultGithubMirrors[1];

    ProxySelector selector = new(fileSystem, userInteraction.Object, new(), (url, token) => {
      if (url.ToString().StartsWith(fastMirror)) {
        return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(5));
      }

      if (url.ToString().StartsWith(slowMirror)) {
        return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(500));
      }

      // Direct access and every other mirror are unreachable.
      return Task.FromResult<TimeSpan?>(null);
    });

    // Act
    IReadOnlyList<Url?> sources = await selector.OrderGithubSources();

    // Assert
    Assert.Equal(fastMirror, sources[0]);
    Assert.Equal(slowMirror, sources[1]);
    Assert.Contains(null, sources);
  }

  [Fact]
  public async Task OrderGithubSources_AllProbesUnreachable_KeepsStaticOrder() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();

    ProxySelector selector = new(fileSystem, userInteraction.Object, new(), (url, token) => {
      return Task.FromResult<TimeSpan?>(null);
    });

    // Act
    IReadOnlyList<Url?> sources = await selector.OrderGithubSources();

    // Assert
    Assert.Equal([.. _defaultGithubMirrors.Select(mirror => (Url?)mirror), null], sources);
  }

  [Fact]
  public async Task OrderByProbe_CachesResultAndSkipsTheSecondProbe() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();
    int probeCalls = 0;

    ProxySelector selector = new(fileSystem, userInteraction.Object, new(), (url, token) => {
      probeCalls++;
      return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(10));
    });

    // Act
    await selector.OrderGithubSources();
    await selector.OrderGithubSources();

    // Assert
    Assert.True(fileSystem.File.Exists(GetCachePath()));
    int candidatesPerProbe = _defaultGithubMirrors.Count + 1; // Mirrors plus direct access.

    // The cached selection means the second call probes nothing.
    Assert.Equal(candidatesPerProbe, probeCalls);
  }

  [Fact]
  public async Task OrderByProbe_ExpiredCache_ProbesAgain() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();
    int probeCalls = 0;

    ProxySelector selector = new(fileSystem, userInteraction.Object, new(), (url, token) => {
      probeCalls++;
      return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(10));
    });

    await selector.OrderGithubSources();

    // Expire the cache entry.
    fileSystem.File.WriteAllText(GetCachePath(), """
        {
          "github_sources": ["https://gh-proxy.com"],
          "tested_at": "2020-01-01T00:00:00Z"
        }
        """);

    // Act
    await selector.OrderGithubSources();

    // Assert
    int candidatesPerProbe = _defaultGithubMirrors.Count + 1;

    Assert.Equal(2 * candidatesPerProbe, probeCalls);
  }

  [Fact]
  public async Task OrderGoModuleProxies_IncludesUserMirrorsWithoutDuplicates() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();

    Url userMirror = Url.Parse("https://my-goproxy.example.com");
    Url duplicatedDefault = ProxySelector.DefaultGoModuleProxies[0];

    List<Url> probedUrls = [];

    ProxySelector selector = new(fileSystem, userInteraction.Object, new() {
      GoModuleMirrors = [userMirror, duplicatedDefault],
    }, (url, token) => {
      probedUrls.Add(url);
      return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(10));
    });

    // Act
    IReadOnlyList<Url> ordered = await selector.OrderGoModuleProxies();

    // Assert
    Assert.Equal(userMirror, ordered[0]); // User-added mirrors are probed and win when tied.
    Assert.Equal(1, probedUrls.Count(url => url.ToString().StartsWith(userMirror)));
    Assert.Equal(ordered.Count, probedUrls.Select(url => url.ToString()).Distinct().Count());
  }

  [Fact]
  public async Task OrderGithubSources_ProbesDirectAccessAgainstItsOwnUrl() {
    // Arrange
    MockFileSystem fileSystem = new();
    Mock<IUserInteraction> userInteraction = CreateMockUserInteraction();

    List<Url> probedUrls = [];

    ProxySelector selector = new(fileSystem, userInteraction.Object, new(), (url, token) => {
      probedUrls.Add(url);
      return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(10));
    });

    // Act
    await selector.OrderGithubSources();

    // Assert
    // Direct access is probed as the target file itself, not as a mirror prefix of itself.
    Assert.Contains(Url.Parse("https://raw.githubusercontent.com/github/gitignore/main/README.md"), probedUrls);
  }
}
