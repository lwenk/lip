using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using DotNet.Globbing;
using Flurl;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.Sources;

namespace Lip.Core.Services;

public interface IPackageInstaller {
  /// <returns>The combined output of the package scripts that were run.</returns>
  Task<string> InstallPackage(
      PackageArtifact packageArtifact,
      bool dryRun,
      bool explicitInstall,
      bool ignoreScripts);

  /// <returns>The combined output of the package scripts that were run.</returns>
  Task<string> UninstallPackage(
      PackageId packageId,
      bool dryRun,
      bool ignoreScripts);
}

public class PackageInstaller(
    ICommandRunner commandRunner,
    IFileSystem fileSystem,
    IUserInteraction userInteraction,
    ISourceService sourceService,
    IWorkspaceService workspaceService) : IPackageInstaller {
  /// <summary>
  /// The most paths of one kind a removal report lists before it summarizes the rest.
  /// </summary>
  private const int _maxReportedPaths = 10;

  private const string _lockFileName = "tooth_lock.json";

  private readonly ICommandRunner _commandRunner = commandRunner;
  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IUserInteraction _userInteraction = userInteraction;

  private readonly ISourceService _sourceService = sourceService;
  private readonly IWorkspaceService _workspaceService = workspaceService;

  public async Task<string> InstallPackage(
      PackageArtifact packageArtifact,
      bool dryRun,
      bool explicitInstall,
      bool ignoreScripts) {
    // A dry run only describes what would happen, so it must not be failed by the state its own
    // described removals would have changed: an update replaces a package's old version, which is
    // still recorded as installed because nothing has actually run.
    if (dryRun) {
      await _userInteraction.PrintInfo(
          $"Dry run: would install package {packageArtifact.Spec.Id} version {packageArtifact.Spec.Version}");

      return "";
    }

    IEnumerable<PackageSpec> installedPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All);

    if (installedPackages.FirstOrDefault(p => p.Id == packageArtifact.Spec.Id) is PackageSpec existingPackageSpec) {
      throw new InvalidOperationException($"Cannot install package {packageArtifact.Spec.Id} version {packageArtifact.Spec.Version} because it is already installed with version {existingPackageSpec.Version}");
    }

    // The caller announces the package as part of its numbered progress line, so nothing is printed
    // here.

    using Stream manifestStream = await packageArtifact.Source.OpenRead("tooth.json");
    PackageManifest manifest = await PackageManifest.FromStream(manifestStream);
    PackageManifestVariant variant = manifest.GetVariant(packageArtifact.Spec.Id.Variant);

    StringBuilder scriptOutput = new();

    async Task RunScripts(IEnumerable<string> scripts) {
      foreach (string script in scripts) {
        scriptOutput.AppendLine(await _commandRunner.Run(script));
      }
    }

    // Step 1: Run pre-install scripts.

    if (!ignoreScripts) {
      await RunScripts(variant.Scripts.PreInstall);
    }

    // Step 2: Place files.

    List<IFileInfo> placedFiles = [];

    foreach (PackageManifestAsset asset in variant.Assets) {
      ISource assetSource = asset.Type switch {
        PackageManifestAsset.AssetType.Self => packageArtifact.Source,
        PackageManifestAsset.AssetType.Uncompressed => await GetSource(asset.Urls, isArchive: false),
        PackageManifestAsset.AssetType.Tar => await GetSource(asset.Urls, isArchive: true),
        PackageManifestAsset.AssetType.Tgz => await GetSource(asset.Urls, isArchive: true),
        PackageManifestAsset.AssetType.Zip => await GetSource(asset.Urls, isArchive: true),
        _ => throw new UnreachableException(),
      };

      foreach (string key in assetSource.Keys) {
        List<IFileInfo> targetLocations = [];
        foreach (PackageManifestAssetPlacement placement in asset.Placements) {
          switch (placement.Type) {
            case PackageManifestAssetPlacement.PlacementType.File:
              if (placement.Src == key) {
                targetLocations.Add(_fileSystem.FileInfo.New(placement.Dst));
              } else if (Glob.Parse(placement.Src).IsMatch(key)) {
                string targetPath = Path.Combine(
                    placement.Dst,
                    Path.GetFileName(key));
                targetLocations.Add(_fileSystem.FileInfo.New(targetPath));
              }

              break;

            case PackageManifestAssetPlacement.PlacementType.Directory:
              // Keep compatibility with older versions of the manifest where src could be empty.
              if (Path.GetRelativePath(string.IsNullOrEmpty(placement.Src) ? "." : placement.Src, key) is string relativePath
                  && !relativePath.StartsWith("..")) {
                string targetPath = Path.Combine(
                    placement.Dst,
                    relativePath);
                targetLocations.Add(_fileSystem.FileInfo.New(targetPath));
              }

              break;

            default:
              throw new UnreachableException();
          }
        }

        foreach (IFileInfo targetLocation in targetLocations) {
          using Stream sourceStream = await assetSource.OpenRead(key);
          using Stream targetStream = _fileSystem.CreateFileWithDirectory(targetLocation.FullName);
          await sourceStream.CopyToAsync(targetStream);

          placedFiles.Add(targetLocation);
        }
      }
    }

    // Step 3: Run post-install scripts.

    if (!ignoreScripts) {
      await RunScripts(variant.Scripts.Install);
      await RunScripts(variant.Scripts.PostInstall);
    }

    // Step 4: Add package to workspace state.

    await _workspaceService.AddInstalledPackage(
        packageArtifact.Spec,
        manifest,
        placedFiles,
        explicitInstall);

    return scriptOutput.ToString();
  }

  public async Task<string> UninstallPackage(
      PackageId packageId,
      bool dryRun,
      bool ignoreScripts) {
    IEnumerable<PackageSpec> installedPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All);


    PackageSpec existingPackageSpec = installedPackages.FirstOrDefault(p => p.Id == packageId)
        ?? throw new InvalidOperationException($"Cannot uninstall package {packageId} because it is not installed");

    PackageManifest manifest = await _workspaceService.GetInstalledPackageManifest(existingPackageSpec);
    PackageManifestVariant variant = manifest.GetVariant(existingPackageSpec.Id.Variant);

    IEnumerable<IFileInfo> recordedFiles = await _workspaceService.GetInstalledPackageFiles(existingPackageSpec);

    if (dryRun) {
      RemovalPlan dryRunPlan = await BuildRemovalPlan(existingPackageSpec, variant, recordedFiles);

      await LogRemovalPlan(existingPackageSpec, dryRunPlan);
      await PrintRemovalPlan(existingPackageSpec, dryRunPlan);

      return "";
    }

    // The caller announces the package as part of its numbered progress line, so nothing is printed
    // here.

    StringBuilder scriptOutput = new();

    async Task RunScripts(IEnumerable<string> scripts) {
      foreach (string script in scripts) {
        scriptOutput.AppendLine(await _commandRunner.Run(script));
      }
    }

    // Step 1: Run pre-uninstall scripts.

    if (!ignoreScripts) {
      await RunScripts(variant.Scripts.PreUninstall);
      await RunScripts(variant.Scripts.Uninstall);
    }

    // Steps 2 and 3: work out what may be deleted, then delete it.

    RemovalPlan plan = await BuildRemovalPlan(existingPackageSpec, variant, recordedFiles);

    // A destructive step is worth an audit trail: the terminal only shows the plan for dry runs, and
    // "what did that uninstall actually delete" has to stay answerable afterwards.
    await LogRemovalPlan(existingPackageSpec, plan);

    foreach (IFileInfo file in plan.Files) {
      file.Delete();
    }

    foreach (string path in plan.RemoveFilesMatches) {
      if (_fileSystem.File.Exists(path)) {
        _fileSystem.File.Delete(path);
      } else if (_fileSystem.Directory.Exists(path)) {
        _fileSystem.Directory.Delete(path, recursive: true);
      }
      // An entry that no longer exists was already removed as part of a matched parent directory.
    }

    await ReportKeptRemovals(plan);

    // Step 4: Run post-uninstall scripts.

    if (!ignoreScripts) {
      await RunScripts(variant.Scripts.PostUninstall);
    }

    // Step 5: Remove package from workspace state.

    await _workspaceService.RemoveInstalledPackage(existingPackageSpec);

    return scriptOutput.ToString();
  }

  /// <summary>
  /// Whether a <c>remove_files</c> pattern matches an entry. The match is confined to the
  /// workspace-relative path, because a pattern is the path the package author wrote: <c>config</c>
  /// means the workspace's own <c>config</c> and nothing deeper.
  /// </summary>
  private bool IsRemoveMatch(Glob glob, string path) => IsGlobMatch(glob, path, matchBareName: false);

  /// <summary>
  /// Whether a <c>preserve_files</c> pattern matches an entry. This also accepts the bare entry name,
  /// unlike <c>remove_files</c>: protecting one file too many is harmless, while deleting one too many
  /// is not, so the two patterns deliberately differ in how forgiving they are.
  /// </summary>
  private bool IsPreserveMatch(Glob glob, string path) => IsGlobMatch(glob, path, matchBareName: true);

  private bool IsGlobMatch(Glob glob, string path, bool matchBareName) {
    string relativePath = _fileSystem.GetWorkspaceRelativePath(path);
    string pattern = glob.ToString();

    // A trailing separator, as in `temp/`, targets a directory together with everything below it.
    // DotNet.Globbing does not express that on its own.
    string directory = pattern.TrimEnd('/', '\\');

    if (directory.Length > 0 && directory.Length < pattern.Length) {
      return relativePath == directory
          || relativePath.StartsWith($"{directory}/", StringComparison.Ordinal);
    }

    return glob.IsMatch(relativePath)
        || (matchBareName && glob.IsMatch(_fileSystem.Path.GetFileName(relativePath)));
  }

  /// <summary>
  /// Everything an uninstall is allowed to delete, and everything it deliberately leaves alone.
  /// </summary>
  private sealed record RemovalPlan(
      List<IFileInfo> Files,
      List<IFileInfo> MissingFiles,
      List<string> OutsideWorkspacePaths,
      List<string> RemoveFilesMatches,
      List<string> KeptPaths,
      List<string> OwnedByOthers);

  /// <summary>
  /// Works out what an uninstall would delete, without deleting anything. Nothing outside the current
  /// workspace is ever planned for deletion: a lockfile written by an older version of lip records
  /// absolute paths, which point at the old location once the workspace has been copied or moved, so
  /// those are mapped back through the package's own placement destinations and reported when they
  /// cannot be.
  /// </summary>
  private async Task<RemovalPlan> BuildRemovalPlan(
      PackageSpec packageSpec,
      PackageManifestVariant variant,
      IEnumerable<IFileInfo> recordedFiles) {
    string workspaceRoot = _fileSystem.Directory.GetCurrentDirectory();

    List<IFileInfo> files = [];
    List<IFileInfo> missingFiles = [];
    List<string> outsideWorkspacePaths = [];
    List<string> keptPaths = [];
    List<string> ownedByOthers = [];
    List<string> ownedFiles = variant.RemoveFiles.Count > 0
        ? await GetFilesOwnedByOtherPackages(packageSpec)
        : [];

    foreach (IFileInfo recordedFile in recordedFiles) {
      IFileInfo? target = ResolveRecordedFile(recordedFile, variant, workspaceRoot);

      if (target is null) {
        outsideWorkspacePaths.Add(recordedFile.FullName);

        continue;
      }

      if (IsPreserved(variant, target.FullName)) {
        keptPaths.Add(target.FullName);

        continue;
      }

      if (!_fileSystem.File.Exists(target.FullName)) {
        missingFiles.Add(target);

        continue;
      }

      files.Add(target);
    }

    List<string> removeFilesMatches = [];
    HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

    foreach (Glob glob in variant.RemoveFiles) {
      // Matching is done over the workspace-relative paths instead of passing the glob to
      // Directory.EnumerateFileSystemEntries as a search pattern, because search patterns do not
      // support recursive '**' segments or directory patterns.
      foreach (string path in _fileSystem.Directory
          .EnumerateFileSystemEntries(".", "*", SearchOption.AllDirectories)
          .Where(path => IsRemoveMatch(glob, path))) {
        if (!seenPaths.Add(path) || IsProtectedPath(path, workspaceRoot)) {
          continue;
        }

        if (IsPreserved(variant, path) || ContainsPreservedFile(path, variant)) {
          keptPaths.Add(path);

          continue;
        }

        // A match that is, or holds, a file another installed package owns is that package's to
        // remove, not this one's.
        if (IsOwnedByAnotherPackage(path, ownedFiles)) {
          ownedByOthers.Add(path);

          continue;
        }

        removeFilesMatches.Add(path);
      }
    }

    // Shortest first, so a matched directory takes its contents with it and the entries below it are
    // already gone by the time they are reached.
    removeFilesMatches.Sort((left, right) => left.Length.CompareTo(right.Length));

    return new(files, missingFiles, outsideWorkspacePaths, removeFilesMatches, keptPaths, ownedByOthers);
  }

  /// <summary>
  /// Every file the other installed packages own. One package's <c>remove_files</c> must not take
  /// another package's files with it, whatever pattern it declares.
  /// </summary>
  private async Task<List<string>> GetFilesOwnedByOtherPackages(PackageSpec packageSpec) {
    List<string> ownedPaths = [];

    foreach (PackageSpec installedPackage in await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All)) {
      if (installedPackage.Id == packageSpec.Id) {
        continue;
      }

      foreach (IFileInfo file in await _workspaceService.GetInstalledPackageFiles(installedPackage)) {
        ownedPaths.Add(NormalizeSeparators(_fileSystem.Path.GetFullPath(file.FullName)));
      }
    }

    return ownedPaths;
  }

  /// <summary>
  /// Whether an entry is, or holds, a file owned by another installed package.
  /// </summary>
  private bool IsOwnedByAnotherPackage(string path, List<string> ownedPaths) {
    if (ownedPaths.Count == 0) {
      return false;
    }

    string fullPath = NormalizeSeparators(_fileSystem.Path.GetFullPath(path)).TrimEnd('/');

    return ownedPaths.Any(ownedPath => string.Equals(ownedPath, fullPath, StringComparison.OrdinalIgnoreCase)
        || ownedPath.StartsWith($"{fullPath}/", StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>
  /// Resolves a file recorded in the lockfile to the file to delete in this workspace, or
  /// <see langword="null"/> when it lies outside the workspace and cannot be mapped back.
  /// </summary>
  private IFileInfo? ResolveRecordedFile(
      IFileInfo recordedFile,
      PackageManifestVariant variant,
      string workspaceRoot) {
    string recordedPath = _fileSystem.Path.GetFullPath(recordedFile.FullName);

    // A path inside the workspace needs no interpretation, which covers both lockfile formats.
    if (_fileSystem.IsInsideWorkspace(recordedPath)) {
      return recordedFile;
    }

    // Paths outside the workspace come from a lockfile written somewhere else. The package's own
    // placement destinations say where its files belong here, so the tail of the recorded path is
    // matched against them, longest destination first.
    string normalizedPath = NormalizeSeparators(recordedPath);

    foreach (string destination in variant.Assets
        .SelectMany(asset => asset.Placements)
        .Select(placement => NormalizeSeparators(placement.Dst).Trim('/'))
        .Where(destination => destination.Length > 0 && destination != ".")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(destination => destination.Length)) {
      int index = IndexOfPathSegment(normalizedPath, destination);

      if (index < 0) {
        continue;
      }

      string remainder = normalizedPath[(index + destination.Length)..].Trim('/');

      string[] segments = [
          workspaceRoot,
              .. destination.Split('/', StringSplitOptions.RemoveEmptyEntries),
              .. remainder.Split('/', StringSplitOptions.RemoveEmptyEntries),
      ];
      string candidatePath = _fileSystem.Path.Combine(segments);

      // A candidate that does not exist here is not evidence of anything, so it is not deleted.
      if (_fileSystem.File.Exists(candidatePath)) {
        return _fileSystem.FileInfo.New(candidatePath);
      }
    }

    return null;
  }

  /// <summary>
  /// Returns the index at which <paramref name="segment"/> appears in <paramref name="path"/> as a
  /// whole path segment, or -1.
  /// </summary>
  private static int IndexOfPathSegment(string path, string segment) {
    int index = 0;

    while ((index = path.IndexOf(segment, index, StringComparison.OrdinalIgnoreCase)) >= 0) {
      bool startsSegment = index == 0 || path[index - 1] == '/';
      int end = index + segment.Length;
      bool endsSegment = end == path.Length || path[end] == '/';

      if (startsSegment && endsSegment) {
        return index;
      }

      index++;
    }

    return -1;
  }

  private static string NormalizeSeparators(string path) {
    return path.Replace('\\', '/');
  }

  private bool IsPreserved(PackageManifestVariant variant, string path) {
    return variant.PreserveFiles.Any(preserveGlob => IsPreserveMatch(preserveGlob, path));
  }

  /// <summary>
  /// Whether a matched entry holds something <c>preserve_files</c> covers. Removing it would take
  /// that file with it, so the whole entry is left alone instead.
  /// </summary>
  private bool ContainsPreservedFile(string path, PackageManifestVariant variant) {
    if (variant.PreserveFiles.Count == 0 || !_fileSystem.Directory.Exists(path)) {
      return false;
    }

    return _fileSystem.Directory
        .EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
        .Any(entry => IsPreserved(variant, entry));
  }

  /// <summary>
  /// Whether an entry is off limits whatever the manifest says: the workspace itself and lip's own
  /// workspace state.
  /// </summary>
  private bool IsProtectedPath(string path, string workspaceRoot) {
    string fullPath = NormalizeSeparators(_fileSystem.Path.GetFullPath(path)).TrimEnd('/');
    string root = NormalizeSeparators(workspaceRoot).TrimEnd('/');

    if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) {
      return true;
    }

    return string.Equals(
        _fileSystem.Path.GetFileName(fullPath),
        _lockFileName,
        StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// States what a dry run would delete, so that a destructive uninstall can be checked before it
  /// runs.
  /// </summary>
  private async Task PrintRemovalPlan(PackageSpec packageSpec, RemovalPlan plan) {
    await _userInteraction.PrintInfo(
        $"Dry run: would uninstall package {packageSpec.Id} version {packageSpec.Version}");
    await _userInteraction.PrintInfo($"  would delete {plan.Files.Count} file(s) placed by the package");

    await PrintPaths(plan.Files.Select(file => FormatReportPath(file.FullName)), "    ");

    await _userInteraction.PrintInfo(
        $"  would delete {plan.RemoveFilesMatches.Count} entry(ies) matched by remove_files");

    await PrintPaths(plan.RemoveFilesMatches.Select(FormatReportPath), "    ");

    if (plan.MissingFiles.Count > 0) {
      await _userInteraction.PrintInfo(
          $"  {plan.MissingFiles.Count} recorded file(s) are already gone");
    }

    await ReportKeptRemovals(plan);
  }

  /// <summary>
  /// Writes the removal plan to the diagnostic log. Only the dry run prints it, but a real uninstall
  /// needs the same record: "what did that command delete" has to stay answerable afterwards.
  /// </summary>
  private async Task LogRemovalPlan(PackageSpec packageSpec, RemovalPlan plan) {
    await _userInteraction.PrintVerbose(
        $"Removal plan for {packageSpec.Id}@{packageSpec.Version}: {plan.Files.Count} file(s) placed by "
        + $"the package, {plan.RemoveFilesMatches.Count} remove_files match(es), "
        + $"{plan.KeptPaths.Count} kept by preserve_files, {plan.OwnedByOthers.Count} owned by other "
        + $"packages, {plan.OutsideWorkspacePaths.Count} outside the workspace");

    foreach (IFileInfo file in plan.Files) {
      await _userInteraction.PrintVerbose($"  delete (placed): {FormatReportPath(file.FullName)}");
    }

    foreach (string path in plan.RemoveFilesMatches) {
      await _userInteraction.PrintVerbose($"  delete (remove_files): {FormatReportPath(path)}");
    }

    foreach (string path in plan.OwnedByOthers) {
      await _userInteraction.PrintVerbose($"  keep (another package owns files here): {FormatReportPath(path)}");
    }
  }

  /// <summary>
  /// Reports what was deliberately not deleted, so that "the file I expected to disappear is still
  /// there" and "the file I expected to keep is gone" can both be told apart from silence.
  /// </summary>
  private async Task ReportKeptRemovals(RemovalPlan plan) {
    foreach (IFileInfo file in plan.MissingFiles) {
      await _userInteraction.PrintWarning(
          $"File '{file.FullName}' does not exist, skipping removal.");
    }

    if (plan.KeptPaths.Count > 0) {
      await _userInteraction.PrintInfo(
          $"  kept {plan.KeptPaths.Count} path(s) because preserve_files covers them");

      await PrintPaths(plan.KeptPaths.Select(FormatReportPath), "    ");
    }

    if (plan.OwnedByOthers.Count > 0) {
      await _userInteraction.PrintInfo(
          $"  kept {plan.OwnedByOthers.Count} path(s) because another installed package owns files there");

      await PrintPaths(plan.OwnedByOthers.Select(FormatReportPath), "    ");
    }

    if (plan.OutsideWorkspacePaths.Count > 0) {
      await _userInteraction.PrintWarning(
          $"{plan.OutsideWorkspacePaths.Count} recorded file(s) are outside this workspace and were left alone");

      await PrintPaths(plan.OutsideWorkspacePaths, "    ");

      await _userInteraction.PrintInfo(
          "      The lockfile was written from a different location. Uninstall there, or install this "
          + "package again here to record it against this workspace.");
    }
  }

  /// <summary>
  /// Formats a path for a removal report: workspace-relative while it is inside the workspace, as
  /// recorded otherwise, so that the report reads the way the package author wrote the manifest.
  /// </summary>
  private string FormatReportPath(string path) {
    return _fileSystem.IsInsideWorkspace(path)
        ? _fileSystem.GetWorkspaceRelativePath(_fileSystem.Path.GetFullPath(path))
        : path;
  }

  private async Task PrintPaths(IEnumerable<string> paths, string indent) {
    List<string> pathList = [.. paths];

    foreach (string path in pathList.Take(_maxReportedPaths)) {
      await _userInteraction.PrintInfo($"{indent}{path}");
    }

    if (pathList.Count > _maxReportedPaths) {
      await _userInteraction.PrintInfo($"{indent}… and {pathList.Count - _maxReportedPaths} more");
    }
  }

  private async Task<ISource> GetSource(IEnumerable<Url> urls, bool isArchive) {
    List<Exception> exceptions = [];

    foreach (Url url in urls) {
      try {
        return await _sourceService.Get(url, isArchive);
      }
      catch (Exception ex) {
        exceptions.Add(new Exception($"Failed to get file source from {url}", ex));
      }
    }

    throw new AggregateException("Failed to get file source from all provided URLs", exceptions);
  }
}
