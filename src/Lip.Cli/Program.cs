using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Lip.Cli;
using Lip.Cli.Commands;
using Lip.Core.Infrastructure;
using Lip.Core.PublicApi;
using Microsoft.Extensions.DependencyInjection;
using Semver;
using Spectre.Console;
using Spectre.Console.Cli;

// Windows consoles default to the ANSI code page, which turns the check marks and box drawing
// characters Spectre.Console emits into question marks or mojibake.
try {
  Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException) {
  // Some hosts refuse the change. Output degrades, but the command still runs.
}

DiagnosticLog diagnosticLog = new(new FileSystem());

// The log is shared with the interaction facade so that verbose detail from the core services is
// recorded alongside the run header and any unhandled exception.
ConsoleUserInteraction userInteraction = new(diagnosticLog);

LipClient lipClient = await LipClient.Create(userInteraction);

diagnosticLog.WriteHeader(Environment.CommandLine);

ServiceCollection services = new();

services.AddSingleton<ILipClient>(lipClient);
services.AddSingleton<IUserInteraction>(userInteraction);

TypeRegistrar registrar = new(services);

CommandApp app = new(registrar);

app.Configure(config => {
  config.SetApplicationName("lip");

  config.SetApplicationVersion(SemVersion.Parse(Assembly
      .GetEntryAssembly()?
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion!).ToString());

  config.SetExceptionHandler((ex, resolver) => {
    diagnosticLog.WriteException(ex);

    IAnsiConsole console = AnsiConsole.Create(new() {
      Out = new AnsiConsoleOutput(Console.Error)
    });

    console.MarkupLine($"[bold red]✗ {GetErrorMessage(ex).EscapeMarkup()}[/]");
    console.MarkupLine($"[grey]Details were written to {diagnosticLog.Path.EscapeMarkup()}[/]");
    console.MarkupLine("[grey]Attach that file when reporting this issue.[/]");

    return 1;
  });

  config.AddBranch("cache", cache => {
    cache.AddCommand<CacheCleanCommand>("clean")
        .WithDescription("Cleans the local cache");
  });

  config.AddBranch("config", config => {
    config.AddCommand<ConfigGetCommand>("get")
        .WithDescription("Gets a configuration value");
    config.AddCommand<ConfigSetCommand>("set")
        .WithDescription("Sets a configuration value");
    config.AddCommand<ConfigListCommand>("list")
        .WithDescription("Lists all configuration values");
    config.AddCommand<ConfigDeleteCommand>("delete")
        .WithDescription("Deletes a configuration value");
  });

  config.AddCommand<InitCommand>("init")
      .WithDescription("Initializes a new project");

  config.AddCommand<InstallCommand>("install")
      .WithDescription("Installs packages");

  config.AddCommand<ListCommand>("list")
      .WithDescription("Lists installed packages");

  config.AddCommand<MigrateCommand>("migrate")
      .WithDescription("Migrates a package manifest");

  config.AddCommand<UninstallCommand>("uninstall")
      .WithDescription("Uninstalls packages");

  config.AddCommand<UpdateCommand>("update")
      .WithDescription("Updates packages");

  config.AddCommand<ViewCommand>("view")
      .WithDescription("Views package details");

  config.AddCommand<VersionsCommand>("versions")
      .WithDescription("Shows available versions for a package");
});

// Reduces an exception tree to the one line that is worth showing on the terminal. The full tree,
// including every inner exception, is written to the diagnostic log instead.
static string GetErrorMessage(Exception exception) {
  if (exception is not AggregateException aggregateException) {
    return exception.Message;
  }

  AggregateException flattenedException = aggregateException.Flatten();

  // AggregateException appends the message of every inner exception to its own, so only the part
  // written at the call site is shown here. The inner messages are all in the diagnostic log.
  string message = flattenedException.Message;
  int innerMessagesStart = message.IndexOf(" (", StringComparison.Ordinal);

  if (innerMessagesStart > 0) {
    message = message[..innerMessagesStart];
  }

  if (message.Length > 0
      && !message.StartsWith("One or more errors occurred.", StringComparison.Ordinal)) {
    return message;
  }

  return flattenedException.InnerExceptions.Count == 1
      ? flattenedException.InnerExceptions[0].Message
      : "More than one error occurred.";
}

return await app.RunAsync(args);
