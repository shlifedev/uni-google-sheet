#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HolySheet.Core.Sources;

namespace HolySheet.Core.Config
{
    public enum SourceType
    {
        Xlsx,

        Csv,

        Google,

        GoogleFolder
    }

    public enum GoogleAuthType
    {
        Public,

        ApiKey,

        ServiceAccount,

        AccessToken,

        Custom,

        Command
    }

    public sealed class GoogleAuthConfig
    {
        public GoogleAuthType Type { get; set; } = GoogleAuthType.Public;

        public string? ApiKey { get; set; }

        public string? ApiKeyEnv { get; set; }

        public string? KeyFile { get; set; }

        public string? AccessTokenEnv { get; set; }

        public string? Provider { get; set; }

        public string? Command { get; set; }
    }

    public sealed record GoogleSheetRef(string Name, long Gid);

    public sealed class SourceConfig
    {
        public SourceType Type { get; set; }

        public string? Path { get; set; }

        public string? SpreadsheetId { get; set; }

        public string? FolderId { get; set; }

        public bool IsRemote => Type is SourceType.Google or SourceType.GoogleFolder;

        public static string ExtractGoogleId(string text)
        {
            string value = text.Trim();
            foreach (string marker in new[] { "/d/", "/folders/", "id=" })
            {
                int start = value.IndexOf(marker, StringComparison.Ordinal);
                if (start >= 0)
                {
                    start += marker.Length;
                    int end = start;
                    while (end < value.Length && (char.IsLetterOrDigit(value[end]) || value[end] is '-' or '_'))
                    {
                        end++;
                    }

                    if (end > start)
                    {
                        return value.Substring(start, end - start);
                    }
                }
            }

            return value;
        }

        public string? Name { get; set; }

        public GoogleAuthConfig Auth { get; set; } = new();

        public List<GoogleSheetRef> Sheets { get; set; } = new();

        public string? Snapshot { get; set; }
    }

    public sealed class OutputConfig
    {
        public string Code { get; set; } = "Generated/Code";

        public string Data { get; set; } = "Generated/Data";

        public string? Csv { get; set; }

        public bool Schema { get; set; } = true;

        public string ManifestFile { get; set; } = "tables.manifest.json";
    }

    public sealed class CodegenConfig
    {
        public string Namespace { get; set; } = "Game.Data";

        public string TablesClass { get; set; } = "GameTables";

        public string Serializer { get; set; } = "memorypack";
    }

    public sealed class SheetConfig
    {
        public const string TypeRole = "type";
        public const string NameRole = "name";
        public const string CommentRole = "comment";

        public List<string> Header { get; set; } = new() { TypeRole, NameRole, CommentRole };

        public char ArraySeparator { get; set; } = ',';

        public int TypeRow => Header.IndexOf(TypeRole);

        public int NameRow => Header.IndexOf(NameRole);

        public int CommentRow => Header.IndexOf(CommentRole);

        public int FirstDataRow => Header.Count;
    }

    public sealed class SigningConfig
    {
        public string? PublicKey { get; set; }

        public string? PrivateKeyFile { get; set; }

        public string? PrivateKeyEnv { get; set; }
    }

    public sealed class HolySheetConfig
    {
        public const string DefaultFileName = "holysheet.json";

        public string BaseDirectory { get; set; } = Directory.GetCurrentDirectory();

        public List<SourceConfig> Sources { get; set; } = new();

        public OutputConfig Output { get; set; } = new();

        public CodegenConfig Codegen { get; set; } = new();

        public SheetConfig Sheet { get; set; } = new();

        public string CsvBackend { get; set; } = "auto";

        public SigningConfig Signing { get; set; } = new();

        public string? BuildLabel { get; set; }

        public List<string> ValidatorAssemblies { get; } = new();

        public bool Parallel { get; set; } = true;

        public string CacheDirectory { get; set; } = SheetCache.DefaultDirectory;

        public bool CanPreviewOffline() => Sources.Where(source => source.IsRemote)
            .All(source => (!string.IsNullOrWhiteSpace(source.Snapshot) && Directory.Exists(Resolve(source.Snapshot!)))
                || new GoogleLocalCopies(BaseDirectory).ReadLocal(source, null, null) != null);

        public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));

        public static HolySheetConfig Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"The config file '{fullPath}' does not exist. Run 'holysheet init' to create one.", fullPath);
            }

            try
            {
                return Parse(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!);
            }
            catch (JsonFormatException exception)
            {
                throw new JsonFormatException($"{fullPath}: {exception.Message}");
            }
        }

        public static HolySheetConfig Parse(string json, string baseDirectory)
        {
            JsonObject root = JsonValue.Parse(json).AsObject ?? throw new JsonFormatException("the config must be a JSON object");
            var config = new HolySheetConfig { BaseDirectory = baseDirectory };

            foreach (JsonValue item in root.GetArray("sources") ?? Array.Empty<JsonValue>())
            {
                JsonObject source = item.AsObject ?? throw new JsonFormatException("each entry of \"sources\" must be an object");
                config.Sources.Add(ParseSource(source));
            }

            if (root.GetObject("output") is { } output)
            {
                config.Output.Code = output.GetString("code") ?? config.Output.Code;
                config.Output.Data = output.GetString("data") ?? config.Output.Data;
                config.Output.Csv = output.GetString("csv");
                config.Output.Schema = output.GetBoolean("schema") ?? config.Output.Schema;
                config.Output.ManifestFile = output.GetString("manifestFile") ?? config.Output.ManifestFile;
            }

            if (root.GetObject("codegen") is { } codegen)
            {
                config.Codegen.Namespace = codegen.GetString("namespace") ?? config.Codegen.Namespace;
                config.Codegen.TablesClass = codegen.GetString("tablesClass") ?? config.Codegen.TablesClass;
                config.Codegen.Serializer = (codegen.GetString("serializer") ?? config.Codegen.Serializer).Trim().ToLowerInvariant();
            }

            if (root.GetObject("sheet") is { } sheet)
            {
                if (sheet.GetArray("header") is { } header)
                {
                    config.Sheet.Header = header.Select(value => (value.AsString ?? "").Trim().ToLowerInvariant()).ToList();
                }

                if (sheet.GetString("arraySeparator") is { Length: > 0 } separator)
                {
                    config.Sheet.ArraySeparator = separator[0];
                }
            }

            if (root.GetObject("csv") is { } csv)
            {
                config.CsvBackend = csv.GetString("backend") ?? config.CsvBackend;
            }

            if (root.GetObject("signing") is { } signing)
            {
                config.Signing.PublicKey = signing.GetString("publicKey");
                config.Signing.PrivateKeyFile = signing.GetString("privateKeyFile");
                config.Signing.PrivateKeyEnv = signing.GetString("privateKeyEnv");
            }

            if (root.GetObject("build") is { } build)
            {
                config.BuildLabel = build.GetString("label");
                if (build["validators"] is { } validators)
                {
                    if (validators.AsArray == null || validators.AsArray.Any(value => string.IsNullOrWhiteSpace(value.AsString)))
                        throw new JsonFormatException("build.validators must be an array of DLL paths.");
                    config.ValidatorAssemblies.AddRange(validators.AsArray.Select(value => value.AsString!));
                }
                config.Parallel = build.GetBoolean("parallel") ?? config.Parallel;
                config.CacheDirectory = build.GetString("cache") ?? config.CacheDirectory;
            }

            return config;
        }

        private static SourceConfig ParseSource(JsonObject source)
        {
            string typeText = (source.GetString("type") ?? "").Trim().ToLowerInvariant();
            var result = new SourceConfig
            {
                Type = typeText switch
                {
                    "xlsx" or "excel" => SourceType.Xlsx,
                    "csv" => SourceType.Csv,
                    "google" or "sheets" or "googlesheets" => SourceType.Google,
                    "googlefolder" or "drive" or "googledrive" => SourceType.GoogleFolder,
                    _ => throw new JsonFormatException($"source type \"{typeText}\" is not one of \"xlsx\", \"csv\", \"google\", \"googleFolder\"")
                },
                Path = source.GetString("path"),
                SpreadsheetId = source.GetString("spreadsheetId"),
                FolderId = source.GetString("folderId") is { } folder ? SourceConfig.ExtractGoogleId(folder) : null,
                Name = source.GetString("name"),
                Snapshot = source.GetString("snapshot")
            };

            if (source.GetObject("auth") is { } auth)
            {
                string authType = (auth.GetString("type") ?? "public").Trim().ToLowerInvariant();
                result.Auth.Type = authType switch
                {
                    "public" or "none" => GoogleAuthType.Public,
                    "apikey" => GoogleAuthType.ApiKey,
                    "serviceaccount" => GoogleAuthType.ServiceAccount,
                    "accesstoken" => GoogleAuthType.AccessToken,
                    "custom" => GoogleAuthType.Custom,
                    "command" => GoogleAuthType.Command,
                    _ => throw new JsonFormatException($"auth type \"{authType}\" is not one of \"public\", \"apiKey\", \"serviceAccount\", \"accessToken\", \"custom\", \"command\"")
                };
                result.Auth.ApiKey = auth.GetString("apiKey");
                result.Auth.ApiKeyEnv = auth.GetString("apiKeyEnv");
                result.Auth.KeyFile = auth.GetString("keyFile");
                result.Auth.AccessTokenEnv = auth.GetString("accessTokenEnv");
                result.Auth.Provider = auth.GetString("provider");
                result.Auth.Command = auth.GetString("command");
            }

            foreach (JsonValue item in source.GetArray("sheets") ?? Array.Empty<JsonValue>())
            {
                JsonObject sheet = item.AsObject ?? throw new JsonFormatException("each entry of \"sheets\" must be an object like { \"name\": \"Item\", \"gid\": 0 }");
                result.Sheets.Add(new GoogleSheetRef(sheet.GetString("name") ?? "", (long)(sheet.GetNumber("gid") ?? 0)));
            }

            return result;
        }

        public string ToJson()
        {
            JsonObject root = JsonValue.Object();
            var sources = new List<JsonValue>();
            foreach (SourceConfig source in Sources)
            {
                JsonObject item = JsonValue.Object().Set("type", source.Type == SourceType.GoogleFolder ? "googleFolder" : source.Type.ToString().ToLowerInvariant());
                if (source.IsRemote)
                {
                    if (source.Type == SourceType.GoogleFolder)
                    {
                        item.Set("folderId", source.FolderId ?? "");
                    }
                    else
                    {
                        item.Set("spreadsheetId", source.SpreadsheetId ?? "");
                    }

                    if (source.Name != null)
                    {
                        item.Set("name", source.Name);
                    }

                    JsonObject auth = JsonValue.Object().Set("type", source.Auth.Type switch
                    {
                        GoogleAuthType.ApiKey => "apiKey",
                        GoogleAuthType.ServiceAccount => "serviceAccount",
                        GoogleAuthType.AccessToken => "accessToken",
                        GoogleAuthType.Custom => "custom",
                        GoogleAuthType.Command => "command",
                        _ => "public"
                    });
                    SetIfPresent(auth, "apiKey", source.Auth.ApiKey);
                    SetIfPresent(auth, "apiKeyEnv", source.Auth.ApiKeyEnv);
                    SetIfPresent(auth, "keyFile", source.Auth.KeyFile);
                    SetIfPresent(auth, "accessTokenEnv", source.Auth.AccessTokenEnv);
                    SetIfPresent(auth, "provider", source.Auth.Provider);
                    SetIfPresent(auth, "command", source.Auth.Command);
                    item.Set("auth", auth);
                    if (source.Sheets.Count > 0)
                    {
                        item.Set("sheets", JsonValue.Array(source.Sheets.Select(sheet => JsonValue.Object().Set("name", sheet.Name).Set("gid", sheet.Gid).ToValue())));
                    }

                    SetIfPresent(item, "snapshot", source.Snapshot);
                }
                else
                {
                    item.Set("path", source.Path ?? "");
                }

                sources.Add(item.ToValue());
            }

            root.Set("sources", JsonValue.Array(sources));

            JsonObject output = JsonValue.Object().Set("code", Output.Code).Set("data", Output.Data);
            SetIfPresent(output, "csv", Output.Csv);
            output.Set("schema", Output.Schema);
            if (Output.ManifestFile != new OutputConfig().ManifestFile)
            {
                output.Set("manifestFile", Output.ManifestFile);
            }

            root.Set("output", output);
            root.Set("codegen", JsonValue.Object().Set("namespace", Codegen.Namespace).Set("tablesClass", Codegen.TablesClass).Set("serializer", Codegen.Serializer));
            root.Set("sheet", JsonValue.Object().Set("header", JsonValue.Array(Sheet.Header)).Set("arraySeparator", Sheet.ArraySeparator.ToString()));
            root.Set("csv", JsonValue.Object().Set("backend", CsvBackend));

            if (Signing.PublicKey != null || Signing.PrivateKeyFile != null || Signing.PrivateKeyEnv != null)
            {
                JsonObject signing = JsonValue.Object();
                SetIfPresent(signing, "publicKey", Signing.PublicKey);
                SetIfPresent(signing, "privateKeyFile", Signing.PrivateKeyFile);
                SetIfPresent(signing, "privateKeyEnv", Signing.PrivateKeyEnv);
                root.Set("signing", signing);
            }

            JsonObject build = JsonValue.Object();
            SetIfPresent(build, "label", BuildLabel);
            build.Set("parallel", Parallel);
            if (CacheDirectory != SheetCache.DefaultDirectory)
            {
                build.Set("cache", CacheDirectory);
            }
            if (ValidatorAssemblies.Count > 0)
                build.Set("validators", JsonValue.Array(ValidatorAssemblies));
            root.Set("build", build);
            return root.ToValue().ToJson();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, ToJson());
        }

        public void Validate(DiagnosticBag diagnostics, bool requireSources = true)
        {
            const string location = "config";
            if (requireSources && Sources.Count == 0)
            {
                diagnostics.AddError(location, "No source is configured.", "Add an entry to \"sources\", for example { \"type\": \"xlsx\", \"path\": \"Sheets\" }.");
            }

            foreach (SourceConfig source in Sources)
            {
                if (source.Type == SourceType.Google)
                {
                    if (string.IsNullOrWhiteSpace(source.SpreadsheetId))
                    {
                        diagnostics.AddError(location, "A google source has no spreadsheetId.", "Copy the id from the spreadsheet URL: docs.google.com/spreadsheets/d/<id>/edit.");
                    }
                }
                else if (source.Type == SourceType.GoogleFolder)
                {
                    if (string.IsNullOrWhiteSpace(source.FolderId))
                    {
                        diagnostics.AddError(location, "A googleFolder source has no folderId.", "Copy the id from the folder URL: drive.google.com/drive/folders/<id>.");
                    }

                    if (source.Auth.Type == GoogleAuthType.Public)
                    {
                        diagnostics.AddError(location, $"The Drive folder '{source.FolderId}' cannot be listed without credentials.",
                            "Google lists a folder's files only through the Drive API: set auth to an API key (for a link-shared folder), a service account or an access token.");
                    }
                }
                else if (string.IsNullOrWhiteSpace(source.Path))
                {
                    diagnostics.AddError(location, $"A {source.Type.ToString().ToLowerInvariant()} source has no path.", "Set \"path\" to a file or folder, relative to the config file.");
                }

                if (source.IsRemote)
                {
                    string where = source.Name ?? source.SpreadsheetId ?? source.FolderId ?? "";
                    if (source.Auth.Type == GoogleAuthType.Custom && string.IsNullOrWhiteSpace(source.Auth.Provider))
                    {
                        diagnostics.AddError(location, $"The source '{where}' uses a custom credential provider but names none.", "Set auth.provider to the provider's name: its class name, or the name in [HolySheetCredentialProvider(\"name\")].");
                    }
                    else if (source.Auth.Type == GoogleAuthType.Command && string.IsNullOrWhiteSpace(source.Auth.Command))
                    {
                        diagnostics.AddError(location, $"The source '{where}' gets its credentials from a command but has none.", "Set auth.command to a command that prints an access token, for example \"gcloud auth print-access-token\".");
                    }
                }
            }

            foreach (string part in Codegen.Namespace.Split('.'))
            {
                if (!Schema.Identifier.IsValid(part))
                {
                    diagnostics.AddError(location, $"The namespace '{Codegen.Namespace}' is not a valid C# namespace.", "Use dot-separated identifiers, for example Game.Data.");
                    break;
                }
            }

            if (!Schema.Identifier.IsValid(Codegen.TablesClass))
            {
                diagnostics.AddError(location, $"The class name '{Codegen.TablesClass}' is not a valid C# identifier.", "For example GameTables.");
            }

            if (Codecs.TableCodecs.Find(Codegen.Serializer) == null)
            {
                diagnostics.AddError(location, $"The serializer '{Codegen.Serializer}' is not known.", "Use \"memorypack\" or \"messagepack\".");
            }

            if (Sheet.TypeRow < 0 || Sheet.NameRow < 0)
            {
                diagnostics.AddError(location, "The sheet header layout must contain both \"type\" and \"name\".", "For example [\"type\", \"name\", \"comment\"].");
            }

            foreach (string role in Sheet.Header)
            {
                if (role is not (SheetConfig.TypeRole or SheetConfig.NameRole or SheetConfig.CommentRole) || Sheet.Header.IndexOf(role) != Sheet.Header.LastIndexOf(role))
                {
                    diagnostics.AddError(location, $"The sheet header layout has an unknown or repeated row '{role}'.", "Each of \"type\", \"name\", \"comment\" may appear once.");
                    break;
                }
            }

            string code = Resolve(Output.Code);
            string data = Resolve(Output.Data);
            foreach (SourceConfig source in Sources)
            {
                if (source.Path == null)
                {
                    continue;
                }

                string input = Resolve(source.Path);
                if (Directory.Exists(input) && (PathsEqual(input, code) || PathsEqual(input, data)))
                {
                    diagnostics.AddError(location, $"The source folder '{source.Path}' is also an output folder.", "Use separate folders for sheets and for generated files.");
                }
            }

            string? csv = Output.Csv == null ? null : Resolve(Output.Csv);
            var inputs = Sources.Where(source => source.Path != null && source.Type is SourceType.Csv or SourceType.Xlsx).Select(source => (Name: source.Path!, Path: Resolve(source.Path!))).ToList();
            if (csv != null && inputs.FirstOrDefault(input => PathsEqual(input.Path, csv)) is { Name: { } shared })
            {
                diagnostics.AddError(location, $"The CSV output folder '{Output.Csv}' is also the source folder '{shared}'.", "Write the CSV output to a folder of its own.");
            }

            var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SourceConfig source in Sources.Where(source => source.Snapshot != null))
            {
                string snapshot = Resolve(source.Snapshot!).TrimEnd('/', '\\');
                if (inputs.FirstOrDefault(input => PathsEqual(input.Path, snapshot)) is { Name: { } sharedInput })
                {
                    diagnostics.AddError(location, $"The snapshot folder '{source.Snapshot}' is also the source folder '{sharedInput}'.", "Give each snapshot a folder of its own.");
                }
                else if (PathsEqual(snapshot, code) || PathsEqual(snapshot, data) || (csv != null && PathsEqual(snapshot, csv)))
                {
                    diagnostics.AddError(location, $"The snapshot folder '{source.Snapshot}' is also an output folder.", "Give each snapshot a folder of its own.");
                }
                else if (snapshots.TryGetValue(snapshot, out string? other))
                {
                    diagnostics.AddError(location, $"Two sources save their snapshot to '{source.Snapshot}' (also '{other}').", "Give each snapshot a folder of its own.");
                }
                else
                {
                    snapshots.Add(snapshot, source.Snapshot!);
                }
            }
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

        private static void SetIfPresent(JsonObject target, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                target.Set(name, value);
            }
        }
    }
}
