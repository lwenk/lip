using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl;
using Flurl.Http;
using Lip.Core.Infrastructure;

namespace Lip.Core.Services;

public interface IProxySelector {
  /// <summary>
  /// Returns the ways to reach GitHub ordered by the latency measured in the most recent probe.
  /// A <see langword="null"/> entry means direct access, any other entry is a prefix mirror. The
  /// explicitly configured proxy, when set, always comes first and skips probing.
  /// </summary>
  Task<IReadOnlyList<Url?>> OrderGithubSources();

  /// <summary>
  /// Returns the Go module proxies ordered by the latency measured in the most recent probe. The
  /// explicitly configured proxy, when set, always comes first and skips probing.
  /// </summary>
  Task<IReadOnlyList<Url>> OrderGoModuleProxies();
}

public class ProxySelectorOptions {
  public Url? FixedGithubProxy { get; init; }
  public IReadOnlyList<Url> GithubMirrors { get; init; } = [];
  public Url? FixedGoModuleProxy { get; init; }
  public IReadOnlyList<Url> GoModuleMirrors { get; init; } = [];
}

/// <summary>
/// Orders the ways to reach GitHub and the Go module ecosystem. Without an explicitly configured
/// proxy, every candidate is probed with a small request and the fastest one wins, so that users do
/// not have to find out which mirror currently works. Probes are cached, because running one per
/// operation would slow every command down.
/// </summary>
public class ProxySelector(
    IFileSystem fileSystem,
    IUserInteraction userInteraction,
    ProxySelectorOptions options,
    Func<Url, CancellationToken, Task<TimeSpan?>>? probe = null) : IProxySelector {
  private const string _githubProbeTarget = "https://raw.githubusercontent.com/github/gitignore/main/README.md";
  private const string _goModuleProbePath = "github.com/golang/protobuf/@v/list";
  private const string _directEntry = ""; // Marks direct access in the cached list.
  private const string _directLabel = "direct access";
  private const int _maxLabelLength = 48;
  private static readonly TimeSpan _cacheLifetime = TimeSpan.FromHours(24);
  private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(2);
  private static readonly JsonSerializerOptions _jsonSerializerOptions = new() {
    WriteIndented = true
  };

  private static readonly string _cachePath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lip", "proxy-selection.json");

  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IUserInteraction _userInteraction = userInteraction;
  private readonly ProxySelectorOptions _options = options;
  private readonly Func<Url, CancellationToken, Task<TimeSpan?>> _probe =
      probe ?? ProbeWithHttpAsync;

  public static readonly IReadOnlyList<Url> DefaultGithubMirrors = [
      Url.Parse("https://gh-proxy.com"),
      Url.Parse("https://ghproxy.net"),
      Url.Parse("https://ghfast.top"),
      Url.Parse("https://github.akams.cn"),
      Url.Parse("https://ghproxy.homeboyc.cn"),
      Url.Parse("https://github.bibk.top"),
  ];

  public static readonly IReadOnlyList<Url> DefaultGoModuleProxies = [
      Url.Parse("https://goproxy.cn"),
      Url.Parse("https://goproxy.io"),
      Url.Parse("https://proxy.golang.org"),
  ];

  private sealed record ProxySelectionCache {
    [JsonPropertyName("github_sources")]
    public List<string>? GithubSources { get; init; }

    [JsonPropertyName("go_module_proxies")]
    public List<string>? GoModuleProxies { get; init; }

    [JsonPropertyName("tested_at")]
    public DateTimeOffset? TestedAt { get; init; }
  }

  public static string GetCachePath() {
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lip", "proxy-selection.json");
  }

  public async Task<IReadOnlyList<Url?>> OrderGithubSources() {
    List<string> entries = [];

    if (_options.FixedGithubProxy is not null) {
      entries.Add(_options.FixedGithubProxy.ToString());
    }

    entries.AddRange(_options.GithubMirrors.Select(mirror => mirror.ToString()));
    entries.AddRange(DefaultGithubMirrors.Select(mirror => mirror.ToString()));
    entries.Add(_directEntry);

    List<string> uniqueEntries = [.. Deduplicate(entries)];

    if (_options.FixedGithubProxy is not null) {
      // The user picked a source; it goes first and the rest stay as fallbacks without probing.
      return [.. uniqueEntries.Select(StringToGithubSource)];
    }

    IEnumerable<string> orderedEntries = await OrderByProbe(uniqueEntries, "GitHub", _githubProbeTarget);

    return [.. orderedEntries.Select(StringToGithubSource)];
  }

  public async Task<IReadOnlyList<Url>> OrderGoModuleProxies() {
    List<string> entries = [];

    if (_options.FixedGoModuleProxy is not null) {
      entries.Add(_options.FixedGoModuleProxy.ToString());
    }

    entries.AddRange(_options.GoModuleMirrors.Select(mirror => mirror.ToString()));
    entries.AddRange(DefaultGoModuleProxies.Select(proxy => proxy.ToString()));

    List<string> uniqueEntries = [.. Deduplicate(entries)];

    if (_options.FixedGoModuleProxy is not null) {
      return [.. uniqueEntries.Select(Url.Parse)];
    }

    IEnumerable<string> orderedEntries = await OrderByProbe(uniqueEntries, "Go module", null);

    return [.. orderedEntries.Select(Url.Parse)];
  }

  /// <summary>
  /// Probes every entry and orders the reachable ones by latency. Entries are plain strings, where
  /// an empty string means direct access and any other entry is a base URL.
  /// </summary>
  private async Task<IEnumerable<string>> OrderByProbe(
      IReadOnlyList<string> entries,
      string kind,
      string? githubProbeTarget) {
    List<string>? cachedEntries = ReadCachedEntries(kind);

    if (cachedEntries is not null) {
      return cachedEntries;
    }

    using CancellationTokenSource cancellationTokenSource = new(_probeTimeout + TimeSpan.FromSeconds(1));

    (string Entry, TimeSpan? Latency)[] results = await Task.WhenAll([.. entries.Select(async entry => {
      try {
        Url probeUrl = BuildProbeUrl(entry, kind, githubProbeTarget);

        return (entry, await _probe(probeUrl, cancellationTokenSource.Token));
      }
      catch (Exception) {
        return (entry, (TimeSpan?)null);
      }
    })]);

    // Reachable entries are ordered by latency; unreachable ones keep their original order at the
    // end so that the download-time fallback can still try them.
    List<string> ordered = [
        .. results.Where(result => result.Latency is not null)
                .OrderBy(result => result.Latency)
                .Select(result => result.Entry),
            .. results.Where(result => result.Latency is null).Select(result => result.Entry),
    ];

    WriteCachedEntries(kind, ordered);

    await _userInteraction.PrintVerbose($"Probed {kind} sources:");

    foreach ((string entry, TimeSpan? latency) in results) {
      string measured = latency is TimeSpan latencyValue ? $"{latencyValue.TotalMilliseconds:0} ms" : "unreachable";

      await _userInteraction.PrintVerbose($"  {Describe(entry)}: {measured}");
    }

    if (ordered.Count > 0) {
      await _userInteraction.PrintInfo($"Using {kind} source: {Describe(ordered[0])}");
    }

    return ordered;
  }

  private Url BuildProbeUrl(string entry, string kind, string? githubProbeTarget) {
    if (entry.Length == 0) {
      return githubProbeTarget is not null
          ? Url.Parse(githubProbeTarget)
          : throw new InvalidOperationException("Cannot probe direct access without a target.");
    }

    return kind switch {
        // GitHub mirrors are prefix proxies: the mirror is followed by the full target URL.
        "GitHub" when githubProbeTarget is not null && entry.TrimEnd('/') == githubProbeTarget => Url.Parse(entry),
        "GitHub" when githubProbeTarget is not null => Url.Parse($"{entry.TrimEnd('/')}/{githubProbeTarget}"),
        _ => Url.Parse(entry).AppendPathSegments(_goModuleProbePath.Split('/')),
    };
  }

  private static string Describe(string entry) {
    if (entry.Length == 0) {
      return _directLabel;
    }

    return entry.Length <= _maxLabelLength ? entry : "…" + entry[^(_maxLabelLength - 1)..];
  }

  private List<string>? ReadCachedEntries(string kind) {
    try {
      if (!_fileSystem.File.Exists(GetCachePath())) {
        return null;
      }

      ProxySelectionCache? cache = JsonSerializer.Deserialize<ProxySelectionCache>(
          _fileSystem.File.ReadAllText(GetCachePath()));

      List<string>? cachedEntries = kind switch {
          "GitHub" => cache?.GithubSources,
          "Go module" => cache?.GoModuleProxies,
          _ => null,
      };

      if (cachedEntries is null) {
        return null;
      }

      if (cache?.TestedAt is not DateTimeOffset testedAt
          || DateTimeOffset.UtcNow - testedAt > _cacheLifetime) {
        return null;
      }

      return cachedEntries;
    }
    catch (Exception) {
      // A broken cache costs one probe, never a failed operation.
      return null;
    }
  }

  private void WriteCachedEntries(string kind, IReadOnlyList<string> ordered) {
    try {
      ProxySelectionCache cache = _fileSystem.File.Exists(GetCachePath())
          ? JsonSerializer.Deserialize<ProxySelectionCache>(_fileSystem.File.ReadAllText(GetCachePath())) ?? new()
          : new();

      ProxySelectionCache newCache = kind switch {
          "GitHub" => cache with { GithubSources = [.. ordered], TestedAt = DateTimeOffset.UtcNow },
          "Go module" => cache with { GoModuleProxies = [.. ordered], TestedAt = DateTimeOffset.UtcNow },
          _ => cache,
      };

      using Stream writeStream = _fileSystem.CreateFileWithDirectory(GetCachePath());

      JsonSerializer.Serialize(writeStream, newCache, _jsonSerializerOptions);
    }
    catch (Exception) {
      // Without a cache the next run simply probes again.
    }
  }

  private static IEnumerable<string> Deduplicate(IEnumerable<string> entries) {
    HashSet<string> seen = [];

    foreach (string entry in entries) {
      if (seen.Add(entry)) {
        yield return entry;
      }
    }
  }

  private static Url? StringToGithubSource(string entry) {
    return entry.Length == 0 ? null : Url.Parse(entry);
  }

  private static async Task<TimeSpan?> ProbeWithHttpAsync(Url probeUrl, CancellationToken cancellationToken) {
    Stopwatch stopwatch = Stopwatch.StartNew();

    IFlurlResponse response = await probeUrl
        .AllowAnyHttpStatus()
        .WithTimeout(_probeTimeout)
        .GetAsync(cancellationToken: cancellationToken);

    stopwatch.Stop();

    return (int)response.StatusCode is >= 200 and < 400 ? stopwatch.Elapsed : null;
  }
}
