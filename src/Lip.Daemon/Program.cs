using System.IO.Abstractions;
using System.Reflection;
using Lip.Core.Infrastructure;
using Lip.Daemon;
using Lip.Daemon.Commands;
using Microsoft.Extensions.DependencyInjection;
using Semver;
using Spectre.Console.Cli;

ServiceCollection services = new();
TypeRegistrar registrar = new(services);
CommandApp app = new(registrar);

DiagnosticLog diagnosticLog = new(new FileSystem());
diagnosticLog.WriteHeader(Environment.CommandLine);

app.Configure(config => {
  config.SetApplicationName("lipd");

  config.SetApplicationVersion(SemVersion.Parse(Assembly
      .GetEntryAssembly()?
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion!).ToString());

  config.SetExceptionHandler((ex, resolver) => {
    diagnosticLog.WriteException(ex);

    // Standard output carries the JSON-RPC stream, so only standard error may be written to.
    Console.Error.WriteLine($"lipd: {ex.Message}");
    Console.Error.WriteLine($"Details were written to {diagnosticLog.Path}");

    return 1;
  });

  config.AddCommand<RunCommand>("run")
      .WithDescription("Runs the daemon");
});

return await app.RunAsync(args);
