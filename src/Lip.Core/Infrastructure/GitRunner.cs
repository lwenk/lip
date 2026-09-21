using System.Text;
using CliWrap;

namespace Lip.Core.Infrastructure;

public interface IGitRunner {
  Task Clone(
      string repo,
      string? dir = null,
      string? branch = null,
      int? depth = null);

  Task<IEnumerable<(string Sha, string Ref)>> LsRemote(
      string repository,
      bool refs = false,
      bool tags = false);
}

public class GitRunner : IGitRunner {
  public async Task Clone(
      string repo,
      string? dir = null,
      string? branch = null,
      int? depth = null) {
    StringBuilder output = new();

    // Git's own output is captured instead of being piped to the console: an unreachable remote
    // otherwise prints raw `fatal: ...` lines in the middle of our formatted output, and the reason
    // for a failure has to reach the caller so that it can be reported and logged.
    CommandResult result = await Cli.Wrap("git")
        .WithArguments(
        [
            "clone",
                .. (branch is not null) ? new[] { "--branch", branch } : [],
                .. (depth is not null) ? new[] { "--depth", depth.ToString()! } : [],
                "--",
                repo,
                .. (dir is not null) ? new[] { dir } : []
        ])
        .WithStandardInputPipe(PipeSource.Null)
        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(output))
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync();

    if (!result.IsSuccess) {
      throw new CommandFailedException(
          $"git clone {repo}",
          result.ExitCode,
          CommandRunner.NormalizeScriptOutput(output.ToString()));
    }
  }

  public async Task<IEnumerable<(string Sha, string Ref)>> LsRemote(
      string repository,
      bool refs = false,
      bool tags = false) {
    // Standard output carries the refs and standard error carries diagnostics, so they cannot share
    // a buffer here.
    StringBuilder output = new();
    StringBuilder errorOutput = new();

    CommandResult result = await Cli.Wrap("git")
        .WithArguments([
            "ls-remote",
                .. refs ? new List<string> { "--refs" } : [],
                .. tags ? new List<string> { "--tags" } : [],
                repository
        ])
        .WithStandardInputPipe(PipeSource.Null)
        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output))
        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(errorOutput))
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync();

    if (!result.IsSuccess) {
      throw new CommandFailedException(
          $"git ls-remote {repository}",
          result.ExitCode,
          CommandRunner.NormalizeScriptOutput(errorOutput.ToString()));
    }

    string outputString = output.ToString();

    return outputString
        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static line => {
          string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
          return (Sha: parts[0], Ref: parts[1]);
        });
  }
}
