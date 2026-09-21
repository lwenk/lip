using Flurl;
using Lip.Core.Entities;
using Lip.Core.PackageRegistries;
using Lip.Core.Services;
using Semver;

namespace Lip.Core.RegressionTests.PackageRegistries;

public class GoModuleProxyPackageRegistryTests {
  // The proxy lip itself prefers, rather than a hard-coded host: proxy.golang.org never answers
  // from some networks, which would make this test permanently red there.
  private static readonly Url _goModuleProxy = ProxySelector.DefaultGoModuleProxies[0];

  [Theory]
  [InlineData("github.com/LiteLDev/MoreDimensions", 21)]
  [InlineData("github.com/LiteLDev/LegacyScriptEngine", 124)]
  [InlineData("github.com/LiteLDev/LeviLamina", 90)]
  [InlineData("github.com/LiteLDev/LeviLamina#client", 17)]
  public async Task GetAvailableVersions_ReturnsSortedMinCount(string packageId, int minVersionCount) {
    GoModuleProxyPackageRegistry registry = new(new StubProxySelector(goModuleProxies: [_goModuleProxy]));

    IOrderedEnumerable<SemVersion> versions = await registry.GetAvailableVersions(PackageId.Parse(packageId));

    Assert.True(versions.Count() >= minVersionCount);
    Assert.Equal(versions, versions.Order(SemVersion.PrecedenceComparer));
  }
}
