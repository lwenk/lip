using System.Diagnostics;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.PackageRegistries;
using Lip.Core.Sources;

using Semver;

namespace Lip.Core.Services;

public interface IInstallService {
  Task<PackageSpec> GetLatestVersion(PackageId packageId);

  Task InstallPackages(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies);

  Task UninstallPackages(
      IEnumerable<PackageId> packages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies);

  Task UpdatePackages(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts);
}

public class InstallService(
    IUserInteraction userInteraction,
    IPackageInstaller packageInstaller,
    IPackageRegistry packageRegistry,
    ISourceService sourceService,
    IWorkspaceService workspaceService) : IInstallService {
  private readonly IUserInteraction _userInteraction = userInteraction;

  private readonly IPackageInstaller _packageInstaller = packageInstaller;
  private readonly IPackageRegistry _packageRegistry = packageRegistry;
  private readonly ISourceService _sourceService = sourceService;
  private readonly IWorkspaceService _workspaceService = workspaceService;

  public async Task<PackageSpec> GetLatestVersion(PackageId packageId) {
    IEnumerable<SemVersion> versions = await _packageRegistry.GetAvailableVersions(packageId);

    SemVersion latestVersion = versions.Max(SemVersion.PrecedenceComparer)
        ?? throw new InvalidOperationException($"Failed to find the latest version for package '{packageId}'.");

    PackageSpec packageSpec = new(packageId, latestVersion);

    return packageSpec;
  }

  public async Task InstallPackages(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies) {
    await InstallPackagesCore(
        packages,
        flexiblePackages,
        localPackages,
        remotePackages,
        dryRun,
        ignoreScripts,
        noDependencies);
  }

  private async Task InstallPackagesCore(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies) {
    if (noDependencies) {
      // Said before anything is downloaded, because the caution applies to what is about to happen.
      await _userInteraction.PrintWarning(
          "--no-dependencies is enabled. This operation may result in a broken workspace.");
    }

    IEnumerable<PackageArtifact> packageArtifacts = await ResolvePackageArtifacts(
        packages,
        flexiblePackages,
        localPackages,
        remotePackages);

    if (noDependencies) {
      List<PackageArtifact> artifacts = [.. packageArtifacts];
      Stopwatch stepStopwatch = Stopwatch.StartNew();
      int count = 0;

      foreach (PackageArtifact packageArtifact in artifacts) {
        count++;

        string scriptOutput = await _packageInstaller.InstallPackage(
            packageArtifact,
            dryRun,
            explicitInstall: true,
            ignoreScripts);

        await _userInteraction.PrintInfo(
            $"[{count}/{artifacts.Count}] {(dryRun ? "would install" : "installed")} {packageArtifact.Spec}");

        await PrintScriptOutput(scriptOutput, "install scripts");
      }

      await PrintSummary(count, 0, stepStopwatch.Elapsed, dryRun);

      return;
    }

    IEnumerable<PackageSpec> explicitlyInstalledPackages = await _workspaceService.GetInstalledPackages(
         IWorkspaceService.PackageScope.Explicit);

    if (packageArtifacts.FirstOrDefault(pa => explicitlyInstalledPackages.Any(ep => ep.Id == pa.Spec.Id)) is PackageArtifact pa) {
      throw new InvalidOperationException(
          $"Cannot install package '{pa.Spec.Id}' because it is already explicitly installed.");
    }

    CompositePackageRegistry packageRegistry = new(
    [
        [new ArtifactsPackageRegistry(packageArtifacts)],
        [_packageRegistry]
    ]);
    DependencySolver dependencySolver = new(packageRegistry, _userInteraction);

    IEnumerable<PackageSpec> newExplicitlyInstalledPackages =
    [
        .. explicitlyInstalledPackages,
            .. packageArtifacts.Select(pa => pa.Spec)
    ]; // Not sorted.
    IEnumerable<PackageSpec> newInstalledPackages = await dependencySolver.Solve(newExplicitlyInstalledPackages
        .Select(p => new PackageReqt(p.Id, SemVersionRange.Equals(p.Version))));

    IEnumerable<PackageSpec> installedPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All); // Topologically sorted.

    Stopwatch stopwatch = Stopwatch.StartNew();

    await PrintPlan(
        newInstalledPackages,
        [.. newExplicitlyInstalledPackages],
        dryRun ? "be installed (dry run)" : "be installed");

    // Uninstall packages that are no longer needed.
    List<PackageSpec> unneededPackages = [.. installedPackages.Reverse().Where(p => !newInstalledPackages.Contains(p))];

    await PrintPlan(
        unneededPackages,
        [],
        dryRun ? "be removed (dry run)" : "be removed");

    int removedCount = 0;

    foreach (PackageSpec unneededPackage in unneededPackages) {
      removedCount++;

      string scriptOutput = await _packageInstaller.UninstallPackage(unneededPackage.Id, dryRun, ignoreScripts);

      // Past tense on purpose: the line is printed once the step is done, so the terminal never
      // ends up showing a list of steps that all still claim to be running.
      await _userInteraction.PrintInfo(
          $"[{removedCount}/{unneededPackages.Count}] {(dryRun ? "would remove" : "removed")} {unneededPackage}");

      await PrintScriptOutput(scriptOutput, "uninstall scripts");
    }

    // Install new packages in topological order.
    List<PackageSpec> missingPackages = [.. newInstalledPackages.Where(p => !installedPackages.Contains(p))];

    int installedCount = 0;

    foreach (PackageSpec newPackage in missingPackages) {
      installedCount++;

      PackageArtifact packageArtifact = packageArtifacts.FirstOrDefault(pa => pa.Spec == newPackage)
          ?? new(newPackage, await _sourceService.Get(newPackage));

      string scriptOutput;

      try {
        scriptOutput = await _packageInstaller.InstallPackage(
            packageArtifact,
            dryRun,
            explicitInstall: newExplicitlyInstalledPackages.Contains(newPackage),
            ignoreScripts);
      }
      catch (CommandFailedException exception) {
        // The output of a failed script is usually the reason, so it is shown right here instead of
        // being left in the log.
        await _userInteraction.PrintError($"[{installedCount}/{missingPackages.Count}] failed to install {newPackage}");
        await PrintScriptOutput(exception.Output, "install scripts");

        throw;
      }

      await _userInteraction.PrintInfo(
          $"[{installedCount}/{missingPackages.Count}] {(dryRun ? "would install" : "installed")} {newPackage}");

      await PrintScriptOutput(scriptOutput, "install scripts");
    }

    await ReconcilePackageExplicitness(
        newInstalledPackages,
        newExplicitlyInstalledPackages,
        explicitlyInstalledPackages,
        dryRun);

    await PrintSummary(installedCount, removedCount, stopwatch.Elapsed, dryRun);
  }

  public async Task UninstallPackages(
      IEnumerable<PackageId> packages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies) {
    await UninstallPackagesCore(
        packages,
        dryRun,
        ignoreScripts,
        noDependencies);
  }

  private async Task UninstallPackagesCore(
      IEnumerable<PackageId> packages,
      bool dryRun,
      bool ignoreScripts,
      bool noDependencies) {
    if (noDependencies) {
      // Said before anything is removed, because the caution applies to what is about to happen.
      await _userInteraction.PrintWarning(
          "--no-dependencies is enabled. This operation may result in a broken workspace.");

      List<PackageId> packageList = [.. packages];
      Stopwatch stepStopwatch = Stopwatch.StartNew();
      int count = 0;

      foreach (PackageId package in packageList) {
        count++;

        string scriptOutput = await _packageInstaller.UninstallPackage(package, dryRun, ignoreScripts);

        await _userInteraction.PrintInfo(
            $"[{count}/{packageList.Count}] {(dryRun ? "would remove" : "removed")} {package}");

        await PrintScriptOutput(scriptOutput, "uninstall scripts");
      }

      await PrintSummary(0, count, stepStopwatch.Elapsed, dryRun);

      return;
    }

    IEnumerable<PackageSpec> explicitlyInstalledPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.Explicit);

    if (packages.FirstOrDefault(p => !explicitlyInstalledPackages.Any(ep => ep.Id == p)) is PackageId packageId) {
      throw new InvalidOperationException(
          $"Package '{packageId}' is not explicitly installed and cannot be uninstalled without --no-dependencies.");
    }

    IEnumerable<PackageSpec> newExplicitlyInstalledPackages = explicitlyInstalledPackages
        .Where(p => !packages.Contains(p.Id));

    IEnumerable<PackageSpec> installedPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All); // Topologically sorted.

    // Uninstalling must not change any version. Resolving against the registries here would take the
    // newest release satisfying each range, so a dependency that merely *could* be upgraded would look
    // unnecessary: the installed version would be deleted and its replacement never installed, leaving
    // the packages that are kept without what they require. The retained set is therefore derived from
    // the dependencies recorded in the lockfile instead, which also makes an uninstall work offline.
    IEnumerable<PackageSpec> newInstalledPackages = await GetReachableInstalledPackages(
        installedPackages,
        newExplicitlyInstalledPackages);

    Stopwatch stopwatch = Stopwatch.StartNew();

    List<PackageSpec> removedPackages = [.. installedPackages.Reverse().Where(p => !newInstalledPackages.Contains(p))];

    await PrintPlan(
        removedPackages,
        [],
        dryRun ? "be removed (dry run)" : "be removed");

    int removedCount = 0;

    foreach (PackageSpec removedPackage in removedPackages) {
      removedCount++;

      string scriptOutput = await _packageInstaller.UninstallPackage(removedPackage.Id, dryRun, ignoreScripts);

      await _userInteraction.PrintInfo(
          $"[{removedCount}/{removedPackages.Count}] {(dryRun ? "would remove" : "removed")} {removedPackage}");

      await PrintScriptOutput(scriptOutput, "uninstall scripts");
    }

    await ReconcilePackageExplicitness(
        newInstalledPackages,
        newExplicitlyInstalledPackages,
        explicitlyInstalledPackages,
        dryRun);

    await PrintSummary(0, removedCount, stopwatch.Elapsed, dryRun);
  }

  /// <summary>
  /// The installed packages that are still required, found by walking the dependencies recorded in the
  /// lockfile from the given roots. No registry is consulted, so a package is never dropped just
  /// because a newer release of it exists.
  /// </summary>
  private async Task<IEnumerable<PackageSpec>> GetReachableInstalledPackages(
      IEnumerable<PackageSpec> installedPackages,
      IEnumerable<PackageSpec> roots) {
    Dictionary<PackageId, PackageSpec> installedById = [];
    Dictionary<string, List<PackageSpec>> installedByPath = new(StringComparer.OrdinalIgnoreCase);

    foreach (PackageSpec spec in installedPackages) {
      installedById[spec.Id] = spec;

      if (!installedByPath.TryGetValue(spec.Id.Path, out List<PackageSpec>? variants)) {
        variants = [];
        installedByPath[spec.Id.Path] = variants;
      }

      variants.Add(spec);
    }

    HashSet<PackageId> reachable = [];
    Queue<PackageSpec> pending = new(roots);

    while (pending.Count > 0) {
      PackageSpec spec = pending.Dequeue();

      if (!reachable.Add(spec.Id)) {
        continue;
      }

      PackageManifest manifest = await _workspaceService.GetInstalledPackageManifest(spec);

      foreach (PackageId requiredId in manifest.GetVariant(spec.Id.Variant).Dependencies.Keys) {
        // A requirement naming a variant is satisfied by that variant; one naming only the package is
        // satisfied by any installed variant of it. Keeping a still-required package is always better
        // than deleting it, so the fallback covers the variants of the same package.
        if (installedById.TryGetValue(requiredId, out PackageSpec? exactMatch)) {
          pending.Enqueue(exactMatch);
        } else if (installedByPath.TryGetValue(requiredId.Path, out List<PackageSpec>? variants)) {
          foreach (PackageSpec variant in variants) {
            pending.Enqueue(variant);
          }
        }
      }
    }

    // Keep the topological order the workspace state is stored in.
    return installedPackages.Where(spec => reachable.Contains(spec.Id));
  }

  public async Task UpdatePackages(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts) {
    await UpdatePackagesCore(
        packages,
        flexiblePackages,
        localPackages,
        remotePackages,
        dryRun,
        ignoreScripts);
  }

  private async Task UpdatePackagesCore(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages,
      bool dryRun,
      bool ignoreScripts) {
    // Naming no package means "every package that was installed on purpose".
    if (!packages.Any() && !flexiblePackages.Any() && !localPackages.Any() && !remotePackages.Any()) {
      await UpdateEveryExplicitPackage(dryRun, ignoreScripts);

      return;
    }

    IEnumerable<PackageArtifact> packageArtifacts = await ResolvePackageArtifacts(
        packages,
        flexiblePackages,
        localPackages,
        remotePackages);

    IEnumerable<PackageSpec> installedPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.All); // Topologically sorted.

    if (packageArtifacts.FirstOrDefault(pa => !installedPackages.Any(ip => ip.Id == pa.Spec.Id)) is PackageArtifact pa) {
      throw new InvalidOperationException(
          $"Cannot update package '{pa.Spec.Id}' because it is not currently installed.");
    }

    CompositePackageRegistry packageRegistry = new(
    [
      [new ArtifactsPackageRegistry(packageArtifacts)],
      [_packageRegistry]
    ]);
    DependencySolver dependencySolver = new(packageRegistry, _userInteraction);

    IEnumerable<PackageSpec> explicitlyInstalledPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.Explicit);

    IEnumerable<PackageSpec> newExplicitlyInstalledPackages = [
        .. explicitlyInstalledPackages.Where(ep => !packageArtifacts.Any(pa => pa.Spec.Id == ep.Id)),
            .. packageArtifacts.Select(pa => pa.Spec)
    ]; // Not sorted.

    IEnumerable<PackageSpec> newInstalledPackages = await dependencySolver.Solve(newExplicitlyInstalledPackages
        .Select(p => new PackageReqt(p.Id, SemVersionRange.Equals(p.Version))));

    Stopwatch stopwatch = Stopwatch.StartNew();

    List<PackageSpec> removedPackages = [.. installedPackages.Reverse().Where(p => !newInstalledPackages.Contains(p))];
    List<PackageSpec> missingPackages = [.. newInstalledPackages.Where(p => !installedPackages.Contains(p))];

    await PrintUpdatePlan(removedPackages, missingPackages, dryRun);

    int removedCount = 0;

    foreach (PackageSpec removedPackage in removedPackages) {
      removedCount++;

      string scriptOutput = await _packageInstaller.UninstallPackage(removedPackage.Id, dryRun, ignoreScripts);

      await _userInteraction.PrintInfo(
          $"[{removedCount}/{removedPackages.Count}] {(dryRun ? "would remove" : "removed")} {removedPackage}");

      await PrintScriptOutput(scriptOutput, "uninstall scripts");
    }

    // Install new packages in topological order.
    int installedCount = 0;

    foreach (PackageSpec newPackage in missingPackages) {
      installedCount++;

      PackageArtifact packageArtifact = packageArtifacts.FirstOrDefault(pa => pa.Spec == newPackage)
          ?? new(newPackage, await _sourceService.Get(newPackage));

      string scriptOutput;

      try {
        scriptOutput = await _packageInstaller.InstallPackage(
            packageArtifact,
            dryRun,
            explicitInstall: newExplicitlyInstalledPackages.Contains(newPackage),
            ignoreScripts);
      }
      catch (CommandFailedException exception) {
        // The output of a failed script is usually the reason, so it is shown right here instead of
        // being left in the log.
        await _userInteraction.PrintError($"[{installedCount}/{missingPackages.Count}] failed to install {newPackage}");
        await PrintScriptOutput(exception.Output, "install scripts");

        throw;
      }

      await _userInteraction.PrintInfo(
          $"[{installedCount}/{missingPackages.Count}] {(dryRun ? "would install" : "installed")} {newPackage}");

      await PrintScriptOutput(scriptOutput, "install scripts");
    }

    await ReconcilePackageExplicitness(
        newInstalledPackages,
        newExplicitlyInstalledPackages,
        explicitlyInstalledPackages,
        dryRun);

    await PrintSummary(installedCount, removedCount, stopwatch.Elapsed, dryRun);
  }

  /// <summary>
  /// Updates every explicitly installed package, one at a time, exactly as naming each of them would.
  /// A package whose newest release cannot be resolved is reported and skipped instead of failing the
  /// whole run, which matters in practice: a single package in a server's set can be broken (an
  /// unsatisfiable range in its manifest, pointing at a release that does not exist) while every other
  /// package is perfectly updatable.
  /// </summary>
  private async Task UpdateEveryExplicitPackage(bool dryRun, bool ignoreScripts) {
    IEnumerable<PackageSpec> explicitlyInstalledPackages = await _workspaceService.GetInstalledPackages(
        IWorkspaceService.PackageScope.Explicit);

    List<(PackageSpec Installed, PackageSpec Latest)> updates = [];

    foreach (PackageSpec installedPackage in explicitlyInstalledPackages) {
      PackageSpec latestPackage;

      try {
        latestPackage = await GetLatestVersion(installedPackage.Id);
      }
      catch (Exception exception) {
        await _userInteraction.PrintWarning(
            $"Cannot look for an update of '{installedPackage.Id}': {exception.Message}");
        await _userInteraction.PrintVerbose(exception.ToString());

        continue;
      }

      if (SemVersion.PrecedenceComparer.Compare(latestPackage.Version, installedPackage.Version) > 0) {
        updates.Add((installedPackage, latestPackage));
      }
    }

    if (updates.Count == 0) {
      await _userInteraction.PrintInfo("Everything is up to date.");

      return;
    }

    // A pre-check, not a promise: a package listed here can still fail to resolve, which is why each
    // update below reports the changes it actually makes.
    await _userInteraction.PrintInfo("The following packages have newer versions available:");
    await _userInteraction.PrintInfo("");

    foreach ((PackageSpec installedPackage, PackageSpec latestPackage) in updates) {
      await _userInteraction.PrintInfo(
          $"  {installedPackage.Id}  {installedPackage.Version} → {latestPackage.Version}");
    }

    await _userInteraction.PrintInfo("");

    foreach ((PackageSpec installedPackage, _) in updates) {
      try {
        await UpdatePackagesCore([], [installedPackage.Id], [], [], dryRun, ignoreScripts);
      }
      catch (Exception exception) {
        await _userInteraction.PrintWarning(
            $"Failed to update '{installedPackage.Id}': {exception.Message}");
        await _userInteraction.PrintVerbose(exception.ToString());
      }
    }
  }

  /// <summary>
  /// Prints what the operation is about to do before anything happens, so that the user can judge
  /// the change at a glance.
  /// </summary>
  private async Task PrintPlan(IEnumerable<PackageSpec> packages, HashSet<PackageSpec> requestedPackages, string verb) {
    List<PackageSpec> packageList = [.. packages];

    if (packageList.Count == 0) {
      return;
    }

    await _userInteraction.PrintInfo($"The following packages will {verb}:");
    await _userInteraction.PrintInfo("");

    // Packages are grouped by role instead of aligning the role in a second column, because a
    // padding column breaks as soon as a package identifier is longer than the terminal width.
    if (requestedPackages.Count > 0) {
      await PrintPlanGroup("Requested", [.. packageList.Where(requestedPackages.Contains)]);
      await PrintPlanGroup("Dependencies", [.. packageList.Where(package => !requestedPackages.Contains(package))]);
    } else {
      await PrintPlanGroup(null, packageList);
    }

    await _userInteraction.PrintInfo("");
  }

  private async Task PrintUpdatePlan(
      IEnumerable<PackageSpec> removedPackages,
      IEnumerable<PackageSpec> missingPackages,
      bool dryRun) {
    List<PackageSpec> removed = [.. removedPackages];
    List<PackageSpec> added = [.. missingPackages];
    List<string> lines = [];

    // An update is a version transition, so it is reported as one: the removed and the installed
    // package describe the same package from two sides.
    foreach (PackageSpec newPackage in added) {
      PackageSpec? oldPackage = removed.FirstOrDefault(p => p.Id == newPackage.Id);

      lines.Add(oldPackage is null
          ? $"  {newPackage.Id}  {newPackage.Version} (new dependency)"
          : $"  {newPackage.Id}  {oldPackage.Version} → {newPackage.Version}");
    }

    foreach (PackageSpec oldPackage in removed.Where(p => added.All(a => a.Id != p.Id))) {
      lines.Add($"  {oldPackage.Id}  {oldPackage.Version} (no longer needed)");
    }

    if (lines.Count == 0) {
      await _userInteraction.PrintInfo("Everything is up to date.");

      return;
    }

    await _userInteraction.PrintInfo(dryRun
        ? "The following changes would be made:"
        : "The following changes will be made:");
    await _userInteraction.PrintInfo("");

    foreach (string line in lines) {
      await _userInteraction.PrintInfo(line);
    }

    await _userInteraction.PrintInfo("");
  }

  private async Task PrintPlanGroup(string? title, List<PackageSpec> packages) {
    if (packages.Count == 0) {
      return;
    }

    if (title is not null) {
      await _userInteraction.PrintInfo($"  {title}:");
    }

    foreach (PackageSpec package in packages) {
      await _userInteraction.PrintInfo($"    {package}");
    }
  }

  /// <summary>
  /// Prints the output a package's scripts produced under the package's own step. The full output is
  /// written to the diagnostic log first, so a bounded tail on the terminal never loses information.
  /// </summary>
  private async Task PrintScriptOutput(string? scriptOutput, string label) {
    string output = scriptOutput ?? "";

    List<string> lines = [.. output
        .Split(["\r\n", "\n"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd())];

    if (lines.Count == 0) {
      return;
    }

    await _userInteraction.PrintVerbose(output);

    const int maxShownLines = 20;
    const string ellipsis = "…";
    int firstShownLine = Math.Max(0, lines.Count - maxShownLines);

    // A long script line would wrap and make the tail hard to scan, so it is cut to the width of
    // the output surface. The uncut text is in the diagnostic log.
    int maxLineLength = Math.Max((_userInteraction.ConsoleWidth ?? int.MaxValue) - 6 - ellipsis.Length, 20);

    await _userInteraction.PrintInfo($"  └ {label}:");

    foreach (string line in lines.Skip(firstShownLine)) {
      await _userInteraction.PrintInfo($"      {(line.Length > maxLineLength ? line[..(maxLineLength - ellipsis.Length)] + ellipsis : line)}");
    }

    if (firstShownLine > 0) {
      await _userInteraction.PrintInfo($"      … {firstShownLine} more lines in the diagnostic log");
    }
  }

  private async Task PrintSummary(int installedCount, int removedCount, TimeSpan elapsed, bool dryRun) {
    int totalCount = installedCount + removedCount;

    if (totalCount == 0) {
      await _userInteraction.PrintInfo("Nothing to do.");
      return;
    }

    string packages = totalCount == 1 ? "package" : "packages";
    string actions = string.Join(" and ", new[] {
        installedCount > 0 ? $"{(dryRun ? "would install" : "installed")} {installedCount}" : null,
        removedCount > 0 ? $"{(dryRun ? "would remove" : "removed")} {removedCount}" : null,
    }.Where(part => part is not null));

    await _userInteraction.PrintInfo(dryRun
        ? $"{actions} {packages} (dry run: nothing was changed)"
        : $"{actions} {packages} in {elapsed.TotalSeconds:0.#}s");
  }

  private async Task ReconcilePackageExplicitness(
      IEnumerable<PackageSpec> installedPackages,
      IEnumerable<PackageSpec> explicitlyInstalledPackages,
      IEnumerable<PackageSpec> previousExplicitlyInstalledPackages,
      bool dryRun) {
    foreach (PackageSpec newPackage in installedPackages) {
      bool isExplicit = explicitlyInstalledPackages.Contains(newPackage);
      bool wasExplicit = previousExplicitlyInstalledPackages.Contains(newPackage);

      if (isExplicit && !wasExplicit) {
        if (dryRun) {
          await _userInteraction.PrintInfo(
              $"Dry run: would mark package '{newPackage.Id}' version {newPackage.Version} as explicitly installed.");

          continue;
        }

        await _userInteraction.PrintVerbose(
            $"Marking package '{newPackage.Id}' version {newPackage.Version} as explicitly installed.");

        await _workspaceService.UpdateInstalledPackageExplicitness(newPackage, isExplicit: true);

      } else if (!isExplicit && wasExplicit) {
        if (dryRun) {
          await _userInteraction.PrintInfo(
              $"Dry run: would mark package '{newPackage.Id}' version {newPackage.Version} as not explicitly installed.");

          continue;
        }

        await _userInteraction.PrintVerbose(
            $"Marking package '{newPackage.Id}' version {newPackage.Version} as not explicitly installed.");

        await _workspaceService.UpdateInstalledPackageExplicitness(newPackage, isExplicit: false);
      }
    }
  }

  private async Task<IEnumerable<PackageArtifact>> ResolvePackageArtifacts(
      IEnumerable<PackageSpec> packages,
      IEnumerable<PackageId> flexiblePackages,
      IEnumerable<LocalPackageSpec> localPackages,
      IEnumerable<RemotePackageSpec> remotePackages) {
    List<PackageArtifact> packageArtifacts = [];

    foreach (PackageSpec packageSpec in packages) {
      ISource source = await _sourceService.Get(packageSpec);
      PackageArtifact packageArtifact = new(packageSpec, source);

      packageArtifacts.Add(packageArtifact);
    }

    foreach (PackageId packageId in flexiblePackages) {
      await _userInteraction.PrintVerbose($"Getting available versions for package '{packageId}'...");

      PackageSpec latestPackageSpec = await GetLatestVersion(packageId);
      ISource source = await _sourceService.Get(latestPackageSpec);
      PackageArtifact packageArtifact = new(latestPackageSpec, source);

      packageArtifacts.Add(packageArtifact);
    }

    foreach (LocalPackageSpec localPackageSpec in localPackages) {
      ISource source = await _sourceService.Get(localPackageSpec);

      using Stream manifestStream = await source.OpenRead("tooth.json");
      PackageManifest manifest = await PackageManifest.FromStream(manifestStream);

      PackageId packageId = new(manifest.Path, localPackageSpec.Variant);
      PackageSpec packageSpec = new(packageId, manifest.Version);
      PackageArtifact packageArtifact = new(packageSpec, source);

      packageArtifacts.Add(packageArtifact);
    }

    foreach (RemotePackageSpec remotePackageSpec in remotePackages) {
      ISource source = await _sourceService.Get(remotePackageSpec);

      using Stream manifestStream = await source.OpenRead("tooth.json");
      PackageManifest manifest = await PackageManifest.FromStream(manifestStream);

      PackageId packageId = new(manifest.Path, remotePackageSpec.Variant);
      PackageSpec packageSpec = new(packageId, manifest.Version);
      PackageArtifact packageArtifact = new(packageSpec, source);

      packageArtifacts.Add(packageArtifact);
    }

    // Check for duplicate package IDs.
    if (packageArtifacts
        .GroupBy(pa => pa.Spec.Id)
        .FirstOrDefault(g => g.Count() > 1) is IGrouping<PackageId, PackageArtifact> duplicateGroup) {
      throw new InvalidOperationException(
          $"Multiple packages with ID '{duplicateGroup.Key}' were specified. Please ensure that each package ID is unique.");
    }

    return packageArtifacts;
  }
}
