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
  public async Task InstallPackage_DryRun_NewerVersionOfAnInstalledPackage_DoesNotThrow() {
    // Arrange
    // An update replaces the installed version. A dry run performs none of the removals it described,
    // so the old version is still recorded and must not fail the description of the new one.
    PackageId pkgId = new("github.com/test/pkg", string.Empty);
    PackageSpec oldSpec = new(pkgId, new SemVersion(1, 0, 0));
    PackageSpec newSpec = new(pkgId, new SemVersion(2, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([oldSpec]);

    // Act
    string output = await _installer.InstallPackage(
        new PackageArtifact(newSpec, new Mock<ISource>().Object),
        dryRun: true,
        explicitInstall: true,
        ignoreScripts: false);

    // Assert
    Assert.Equal("", output);
    _userInteraction.Verify(
        u => u.PrintInfo(It.Is<string>(message => message.Contains("would install"))), Times.Once);
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

  [Fact]
  public async Task UninstallPackage_RemoveFiles_RemovesNestedFilesMatchedByRecursiveGlob() {
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
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("**/*.log")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([]);

    _mockFileSystem.AddFile("top.log", new MockFileData("log"));
    _mockFileSystem.AddFile(Path.Combine("logs", "a.log"), new MockFileData("log"));
    _mockFileSystem.AddFile(Path.Combine("logs", "nested", "b.log"), new MockFileData("log"));
    _mockFileSystem.AddFile("keep.txt", new MockFileData("keep"));

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(_mockFileSystem.File.Exists("top.log"));
    Assert.False(_mockFileSystem.File.Exists(Path.Combine("logs", "a.log")));
    Assert.False(_mockFileSystem.File.Exists(Path.Combine("logs", "nested", "b.log")));
    Assert.True(_mockFileSystem.File.Exists("keep.txt"));
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_RemovesDirectoryMatchedByTrailingSlashPattern() {
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
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("temp/")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([]);

    _mockFileSystem.AddFile(Path.Combine("temp", "a.log"), new MockFileData("log"));
    _mockFileSystem.AddFile(Path.Combine("temp", "nested", "b.log"), new MockFileData("log"));
    _mockFileSystem.AddFile("keep.txt", new MockFileData("keep"));

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(_mockFileSystem.Directory.Exists("temp"));
    Assert.True(_mockFileSystem.File.Exists("keep.txt"));
  }

  [Fact]
  public async Task UninstallPackage_PreserveFiles_KeepsFilesMatchedByPathGlob() {
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
                    PreserveFiles = [DotNet.Globbing.Glob.Parse("config/*.json")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(manifest);

    string preservedFile = Path.Combine("config", "settings.json");
    string removedFile = Path.Combine("config", "notes.txt");
    _mockFileSystem.AddFile(preservedFile, new MockFileData("{}"));
    _mockFileSystem.AddFile(removedFile, new MockFileData("notes"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([
            _mockFileSystem.FileInfo.New(preservedFile),
            _mockFileSystem.FileInfo.New(removedFile)
        ]);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(_mockFileSystem.File.Exists(preservedFile));
    Assert.False(_mockFileSystem.File.Exists(removedFile));
  }

  [Fact]
  public async Task UninstallPackage_FileAlreadyGone_StillRemovesPackage() {
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
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(Path.Combine("plugins", "deleted.dll"))]);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    _mockWorkspaceService.Verify(w => w.RemoveInstalledPackage(pkgSpec), Times.Once);
  }

  [Fact]
  public async Task UninstallPackage_RecordedFileOutsideWorkspace_IsLeftAlone() {
    // Arrange
    MockFileSystem fileSystem = new(null, @"C:\ws");
    PackageInstaller installer = CreateInstaller(fileSystem);

    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(new PackageManifest {
          Path = "github.com/test/pkg",
          Version = new SemVersion(1, 0, 0),
          Variants = [new()]
        });

    // A lockfile written by an older lip, recording the location the workspace used to be in.
    string outsidePath = @"C:\elsewhere\plugins\mod.js";
    fileSystem.AddFile(outsidePath, new MockFileData("content"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([fileSystem.FileInfo.New(outsidePath)]);

    // Act
    await installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(fileSystem.File.Exists(outsidePath));
    _userInteraction.Verify(
        u => u.PrintWarning(It.Is<string>(message => message.Contains("outside this workspace"))),
        Times.Once);
  }

  [Fact]
  public async Task UninstallPackage_RecordedFileOutsideWorkspace_IsMappedThroughPlacementDestinations() {
    // Arrange
    MockFileSystem fileSystem = new(null, @"C:\ws");
    PackageInstaller installer = CreateInstaller(fileSystem);

    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(new PackageManifest {
          Path = "github.com/test/pkg",
          Version = new SemVersion(1, 0, 0),
          Variants = [
                new() {
                        Assets = [
                            new() {
                                    Type = PackageManifestAsset.AssetType.Self,
                                    Placements = [
                                        new() {
                                                Type = PackageManifestAssetPlacement.PlacementType.Directory,
                                                Src = "pkg/",
                                                Dst = "plugins/pkg/"
                                        }
                                    ]
                            }
                        ]
                }
            ]
        });

    // The same workspace, after being copied: the lockfile still names the old location, while the
    // package's own files sit here.
    string outsidePath = @"C:\build\ws\plugins\pkg\mod.js";
    string insidePath = @"C:\ws\plugins\pkg\mod.js";
    fileSystem.AddFile(outsidePath, new MockFileData("content"));
    fileSystem.AddFile(insidePath, new MockFileData("content"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([fileSystem.FileInfo.New(outsidePath)]);

    // Act
    await installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(fileSystem.File.Exists(insidePath)); // the copy is cleaned up
    Assert.True(fileSystem.File.Exists(outsidePath)); // the original is untouched
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_KeepsPathsCoveredByPreserveFiles() {
    // Arrange
    MockFileSystem fileSystem = new(null, @"C:\ws");
    PackageInstaller installer = CreateInstaller(fileSystem);

    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(new PackageManifest {
          Path = "github.com/test/pkg",
          Version = new SemVersion(1, 0, 0),
          Variants = [
                new() {
                        RemoveFiles = [DotNet.Globbing.Glob.Parse("config/")],
                        PreserveFiles = [DotNet.Globbing.Glob.Parse("config/settings.json")]
                }
            ]
        });
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([]);

    string preservedPath = @"C:\ws\config\settings.json";
    string removedPath = @"C:\ws\config\other.txt";
    fileSystem.AddFile(preservedPath, new MockFileData("{}"));
    fileSystem.AddFile(removedPath, new MockFileData("other"));

    // Act
    await installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(fileSystem.File.Exists(preservedPath));
    Assert.False(fileSystem.File.Exists(removedPath));
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_NeverDeletesTheWorkspaceState() {
    // Arrange
    MockFileSystem fileSystem = new(null, @"C:\ws");
    PackageInstaller installer = CreateInstaller(fileSystem);

    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(new PackageManifest {
          Path = "github.com/test/pkg",
          Version = new SemVersion(1, 0, 0),
          Variants = [new() {
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("*.json")]
          }]
        });
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([]);

    string lockFilePath = @"C:\ws\tooth_lock.json";
    string settingsPath = @"C:\ws\settings.json";
    fileSystem.AddFile(lockFilePath, new MockFileData("{}"));
    fileSystem.AddFile(settingsPath, new MockFileData("{}"));

    // Act
    await installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(fileSystem.File.Exists(lockFilePath));
    Assert.False(fileSystem.File.Exists(settingsPath));
  }

  [Fact]
  public async Task UninstallPackage_DryRun_ReportsWithoutDeleting() {
    // Arrange
    MockFileSystem fileSystem = new(null, @"C:\ws");
    PackageInstaller installer = CreateInstaller(fileSystem);

    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec))
        .ReturnsAsync(new PackageManifest {
          Path = "github.com/test/pkg",
          Version = new SemVersion(1, 0, 0),
          Variants = [new() {
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("temp/")]
          }]
        });

    string placedPath = @"C:\ws\plugins\mod.js";
    string matchedPath = @"C:\ws\temp\cache.bin";
    fileSystem.AddFile(placedPath, new MockFileData("content"));
    fileSystem.AddFile(matchedPath, new MockFileData("cache"));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([fileSystem.FileInfo.New(placedPath)]);

    // Act
    await installer.UninstallPackage(pkgId, true, false);

    // Assert
    Assert.True(fileSystem.File.Exists(placedPath));
    Assert.True(fileSystem.File.Exists(matchedPath));
    _mockWorkspaceService.Verify(w => w.RemoveInstalledPackage(It.IsAny<PackageSpec>()), Times.Never);
    _userInteraction.Verify(
        u => u.PrintInfo(It.Is<string>(message => message.Contains("would delete 1 file(s)"))),
        Times.Once);
    _userInteraction.Verify(
        u => u.PrintInfo(It.Is<string>(message => message.Contains("would delete 2 entry(ies)"))),
        Times.Once);
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_DoesNotReachASameNamedEntryDeeperInTheWorkspace() {
    // Arrange
    // A pattern names a path in the workspace, so `config` is the workspace's own `config` and
    // nothing deeper. Matching the bare entry name as well used to let bds's `behavior_packs` reach
    // `worlds/<level>/behavior_packs` and delete it recursively.
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("config")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec)).ReturnsAsync(manifest);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec)).ReturnsAsync([]);

    string nestedPluginConfig = Path.Combine("plugins", "GMLIB", "config", "settings.json");
    _mockFileSystem.AddFile(Path.Combine("config", "root.txt"), new MockFileData("root"));
    _mockFileSystem.AddFile(nestedPluginConfig, new MockFileData("plugin"));

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.False(_mockFileSystem.Directory.Exists("config"));
    Assert.True(_mockFileSystem.File.Exists(nestedPluginConfig));
  }

  [Fact]
  public async Task UninstallPackage_RemoveFiles_KeepsEntriesAnotherPackageOwns() {
    // Arrange
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));
    PackageSpec otherSpec = new(new PackageId("github.com/other/pkg", ""), new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec, otherSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    RemoveFiles = [DotNet.Globbing.Glob.Parse("plugins")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec)).ReturnsAsync(manifest);
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec)).ReturnsAsync([]);

    string ownedPath = Path.Combine("plugins", "Other", "plugin.dll");
    string loosePath = Path.Combine("plugins", "Leftover", "old.dll");
    _mockFileSystem.AddFile(ownedPath, new MockFileData("dll"));
    _mockFileSystem.AddFile(loosePath, new MockFileData("dll"));
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(otherSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(ownedPath)]);

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    // The matched entry is left whole rather than partially emptied, so the other package keeps
    // working and nothing is deleted from a directory this package does not own.
    Assert.True(_mockFileSystem.File.Exists(ownedPath));
    Assert.True(_mockFileSystem.File.Exists(loosePath));
  }

  [Fact]
  public async Task UninstallPackage_PreserveFiles_KeepsNestedFileMatchedByBareName() {
    // Arrange
    // The two pattern kinds differ on purpose: preserve_files stays forgiving because protecting too
    // much is harmless, while remove_files must not be.
    PackageId pkgId = new("github.com/test/pkg", "");
    PackageSpec pkgSpec = new(pkgId, new SemVersion(1, 0, 0));

    _mockWorkspaceService.Setup(w => w.GetInstalledPackages(IWorkspaceService.PackageScope.All))
        .ReturnsAsync([pkgSpec]);

    PackageManifest manifest = new() {
      Path = "github.com/test/pkg",
      Version = new SemVersion(1, 0, 0),
      Variants = [
            new() {
                    PreserveFiles = [DotNet.Globbing.Glob.Parse("config.json")]
                }
        ]
    };
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageManifest(pkgSpec)).ReturnsAsync(manifest);

    string nestedConfig = Path.Combine("plugins", "Pkg", "config.json");
    _mockWorkspaceService.Setup(w => w.GetInstalledPackageFiles(pkgSpec))
        .ReturnsAsync([_mockFileSystem.FileInfo.New(nestedConfig)]);
    _mockFileSystem.AddFile(nestedConfig, new MockFileData("{}"));

    // Act
    await _installer.UninstallPackage(pkgId, false, false);

    // Assert
    Assert.True(_mockFileSystem.File.Exists(nestedConfig));
  }

  private PackageInstaller CreateInstaller(IFileSystem fileSystem) {
    return new PackageInstaller(
        _mockCommandRunner.Object,
        fileSystem,
        _userInteraction.Object,
        _mockSourceService.Object,
        _mockWorkspaceService.Object);
  }
}
