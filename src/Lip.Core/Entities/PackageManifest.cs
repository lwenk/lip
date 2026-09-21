using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.Globbing;
using Lip.Core.Json;
using Lip.Core.Migration.PackageManifests;
using Semver;

namespace Lip.Core.Entities;

public record PackageManifest {
  private const int _currentFormatVersion = 3;
  private const string _currentFormatUuid = "289f771f-2c9a-4d73-9f3f-8492495a924d";

  [JsonInclude]
  [JsonRequired]
  [JsonPropertyName("format_version")]
  public int FormatVersion {
    get => _currentFormatVersion;
    init {
      if (value != _currentFormatVersion) {
        throw new ArgumentException($"Unsupported format version: {value}", nameof(FormatVersion));
      }
    }
  }

  [JsonInclude]
  [JsonRequired]
  [JsonPropertyName("format_uuid")]
  public string FormatUuid {
    get => _currentFormatUuid;
    init {
      if (value != _currentFormatUuid) {
        throw new ArgumentException($"Unsupported format UUID: {value}", nameof(FormatUuid));
      }
    }
  }

  [JsonPropertyName("tooth")]
  public required string Path {
    get;
    init => field = (Golang.Org.X.Mod.Module.CheckPath(value) is null)
        ? value
        : throw new FormatException($"Invalid package path: {value}");
  }

  [JsonConverter(typeof(SemVersionJsonConverter))]
  [JsonPropertyName("version")]
  public required SemVersion Version { get; init; }

  [JsonPropertyName("info")]
  public PackageManifestInfo Info { get; init; } = new();

  [JsonPropertyName("variants")]
  public List<PackageManifestVariant> Variants { get; init; } = [];

  public static async Task<PackageManifest> FromStream(Stream stream) {
    JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream);

    return PackageManifestMigration.Migrate(jsonDocument);
  }

  public PackageManifestVariant GetVariant(string label) {
    string platform = RuntimeInformation.RuntimeIdentifier;

    List<PackageManifestVariant> matchingVariants = [];
    foreach (PackageManifestVariant variant in Variants) {
      if (label != variant.Label) {
        continue;
      }

      if (
          variant.Platform != ""
          && !(platform == variant.Platform)
          && !Glob.Parse(variant.Platform).IsMatch(platform)) {
        continue;
      }

      matchingVariants.Add(variant);
    }

    // An empty platform matches every platform, so any match at all indicates that the current
    // platform is supported for the given label.
    if (matchingVariants.Count == 0) {
      throw new KeyNotFoundException($"No variant found for label '{label}' and platform '{platform}'.");
    }

    // Variants are merged in declaration order, and the first declaration of a dependency or a
    // script phase wins. This allows a more specific variant, listed before a general one, to
    // override it.
    Dictionary<PackageId, SemVersionRange> dependencies = [];
    foreach (PackageManifestVariant variant in matchingVariants) {
      foreach (KeyValuePair<PackageId, SemVersionRange> dependency in variant.Dependencies) {
        dependencies.TryAdd(dependency.Key, dependency.Value);
      }
    }

    return new() {
      Label = label,
      Platform = platform,
      Dependencies = dependencies,
      Assets = [.. matchingVariants.SelectMany(variant => variant.Assets)],
      PreserveFiles = [.. matchingVariants.SelectMany(variant => variant.PreserveFiles)],
      RemoveFiles = [.. matchingVariants.SelectMany(variant => variant.RemoveFiles)],
      Scripts = new() {
        PreInstall = GetScripts(matchingVariants, variant => variant.Scripts.PreInstall),
        Install = GetScripts(matchingVariants, variant => variant.Scripts.Install),
        PostInstall = GetScripts(matchingVariants, variant => variant.Scripts.PostInstall),
        PreUninstall = GetScripts(matchingVariants, variant => variant.Scripts.PreUninstall),
        Uninstall = GetScripts(matchingVariants, variant => variant.Scripts.Uninstall),
        PostUninstall = GetScripts(matchingVariants, variant => variant.Scripts.PostUninstall),
      }
    };
  }

  private static List<string> GetScripts(
      IEnumerable<PackageManifestVariant> variants,
      Func<PackageManifestVariant, List<string>> selector) {
    return variants.Select(selector).FirstOrDefault(commands => commands.Count > 0) ?? [];
  }
}
