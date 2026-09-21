using Flurl;
using Flurl.Http;
using Golang.Org.X.Mod;
using Lip.Core.Entities;
using Lip.Core.Services;
using Semver;

namespace Lip.Core.PackageRegistries;

public class GoModuleProxyPackageRegistry(IProxySelector proxySelector) : IPackageRegistry {

  public async Task<IOrderedEnumerable<SemVersion>> GetAvailableVersions(PackageId packageId) {
    List<Exception> exceptions = [];

    foreach (Url goModuleProxy in await proxySelector.OrderGoModuleProxies()) {
      try {
        return await GetAvailableVersions(goModuleProxy, packageId);
      }
      catch (Exception ex) {
        exceptions.Add(ex);
      }
    }

    throw new AggregateException(
        $"Failed to get available versions for package '{packageId}' from the Go module proxies.",
        exceptions);
  }

  private static async Task<IOrderedEnumerable<SemVersion>> GetAvailableVersions(
      Url goModuleProxy,
      PackageId packageId) {
    Url url = goModuleProxy
        .Clone()
        .AppendPathSegments(Module.EscapePath(packageId.Path).Item1, "@v", "list");

    string response = await url.GetStringAsync();

    return response
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.StartsWith('v'))
        .Select(v =>
            SemVersion.TryParse(Golang.Org.X.Mod.Semver.Canonical(v).Trim('v'), out SemVersion? version)
                ? version
                : null)
        .Where(version => version is not null)
        .Select(version => version!)
        .Order(SemVersion.PrecedenceComparer);
  }

  public Task<PackageManifest> GetPackageManifest(PackageSpec packageSpec) {
    throw new NotSupportedException();
  }
}
