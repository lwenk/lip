using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.Services;
using Lip.Core.Sources;
using Moq;
using Semver;

namespace Lip.Core.Tests.Services;

public class PackageInstallerTests {
  private readonly Mock<ICommandRunner> _mockCommandRunner;
  private readonly MockFileSystem _mockFileSystem;
  private readonly Mock<IUserInteraction> _userInteraction;
  private readonly Mock<ISourceService> _mockSourceService;
  private readonly Mock<IWorkspaceService> _mockWorkspaceService;
  private readonly PackageInstaller _installer;

  public PackageInstallerTests() {
    _mockCommandRunner = new Mock<ICommandRunner>();
    _mockFileSystem = new MockFileSystem();
    _userInteraction = new Mock<IUserInteraction>();
    _mockSourceService = new Mock<ISourceService>();
    _mockWorkspaceService = new Mock<IWorkspaceService>();

    _installer = new PackageInstaller(
        _mockCommandRunner.Object,
        _mockFileSystem,
        _userInteraction.Object,
        _mockSourceService.Object,
        _mockWorkspaceService.Object);
  }

  [Fact]
  public async Task InstallPackage_AlreadyInstalled_ThrowsException() {
    // Arrange
    PackageSpec pkgSpec = new(new PackageId("github.com/test/pkg", string.Empty), new SemVersion(1, 0, 0));
    PackageArtifact artifact = new(pkgSpec, new Mock<ISource>().Object);

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(() => _installer.InstallPackage(
        artifact,
        dryRun: false,
        explicitInstall: false,
        ignoreScripts: false));
  }

  [Fact]
  public async Task UninstallPackage_NotInstalled_ThrowsException() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", string.Empty);

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([]);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(() => _installer.UninstallPackage(
        pkgId,
        dryRun: false,
        ignoreScripts: false));
  }
  [Fact]
  public async Task InstallPackage_ValidArtifact_InstallsFiles() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    SemVersion pkgVer = new(1, 0, 0);
    PackageSpec pkgSpec = new(pkgId, pkgVer);

    // Setup WorkspaceService to allow install
    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([]);

    // Setup Manifest
    string manifestJson = """
            {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/pkg",
                "version": "1.0.0",
                "variants": [
                    {
                        "assets": [
                            {
                                "type": "self",
                                "placements": [
                                    { "type": "file", "src": "file.txt", "dest": "plugins/file.txt" }
                                ] 
                            }
                        ]
                    }
                ]
            }
            """;
    Mock<ISource> mockSource = new();
    mockSource.Setup(p => p.OpenRead("tooth.json"))
        .ReturnsAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)));

    mockSource.Setup(p => p.Keys).Returns(["file.txt"]);
    mockSource.Setup(p => p.OpenRead("file.txt"))
        .ReturnsAsync(new MemoryStream("content"u8.ToArray()));

    PackageArtifact artifact = new(pkgSpec, mockSource.Object);

    // Act
    await _installer.InstallPackage(artifact, false, false, false);

    // Assert
    Assert.True(_mockFileSystem.File.Exists(Path.Combine("plugins", "file.txt")));
    _mockWorkspaceService.Verify(w => w.AddInstalledPackage(pkgSpec, It.IsAny<PackageManifest>(), It.IsAny<IEnumerable<IFileInfo>>(), false), Times.Once);
  }

  [Fact]
  public async Task InstallPackage_RunsScripts() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([]);

    string manifestJson = """
            {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/pkg",
                "version": "1.0.0",
                "variants": [
                    {
                        "scripts": {
                            "pre_install": ["echo pre"],
                            "post_install": ["echo post"]
                        }
                    }
                ]
            }
            """;

    Mock<ISource> mockSource = new();
    mockSource.Setup(p => p.OpenRead("tooth.json"))
        .ReturnsAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)));
    mockSource.Setup(p => p.Keys).Returns([]);

    PackageArtifact artifact = new(pkgSpec, mockSource.Object);

    // Act
    await _installer.InstallPackage(artifact, false, false, false);

    // Assert
    _mockCommandRunner.Verify(c => c.Run("echo pre"), Times.Once);
    _mockCommandRunner.Verify(c => c.Run("echo post"), Times.Once);
  }

  [Fact]
  public async Task InstallPackage_InstallAliasRunsAfterFilesArePlaced() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([]);

    string filePath = Path.Combine("plugins", "file.txt");
    string manifestJson = """
            {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/pkg",
                "version": "1.0.0",
                "variants": [
                    {
                        "assets": [
                            {
                                "type": "self",
                                "placements": [
                                    { "type": "file", "src": "file.txt", "dest": "plugins/file.txt" }
                                ]
                            }
                        ],
                        "scripts": {
                            "pre_install": ["echo pre"],
                            "install": ["echo install"],
                            "post_install": ["echo post"]
                        }
                    }
                ]
            }
            """;

    Mock<ISource> mockSource = new();
    mockSource.Setup(p => p.OpenRead("tooth.json"))
        .ReturnsAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)));
    mockSource.Setup(p => p.Keys).Returns(["file.txt"]);
    mockSource.Setup(p => p.OpenRead("file.txt"))
        .ReturnsAsync(new MemoryStream("content"u8.ToArray()));

    List<string> executionOrder = [];
    _mockCommandRunner.Setup(c => c.Run(It.IsAny<string>()))
        .Returns<string>(command => {
          if (command == "echo pre") {
            Assert.False(_mockFileSystem.File.Exists(filePath));
          }

          if (command is "echo install" or "echo post") {
            Assert.True(_mockFileSystem.File.Exists(filePath));
          }

          executionOrder.Add(command);
          return Task.FromResult("");
        });
    _mockWorkspaceService.Setup(w => w.AddInstalledPackage(
            pkgSpec,
            It.IsAny<PackageManifest>(),
            It.IsAny<IEnumerable<IFileInfo>>(),
            false))
        .Callback(() => executionOrder.Add("state"))
        .Returns(Task.CompletedTask);

    PackageArtifact artifact = new(pkgSpec, mockSource.Object);

    // Act
    await _installer.InstallPackage(artifact, false, false, false);

    // Assert
    Assert.Equal(["echo pre", "echo install", "echo post", "state"], executionOrder);
  }

  [Fact]
  public async Task UninstallPackage_RemovesFiles() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [new()]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);

    string filePath = Path.Combine("plugins", "file.txt");
    _mockFileSystem.AddFile(filePath, new MockFileData("content"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(filePath)]);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(_mockFileSystem.File.Exists(filePath));
    _mockWorkspaceService.Verify(w => w.RemoveInstalledPackage(pkgSpec), Times.Once);
  }

  [Fact]
  public async Task UninstallPackage_PreserveFiles_KeepsFiles() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    PreserveFiles = [DotNet.Globbing.Glob.Parse("*.txt")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);

    string filePath = Path.Combine("plugins", "file.txt");
    _mockFileSystem.AddFile(filePath, new MockFileData("content"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(filePath)]);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(_mockFileSystem.File.Exists(filePath)); // Should exist
  }

  [Fact]
  public async Task UninstallPackage_UninstallAliasRunsBeforeFilesAreRemoved() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    Scripts = new PackageManifestScripts {
                      PreUninstall = ["echo pre"],
                      Uninstall = ["echo uninstall"],
                      PostUninstall = ["echo post"]
                    }
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);

    string filePath = Path.Combine("plugins", "file.txt");
    _mockFileSystem.AddFile(filePath, new MockFileData("content"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(filePath)]);

    List<string> executionOrder = [];
    _mockCommandRunner.Setup(c => c.Run(It.IsAny<string>()))
        .Returns<string>(command => {
          if (command is "echo pre" or "echo uninstall") {
            Assert.True(_mockFileSystem.File.Exists(filePath));
          }

          if (command == "echo post") {
            Assert.False(_mockFileSystem.File.Exists(filePath));
          }

          executionOrder.Add(command);
          return Task.FromResult("");
        });
    _mockWorkspaceService.Setup(w => w.RemoveInstalledPackage(pkgSpec))
        .Callback(() => executionOrder.Add("state"))
        .Returns(Task.CompletedTask);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.Equal(["echo pre", "echo uninstall", "echo post", "state"], executionOrder);
    Assert.False(_mockFileSystem.File.Exists(filePath));
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_RemovesExtraFiles() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("extra.log")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([]);

    string extraFile = "extra.log";
    _mockFileSystem.AddFile(extraFile, new MockFileData("log"));

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(_mockFileSystem.File.Exists(extraFile));
  }
}
