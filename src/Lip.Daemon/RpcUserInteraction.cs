using Lip.Core.Infrastructure;

namespace Lip.Daemon;

public class RpcUserInteraction(IClientContract clientContract) : IUserInteraction {
  private readonly IClientContract _clientContract = clientContract;

  public int? ConsoleWidth => null;

  public async Task PrintError(string message) {
    await _clientContract.PrintError(message);
  }

  public async Task PrintInfo(string message) {
    await _clientContract.PrintInfo(message);
  }

  public async Task PrintVerbose(string message) {
    // Verbosity is aimed at the diagnostic log, which the daemon writes on its own, so it is not
    // forwarded over the RPC channel.
    await Task.CompletedTask;
  }

  public async Task PrintSuccess(string message) {
    await _clientContract.PrintSuccess(message);
  }

  public async Task PrintWarning(string message) {
    await _clientContract.PrintWarning(message);
  }

  public async Task RunWithProgress(string message, Func<IProgress<TransferProgress>, Task> action) {
    string progressId = Guid.NewGuid().ToString();

    Progress<TransferProgress> progress = new(update => {
      _clientContract.ReportProgress(progressId, message, update.Percentage);
    });

    await action(progress);
  }
}
