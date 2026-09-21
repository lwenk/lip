using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl;
using Lip.Core.Json;

namespace Lip.Core.Entities;

public record RuntimeConfig {
  private const int _currentFormatVersion = 3;
  private const string _currentFormatUuid = "289f771f-2c9a-4d73-9f3f-8492495a924d";
  private static readonly JsonSerializerOptions _urlJsonSerializerOptions = new() {
    Converters = { new UrlJsonConverter() }
  };

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

  [JsonConverter(typeof(UrlJsonConverter))]
  [JsonPropertyName("github_proxy")]
  public Url? GithubProxy { get; init; }

  /// <summary>
  /// Additional GitHub mirrors to probe alongside the built-in ones, in the form
  /// <c>{mirror}/https://github.com/...</c>. Only used when <see cref="GithubProxy"/> is not set.
  /// </summary>
  [JsonPropertyName("github_mirrors")]
  public List<Url>? GithubMirrors { get; init; }

  [JsonConverter(typeof(UrlJsonConverter))]
  [JsonPropertyName("go_module_proxy")]
  public Url? GoModuleProxy { get; init; }

  /// <summary>
  /// Additional Go module proxies to probe alongside the built-in ones. Only used when
  /// <see cref="GoModuleProxy"/> is not set.
  /// </summary>
  [JsonPropertyName("go_module_mirrors")]
  public List<Url>? GoModuleMirrors { get; init; }

  public IDictionary<string, dynamic?> AsDictionary() {
    return GetType()
        .GetProperties()
        .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
        .Where(p => p.Name != nameof(FormatVersion) && p.Name != nameof(FormatUuid))
        .Select(p => new KeyValuePair<string, dynamic?>(
            p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name,
            p.GetValue(this)
        ))
        .ToDictionary();
  }

  public RuntimeConfig With(string key, dynamic? value) {
    RuntimeConfig newConfig = this with { };

    PropertyInfo prop = GetType().GetProperties()
        .Where(p => p.Name != nameof(FormatVersion) && p.Name != nameof(FormatUuid))
        .FirstOrDefault(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == key)
        ?? throw new KeyNotFoundException($"Configuration key '{key}' not found.");

    // If type mismatch, try to serialize and deserialize to convert.
    if (!prop.PropertyType.IsAssignableFrom(value?.GetType())) {
      string json = JsonSerializer.Serialize(value);
      value = JsonSerializer.Deserialize(json, prop.PropertyType, _urlJsonSerializerOptions);
    }

    prop.SetValue(newConfig, value);

    return newConfig;
  }
}
