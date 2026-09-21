using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Lip.Core.Entities;
using Lip.Core.Services;
using Semver;

namespace Lip.Core.Tests.Services;

public class WorkspaceServiceTests {
  private readonly MockFileSystem _fileSystem;
  private readonly WorkspaceService _service;

  public WorkspaceServiceTests() {
    _fileSystem = new MockFileSystem();
    _service = new WorkspaceService(_fileSystem);
  }

  [Fact]
  public async Task AddInstalledPackage_AddsPackageToState() {
    PackageId pkgId = new("github.com/foo/bar", "");
    SemVersion version = new(1, 0, 0);
    PackageSpec pkgSpec = new(pkgId, version);
    PackageManifest manifest = new() { Path = "github.com/foo/bar", Version = version, Variants = [new()] };
    List<string> files = [@"C:\lip\plugins\foo.dll"];
    List<IFileInfo> fileInfoList = [.. files.Select(_fileSystem.FileInfo.New)];

    await _service.AddInstalledPackage(pkgSpec, manifest, fileInfoList, true);

    IEnumerable<PackageSpec> installed = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.All);
    Assert.Single(installed);
    Assert.Equal(pkgSpec, installed.First());
  }

  [Fact]
  public async Task AddInstalledPackage_MismatchedSpec_ThrowsArgumentException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/other/repo", Version = new SemVersion(2, 0, 0), Variants = [new()] };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _service.AddInstalledPackage(pkgSpec, manifest, [], true));
  }

  [Fact]
  public async Task AddInstalledPackage_AlreadyInstalled_ThrowsInvalidOperationException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _service.AddInstalledPackage(pkgSpec, manifest, [], true));
  }

  [Fact]
  public async Task GetInstalledPackageFiles_ReturnsFiles() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    _fileSystem.AddFile(@"C:\test\file.dll", new MockFileData("content"));
    IFileInfo[] files = [_fileSystem.FileInfo.New(@"C:\test\file.dll")];
    await _service.AddInstalledPackage(pkgSpec, manifest, files, true);

    IEnumerable<IFileInfo> result = await _service.GetInstalledPackageFiles(pkgSpec);

    Assert.Single(result);
  }

  [Fact]
  public async Task GetInstalledPackageFiles_NotInstalled_ThrowsInvalidOperationException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _service.GetInstalledPackageFiles(pkgSpec));
  }

  [Fact]
  public async Task GetInstalledPackageManifest_ReturnsCorrectManifest() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    PackageManifest result = await _service.GetInstalledPackageManifest(pkgSpec);

    Assert.Equal(manifest.Path, result.Path);
  }

  [Fact]
  public async Task GetInstalledPackageManifest_NotInstalled_ThrowsInvalidOperationException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _service.GetInstalledPackageManifest(pkgSpec));
  }

  [Fact]
  public async Task GetInstalledPackages_ExplicitScope_ReturnsOnlyExplicit() {
    PackageSpec explicitPkg = new(new PackageId("github.com/explicit/pkg", ""), new SemVersion(1, 0, 0));
    PackageSpec implicitPkg = new(new PackageId("github.com/implicit/pkg", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest1 = new() { Path = "github.com/explicit/pkg", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    PackageManifest manifest2 = new() { Path = "github.com/implicit/pkg", Version = new SemVersion(1, 0, 0), Variants = [new()] };

    await _service.AddInstalledPackage(explicitPkg, manifest1, [], true);
    await _service.AddInstalledPackage(implicitPkg, manifest2, [], false);

    IEnumerable<PackageSpec> result = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.Explicit);

    Assert.Single(result);
    Assert.Equal(explicitPkg, result.First());
  }

  [Fact]
  public async Task GetInstalledPackages_ImplicitScope_ReturnsOnlyImplicit() {
    PackageSpec explicitPkg = new(new PackageId("github.com/explicit/pkg", ""), new SemVersion(1, 0, 0));
    PackageSpec implicitPkg = new(new PackageId("github.com/implicit/pkg", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest1 = new() { Path = "github.com/explicit/pkg", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    PackageManifest manifest2 = new() { Path = "github.com/implicit/pkg", Version = new SemVersion(1, 0, 0), Variants = [new()] };

    await _service.AddInstalledPackage(explicitPkg, manifest1, [], true);
    await _service.AddInstalledPackage(implicitPkg, manifest2, [], false);

    IEnumerable<PackageSpec> result = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.Implicit);

    Assert.Single(result);
    Assert.Equal(implicitPkg, result.First());
  }

  [Fact]
  public async Task RemoveInstalledPackage_RemovesFromState() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    await _service.RemoveInstalledPackage(pkgSpec);

    IEnumerable<PackageSpec> installed = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.All);
    Assert.Empty(installed);
  }

  [Fact]
  public async Task RemoveInstalledPackage_NotInstalled_ThrowsInvalidOperationException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _service.RemoveInstalledPackage(pkgSpec));
  }

  [Fact]
  public async Task UpdateInstalledPackageExplicitness_UpdatesExplicitness() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };
    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    await _service.UpdateInstalledPackageExplicitness(pkgSpec, false);

    IEnumerable<PackageSpec> explicitPkgs = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.Explicit);
    IEnumerable<PackageSpec> implicitPkgs = await _service.GetInstalledPackages(IWorkspaceService.PackageScope.Implicit);
    Assert.Empty(explicitPkgs);
    Assert.Single(implicitPkgs);
  }

  [Fact]
  public async Task UpdateInstalledPackageExplicitness_NotInstalled_ThrowsInvalidOperationException() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _service.UpdateInstalledPackageExplicitness(pkgSpec, false));
  }

  [Fact]
  public async Task CreateFileWithDirectory_HandlesRootFilePath() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };

    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    Assert.True(_fileSystem.File.Exists("tooth_lock.json"));
  }

  [Fact]
  public async Task AddInstalledPackage_StoresWorkspaceRelativeFilePaths() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };

    string filePath = _fileSystem.Path.Combine("plugins", "nested", "file.dll");
    await _service.AddInstalledPackage(pkgSpec, manifest, [_fileSystem.FileInfo.New(filePath)], true);

    string lockFileContent = _fileSystem.File.ReadAllText("tooth_lock.json");
    Assert.Contains("plugins/nested/file.dll", lockFileContent);

    // The relative path must still resolve back to the same file.
    IFileInfo storedFile = (await _service.GetInstalledPackageFiles(pkgSpec)).Single();
    Assert.Equal(_fileSystem.Path.GetFullPath(filePath), storedFile.FullName);
  }

  [Fact]
  public async Task GetInstalledPackageFiles_AbsolutePathFromLegacyLockFile_IsHonoured() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    string absolutePath = _fileSystem.Path.Combine(
        _fileSystem.Directory.GetCurrentDirectory(), "plugins", "file.dll");

    _fileSystem.AddFile("tooth_lock.json", new MockFileData($$"""
        {
          "packages": [
            {
              "files": [{{System.Text.Json.JsonSerializer.Serialize(absolutePath)}}],
              "locked": true,
              "manifest": {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/repo",
                "version": "1.0.0",
                "variants": []
              },
              "variant": ""
            }
          ]
        }
        """));

    IFileInfo storedFile = (await _service.GetInstalledPackageFiles(pkgSpec)).Single();

    Assert.Equal(absolutePath, storedFile.FullName);
  }

  [Fact]
  public async Task UpdateInstalledPackageExplicitness_NormalizesLegacyLockFilePaths() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    string workspaceRoot = _fileSystem.Directory.GetCurrentDirectory();
    string insidePath = _fileSystem.Path.Combine(workspaceRoot, "plugins", "file.dll");

    // A path on another drive can never be inside the workspace, whatever the mock's default
    // current directory is.
    string otherDrive = _fileSystem.Path.GetPathRoot(workspaceRoot)!.StartsWith("C:", StringComparison.OrdinalIgnoreCase)
        ? @"D:\"
        : @"C:\";
    string outsidePath = _fileSystem.Path.Combine(otherDrive, "elsewhere", "file.dll");

    _fileSystem.AddFile("tooth_lock.json", new MockFileData($$"""
        {
          "packages": [
            {
              "files": [
                {{System.Text.Json.JsonSerializer.Serialize(insidePath)}},
                {{System.Text.Json.JsonSerializer.Serialize(outsidePath)}}
              ],
              "locked": false,
              "manifest": {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/repo",
                "version": "1.0.0",
                "variants": []
              },
              "variant": ""
            }
          ]
        }
        """));

    await _service.UpdateInstalledPackageExplicitness(pkgSpec, isExplicit: true);

    string lockFileContent = _fileSystem.File.ReadAllText("tooth_lock.json");

    // A path inside the workspace is stored relative to it, so the lockfile keeps working after the
    // workspace is copied or moved.
    Assert.Contains("plugins/file.dll", lockFileContent);
    Assert.DoesNotContain(System.Text.Json.JsonSerializer.Serialize(insidePath), lockFileContent);

    // A path outside the workspace is left alone: rewriting it relative to this workspace would
    // silently point it at a different file.
    Assert.Contains(System.Text.Json.JsonSerializer.Serialize(outsidePath), lockFileContent);
  }

  [Fact]
  public async Task GetInstalledPackages_CorruptLockFile_ThrowsInsteadOfReportingEmptyWorkspace() {
    _fileSystem.AddFile("tooth_lock.json", new MockFileData("{ \"packages\": ["));

    InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
        () => _service.GetInstalledPackages(IWorkspaceService.PackageScope.All));

    Assert.Contains("tooth_lock.json", exception.Message);
  }

  [Fact]
  public async Task SaveWorkspaceState_LeavesNoTemporaryFilesBehind() {
    PackageSpec pkgSpec = new(new PackageId("github.com/test/repo", ""), new SemVersion(1, 0, 0));
    PackageManifest manifest = new() { Path = "github.com/test/repo", Version = new SemVersion(1, 0, 0), Variants = [new()] };

    await _service.AddInstalledPackage(pkgSpec, manifest, [], true);

    Assert.Empty(_fileSystem.Directory.GetFiles(".", "*.tmp"));
  }
}
