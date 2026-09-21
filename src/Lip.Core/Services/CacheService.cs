using System.IO.Abstractions;
using Lip.Core.Infrastructure;

namespace Lip.Core.Services;

public interface ICacheService {
  Task Clean();
  Task<IDirectoryInfo> GetOrCreateDirectory(
      string key,
      Func<IDirectoryInfo, Task> factory);
  Task<IFileInfo> GetOrCreateFile(
      string key,
      Func<IFileInfo, Task> factory);
}

public class CacheService(IFileSystem fileSystem, IUserInteraction userInteraction) : ICacheService {
  private readonly IDirectoryInfo _cacheDirectory = fileSystem.DirectoryInfo.New(
      Path.Join(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "lip",
          "cache"));
  private readonly IDirectoryInfo _tempDirectory = fileSystem.DirectoryInfo.New(
      Path.Join(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "lip",
          "temp"));

  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IUserInteraction _userInteraction = userInteraction;

  public async Task Clean() {
    DeleteDirectory(_cacheDirectory);

    // Partial downloads and abandoned staging directories are never reclaimed otherwise, so the
    // temp directory is cleaned together with the cache.
    DeleteDirectory(_tempDirectory);
  }

  private static void DeleteDirectory(IDirectoryInfo directory) {
    if (!directory.Exists) {
      return;
    }

    RemoveReadOnlyRecursive(directory);

    directory.Delete(recursive: true);
  }

  /// <summary>
  /// Recursively removes the read-only attribute so that the directory can be deleted. Cached git
  /// checkouts contain read-only files on Windows.
  /// </summary>
  private static void RemoveReadOnlyRecursive(IDirectoryInfo directory) {
    if (directory.Attributes.HasFlag(FileAttributes.ReadOnly)) {
      directory.Attributes &= ~FileAttributes.ReadOnly;
    }

    foreach (IFileInfo file in directory.GetFiles()) {
      if (file.IsReadOnly) {
        file.IsReadOnly = false;
      }
    }

    foreach (IDirectoryInfo subDirectory in directory.GetDirectories()) {
      RemoveReadOnlyRecursive(subDirectory);
    }
  }

  public async Task<IDirectoryInfo> GetOrCreateDirectory(string key, Func<IDirectoryInfo, Task> factory) {
    string safeKey = Uri.EscapeDataString(key);

    IDirectoryInfo cacheDir = _fileSystem.DirectoryInfo.New(Path.Join(_cacheDirectory.FullName, safeKey));

    if (cacheDir.Exists) {
      await _userInteraction.PrintVerbose($"Cache hit for directory key '{key}'.");

      return cacheDir;
    }
    cacheDir.Parent?.Create();

    await _userInteraction.PrintVerbose($"Caching directory for key '{key}'...");

    IDirectoryInfo tempDir = _fileSystem.DirectoryInfo.New(Path.Join(_tempDirectory.FullName, Guid.NewGuid().ToString()));
    tempDir.Parent?.Create();

    try {
      await factory(tempDir);

      _fileSystem.Directory.Move(tempDir.FullName, cacheDir.FullName);
    }
    catch (IOException) when (cacheDir.Exists) {
      // Another process has already created the cache directory, so we can ignore this exception.
    }
    finally {
      if (tempDir.Exists) {
        tempDir.Delete(recursive: true);
      }
    }

    cacheDir.Refresh();

    return cacheDir;
  }

  public async Task<IFileInfo> GetOrCreateFile(string key, Func<IFileInfo, Task> factory) {
    string safeKey = Uri.EscapeDataString(key);

    IFileInfo cacheFile = _fileSystem.FileInfo.New(Path.Join(_cacheDirectory.FullName, safeKey));

    if (cacheFile.Exists) {
      await _userInteraction.PrintVerbose($"Cache hit for file key '{key}'.");

      return cacheFile;
    }
    cacheFile.Directory?.Create();

    await _userInteraction.PrintVerbose($"Caching file for key '{key}'...");

    IFileInfo tempFile = _fileSystem.FileInfo.New(Path.Join(_tempDirectory.FullName, Guid.NewGuid().ToString()));
    tempFile.Directory?.Create();

    try {
      await factory(tempFile);

      _fileSystem.File.Move(tempFile.FullName, cacheFile.FullName);
    }
    catch (IOException) when (cacheFile.Exists) {
      // Another process has already created the cache file, so we can ignore this exception.
    }
    finally {
      tempFile.Delete();
    }

    cacheFile.Refresh();

    return cacheFile;
  }
}
