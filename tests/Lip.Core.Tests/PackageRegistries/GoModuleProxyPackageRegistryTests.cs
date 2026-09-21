using Flurl;
using Flurl.Http.Testing;
using Lip.Core.Entities;
using Lip.Core.PackageRegistries;
using Semver;

namespace Lip.Core.Tests.PackageRegistries;

public class GoModuleProxyPackageRegistryTests {
  [Fact]
  public async Task GetAvailableVersions_ParsesResponseCorrectly() {
    // Arrange
    using HttpTest httpTest = new();
    Url proxyUrl = Url.Parse("https://proxy.golang.org");
    GoModuleProxyPackageRegistry registry = new(new StubProxySelector(goModuleProxies: [proxyUrl]));
    PackageId pkgId = new("github.com/test/pkg", "");

    httpTest.RespondWith("v1.0.0\nv1.1.0\nv2.0.0-rc.1\ninvalid-version\n");

    // Act
    List<SemVersion> result = [.. (await registry.GetAvailableVersions(pkgId))];

    // Assert
    Assert.Equal(3, result.Count);
    Assert.Contains(SemVersion.Parse("1.0.0"), result);
    Assert.Contains(SemVersion.Parse("1.1.0"), result);
    Assert.Contains(SemVersion.Parse("2.0.0-rc.1"), result);
  }

  [Fact]
  public async Task GetPackageManifest_ThrowsNotSupportedException() {
    GoModuleProxyPackageRegistry registry = new(new StubProxySelector());
    await Assert.ThrowsAsync<NotSupportedException>(() =>
        registry.GetPackageManifest(new PackageSpec(PackageId.Parse("github.com/test/repo"), new SemVersion(1, 0, 0))));
  }
}
