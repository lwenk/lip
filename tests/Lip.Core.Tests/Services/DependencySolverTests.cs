using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.PackageRegistries;
using Lip.Core.Services;
using Moq;
using Semver;

namespace Lip.Core.Tests.Services;

public class DependencySolverTests {
  private readonly Mock<IUserInteraction> _mockLogger;
  private readonly Mock<IPackageRegistry> _mockRegistry;
  private readonly DependencySolver _solver;

  public DependencySolverTests() {
    _mockLogger = new Mock<IUserInteraction>();
    _mockRegistry = new Mock<IPackageRegistry>();
    _solver = new DependencySolver(_mockRegistry.Object, _mockLogger.Object);
  }

  [Fact]
  public async Task Solve_NoDependencies_ReturnsEmpty() {
    // Act
    IEnumerable<PackageSpec> result = await _solver.Solve([]);

    // Assert
    Assert.Empty(result);
  }

  [Fact]
  public async Task Solve_SinglePackage_ReturnsPackage() {
    // Arrange
    PackageId packageId = new("github.com/test/pkg", string.Empty);
    SemVersion version = new(1, 0, 0);
    PackageSpec spec = new(packageId, version);
    PackageReqt reqt = new(packageId, SemVersionRange.All);

    _mockRegistry.Setup(r => r.GetAvailableVersions(packageId))
        .ReturnsAsync(new[] { version }.OrderBy(v => v));

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = version,
      Variants =
        [
            new PackageManifestVariant
                {
                    Label = string.Empty,
                    Dependencies = []
                }
        ]
    };

    _mockRegistry.Setup(r => r.GetPackageManifest(spec))
        .ReturnsAsync(manifest);

    // Act
    IEnumerable<PackageSpec> result = await _solver.Solve([reqt]);

    // Assert
    Assert.Single(result);
    Assert.Equal(spec, result.First());
  }

  [Fact]
  public async Task Solve_Conflict_ThrowsException() {
    // Arrange
    PackageId pkgA = new("github.com/test/a", string.Empty);
    SemVersion verA = new(1, 0, 0);
    PackageSpec specA = new(pkgA, verA);

    PackageId pkgB = new("github.com/test/b", string.Empty);
    SemVersion verB1 = new(1, 0, 0);
    SemVersion verB2 = new(2, 0, 0);

    PackageReqt reqtA = new(pkgA, SemVersionRange.All);

    _mockRegistry.Setup(r => r.GetAvailableVersions(pkgA))
        .ReturnsAsync(new[] { verA }.OrderBy(v => v));

    PackageManifest manifestA = new() {
      Path = "github.com/test/a",
      Version = verA,
      Variants =
        [
            new PackageManifestVariant
                {
                    Label = string.Empty,
                    Dependencies = new Dictionary<PackageId, SemVersionRange>
                    {
                        { pkgB, SemVersionRange.Parse("1.0.0") }
                    }
                }
        ]
    };

    _mockRegistry.Setup(r => r.GetPackageManifest(specA))
        .ReturnsAsync(manifestA);

    // But we also request pkg-b 2.0.0 exactly at root level (contradiction)
    PackageReqt reqtB = new(pkgB, SemVersionRange.Parse("2.0.0"));

    _mockRegistry.Setup(r => r.GetAvailableVersions(pkgB))
        .ReturnsAsync(new[] { verB1, verB2 }.OrderBy(v => v));

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(() => _solver.Solve([reqtA, reqtB]));
  }

  [Fact]
  public async Task Solve_BacktrackingBetweenVersions_KeepsCandidateVersionsOfOtherPackages() {
    // Arrange
    PackageId pkgA = new("github.com/test/a", string.Empty);
    PackageId pkgX = new("github.com/test/x", string.Empty);
    PackageId pkgY = new("github.com/test/y", string.Empty);

    SemVersion verA1 = new(1, 0, 0);
    SemVersion verA2 = new(2, 0, 0);
    SemVersion verX1 = new(1, 0, 0);
    SemVersion verX2 = new(2, 0, 0);
    SemVersion verY1 = new(1, 0, 0);

    // pkg-a 2.0.0 is the newest version, so the solver tries it first. It narrows pkg-x down to
    // 1.x before failing on the unsatisfiable pkg-y dependency. pkg-a 1.0.0 needs 2.x of pkg-x
    // afterwards, so the versions dropped while exploring the failed branch must still be
    // available when the solver backtracks.
    _mockRegistry.Setup(r => r.GetAvailableVersions(pkgA))
        .ReturnsAsync(new[] { verA1, verA2 }.Order(SemVersion.PrecedenceComparer));
    _mockRegistry.Setup(r => r.GetAvailableVersions(pkgX))
        .ReturnsAsync(new[] { verX1, verX2 }.Order(SemVersion.PrecedenceComparer));
    _mockRegistry.Setup(r => r.GetAvailableVersions(pkgY))
        .ReturnsAsync(new[] { verY1 }.Order(SemVersion.PrecedenceComparer));

    _mockRegistry.Setup(r => r.GetPackageManifest(new PackageSpec(pkgA, verA2)))
        .ReturnsAsync(CreateManifest(pkgA.Path, verA2,
            (pkgX, SemVersionRange.Parse("1.0.*")),
            (pkgY, SemVersionRange.Parse("3.0.*"))));
    _mockRegistry.Setup(r => r.GetPackageManifest(new PackageSpec(pkgA, verA1)))
        .ReturnsAsync(CreateManifest(pkgA.Path, verA1,
            (pkgX, SemVersionRange.Parse("2.0.*"))));
    _mockRegistry.Setup(r => r.GetPackageManifest(new PackageSpec(pkgX, verX1)))
        .ReturnsAsync(CreateManifest(pkgX.Path, verX1));
    _mockRegistry.Setup(r => r.GetPackageManifest(new PackageSpec(pkgX, verX2)))
        .ReturnsAsync(CreateManifest(pkgX.Path, verX2));

    // Act
    IEnumerable<PackageSpec> result = await _solver.Solve([
        new PackageReqt(pkgA, SemVersionRange.All),
        new PackageReqt(pkgX, SemVersionRange.All),
    ]);

    // Assert
    Assert.Equal(2, result.Count());
    Assert.Contains(new PackageSpec(pkgA, verA1), result);
    Assert.Contains(new PackageSpec(pkgX, verX2), result);
  }

  private static PackageManifest CreateManifest(
      string path,
      SemVersion version,
      params (PackageId Id, SemVersionRange Range)[] dependencies) {
    return new PackageManifest {
      Path = path,
      Version = version,
      Variants =
        [
            new PackageManifestVariant
                {
                    Label = string.Empty,
                    Dependencies = dependencies.ToDictionary(d => d.Id, d => d.Range)
                }
        ]
    };
  }
}
