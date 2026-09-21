using System.IO.Abstractions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lip.Core.Infrastructure;

public interface IDiagnosticLog {
  /// <summary>
  /// The path of the log file written by this run.
  /// </summary>
  string Path { get; }

  void Write(string message);

  void WriteException(Exception exception);
}

/// <summary>
/// Records details of the current run in a per-run file so that a user can attach a single file when
/// reporting an issue. Only the most recent files are kept, because the log is written on every run
/// and must not grow without bound.
/// </summary>
public class DiagnosticLog : IDiagnosticLog {
  private const int _retainedFileCount = 10;

  private static readonly JsonSerializerOptions _jsonSerializerOptions = new() {
    WriteIndented = true
  };

  private readonly IFileSystem _fileSystem;

  public DiagnosticLog(IFileSystem fileSystem) {
    _fileSystem = fileSystem;

    IDirectoryInfo directory = fileSystem.DirectoryInfo.New(GetDirectoryPath());

    directory.Create();
    DeleteOldLogFiles(directory);

    Path = fileSystem.Path.Combine(
        directory.FullName,
        $"lip-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
  }

  public string Path { get; }

  public static string GetDirectoryPath() {
    return System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lip", "logs");
  }

  /// <summary>
  /// Masks the credentials of a URL. Logs are meant to be attached to public issue reports, so
  /// proxy settings such as <c>http://user:password@host</c> must not be written verbatim.
  /// </summary>
  public static string RedactCredentials(string value) {
    int schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);

    if (schemeSeparator < 0) {
      return value;
    }

    int credentialsSeparator = value.IndexOf('@', schemeSeparator + 3);

    if (credentialsSeparator < 0) {
      return value;
    }

    // Only mask userinfo, not an '@' that happens to appear later in the path or query.
    int pathSeparator = value.IndexOf('/', schemeSeparator + 3);

    if (pathSeparator >= 0 && pathSeparator < credentialsSeparator) {
      return value;
    }

    return string.Concat(value.AsSpan(0, schemeSeparator + 3), "***", value.AsSpan(credentialsSeparator));
  }

  public void Write(string message) {
    _fileSystem.File.AppendAllText(Path, message + Environment.NewLine);
  }

  public void WriteException(Exception exception) {
    Write("=== exception ===");
    Write(exception.ToString());

    if (exception is AggregateException aggregateException) {
      int index = 0;

      foreach (Exception innerException in aggregateException.Flatten().InnerExceptions) {
        index++;

        Write($"--- inner exception #{index} ---");
        Write(innerException.ToString());
      }
    }

    // The output a failed command produced is the part that explains the failure, and it is not part
    // of any stack trace. Without it the log would say only "exit code 128".
    foreach (CommandFailedException failedCommand in EnumerateExceptions(exception)
        .OfType<CommandFailedException>()
        .Distinct()) {
      if (failedCommand.Output.Trim().Length == 0) {
        continue;
      }

      Write($"--- output of the failed command: {failedCommand.Command} ---");
      Write(failedCommand.Output.TrimEnd());
    }
  }

  /// <summary>
  /// Yields an exception and every exception nested in it, whatever the nesting shape.
  /// </summary>
  private static IEnumerable<Exception> EnumerateExceptions(Exception exception) {
    yield return exception;

    foreach (Exception innerException in exception is AggregateException aggregateException
        ? aggregateException.InnerExceptions
        : exception.InnerException is Exception inner ? [inner] : []) {
      foreach (Exception nestedException in EnumerateExceptions(innerException)) {
        yield return nestedException;
      }
    }
  }

  public void WriteHeader(string commandLine) {
    Write($"lip {GetVersion()}");
    Write($".NET {Environment.Version}");
    Write($"os: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
    Write($"cwd: {_fileSystem.Directory.GetCurrentDirectory()}");
    Write($"command: {commandLine}");
    Write("=== configuration ===");
    Write(GetRedactedConfiguration());
  }

  private string GetRedactedConfiguration() {
    // Read through the same file the CLI uses, without pulling in the configuration service, so
    // that the log can be written even when service construction failed.
    string configPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lip", "liprc.json");

    try {
      if (!_fileSystem.File.Exists(configPath)) {
        return "(no configuration file)";
      }

      JsonNode? node = JsonNode.Parse(_fileSystem.File.ReadAllText(configPath));

      if (node is not JsonObject jsonObject) {
        return "(unreadable configuration file)";
      }

      JsonObject redacted = [];

      foreach (KeyValuePair<string, JsonNode?> property in jsonObject) {
        string? value = property.Value?.ToString();

        redacted[property.Key] = value is null
            ? null
            : JsonValue.Create(RedactCredentials(value));
      }

      return redacted.ToJsonString(_jsonSerializerOptions);
    }
    catch (Exception ex) {
      return $"(failed to read configuration: {ex.Message})";
    }
  }

  private static string GetVersion() {
    // The informational version is the one `lip --version` reports, so the log agrees with it.
    return Assembly
        .GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
  }

  private static void DeleteOldLogFiles(IDirectoryInfo directory) {
    // File names start with a sortable UTC timestamp, so the newest file sorts last.
    IFileInfo[] oldFiles = [.. directory
        .GetFiles("lip-*.log")
        .OrderByDescending(file => file.Name)
        .Skip(_retainedFileCount - 1)];

    foreach (IFileInfo oldFile in oldFiles) {
      try {
        oldFile.Delete();
      }
      catch (IOException) {
        // A log file held open by a concurrent run is removed the next time instead.
      }
    }
  }
}
