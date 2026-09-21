using Flurl;
using Lip.Core.Services;

namespace Lip.Core.Tests;

/// <summary>
/// A proxy selector with fixed results, so that tests do not probe the network. A null entry in
/// <paramref name="githubSources"/> means direct access.
/// </summary>
public class StubProxySelector(
    IReadOnlyList<Url?>? githubSources = null,
    IReadOnlyList<Url>? goModuleProxies = null) : IProxySelector {
  private readonly IReadOnlyList<Url?> _githubSources = githubSources ?? [null];
  private readonly IReadOnlyList<Url> _goModuleProxies =
      goModuleProxies ?? [Url.Parse("https://proxy.golang.org")];

  public Task<IReadOnlyList<Url?>> OrderGithubSources() {
    return Task.FromResult(_githubSources);
  }

  public Task<IReadOnlyList<Url>> OrderGoModuleProxies() {
    return Task.FromResult(_goModuleProxies);
  }
}
