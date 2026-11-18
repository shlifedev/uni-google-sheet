namespace UGS.Core.Config;

public enum SourceType
{
    /// <summary>One .xlsx file, or a folder of them.</summary>
    Xlsx,

    /// <summary>A folder of .csv files. The file name is the sheet name.</summary>
    Csv,

    /// <summary>A Google spreadsheet, fetched over HTTPS and converted to CSV.</summary>
    Google,

    /// <summary>
    /// A Google Drive folder: every Google spreadsheet and .xlsx file directly inside it is a workbook.
    /// Listing a folder needs an API key, a service account or an access token.
    /// </summary>
    GoogleFolder
}

public enum GoogleAuthType
{
    /// <summary>The spreadsheet is shared as "anyone with the link can view". No credentials are sent.</summary>
    Public,

    /// <summary>A Google API key. Reads the sheet list of spreadsheets that are link-shared.</summary>
    ApiKey,

    /// <summary>A service account key file. The spreadsheet must be shared with the service account's e-mail address.</summary>
    ServiceAccount,

    /// <summary>An OAuth access token supplied by the environment, for CI that already has one.</summary>
    AccessToken
}

public sealed class GoogleAuthConfig
{
    public GoogleAuthType Type { get; set; } = GoogleAuthType.Public;

    /// <summary>The API key itself. Prefer <see cref="ApiKeyEnv"/> so the key is not committed.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of the environment variable holding the API key.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>Path of the service account JSON key file, relative to the config file.</summary>
    public string? KeyFile { get; set; }

    /// <summary>Name of the environment variable holding an OAuth access token.</summary>
    public string? AccessTokenEnv { get; set; }
}

/// <summary>One tab of a Google spreadsheet, named explicitly so no API call is needed to list the tabs.</summary>
public sealed record GoogleSheetRef(string Name, long Gid);

public sealed class SourceConfig
{
    public SourceType Type { get; set; }

    /// <summary>File or folder for xlsx and csv sources, relative to the config file.</summary>
    public string? Path { get; set; }

    /// <summary>The id from the spreadsheet URL: docs.google.com/spreadsheets/d/{this}/edit.</summary>
    public string? SpreadsheetId { get; set; }

    /// <summary>The id from the folder URL: drive.google.com/drive/folders/{this}. For Google Drive folder sources.</summary>
    public string? FolderId { get; set; }

    /// <summary>True for the sources read from Google over the network.</summary>
    public bool IsRemote => Type is SourceType.Google or SourceType.GoogleFolder;

    /// <summary>
    /// Takes a pasted URL or a bare id and returns the id: the part after /d/ (spreadsheets), /folders/ (Drive folders)
    /// or ?id=. Anything that does not look like a URL is returned trimmed.
    /// </summary>
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

    /// <summary>Name used for this spreadsheet in messages. Defaults to its title, or its id.</summary>
    public string? Name { get; set; }

    public GoogleAuthConfig Auth { get; set; } = new();

    /// <summary>Tabs to read. Empty means "all of them", which needs a way to list them (an API key, a token, or the xlsx export).</summary>
    public List<GoogleSheetRef> Sheets { get; set; } = [];

    /// <summary>
    /// Folder where the CSV fetched from Google is also saved, relative to the config file.
    /// Committing it gives reviewable diffs of sheet edits and lets builds run offline.
    /// </summary>
    public string? Snapshot { get; set; }
}

public sealed class OutputConfig
{
    /// <summary>Folder for generated .g.cs files.</summary>
    public string Code { get; set; } = "Generated/Code";

    /// <summary>Folder for the data files and the manifest.</summary>
    public string Data { get; set; } = "Generated/Data";

    /// <summary>Optional folder that receives each table as CSV after the transform rules were applied.</summary>
    public string? Csv { get; set; }

    /// <summary>Write tables.schema.json next to the data: the full layout, for patch tools and other consumers that have no generated code.</summary>
    public bool Schema { get; set; } = true;

    public string ManifestFile { get; set; } = "tables.manifest.txt";
}

public sealed class CodegenConfig
{
    public string Namespace { get; set; } = "Game.Data";

    /// <summary>Name of the class that holds every table.</summary>
    public string TablesClass { get; set; } = "GameTables";

    /// <summary>"memorypack" or "messagepack".</summary>
    public string Serializer { get; set; } = "memorypack";
}

/// <summary>How a table sheet is laid out.</summary>
public sealed class SheetConfig
{
    public const string TypeRole = "type";
    public const string NameRole = "name";
    public const string CommentRole = "comment";

    /// <summary>What each header row holds, top to bottom. Data starts on the row after the last one.</summary>
    public List<string> Header { get; set; } = [TypeRole, NameRole, CommentRole];

    /// <summary>Separates the elements of an array written in one cell.</summary>
    public char ArraySeparator { get; set; } = '|';

    public int TypeRow => Header.IndexOf(TypeRole);

    public int NameRow => Header.IndexOf(NameRole);

    /// <summary>-1 when the layout has no comment row.</summary>
    public int CommentRow => Header.IndexOf(CommentRole);

    public int FirstDataRow => Header.Count;
}

public sealed class SigningConfig
{
    /// <summary>Public key (rsa:modulus:exponent). When set it is baked into the generated code and loads require a valid signature.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Path of the private key file, relative to the config file. Keep it out of version control.</summary>
    public string? PrivateKeyFile { get; set; }

    /// <summary>Name of the environment variable holding the private key text, for CI.</summary>
    public string? PrivateKeyEnv { get; set; }
}

/// <summary>
/// Everything a build needs, read from <c>ugs.json</c>. The CLI and the Unity editor window read and write the same file,
/// so a project is configured once and builds the same way on a developer machine and in CI.
/// </summary>
public sealed class UgsConfig
{
    public const string DefaultFileName = "ugs.json";

    /// <summary>Folder the relative paths in this config are resolved against: the folder of the config file.</summary>
    public string BaseDirectory { get; set; } = Directory.GetCurrentDirectory();

    public List<SourceConfig> Sources { get; set; } = [];

    public OutputConfig Output { get; set; } = new();

    public CodegenConfig Codegen { get; set; } = new();

    public SheetConfig Sheet { get; set; } = new();

    /// <summary>"auto" (the built-in parser), "builtin" or "sep" (needs .NET 8 or newer).</summary>
    public string CsvBackend { get; set; } = "auto";

    public SigningConfig Signing { get; set; } = new();

    /// <summary>Free text written to the manifest as the build line, such as a version number or commit id.</summary>
    public string? BuildLabel { get; set; }

    /// <summary>Convert and encode tables on several threads.</summary>
    public bool Parallel { get; set; } = true;

    /// <summary>Where the sheets of the last build are kept for partial builds, relative to the config file.</summary>
    public string CacheDirectory { get; set; } = SheetCache.DefaultDirectory;

    public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));

    public static UgsConfig Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The config file '{fullPath}' does not exist. Run 'ugs init' to create one.", fullPath);
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

    public static UgsConfig Parse(string json, string baseDirectory)
    {
        JsonObject root = JsonValue.Parse(json).AsObject ?? throw new JsonFormatException("the config must be a JSON object");
        var config = new UgsConfig { BaseDirectory = baseDirectory };

        foreach (JsonValue item in root.GetArray("sources") ?? [])
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
                _ => throw new JsonFormatException($"auth type \"{authType}\" is not one of \"public\", \"apiKey\", \"serviceAccount\", \"accessToken\"")
            };
            result.Auth.ApiKey = auth.GetString("apiKey");
            result.Auth.ApiKeyEnv = auth.GetString("apiKeyEnv");
            result.Auth.KeyFile = auth.GetString("keyFile");
            result.Auth.AccessTokenEnv = auth.GetString("accessTokenEnv");
        }

        foreach (JsonValue item in source.GetArray("sheets") ?? [])
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
                    _ => "public"
                });
                SetIfPresent(auth, "apiKey", source.Auth.ApiKey);
                SetIfPresent(auth, "apiKeyEnv", source.Auth.ApiKeyEnv);
                SetIfPresent(auth, "keyFile", source.Auth.KeyFile);
                SetIfPresent(auth, "accessTokenEnv", source.Auth.AccessTokenEnv);
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
        root.Set("build", build);
        return root.ToValue().ToJson();
    }

    public void Save(string path)
    {
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Reports settings that cannot work, before any sheet is read.</summary>
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

            // The output folders are places the tool deletes stale files from. They must never be a folder people put sheets in.
            string input = Resolve(source.Path);
            if (Directory.Exists(input) && (PathsEqual(input, code) || PathsEqual(input, data)))
            {
                diagnostics.AddError(location, $"The source folder '{source.Path}' is also an output folder.", "Use separate folders for sheets and for generated files.");
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
