using System.Text;
using System.Text.RegularExpressions;
using CliWrap;

namespace Lip.Core.Infrastructure;

public interface ICommandRunner {
  /// <summary>
  /// Runs a command in a shell and returns its combined standard output and error. Output is
  /// captured instead of streamed so that it can be shown in context or written to a log.
  /// </summary>
  Task<string> Run(string command);
}

public class CommandRunner : ICommandRunner {
  // Scripts that draw their own progress emit ANSI control sequences and carriage-return redraw
  // frames, which would otherwise be captured as unreadable fragments.
  private static readonly Regex _ansiEscapeSequenceRegex =
      new(@"\x1B(?:\[[0-9;?]*[A-Za-z]|\][^\x07]*\x07|\([0-9A-B])", RegexOptions.Compiled);

  public async Task<string> Run(string command) {
    StringBuilder output = new();

    CommandResult result = await Cli.Wrap(OperatingSystem.IsWindows() ? "cmd.exe" : "sh")
        .WithArguments([
            OperatingSystem.IsWindows() ? "/c" : "-c",
                command
        ])
        .WithStandardInputPipe(PipeSource.Null)
        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(output))
        .ExecuteAsync();

    string normalizedText = NormalizeScriptOutput(output.ToString());

    if (!result.IsSuccess) {
      throw new CommandFailedException(command, result.ExitCode, normalizedText);
    }

    return normalizedText;
  }

  /// <summary>
  /// Reduces raw script output to the text a reader cares about: escape sequences are removed and
  /// lines that a script rewrote in place keep only their final frame, so a script's own progress
  /// display collapses to one line instead of flooding the log with every intermediate frame.
  /// </summary>
  public static string NormalizeScriptOutput(string text) {
    string withoutEscapeSequences = _ansiEscapeSequenceRegex.Replace(text, "");

    // A carriage return not followed by a newline rewrites the current line.
    string[] lines = withoutEscapeSequences.Replace("\r\n", "\n").Split('\n');

    return string.Join('\n', lines.Select(CollapseRedrawFrames));
  }

  private static string CollapseRedrawFrames(string line) {
    string[] frames = line.Split('\r');

    return frames.LastOrDefault(frame => frame.Trim().Length > 0) ?? frames[^1];
  }
}

/// <summary>
/// Indicates that a command exited with a non-zero code, and carries the output it produced so that
/// the caller can show it in context.
/// </summary>
public class CommandFailedException(string command, int exitCode, string output)
    : InvalidOperationException($"The command failed with exit code {exitCode}: {command}") {
  public string Command { get; } = command;

  public int ExitCode { get; } = exitCode;

  public string Output { get; } = output;
}
