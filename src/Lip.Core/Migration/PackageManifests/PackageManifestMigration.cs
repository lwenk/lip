using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Globbing;
using Lip.Core.Entities;
using Semver;

namespace Lip.Core.Migration.PackageManifests;

public static partial class PackageManifestMigration {
  public static PackageManifest Migrate(JsonDocument jsonDocument) {
    int formatVersion = jsonDocument.RootElement.GetProperty("format_version").GetInt32();

    return formatVersion switch {
      1 => MigrateV2ToV3(MigrateV1ToV2(JsonSerializer.Deserialize<PackageManifestV1>(jsonDocument)!)),
      2 => MigrateV2ToV3(JsonSerializer.Deserialize<PackageManifestV2>(jsonDocument)!),
      3 => ParseV3(jsonDocument),
      _ => throw new NotSupportedException(),
    };
  }

  private static PackageManifestV2 MigrateV1ToV2(PackageManifestV1 manifestV1) {
    Dictionary<string, JsonElement>? data = manifestV1.Information?.Data;
    PackageManifestV2Info info = new() {
      Name = data is not null && data.TryGetValue("name", out JsonElement n)
            ? n.GetString() ?? ""
            : "",
      Description = data is not null && data.TryGetValue("description", out JsonElement d)
            ? d.GetString() ?? ""
            : "",
      Author = data is not null && data.TryGetValue("author", out JsonElement a)
            ? a.GetString() ?? ""
            : "",
      Tags = data is not null && data.TryGetValue("tags", out JsonElement t) && t.ValueKind == JsonValueKind.Array
            ? [.. t.EnumerateArray().Select(e => e.GetString() ?? "")]
            : []
    };

    List<PackageManifestV2Platform> platforms = [];

    PackageManifestV2Platform GetOrCreatePlatform(string goos, string? goarch) {
      PackageManifestV2Platform? platform = platforms.SingleOrDefault(p => p.GOOS == goos && p.GOARCH == goarch);
      if (platform is not null) {
        return platform;
      }

      PackageManifestV2Platform result = new() {
        GOOS = goos,
        GOARCH = goarch
      };
      platforms.Add(result);
      return result;
    }

    static bool HasCommands(PackageManifestV2Commands commands) {
      return commands.PreInstall is not null
             || commands.PostInstall is not null
             || commands.PreUninstall is not null
             || commands.PostUninstall is not null;
    }

    PackageManifestV2Commands? commands = null;
    if (manifestV1.Commands is not null) {
      PackageManifestV2Commands rootCommands = new();
      foreach (PackageManifestV1Command cmd in manifestV1.Commands) {
        PackageManifestV2Commands targetCommands;
        if (string.IsNullOrEmpty(cmd.GOOS)) {
          targetCommands = rootCommands;
        } else {
          PackageManifestV2Platform platform = GetOrCreatePlatform(cmd.GOOS, cmd.GOARCH);
          platform.Commands ??= new();
          targetCommands = platform.Commands;
        }

        if (cmd.Type.Equals("install", StringComparison.OrdinalIgnoreCase)) {
          targetCommands.PostInstall ??= [];
          targetCommands.PostInstall.AddRange(cmd.Commands);
        } else if (cmd.Type.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) {
          targetCommands.PreUninstall ??= [];
          targetCommands.PreUninstall.AddRange(cmd.Commands);
        }
      }
      if (HasCommands(rootCommands)) {
        commands = rootCommands;
      }
    }

    return new PackageManifestV2 {
      FormatVersion = 2,
      Tooth = manifestV1.Tooth,
      Version = manifestV1.Version,
      Info = info,
      AssetUrl = null,
      Commands = commands,
      Dependencies = manifestV1.Dependencies?.ToDictionary(
            kvp => kvp.Key,
            kvp => string.Join(" || ", kvp.Value.Select(group => string.Join(" && ", group)))
        ),
      Prerequisites = null,
      Files = manifestV1.Placement is not null
            ? new PackageManifestV2Files {
              Place = [.. manifestV1.Placement.Select(p => new PackageManifestV2Place { Src = p.Source, Dest = p.Destination.TrimEnd('*') })]
            }
            : null,
      Platforms = platforms.Count > 0 ? platforms : null
    };
  }

  private static PackageManifest MigrateV2ToV3(PackageManifestV2 manifestV2) {
    Dictionary<string, object> model = new()
    {
      { "version", manifestV2.Version },
      { "path", manifestV2.Tooth }
    };

    string RenderTemplate(string? template) {
      if (template is null) return "";
      // Convert $(variable) format to {{variable}} format for Scriban
      string converted = TemplateVariableRegex().Replace(template, "{{$1}}");
      return Scriban.Template.Parse(converted).Render(model);
    }

    PackageManifest manifest = new() {
      Path = manifestV2.Tooth,
      Version = SemVersion.Parse(manifestV2.Version, SemVersionStyles.Any),
      Info = new PackageManifestInfo {
        Name = manifestV2.Info.Name,
        Description = manifestV2.Info.Description,
        Tags = manifestV2.Info.Tags,
        AvatarUrl = RenderTemplate(manifestV2.Info.AvatarUrl)
      },
      Variants = []
    };

    switch (manifestV2.Platforms) {
      case not null: {
          foreach (PackageManifestV2Platform p in manifestV2.Platforms) {
            string prefix = p.GOOS switch {
              "windows" => "win",
              "darwin" => "osx",
              "linux" => "linux",
              "ios" => "ios",
              "android" => "android",
              _ => throw new PlatformNotSupportedException($"Unsupported GOOS: {p.GOOS}")
            };

            string suffix = p.GOARCH switch {
              "amd64" => "x64",
              "386" => "x86",
              "arm64" => "arm64",
              "arm" => "arm",
              "loong64" => "loongarch64",
              null => "*",
              _ => throw new PlatformNotSupportedException($"Unsupported GOARCH: {p.GOARCH}")
            };

            Dictionary<PackageId, SemVersionRange> dependencies = [];
            if (p.Dependencies is not null || manifestV2.Dependencies is not null) {
              foreach (KeyValuePair<string, string> kvp in p.Dependencies ?? manifestV2.Dependencies!) {
                try {
                  dependencies[PackageId.Parse(kvp.Key)] = SemVersionRange.ParseNpm(kvp.Value);
                }
                catch {
                  // Ignore invalid dependencies
                }
              }
            }

            List<PackageManifestAsset> assets = [];
            string? assetUrl = p.AssetUrl ?? manifestV2.AssetUrl;
            if (assetUrl is not null) {
              string url = RenderTemplate(assetUrl);
              PackageManifestAsset.AssetType type = url.Split('.').Last() switch {
                "tar" => PackageManifestAsset.AssetType.Tar,
                "tgz" => PackageManifestAsset.AssetType.Tgz,
                "gz" => PackageManifestAsset.AssetType.Tgz,
                "zip" => PackageManifestAsset.AssetType.Zip,
                _ => PackageManifestAsset.AssetType.Uncompressed
              };

              List<PackageManifestAssetPlacement> placements = [];
              List<PackageManifestV2Place>? places = p.Files?.Place ?? manifestV2.Files?.Place;
              if (places is not null) {
                foreach (PackageManifestV2Place pl in places) {
                  string src = RenderTemplate(pl.Src)?.TrimEnd('*') ?? "";
                  string dst = RenderTemplate(pl.Dest) ?? "";
                  placements.Add(new PackageManifestAssetPlacement {
                    Src = src,
                    Dst = dst,
                    Type = pl.Src.EndsWith('*')
                          ? PackageManifestAssetPlacement.PlacementType.Directory
                          : PackageManifestAssetPlacement.PlacementType.File
                  });
                }
              }

              assets.Add(new PackageManifestAsset {
                Type = type,
                Urls = [new Flurl.Url(url)],
                Placements = placements
              });
            }

            PackageManifestScripts scripts = new();
            PackageManifestV2Commands? cmds = p.Commands ?? manifestV2.Commands;
            if (cmds is not null) {
              scripts = new PackageManifestScripts {
                PreInstall = cmds.PreInstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
                PostInstall = cmds.PostInstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
                PreUninstall = cmds.PreUninstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
                PostUninstall = cmds.PostUninstall?.Select(s => RenderTemplate(s)).ToList() ?? []
              };
            }

            manifest.Variants.Add(new PackageManifestVariant {
              Platform = $"{prefix}-{suffix}",
              Dependencies = dependencies,
              Assets = assets,
              PreserveFiles = (p.Files?.Preserve ?? manifestV2.Files?.Preserve)?.Select(s => RenderTemplate(s)).Select(s => Glob.Parse(s)).ToList() ?? [],
              RemoveFiles = (p.Files?.Remove ?? manifestV2.Files?.Remove)?.Select(s => RenderTemplate(s)).Select(s => Glob.Parse(s)).ToList() ?? [],
              Scripts = scripts
            });
          }

          break;
        }

      default: {
          Dictionary<PackageId, SemVersionRange> dependencies = [];
          if (manifestV2.Dependencies is not null) {
            foreach (KeyValuePair<string, string> kvp in manifestV2.Dependencies) {
              try {
                dependencies[PackageId.Parse(kvp.Key)] = SemVersionRange.Parse(kvp.Value);
              }
              catch {
                // Ignore invalid dependencies
              }
            }
          }

          List<PackageManifestAsset> assets = [];
          switch (manifestV2.AssetUrl) {
            case not null: {
                string url = RenderTemplate(manifestV2.AssetUrl);
                PackageManifestAsset.AssetType type = url.Split('.').Last() switch {
                  "tar" => PackageManifestAsset.AssetType.Tar,
                  "tgz" => PackageManifestAsset.AssetType.Tgz,
                  "gz" => PackageManifestAsset.AssetType.Tgz,
                  "zip" => PackageManifestAsset.AssetType.Zip,
                  _ => PackageManifestAsset.AssetType.Uncompressed
                };

                List<PackageManifestAssetPlacement> placements = [];
                if (manifestV2.Files?.Place is not null) {
                  foreach (PackageManifestV2Place pl in manifestV2.Files.Place) {
                    string src = RenderTemplate(pl.Src)?.TrimEnd('*') ?? "";
                    string dst = RenderTemplate(pl.Dest) ?? "";
                    placements.Add(new PackageManifestAssetPlacement {
                      Src = src,
                      Dst = dst,
                      Type = pl.Src.EndsWith('*')
                            ? PackageManifestAssetPlacement.PlacementType.Directory
                            : PackageManifestAssetPlacement.PlacementType.File
                    });
                  }
                }

                assets.Add(new PackageManifestAsset {
                  Type = type,
                  Urls = [new Flurl.Url(url)],
                  Placements = placements
                });
                break;
              }

            default: {
                List<PackageManifestAssetPlacement> placements = [];
                if (manifestV2.Files?.Place is not null) {
                  foreach (PackageManifestV2Place pl in manifestV2.Files.Place) {
                    string src = RenderTemplate(pl.Src)?.TrimEnd('*') ?? "";
                    string dst = RenderTemplate(pl.Dest) ?? "";
                    placements.Add(new PackageManifestAssetPlacement {
                      Src = src,
                      Dst = dst,
                      Type = pl.Src.EndsWith('*')
                            ? PackageManifestAssetPlacement.PlacementType.Directory
                            : PackageManifestAssetPlacement.PlacementType.File
                    });
                  }

                  assets.Add(new PackageManifestAsset {
                    Type = PackageManifestAsset.AssetType.Self,
                    Placements = placements
                  });
                }

                break;
              }
          }

          PackageManifestScripts scripts = new();
          if (manifestV2.Commands is not null) {
            scripts = new PackageManifestScripts {
              PreInstall = manifestV2.Commands.PreInstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
              PostInstall = manifestV2.Commands.PostInstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
              PreUninstall = manifestV2.Commands.PreUninstall?.Select(s => RenderTemplate(s)).ToList() ?? [],
              PostUninstall = manifestV2.Commands.PostUninstall?.Select(s => RenderTemplate(s)).ToList() ?? []
            };
          }

          manifest.Variants.Add(new PackageManifestVariant {
            Dependencies = dependencies,
            Assets = assets,
            PreserveFiles = manifestV2.Files?.Preserve?.Select(s => RenderTemplate(s)).Select(s => Glob.Parse(s)).ToList() ?? [],
            RemoveFiles = manifestV2.Files?.Remove?.Select(s => RenderTemplate(s)).Select(s => Glob.Parse(s)).ToList() ?? [],
            Scripts = scripts
          });
          break;
        }
    }

    if (manifest.Variants.Any(variant =>
            !string.IsNullOrEmpty(variant.Platform) &&
            manifest.Variants.Any(s => s.Platform is not null && s.Platform.Contains('*')))) {
      manifest.Variants.Insert(0, new PackageManifestVariant { Platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier });
    }

    return manifest;
  }

  private static PackageManifest ParseV3(JsonDocument jsonDocument) {
    Dictionary<string, object> model = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonDocument) ?? [];

    JsonNode? node = JsonNode.Parse(jsonDocument.RootElement.GetRawText());

    ProcessNode(node);

    return node.Deserialize<PackageManifest>()!;

    // Only names that exist as fields of the manifest count as placeholders. Text such as
    // `$(date)` inside a script must stay verbatim, otherwise it is replaced by an unrelated
    // Scriban builtin.
    bool IsManifestField(string name) {
      object? current = model;

      foreach (string segment in name.Split('.')) {
        current = current switch {
          Dictionary<string, object> dictionary when dictionary.TryGetValue(segment, out object? value) => value,
          JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty(segment, out JsonElement property) => property,
          _ => null,
        };

        if (current is null) {
          return false;
        }
      }

      return true;
    }

    void ProcessNode(JsonNode? current) {
      switch (current) {
        case null:
          return;

        case JsonValue value when value.TryGetValue(out string? s): {
            // Manifests already published as format version 3 may still use the legacy
            // `$(variable)` placeholder, so convert it the same way the v1/v2 migrations do.
            // Leaving it untouched would silently produce a URL containing literal '$(version)'.
            string converted = TemplateVariableRegex().Replace(s, match => {
              string name = match.Groups[1].Value;

              return IsManifestField(name)
                  ? "{{" + name + "}}"
                  : match.Value;
            });
            string newValue = Scriban.Template.Parse(converted).Render(model);

            value.ReplaceWith(newValue);

            return;
          }

        case JsonObject obj: {
            foreach (KeyValuePair<string, JsonNode?> kvp in obj.ToList()) {
              ProcessNode(kvp.Value);
            }
            return;
          }

        case JsonArray arr: {
            for (int i = 0; i < arr.Count; i++) {
              ProcessNode(arr[i]);
            }
            return;
          }
      }
    }
  }

  [GeneratedRegex(@"\$\(([^)]+?)\)")]
  private static partial Regex TemplateVariableRegex();
}
