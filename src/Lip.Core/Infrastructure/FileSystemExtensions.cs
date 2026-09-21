using System.IO.Abstractions;

namespace Lip.Core.Infrastructure;

public static class FileSystemExtensions {
  public static FileSystemStream CreateFileWithDirectory(this IFileSystem fs, string path) {
    string? dirPath = fs.Path.GetDirectoryName(path);

    if (!string.IsNullOrEmpty(dirPath)) {
      fs.Directory.CreateDirectory(dirPath);
    }

    return fs.File.Create(path);
  }

  /// <summary>
  /// Converts a path into the workspace-relative form used by lockfiles and manifest globs, always
  /// using forward slashes. Paths outside the workspace are returned unchanged.
  /// </summary>
  public static string GetWorkspaceRelativePath(this IFileSystem fs, string path) {
    string relativePath = fs.Path.GetRelativePath(
        fs.Directory.GetCurrentDirectory(), fs.Path.GetFullPath(path));

    // GetRelativePath returns an absolute path when the target is not below the workspace.
    return fs.Path.IsPathRooted(relativePath)
        ? relativePath
        : relativePath.Replace('\\', '/');
  }

  /// <summary>
  /// Whether a path lies inside the workspace, which is what decides if lip may delete it. A sibling
  /// of the workspace yields a relative path with <c>..</c> segments rather than an absolute one, so
  /// a rooted result alone does not answer the question.
  /// </summary>
  public static bool IsInsideWorkspace(this IFileSystem fs, string path) {
    string relativePath = fs.GetWorkspaceRelativePath(path);

    return !fs.Path.IsPathRooted(relativePath)
        && relativePath != ".."
        && !relativePath.StartsWith("../", StringComparison.Ordinal);
  }
}
