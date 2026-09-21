using Lip.Core.Infrastructure;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Lip.Cli;

public class ConsoleUserInteraction : IUserInteraction {
  private readonly IAnsiConsole _console;

  private readonly IDiagnosticLog? _diagnosticLog;

  public int? ConsoleWidth => _console.Profile.Width;

  public ConsoleUserInteraction(IDiagnosticLog? diagnosticLog = null, IAnsiConsole? console = null) {
    _diagnosticLog = diagnosticLog;
    _console = console ?? AnsiConsole.Create(new() {
      Out = new AnsiConsoleOutput(Console.Error)
    });
  }

  public async Task PrintInfo(string message) {
    // URLs become clickable terminal hyperlinks, so a link that wraps across lines still opens its
    // full address when clicked.
    _console.MarkupLine(TextLinker.Linkify(message));
  }

  public async Task PrintVerbose(string message) {
    if (_diagnosticLog is not null) {
      _diagnosticLog.Write(message);
    }
  }

  public async Task PrintSuccess(string message) {
    _console.MarkupLine($"[green]✓ {message.EscapeMarkup()}[/]");
  }

  public async Task PrintWarning(string message) {
    _console.MarkupLine($"[#FFA500]⚠[/] [yellow]{message.EscapeMarkup()}[/]");
  }

  public async Task PrintError(string message) {
    _console.MarkupLine($"[bold red]✗ {message.EscapeMarkup()}[/]");
  }

  public async Task RunWithProgress(string message, Func<IProgress<TransferProgress>, Task> action) {
    if (!_console.Profile.Capabilities.Interactive) {
      // Redirected output cannot show a live bar; rendering one would print a copy of it for every
      // update. The durable "Downloaded" line that the caller prints afterwards is enough.
      await action(new NullTransferProgress());
      return;
    }

    // AutoClear removes the bar once the transfer finishes. Keeping it would leave one stale row
    // behind per file, which turns a long operation into a wall of finished bars.
    await _console.Progress()
        .AutoClear(true)
        .Columns(new TransferStatusColumn(_console.Profile.Width))
        .StartAsync(async context => {
          // The task description carries the link shown above the bar. MaxValue starts at the
          // "unknown length" sentinel so that the first frame cannot claim a total it does not know.
          ProgressTask task = context.AddTask(message, maxValue: double.MaxValue);
          task.IsIndeterminate = true;

          IProgress<TransferProgress> reporter = new Progress<TransferProgress>(update => {
            if (update.TotalBytes > 0) {
              task.IsIndeterminate = false;
              task.MaxValue = update.TotalBytes;
              task.Value = update.ReceivedBytes;
              return;
            }

            // Without an announced length the bar animates instead of showing a fraction, but Value
            // still carries the transferred byte count so the details stay truthful.
            task.IsIndeterminate = true;
            task.MaxValue = double.MaxValue;
            task.Value = update.ReceivedBytes;
          });

          try {
            await action(reporter);
          }
          finally {
            task.StopTask();
          }
        });
  }

  /// <summary>
  /// Renders one transfer as two lines: the link, then a solid block bar with percentage, bytes and
  /// speed. Keeping the link and the bar on separate lines means a long link never squeezes the
  /// bar. Both lines are kept within the console width so that the live region has a fixed height —
  /// a wrapped line makes the region's cursor accounting wrong, which leaves fragments on screen.
  /// </summary>
  private sealed class TransferStatusColumn(int width) : ProgressColumn {
    private const int _minBarWidth = 10;
    private const int _maxBarWidth = 40;
    private const double _marqueeBlocksPerSecond = 14;

    private long _lastValue;
    private double _smoothedSpeed;
    private double _marqueePosition;

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) {
      bool isIndeterminate = task.IsIndeterminate;

      double fraction = !isIndeterminate && task.MaxValue > 0
          ? Math.Clamp(task.Value / task.MaxValue, 0, 1)
          : 0;

      double seconds = deltaTime.TotalSeconds;
      double instantSpeed = seconds > 0 ? Math.Max(task.Value - _lastValue, 0) / seconds : 0;

      // Only update the estimate when the counter moved, so idle refreshes do not drag it to zero.
      if (task.Value > _lastValue) {
        _smoothedSpeed = _smoothedSpeed == 0 ? instantSpeed : (_smoothedSpeed * 0.7) + (instantSpeed * 0.3);
      }

      _lastValue = (long)task.Value;

      // The percentage appears once, in the details: repeating it next to the bar made the line
      // long enough to wrap at narrow widths.
      string details = !isIndeterminate && task.MaxValue > 0
          ? $"{fraction * 100,3:0}%  {FormatBytes(task.Value)} / {FormatBytes(task.MaxValue)}"
          : FormatBytes(task.Value);

      string speed = _smoothedSpeed > 0 ? $"  {TransferProgress.FormatBytes((long)_smoothedSpeed)}/s" : "";
      string suffix = details + speed;

      // The bar takes whatever is left once the details and the speed fit. On a narrow terminal the
      // speed is dropped first, then the bar, so that the line never wraps.
      int barWidth = Math.Clamp(width - suffix.Length - 2, 0, _maxBarWidth);

      if (barWidth < _minBarWidth && speed.Length > 0) {
        suffix = details;
        barWidth = Math.Clamp(width - suffix.Length - 2, 0, _maxBarWidth);
      }

      string blocks = isIndeterminate
          ? Marquee(barWidth, seconds)
          : new string('█', (int)Math.Round(barWidth * fraction)).PadRight(barWidth, '░');

      // The displayed text may be shortened with the width, but the target keeps the whole address:
      // a truncated target would make clicking the link open a wrong location.
      string target = Markup.Escape(task.Description);
      string text = Markup.Escape(TextLinker.Fit(task.Description, width));

      return new Rows(
          new Markup($"[grey][link={target}]{text}[/][/]"),
          new Markup(barWidth > 0
              ? $"[grey]{blocks}  {Markup.Escape(suffix)}[/]"
              : $"[grey]{Markup.Escape(suffix)}[/]"));
    }

    public override int? GetColumnWidth(RenderOptions options) {
      return null;
    }

    /// <summary>
    /// Animates a window of filled cells across the bar, which is what a transfer of unknown length
    /// can honestly show instead of a fraction.
    /// </summary>
    private string Marquee(int barWidth, double seconds) {
      if (barWidth <= 0) {
        return "";
      }

      int window = Math.Max(2, barWidth / 3);
      int travel = barWidth + window;

      _marqueePosition = (_marqueePosition + (seconds * _marqueeBlocksPerSecond)) % travel;

      int left = (int)_marqueePosition - window;

      return new string([.. Enumerable.Range(0, barWidth)
          .Select(index => index >= left && index < left + window ? '█' : '░')]);
    }

    /// <summary>
    /// Formats a progress value, which is a <see cref="double"/> and may hold the "unknown length"
    /// sentinel.
    /// </summary>
    private static string FormatBytes(double bytes) {
      return bytes >= long.MaxValue
          ? TransferProgress.FormatBytes(long.MaxValue)
          : TransferProgress.FormatBytes((long)Math.Max(bytes, 0));
    }
  }

  private sealed class NullTransferProgress : IProgress<TransferProgress> {
    public void Report(TransferProgress value) {
    }
  }
}
