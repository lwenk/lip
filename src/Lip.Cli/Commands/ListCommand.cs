using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lip.Core.PublicApi;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lip.Cli.Commands;

public class ListCommand(ILipClient lipClient) : AsyncCommand<ListCommand.Settings> {
  private readonly JsonSerializerOptions _jsonSerializerOptions = new() {
    WriteIndented = true
  };

  private readonly ILipClient _lipClient = lipClient;

  public class Settings : CommandSettings {
    [CommandOption("--json")]
    [Description("Output as JSON")]
    public bool Json { get; init; }
  }

  /// <summary>
  /// The shape of the <c>--json</c> output. A dedicated type is required because
  /// <see cref="JsonSerializer"/> ignores public fields, which is all a value tuple exposes, so
  /// serializing the tuple directly would produce an empty object.
  /// </summary>
  private sealed record ListResult {
    [JsonPropertyName("explicit_installed")]
    public required IEnumerable<string> ExplicitInstalled { get; init; }

    [JsonPropertyName("implicit_installed")]
    public required IEnumerable<string> ImplicitInstalled { get; init; }
  }

  protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
    (IEnumerable<string> ExplicitInstalled, IEnumerable<string> ImplicitInstalled) packages = await _lipClient.List();

    if (settings.Json) {
      // Machine readable output is written straight to standard output. Routing it through
      // Spectre.Console would interpret it as markup and omit the trailing newline scripts expect.
      Console.Out.WriteLine(JsonSerializer.Serialize(
          new ListResult {
            ExplicitInstalled = packages.ExplicitInstalled,
            ImplicitInstalled = packages.ImplicitInstalled,
          },
          _jsonSerializerOptions));
    } else {
      Tree explicitTree = new("User-Requested");
      foreach (string package in packages.ExplicitInstalled) {
        explicitTree.AddNode(package);
      }
      if (!packages.ExplicitInstalled.Any()) {
        explicitTree.AddNode("[grey italic]None[/]");
      }

      Tree implicitTree = new("Dependencies");
      foreach (string package in packages.ImplicitInstalled) {
        implicitTree.AddNode(package);
      }
      if (!packages.ImplicitInstalled.Any()) {
        implicitTree.AddNode("[grey italic]None[/]");
      }

      AnsiConsole.Write(explicitTree);
      AnsiConsole.Write(implicitTree);
    }

    return 0;
  }
}
