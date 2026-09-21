using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;

namespace Lip.Core.Services;

public interface IWorkspaceService {
  enum PackageScope {
    All,
    Explicit,
    Implicit,
  }

  Task AddInstalledPackage(
      PackageSpec packageSpec,
      PackageManifest manifest,
      IEnumerable<IFileInfo> files,
      bool isExplicit);
  Task<IEnumerable<IFileInfo>> GetInstalledPackageFiles(PackageSpec packageSpec);
  Task<PackageManifest> GetInstalledPackageManifest(PackageSpec packageSpec);
  Task<IEnumerable<PackageSpec>> GetInstalledPackages(PackageScope scope);
  Task RemoveInstalledPackage(PackageSpec packageSpec);
  Task UpdateInstalledPackageExplicitness(PackageSpec packageSpec, bool isExplicit);
}

public class WorkspaceService(IFileSystem fileSystem) : IWorkspaceService {
  private const string _lockFilePath = "tooth_lock.json";

  private static readonly JsonSerializerOptions _jsonSerializerOptions = new() {
    WriteIndented = true
  };

  private readonly IFileSystem _fileSystem = fileSystem;

  public async Task AddInstalledPackage(
      PackageSpec packageSpec,
      PackageManifest manifest,
      IEnumerable<IFileInfo> files,
      bool isExplicit) {
    if (packageSpec.Id.Path != manifest.Path || packageSpec.Version != manifest.Version) {
      throw new ArgumentException(
          $"Package spec '{packageSpec}' does not match manifest path '{manifest.Path}' and version '{manifest.Version}'.", nameof(packageSpec));
    }

    WorkspaceStatePackage newPackage = new() {
      Files = [.. files.Select(GetLockFilePath)],
      IsExplicit = isExplicit,
      Manifest = manifest,
      Variant = packageSpec.Id.Variant,
    };

    WorkspaceState state = await LoadWorkspaceState();

    if (state.Packages.Any(p => p.GetPackageSpec() == packageSpec)) {
      throw new InvalidOperationException(
          $"Cannot add package '{packageSpec}' because it is already installed.");
    }

    WorkspaceState newState = state with {
      Packages = [.. state.Packages, newPackage]
    };

    await SaveWorkspaceState(newState);
  }

  public async Task<IEnumerable<IFileInfo>> GetInstalledPackageFiles(PackageSpec packageSpec) {
    WorkspaceState state = await LoadWorkspaceState();

    if (!state.Packages.Any(p => p.GetPackageSpec() == packageSpec)) {
      throw new InvalidOperationException(
          $"Cannot get files of package '{packageSpec}' because it is not installed.");
    }

    return state.Packages
        .Single(p => p.GetPackageSpec() == packageSpec)
        .Files
        .Select(GetFileInfo);
  }

  public async Task<PackageManifest> GetInstalledPackageManifest(PackageSpec packageSpec) {
    WorkspaceState state = await LoadWorkspaceState();

    if (!state.Packages.Any(p => p.GetPackageSpec() == packageSpec)) {
      throw new InvalidOperationException(
          $"Cannot get manifest of package '{packageSpec}' because it is not installed.");
    }

    return state.Packages
        .Single(p => p.GetPackageSpec() == packageSpec)
        .Manifest;
  }

  public async Task<IEnumerable<PackageSpec>> GetInstalledPackages(
      IWorkspaceService.PackageScope scope) {
    WorkspaceState state = await LoadWorkspaceState();

    IEnumerable<PackageSpec> packages = scope switch {
      IWorkspaceService.PackageScope.All => state.Packages
          .Select(p => p.GetPackageSpec()),
      IWorkspaceService.PackageScope.Explicit => state.Packages
          .Where(p => p.IsExplicit)
          .Select(p => p.GetPackageSpec()),
      IWorkspaceService.PackageScope.Implicit => state.Packages
          .Where(p => !p.IsExplicit)
          .Select(p => p.GetPackageSpec()),
      _ => throw new UnreachableException(),
    };

    // Topologically sort packages.
    IEnumerable<DependencyNode> dependencyNodes = packages
        .Select(p => new DependencyNode(
            p,
            state.Packages
                .Single(sp => sp.GetPackageSpec() == p)
                .Manifest
                .GetVariant(p.Id.Variant)
                .Dependencies
                .Select(d => new PackageReqt(d.Key, d.Value))));

    IEnumerable<PackageSpec> sortedPackages = IDependencySolver.TopologicalSort(dependencyNodes);

    return sortedPackages;
  }

  public async Task RemoveInstalledPackage(PackageSpec packageSpec) {
    WorkspaceState state = await LoadWorkspaceState();

    if (!state.Packages.Any(p => p.GetPackageSpec() == packageSpec)) {
      throw new InvalidOperationException(
          $"Cannot remove package '{packageSpec}' because it is not installed.");
    }

    WorkspaceState newState = state with {
      Packages = [.. state.Packages.Where(p => p.GetPackageSpec() != packageSpec)]
    };

    await SaveWorkspaceState(newState);
  }

  public async Task UpdateInstalledPackageExplicitness(PackageSpec packageSpec, bool isExplicit) {
    WorkspaceState state = await LoadWorkspaceState();

    if (!state.Packages.Any(p => p.GetPackageSpec() == packageSpec)) {
      throw new InvalidOperationException(
          $"Cannot update explicitness of package '{packageSpec}' because it is not installed.");
    }

    WorkspaceState newState = state with {
      Packages = [.. state.Packages
                .Select(p => p.GetPackageSpec() == packageSpec
                    ? p with { IsExplicit = isExplicit }
                    : p)]
    };

    await SaveWorkspaceState(newState);
  }

  private async Task<WorkspaceState> LoadWorkspaceState() {
    if (!_fileSystem.File.Exists(_lockFilePath)) {
      return new();
    }

    try {
      using Stream readStream = _fileSystem.File.OpenRead(_lockFilePath);

      return await JsonSerializer.DeserializeAsync<WorkspaceState>(readStream)
          ?? throw new InvalidDataException("The lockfile is empty.");
    }
    catch (Exception ex) {
      // Falling back to an empty state here would silently hide every installed package, which
      // makes the next install overwrite files that are no longer tracked by anyone.
      throw new InvalidOperationException(
          $"Failed to load workspace state from '{_lockFilePath}'. " +
          $"Repair or delete the file to continue. Underlying error: {ex.Message}",
          ex);
    }
  }

  private async Task SaveWorkspaceState(WorkspaceState state) {
    string tempPath = $"{_lockFilePath}.{Guid.NewGuid():N}.tmp";

    // Every recorded path is rewritten into the canonical form, so a lockfile written by an older
    // version of lip (which stored absolute paths) converges to workspace-relative paths the first
    // time it is saved. Paths outside the workspace are left exactly as they are.
    WorkspaceState normalizedState = state with {
      Packages = [.. state.Packages.Select(package => package with {
        Files = [.. package.Files.Select(GetCanonicalLockFilePath)]
      })]
    };

    try {
      using (Stream writeStream = _fileSystem.CreateFileWithDirectory(tempPath)) {
        await JsonSerializer.SerializeAsync(writeStream, normalizedState, _jsonSerializerOptions);
      }

      // Swap the lockfile in one step so an interrupted write cannot leave a truncated file behind.
      _fileSystem.File.Move(tempPath, _lockFilePath, overwrite: true);
    }
    finally {
      if (_fileSystem.File.Exists(tempPath)) {
        _fileSystem.File.Delete(tempPath);
      }
    }
  }

  /// <summary>
  /// Converts a file path into the workspace-relative form stored in the lockfile, so that a
  /// lockfile stays valid when the workspace is moved or checked out elsewhere.
  /// </summary>
  private string GetLockFilePath(IFileInfo file) {
    return _fileSystem.GetWorkspaceRelativePath(file.FullName);
  }

  /// <summary>
  /// Canonicalizes a path already stored in the lockfile: a path inside the workspace becomes
  /// workspace-relative, anything else is returned unchanged. Applying it on every save is what
  /// migrates a lockfile written by an older lip that stored absolute paths.
  /// </summary>
  private string GetCanonicalLockFilePath(string path) {
    // A path outside the workspace is left exactly as it is: rewriting it relative to this workspace
    // would silently point it at a different file.
    return _fileSystem.IsInsideWorkspace(path)
        ? _fileSystem.GetWorkspaceRelativePath(_fileSystem.Path.GetFullPath(path))
        : path;
  }

  /// <summary>
  /// Resolves a path stored in the lockfile. Lockfiles written by older versions store absolute
  /// paths, which are still honoured.
  /// </summary>
  private IFileInfo GetFileInfo(string path) {
    return _fileSystem.FileInfo.New(_fileSystem.Path.IsPathRooted(path)
        ? path
        : _fileSystem.Path.GetFullPath(path));
  }
}
