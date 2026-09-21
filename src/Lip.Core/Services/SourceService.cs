using System.IO.Abstractions;
using Flurl;
using Golang.Org.X.Mod;
using Lip.Core.Entities;
using Lip.Core.Infrastructure;
using Lip.Core.Sources;
using Semver;

namespace Lip.Core.Services;

public interface ISourceService {
  Task<ISource> Get(LocalPackageSpec localPackageSpec);
  Task<ISource> Get(PackageSpec packageSpec);
  Task<ISource> Get(RemotePackageSpec remotePackageSpec);
  Task<ISource> Get(Url url, bool isArchive);
}

public class SourceService(
    IFileDownloader fileDownloader,
    IGitRunner gitRunner,
    IUserInteraction userInteraction,
    ICacheService cacheService,
    IProxySelector proxySelector) : ISourceService {
  private readonly IFileDownloader _fileDownloader = fileDownloader;
  private readonly IGitRunner _gitRunner = gitRunner;
  private readonly IUserInteraction _userInteraction = userInteraction;

  private readonly ICacheService _cacheService = cacheService;
  private readonly IProxySelector _proxySelector = proxySelector;

  public async Task<ISource> Get(LocalPackageSpec localPackageSpec) {
    if (!localPackageSpec.ArchiveFile.Exists) {
      throw new FileNotFoundException($"The specified local package file does not exist: {localPackageSpec.ArchiveFile.FullName}");
    }

    return new ArchiveSource(localPackageSpec.ArchiveFile);
  }

  public async Task<ISource> Get(PackageSpec packageSpec) {
    List<Exception> exceptions = [];

    foreach (Func<Task<ISource>> sourceFunc in new Func<Task<ISource>>[]
    {
            () => GetPackageViaGoModuleProxy(packageSpec),
            () => GetPackageViaGit(packageSpec),
    }) {
      try {
        return await sourceFunc();
      }
      catch (Exception ex) {
        exceptions.Add(ex);
      }
    }

    throw new AggregateException($"Failed to retrieve package '{packageSpec}' from all sources.", exceptions);
  }

  public async Task<ISource> Get(RemotePackageSpec remotePackageSpec) {
    return await Get(remotePackageSpec.ArchiveUrl, isArchive: true);
  }

  public async Task<ISource> Get(Url url, bool isArchive) {
    IEnumerable<Url> downloadUrls = await GetCandidateUrls(url);

    IFileInfo archiveFile = await _cacheService.GetOrCreateFile(url, async cacheFile => {
      List<Exception> exceptions = [];

      foreach (Url downloadUrl in downloadUrls) {
        try {
          await _fileDownloader.DownloadFile(downloadUrl, cacheFile);
          return;
        }
        catch (Exception ex) {
          exceptions.Add(new Exception($"Failed to download from {downloadUrl}", ex));
        }
      }

      throw new AggregateException($"Failed to download file source from all URLs for {url}", exceptions);
    });

    return isArchive
        ? new ArchiveSource(archiveFile)
        : new SingleFileSource(archiveFile);
  }

  /// <summary>
  /// Builds the ordered candidates for reaching a URL: every probed source as a prefix mirror,
  /// followed by direct access, so that a mirror that is slow or broken never blocks the operation.
  /// Prefix mirrors are a GitHub feature, so any other host is reached directly.
  /// </summary>
  private async Task<IEnumerable<Url>> GetCandidateUrls(Url url) {
    if (url.Host != "github.com") {
      return [url];
    }

    List<Url> candidateUrls = [];

    foreach (Url? source in await _proxySelector.OrderGithubSources()) {
      // A prefix mirror is followed by the whole target URL, host included.
      Url candidateUrl = source is null
          ? url
          : Url.Parse($"{source.ToString().TrimEnd('/')}/{url}");

      if (!candidateUrls.Contains(candidateUrl)) {
        candidateUrls.Add(candidateUrl);
      }
    }

    return candidateUrls;
  }

  private async Task<ISource> GetPackageViaGit(PackageSpec packageSpec) {
    Url repoUrl = Url.Parse($"https://{packageSpec.Id.Path}.git");

    string @ref = $"v{packageSpec.Version}";

    // The cache key is the repository, not the candidate that served it, so a checkout obtained
    // through one mirror is reused instead of being cloned again through another.
    Url keyUrl = repoUrl.Clone();
    keyUrl.Scheme = $"git+{keyUrl.Scheme}";
    keyUrl.Fragment = @ref;

    List<Exception> exceptions = [];

    foreach (Url cloneUrl in await GetCandidateUrls(repoUrl)) {
      bool cloned = false;

      try {
        IDirectoryInfo repoDir = await _cacheService.GetOrCreateDirectory(keyUrl, async cacheDir => {
          await _gitRunner.Clone(cloneUrl, cacheDir.FullName, branch: @ref);
          cloned = true;
        });

        // Past tense on purpose, matching the download lines: this is only reached once the clone
        // succeeded — and only when something was actually cloned rather than served from the cache.
        if (cloned) {
          await _userInteraction.PrintInfo($"Cloned git repository from {cloneUrl}");
        }

        return new DirectorySource(repoDir);
      }
      catch (Exception ex) {
        exceptions.Add(new Exception($"Failed to clone from {cloneUrl}", ex));
      }
    }

    throw new AggregateException($"Failed to clone repository '{repoUrl}' from all sources.", exceptions);
  }

  private async Task<ISource> GetPackageViaGoModuleProxy(PackageSpec packageSpec) {
    SemVersion version = (packageSpec.Version.Major >= 2)
        ? packageSpec.Version.WithMetadata("incompatible")
        : packageSpec.Version;

    string archivePath = Module.EscapePath(packageSpec.Id.Path).Item1 + "/@v/"
        + Module.EscapeVersion(Module.CanonicalVersion($"v{version}")).Item1 + ".zip";

    List<Exception> exceptions = [];

    foreach (Url goModuleProxy in await _proxySelector.OrderGoModuleProxies()) {
      Url archiveUrl = goModuleProxy.Clone().AppendPathSegments(archivePath.Split('/'));

      try {
        IFileInfo archiveFile = await _cacheService.GetOrCreateFile(archiveUrl, async cacheFile => {
          await _fileDownloader.DownloadFile(archiveUrl, cacheFile);
        });

        return new GoModuleArchiveSource(archiveFile);
      }
      catch (Exception ex) {
        exceptions.Add(ex);
      }
    }

    throw new AggregateException(
        $"Failed to retrieve package '{packageSpec}' from the Go module proxies.", exceptions);
  }
}
