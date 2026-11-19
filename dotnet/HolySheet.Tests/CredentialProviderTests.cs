using System.Net;
using System.Security.Cryptography;
using System.Text;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Sources;

namespace HolySheet.Tests;

/// <summary>Credentials from the project's own code (auth type "custom") and from a command (auth type "command"). No network is used.</summary>
public class CredentialProviderTests
{
    private static readonly GoogleEndpoints Endpoints = new() { SheetsApi = "https://sheets.test", Docs = "https://docs.test", DriveApi = "https://drive.test", Wait = (_, _) => Task.CompletedTask };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // One spreadsheet with one tab, as the Sheets API serves it. The Drive version check gets a 404, which only means "unknown".
    private static HttpResponseMessage Serve(HttpRequestMessage request)
    {
        string url = request.RequestUri!.ToString();
        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id?", StringComparison.Ordinal))
        {
            return Json("""{ "properties": { "title": "Live Data" }, "sheets": [ { "properties": { "sheetId": 0, "title": "Item" } } ] }""");
        }

        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id/values:batchGet?", StringComparison.Ordinal))
        {
            return Json("""{ "valueRanges": [ { "range": "Item!A1:B4", "values": [ ["Key<int>", "string"], ["Id", "Name"], [], ["1", "Sword"] ] } ] }""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static SourceConfig Custom(string provider) => new() { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.Custom, Provider = provider } };

    private static SourceConfig Command(string command) => new() { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.Command, Command = command } };

    private static (BuildResult Result, StubHandler Handler) Build(SourceConfig source, IReadOnlyList<IGoogleCredentialProvider>? providers, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        using var temp = new TempDir();
        var handler = new StubHandler(respond ?? Serve);
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { source }, CsvBackend = "builtin" };
        return (TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = Endpoints, CredentialProviders = providers }), handler);
    }

    private static IEnumerable<string?> Authorizations(StubHandler handler) => handler.Requests.Select(request => request.Headers.Authorization?.ToString());

    public sealed class TokenProvider : IGoogleCredentialProvider
    {
        public List<GoogleCredentialRequest> Requests { get; } = [];

        public string? Account { get; set; }

        public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(GoogleCredential.FromAccessToken("TOKEN-FROM-CODE", Account));
        }
    }

    [HolySheetCredentialProvider("vault")]
    public sealed class VaultProvider : IGoogleCredentialProvider
    {
        public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(GoogleCredential.FromApiKey("KEY-FROM-VAULT"));
    }

    public sealed class BrokenProvider : IGoogleCredentialProvider
    {
        public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the vault is sealed");
    }

    public sealed class KeyJsonProvider(string keyJson) : IGoogleCredentialProvider
    {
        public Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(GoogleCredential.FromServiceAccountKey(keyJson));
    }

    [HolySheetCredentialProvider("abstract")]
    public abstract class AbstractProvider : IGoogleCredentialProvider
    {
        public abstract Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken);
    }

    [HolySheetCredentialProvider]
    public sealed class NotAProvider
    {
    }

    [Fact]
    public void A_provider_class_is_named_by_its_class_or_by_the_attribute()
    {
        Assert.Equal("TokenProvider", GoogleCredentialProviders.NameOf(typeof(TokenProvider)));
        Assert.Equal("vault", GoogleCredentialProviders.NameOf(typeof(VaultProvider)));
        Assert.Equal("NotAProvider", GoogleCredentialProviders.NameOf(typeof(NotAProvider)));
    }

    [Fact]
    public void Provider_classes_are_created_and_unusable_marked_classes_are_reported()
    {
        var problems = new List<string>();
        IReadOnlyList<IGoogleCredentialProvider> providers = GoogleCredentialProviders.Create(
            [typeof(VaultProvider), typeof(TokenProvider), typeof(TokenProvider), typeof(AbstractProvider), typeof(NotAProvider), typeof(KeyJsonProvider), typeof(string)], problems);

        // Each class once; the one without a parameterless constructor and the abstract one are skipped.
        Assert.Equal([typeof(TokenProvider), typeof(VaultProvider)], providers.Select(provider => provider.GetType()).OrderBy(type => type.Name));
        // Only classes marked with the attribute are reported: an unmarked class may implement the interface for other reasons.
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains(nameof(AbstractProvider)));
        Assert.Contains(problems, problem => problem.Contains(nameof(NotAProvider)));
    }

    [Fact]
    public void A_custom_provider_token_is_sent_with_every_request()
    {
        var provider = new TokenProvider();
        (BuildResult result, StubHandler handler) = Build(Custom("TokenProvider"), [new VaultProvider(), provider]);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal("Sword", result.Tables[0].Rows[0].Values[1]);
        Assert.All(Authorizations(handler), header => Assert.Equal("Bearer TOKEN-FROM-CODE", header));
        Assert.Equal(3, handler.Requests.Count);

        GoogleCredentialRequest request = Assert.Single(provider.Requests);
        Assert.Equal("sheet-id", request.Source.SpreadsheetId);
        Assert.Contains("drive.readonly", request.Scopes);
        Assert.NotNull(request.Http);
    }

    [Fact]
    public void The_provider_name_ignores_case_and_an_api_key_goes_in_the_query()
    {
        (BuildResult result, StubHandler handler) = Build(Custom("Vault"), [new VaultProvider()]);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.All(handler.Requests, request => Assert.Contains("key=KEY-FROM-VAULT", request.RequestUri!.ToString()));
        Assert.All(Authorizations(handler), Assert.Null);
    }

    [Fact]
    public void An_unknown_provider_lists_the_ones_there_are_and_nothing_is_requested()
    {
        (BuildResult result, StubHandler handler) = Build(Custom("vaullt"), [new VaultProvider(), new TokenProvider()]);
        Assert.False(result.Success);
        Assert.Contains("no credential provider named 'vaullt'; there are: TokenProvider, vault.", Sheets.Errors(result));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_host_without_providers_points_at_the_command_auth()
    {
        // The CLI: it cannot load the editor's classes.
        (BuildResult result, _) = Build(Custom("vault"), providers: null);
        Assert.False(result.Success);
        Assert.Contains("only the Unity editor can use them", Sheets.Errors(result));
        Assert.Contains("\"command\"", Sheets.Errors(result));
    }

    [Fact]
    public void A_provider_that_throws_is_named_in_the_error()
    {
        (BuildResult result, StubHandler handler) = Build(Custom("BrokenProvider"), [new BrokenProvider()]);
        Assert.False(result.Success);
        Assert.Contains("The credential provider 'BrokenProvider' failed: InvalidOperationException: the vault is sealed", Sheets.Errors(result));
        Assert.Contains(typeof(BrokenProvider).FullName + ".GetCredentialAsync", Sheets.Errors(result));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_refusal_names_the_account_the_provider_reported()
    {
        var provider = new TokenProvider { Account = "builder@studio.example" };
        (BuildResult result, _) = Build(Custom("TokenProvider"), [provider], _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.False(result.Success);
        Assert.Contains("Share the spreadsheet with builder@studio.example as a viewer", Sheets.Errors(result));
    }

    [Fact]
    public void A_service_account_key_from_a_provider_is_exchanged_for_a_token()
    {
        using RSA rsa = RSA.Create(2048);
        string keyJson = JsonValue.Object()
            .Set("client_email", "vault-bot@project.iam.gserviceaccount.com")
            .Set("token_uri", "https://oauth.test/token")
            .Set("private_key", rsa.ExportPkcs8PrivateKeyPem())
            .ToValue().ToJson();

        (BuildResult result, StubHandler handler) = Build(Custom("KeyJsonProvider"), [new KeyJsonProvider(keyJson)], request =>
            request.RequestUri!.ToString() == "https://oauth.test/token" ? Json("""{ "access_token": "SA-TOKEN", "expires_in": 3600 }""") : Serve(request));

        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal("https://oauth.test/token", handler.Requests[0].RequestUri!.ToString());
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("Bearer SA-TOKEN", request.Headers.Authorization?.ToString()));
    }

    [Fact]
    public void A_command_prints_the_token()
    {
        (BuildResult result, StubHandler handler) = Build(Command("echo TOKEN-FROM-COMMAND"), providers: null);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.All(Authorizations(handler), header => Assert.Equal("Bearer TOKEN-FROM-COMMAND", header));
    }

    [Fact]
    public void A_command_can_print_json_and_reaches_the_shell_with_its_quotes_intact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (BuildResult result, StubHandler handler) = Build(Command("""printf '%s' "{\"access_token\": \"JSON-TOKEN\"}" """), providers: null);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.All(Authorizations(handler), header => Assert.Equal("Bearer JSON-TOKEN", header));
    }

    [Fact]
    public void A_failing_command_reports_its_exit_code_and_error_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (BuildResult result, StubHandler handler) = Build(Command("echo 'not logged in' >&2; exit 3"), providers: null);
        Assert.False(result.Success);
        Assert.Contains("failed with exit code 3: not logged in", Sheets.Errors(result));
        Assert.Empty(handler.Requests);

        (BuildResult missing, _) = Build(Command("holysheet-no-such-program-here"), providers: null);
        Assert.Contains("The program was not found", Sheets.Errors(missing));
    }

    [Fact]
    public void Command_output_must_be_a_token_or_json_with_credentials()
    {
        Assert.Equal("abc", CredentialCommand.Parse("  abc\n", "cmd").AccessToken);
        Assert.Equal("me@example.com", CredentialCommand.Parse("""{ "access_token": "t", "account": "me@example.com" }""", "cmd").Account);
        Assert.NotNull(CredentialCommand.Parse("""{ "client_email": "a", "private_key": "b" }""", "cmd").ServiceAccountKeyJson);

        Assert.Contains("more than one token", Assert.ThrowsAny<Exception>(() => CredentialCommand.Parse("Updating…\nabc", "cmd")).Message);
        Assert.Contains("printed nothing", Assert.ThrowsAny<Exception>(() => CredentialCommand.Parse("", "cmd")).Message);
        Assert.Contains("without access_token", Assert.ThrowsAny<Exception>(() => CredentialCommand.Parse("""{ "token": "t" }""", "cmd")).Message);
    }

    [Fact]
    public void Custom_and_command_auth_round_trip_through_the_config_and_are_validated()
    {
        const string json = """
            { "sources": [
              { "type": "google", "spreadsheetId": "a", "auth": { "type": "custom", "provider": "vault" } },
              { "type": "googleFolder", "folderId": "b", "auth": { "type": "command", "command": "gcloud auth print-access-token" } } ] }
            """;
        HolySheetConfig config = HolySheetConfig.Parse(json, Path.GetTempPath());
        Assert.Equal(GoogleAuthType.Custom, config.Sources[0].Auth.Type);
        Assert.Equal("vault", config.Sources[0].Auth.Provider);
        Assert.Equal(GoogleAuthType.Command, config.Sources[1].Auth.Type);
        Assert.Equal("gcloud auth print-access-token", config.Sources[1].Auth.Command);

        HolySheetConfig again = HolySheetConfig.Parse(config.ToJson(), Path.GetTempPath());
        Assert.Equal("vault", again.Sources[0].Auth.Provider);
        Assert.Equal("gcloud auth print-access-token", again.Sources[1].Auth.Command);

        var valid = new DiagnosticBag();
        config.Validate(valid);
        Assert.False(valid.HasErrors);

        config.Sources[0].Auth.Provider = " ";
        config.Sources[1].Auth.Command = null;
        var invalid = new DiagnosticBag();
        config.Validate(invalid);
        Assert.Contains(invalid.Items, item => item.Message.Contains("uses a custom credential provider but names none"));
        Assert.Contains(invalid.Items, item => item.Message.Contains("gets its credentials from a command but has none"));
    }
}
