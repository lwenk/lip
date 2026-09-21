using System.IO.Abstractions;
using Downloader;
using Flurl;
using Flurl.Http;

namespace Lip.Core.Infrastructure;

public interface IFileDownloader {
  Task DownloadFile(Url url, IFileInfo destination);
}

public class FileDownloader(IUserInteraction userInteraction) : IFileDownloader {
  private const long _unknownLengthReportStep = 256 * 1024;

  private readonly IUserInteraction _userInteraction = userInteraction;

  public async Task DownloadFile(Url url, IFileInfo destination) {
    int lastReportedPercentage = -1;
    long lastReportedBytes = 0;
    long totalBytes = 0;
    long receivedBytes = 0;

    // The message is the hyperlink shown next to the bar. The durable line is printed afterwards,
    // outside the progress region: writing to the console while a live region is active makes the
    // region's cursor accounting wrong, which leaves fragments on screen.
    await _userInteraction.RunWithProgress(url.ToString(), async progress => {
      await using IDownload downloader = DownloadBuilder.New()
          .WithUrl(url)
          .WithFileLocation(destination.FullName)
          .WithConfiguration(new() {
            ChunkCount = 4,
            ParallelDownload = true,
            RequestConfiguration =
              {
                        Proxy = HttpClient.DefaultProxy,
              }
          })
          .Build();

      // Failures are only reported through the completion event, so surface them through a task
      // that can actually be awaited. A swallowed error would leave a truncated file behind that
      // later looks like a cache hit.
      TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

      downloader.DownloadProgressChanged += (sender, e) => {
        totalBytes = Math.Max(e.TotalBytesToReceive, 0);

        // ReceivedBytesSize is the cumulative total; ProgressedByteSize is only the delta since
        // the previous event and would keep the bar near zero.
        receivedBytes = e.ReceivedBytesSize;

        // Report at most once per whole percent, or per 256 KiB when the length is unknown, so that
        // redirected output does not fill up with progress lines.
        bool shouldReport;

        if (totalBytes > 0) {
          int percentage = (int)(100d * receivedBytes / totalBytes);

          shouldReport = percentage != lastReportedPercentage;
          lastReportedPercentage = percentage;
        } else {
          shouldReport = receivedBytes - lastReportedBytes >= _unknownLengthReportStep;
          lastReportedBytes = receivedBytes;
        }

        if (!shouldReport) {
          return;
        }

        progress.Report(new TransferProgress(receivedBytes, totalBytes));
      };

      downloader.DownloadFileCompleted += (sender, e) => {
        if (e.Error is not null) {
          completion.TrySetException(e.Error);
        } else if (e.Cancelled) {
          completion.TrySetCanceled();
        } else {
          completion.TrySetResult();
        }
      };

      try {
        await downloader.StartAsync();
        await completion.Task;
      }
      finally {
        // The whole-percent throttle can leave the bar one percent short of the truth.
        if (totalBytes > 0) {
          progress.Report(new TransferProgress(totalBytes, totalBytes));
        }
      }
    });

    long transferredBytes = totalBytes > 0 ? totalBytes : receivedBytes;
    string amount = totalBytes > 0
        ? $"{transferredBytes} of {totalBytes} bytes"
        : $"{transferredBytes} bytes (length not announced)";

    await _userInteraction.PrintVerbose($"Downloaded {amount} from {url}");

    // Past tense on purpose: this line is only reached once the transfer succeeded, so it never
    // claims something is still downloading.
    await _userInteraction.PrintInfo(
        $"Downloaded {TransferProgress.FormatBytes(transferredBytes)} from {url}");
  }
}
