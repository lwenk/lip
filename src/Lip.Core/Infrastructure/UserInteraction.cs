namespace Lip.Core.Infrastructure;

/// <summary>
/// The state of a byte transfer at one point in time.
/// </summary>
/// <param name="ReceivedBytes">The number of bytes transferred so far.</param>
/// <param name="TotalBytes">
/// The number of bytes to transfer in total, or zero when the server did not announce a length.
/// </param>
public record TransferProgress(long ReceivedBytes, long TotalBytes) {
  public double Percentage => TotalBytes > 0
      ? 100d * ReceivedBytes / TotalBytes
      : 0d;

  /// <summary>
  /// Formats a byte count for display, e.g. <c>11.2 MB</c>.
  /// </summary>
  public static string FormatBytes(long bytes) {
    return bytes switch {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} B",
    };
  }
}

public interface IUserInteraction {
  /// <summary>
  /// The width available for output, or <see langword="null"/> when the implementation does not
  /// render to a width-limited surface. Lines longer than this wrap, which breaks aligned columns,
  /// so callers that depend on line shape should shorten their output themselves.
  /// </summary>
  int? ConsoleWidth { get; }

  Task PrintInfo(string message);

  /// <summary>
  /// Reports a detail that is useful when diagnosing a problem but is not part of the progress a
  /// user needs to follow. Implementations usually send it to the diagnostic log instead of the
  /// terminal.
  /// </summary>
  Task PrintVerbose(string message);

  Task PrintSuccess(string message);

  Task PrintWarning(string message);

  Task PrintError(string message);

  /// <summary>
  /// Runs an action while showing the progress of a byte transfer. The bar is transient: it is
  /// removed once the transfer finishes, so a sequence of transfers does not accumulate rows.
  /// </summary>
  Task RunWithProgress(string message, Func<IProgress<TransferProgress>, Task> action);
}
