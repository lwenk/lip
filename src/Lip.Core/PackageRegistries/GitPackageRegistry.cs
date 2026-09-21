using Flurl;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.Services;
using Semver;

namespace Lip.Core.PackageRegistries;

public class GitPackageRegistry(IGitRunner gitRunner, IProxySelector proxySelector) : IPackageRegistry {
  private readonly IGitRunner _gitRunner = gitRunner;

  private readonly IProxySelector _proxySelector = proxySelector;

  public async Task<IOrderedEnumerable<SemVersion>> GetAvailableVersions(PackageId packageId) {
    Url repoUrl = Url.Parse($"https://{packageId.Path}.git");

    Url? githubProxy = (await _proxySelector.OrderGithubSources()).FirstOrDefault(source => source is not null);

    if (githubProxy is not null && repoUrl.Host == "github.com") {
      repoUrl = githubProxy
          .Clone()
          .AppendPathSegments(repoUrl.PathSegments);
    }

    return (await _gitRunner.LsRemote(repoUrl, refs: true, tags: true))
        .Where(item => item.Ref.StartsWith("refs/tags/v"))
        .Select(item => item.Ref["refs/tags/v".Length..])
        .Where(version => SemVersion.TryParse(version, out _))
        .Select(version => SemVersion.Parse(version))
        .Order(SemVersion.PrecedenceComparer);
  }

  public Task<PackageManifest> GetPackageManifest(PackageSpec packageSpec) {
    throw new NotSupportedException();
  }
}
