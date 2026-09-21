using System.Text.Json;
using Lip.Core.Entities;
using Lip.Core.Migration.PackageManifests;

namespace Lip.Core.Tests.Migration.PackageManifests;

public class PackageManifestMigrationTests {
  private static readonly JsonSerializerOptions _jsonSerializerOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters =
      {
            new Core.Json.SemVersionJsonConverter(),
            new Core.Json.UrlJsonConverter(),
            new Core.Json.GlobListJsonConverter()
        }
  };


  [Fact]
  public void Migrate_ValidV1Json_ReturnsMigratedV3Json() {
    // Arrange
    string jsonTextV1 = """
            {
                "format_version": 1,
                "tooth": "github.com/LiteLScript-Dev/HelperLib",
                "version": "2.14.1",
                "dependencies": {},
                "information": {
                    "name": "HelperLib",
                    "description": "TypeScript.d.ts file with auto-completion and code hints for LiteLScript developers",
                    "homepage": "https://github.com/LiteLScript-Dev/HelperLib"
                },
                "placement": [
                    {
                        "source": "src/*",
                        "destination": "declaration/llse/*"
                    }
                ]
            }
            """;

    using JsonDocument jsonDocumentV1 = JsonDocument.Parse(jsonTextV1);

    string expectedJsonTextV3 = $$"""
            {
              "format_version": 3,
              "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
              "tooth": "github.com/LiteLScript-Dev/HelperLib",
              "version": "2.14.1",
              "info": {
                "name": "HelperLib",
                "description": "TypeScript.d.ts file with auto-completion and code hints for LiteLScript developers",
                "tags": [],
                "avatar_url": ""
              },
              "variants": [
                {
                  "label": "",
                  "platform": "",
                  "dependencies": {},
                  "assets": [
                    {
                      "type": "self",
                      "urls": [],
                      "placements": [
                        {
                          "type": "dir",
                          "src": "src/",
                          "dest": "declaration/llse/"
                        }
                      ]
                    }
                  ],
                  "preserve_files": [],
                  "remove_files": [],
                  "scripts": {
                    "pre_install": [],
                    "install": [],
                    "post_install": [],
                    "pre_uninstall": [],
                    "uninstall": [],
                    "post_uninstall": []
                  }
                }
              ]
            }
            """;

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(jsonDocumentV1);
    string resultJson = JsonSerializer.Serialize(result, _jsonSerializerOptions);

    // Assert
    AssertJsonEqual(expectedJsonTextV3, resultJson);
  }

  [Fact]
  public void Migrate_V1Commands_PreserveLegacyLifecyclePhases() {
    // Arrange
    string jsonTextV1 = """
            {
                "format_version": 1,
                "tooth": "github.com/LiteLScript-Dev/HelperLib",
                "version": "2.14.1",
                "commands": [
                    {
                        "type": "install",
                        "commands": ["echo install"],
                        "GOOS": "windows",
                        "GOARCH": "amd64"
                    },
                    {
                        "type": "uninstall",
                        "commands": ["echo uninstall"],
                        "GOOS": "windows",
                        "GOARCH": "amd64"
                    }
                ]
            }
            """;

    using JsonDocument jsonDocumentV1 = JsonDocument.Parse(jsonTextV1);

    string expectedJsonTextV3 = $$"""
            {
              "format_version": 3,
              "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
              "tooth": "github.com/LiteLScript-Dev/HelperLib",
              "version": "2.14.1",
              "info": {
                "name": "",
                "description": "",
                "tags": [],
                "avatar_url": ""
              },
              "variants": [
                {
                  "label": "",
                  "platform": "win-x64",
                  "dependencies": {},
                  "assets": [],
                  "preserve_files": [],
                  "remove_files": [],
                  "scripts": {
                    "pre_install": [],
                    "install": [],
                    "post_install": [
                      "echo install"
                    ],
                    "pre_uninstall": [
                      "echo uninstall"
                    ],
                    "uninstall": [],
                    "post_uninstall": []
                  }
                }
              ]
            }
            """;

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(jsonDocumentV1);
    string resultJson = JsonSerializer.Serialize(result, _jsonSerializerOptions);

    // Assert
    AssertJsonEqual(expectedJsonTextV3, resultJson);
  }

  [Fact]
  public void Migrate_V1Commands_WithoutGoos_AreTreatedAsGlobalForCompatibility() {
    // Arrange
    string jsonTextV1 = """
            {
                "format_version": 1,
                "tooth": "github.com/LiteLScript-Dev/HelperLib",
                "version": "2.14.1",
                "commands": [
                    {
                        "type": "install",
                        "commands": ["echo install"]
                    },
                    {
                        "type": "uninstall",
                        "commands": ["echo uninstall"]
                    }
                ]
            }
            """;

    using JsonDocument jsonDocumentV1 = JsonDocument.Parse(jsonTextV1);

    string expectedJsonTextV3 = $$"""
            {
              "format_version": 3,
              "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
              "tooth": "github.com/LiteLScript-Dev/HelperLib",
              "version": "2.14.1",
              "info": {
                "name": "",
                "description": "",
                "tags": [],
                "avatar_url": ""
              },
              "variants": [
                {
                  "label": "",
                  "platform": "",
                  "dependencies": {},
                  "assets": [],
                  "preserve_files": [],
                  "remove_files": [],
                  "scripts": {
                    "pre_install": [],
                    "install": [],
                    "post_install": [
                      "echo install"
                    ],
                    "pre_uninstall": [
                      "echo uninstall"
                    ],
                    "uninstall": [],
                    "post_uninstall": []
                  }
                }
              ]
            }
            """;

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(jsonDocumentV1);
    string resultJson = JsonSerializer.Serialize(result, _jsonSerializerOptions);

    // Assert
    AssertJsonEqual(expectedJsonTextV3, resultJson);
  }

  [Fact]
  public void Migrate_ValidV2Json_ReturnsMigratedV3Json() {
    // Arrange
    string textV2 = """
            {
                "format_version": 2,
                "tooth": "github.com/LiteLDev/LeviLamina",
                "version": "1.1.0",
                "info": {
                    "name": "LeviLamina",
                    "description": "A lightweight, modular and versatile mod loader for Minecraft Bedrock Edition.",
                    "author": "levimc",
                    "tags": []
                },
                "asset_url": "https://github.com/LiteLDev/LeviLamina/releases/download/v$(version)/levilamina-release-windows-x64.zip",
                "dependencies": {
                    "github.com/LiteLDev/bds": "1.21.60",
                    "github.com/LiteLDev/CrashLogger": "1.3.x",
                    "github.com/LiteLDev/levilamina-loc": "1.5.x",
                    "github.com/LiteLDev/PeEditor": "3.8.x",
                    "github.com/LiteLDev/PreLoader": "1.12.x",
                    "github.com/LiteLDev/bedrock-runtime-data": "1.21.6010-server"
                },
                "files": {
                    "place": [
                        {
                            "src": "LeviLamina/*",
                            "dest": "plugins/LeviLamina/"
                        }
                    ],
                    "remove": [
                        "bedrock_server_mod.exe"
                    ]
                },
                "platforms": [
                    {
                        "goos": "windows",
                        "goarch": "amd64",
                        "commands": {
                            "post_install": [
                                ".\\PeEditor.exe -mb"
                            ],
                            "post_uninstall": [
                                "IF EXIST bedrock_server.exe (DEL bedrock_server.exe.bak) ELSE (REN bedrock_server.exe.bak bedrock_server.exe)"
                            ]
                        }
                    }
                ]
            }
            """;

    string expectedJsonTextV3 = $$"""
            {
              "format_version": 3,
              "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
              "tooth": "github.com/LiteLDev/LeviLamina",
              "version": "1.1.0",
              "info": {
                "name": "LeviLamina",
                "description": "A lightweight, modular and versatile mod loader for Minecraft Bedrock Edition.",
                "tags": [],
                "avatar_url": ""
              },
              "variants": [
                {
                  "label": "",
                  "platform": "win-x64",
                  "dependencies": {
                    "github.com/LiteLDev/bds": "1.21.60",
                    "github.com/LiteLDev/CrashLogger": "1.3.*",
                    "github.com/LiteLDev/levilamina-loc": "1.5.*",
                    "github.com/LiteLDev/PeEditor": "3.8.*",
                    "github.com/LiteLDev/PreLoader": "1.12.*",
                    "github.com/LiteLDev/bedrock-runtime-data": "1.21.6010-server"
                  },
                  "assets": [
                    {
                      "type": "zip",
                      "urls": [
                        "https://github.com/LiteLDev/LeviLamina/releases/download/v1.1.0/levilamina-release-windows-x64.zip"
                      ],
                      "placements": [
                        {
                          "type": "dir",
                          "src": "LeviLamina/",
                          "dest": "plugins/LeviLamina/"
                        }
                      ]
                    }
                  ],
                  "preserve_files": [],
                  "remove_files": [
                    "bedrock_server_mod.exe"
                  ],
                  "scripts": {
                    "pre_install": [],
                    "install": [],
                    "post_install": [
                        ".\\PeEditor.exe -mb"
                    ],
                    "pre_uninstall": [],
                    "uninstall": [],
                    "post_uninstall": [
                        "IF EXIST bedrock_server.exe (DEL bedrock_server.exe.bak) ELSE (REN bedrock_server.exe.bak bedrock_server.exe)"
                    ]
                  }
                }
              ]
            }
            """;

    using JsonDocument docV2 = JsonDocument.Parse(textV2);

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(docV2);
    string resultJson = JsonSerializer.Serialize(result, _jsonSerializerOptions);

    // Assert
    AssertJsonEqual(expectedJsonTextV3, resultJson);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(4)] // Assume 4 is invalid for now
  public void Migrate_InvalidFormatVersion_ThrowsNotSupportedException(int version) {
    string text = $$"""
            {
                "format_version": {{version}}
            }
            """;
    using JsonDocument doc = JsonDocument.Parse(text);

    Assert.Throws<NotSupportedException>(() => PackageManifestMigration.Migrate(doc));
  }

  [Fact]
  public void Migrate_V3ManifestWithLegacyPlaceholders_RendersThem() {
    // Manifests published as version 3 can still contain the legacy `$(variable)` placeholder.
    string textV3 = """
            {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/GroupMountain/ThrowableFireball",
                "version": "1.0.0",
                "info": {
                    "name": "ThrowableFireball"
                },
                "variants": [
                    {
                        "assets": [
                            {
                                "type": "zip",
                                "urls": [
                                    "https://github.com/GroupMountain/ThrowableFireball/releases/download/v$(version)/ThrowableFireball-windows-x64.zip",
                                    "https://example.com/$(info.name)/$(version).zip"
                                ]
                            }
                        ]
                    }
                ]
            }
            """;

    using JsonDocument doc = JsonDocument.Parse(textV3);

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(doc);

    // Assert
    Assert.Equal(
        [
            "https://github.com/GroupMountain/ThrowableFireball/releases/download/v1.0.0/ThrowableFireball-windows-x64.zip",
            "https://example.com/ThrowableFireball/1.0.0.zip"
        ],
        result.Variants.Single().Assets.Single().Urls.Select(url => url.ToString()));
  }

  [Fact]
  public void Migrate_V3ManifestWithUnrelatedDollarParentheses_KeepsThemVerbatim() {
    // `$(date)` is a shell expansion, not a lip placeholder, and Scriban happens to expose its own
    // `date` object, so rendering it would corrupt the script.
    string textV3 = """
            {
                "format_version": 3,
                "format_uuid": "289f771f-2c9a-4d73-9f3f-8492495a924d",
                "tooth": "github.com/test/pkg",
                "version": "1.0.0",
                "variants": [
                    {
                        "scripts": {
                            "pre_install": ["echo $(date)", "echo $(not_a_manifest_field)"]
                        }
                    }
                ]
            }
            """;

    using JsonDocument doc = JsonDocument.Parse(textV3);

    // Act
    PackageManifest result = PackageManifestMigration.Migrate(doc);

    // Assert
    Assert.Equal(
        ["echo $(date)", "echo $(not_a_manifest_field)"],
        result.Variants.Single().Scripts.PreInstall);
  }

  private static void AssertJsonEqual(string expected, string actual) {
    using JsonDocument expectedDoc = JsonDocument.Parse(expected);
    using JsonDocument actualDoc = JsonDocument.Parse(actual);

    string expectedNormalized = JsonSerializer.Serialize(expectedDoc.RootElement, _jsonSerializerOptions);
    string actualNormalized = JsonSerializer.Serialize(actualDoc.RootElement, _jsonSerializerOptions);

    if (expectedNormalized.ReplaceLineEndings() != actualNormalized.ReplaceLineEndings()) {
      System.IO.File.WriteAllText("expected_debug.json", expectedNormalized);
      System.IO.File.WriteAllText("actual_debug.json", actualNormalized);
    }

    Assert.Equal(expectedNormalized.ReplaceLineEndings(), actualNormalized.ReplaceLineEndings());
  }
}
