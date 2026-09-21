using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.PackageRegistries;
using Semver;

namespace Lip.Core.RegressionTests.PackageRegistries;

public class GitPackageRegistryTests {
  [Theory]
  [InlineData("github.com/LiteLDev/MoreDimensions", 21)]
  [InlineData("github.com/LiteLDev/LegacyScriptEngine", 124)]
  [InlineData("github.com/LiteLDev/LeviLamina", 90)]
  [InlineData("github.com/LiteLDev/LeviLamina#client", 17)]
  public async Task GetAvailableVersions_ReturnsSortedMinCount(string packageId, int minVersionCount) {
    GitRunner gitRunner = new();
    GitPackageRegistry registry = new(gitRunner, new StubProxySelector());

    IOrderedEnumerable<SemVersion> versions = await registry.GetAvailableVersions(PackageId.Parse(packageId));

    Assert.True(versions.Count() >= minVersionCount);
    Assert.Equal(versions, versions.Order(SemVersion.PrecedenceComparer));
  }
}
