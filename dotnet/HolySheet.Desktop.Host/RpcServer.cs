using System.Text.Json;
using System.Text.Json.Nodes;
using HolySheet.Core.Config;

namespace HolySheet.Desktop.Host;

/// <summary>An error the client should show: JSON-RPC error codes, plus the host's own.</summary>
public sealed class RpcException(int code, string message) : Exception(message)
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>The tool could not do it: a file is missing or unreadable, the config does not parse.</summary>
    public const int ToolError = 1;

    /// <summary>A build is already running.</summary>
    public const int Busy = 2;

    /// <summary>No project folder is open.</summary>
    public const int NoProject = 3;

    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC 2.0 over a pair of text streams, one message per line. Requests are handled concurrently, so a
/// preview can be answered while a build runs; responses carry the request id. The host also sends notifications
/// (no id) when a build starts or ends, whoever started it.
/// </summary>
public sealed class RpcServer
{
    // Relaxed escaping: the reader is the app, not an HTML page, and messages full of \u0027 are hard to debug.
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();

    public RpcServer(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        Project = new DesktopProject(Notify);
    }

    public DesktopProject Project { get; }

    /// <summary>Reads requests until the input ends, then waits for the ones still running.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var running = new List<Task>();
        while (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            running.RemoveAll(task => task.IsCompleted);
            running.Add(Task.Run(() => HandleLineAsync(line), cancellationToken));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Handles one request line and writes its response, if it has an id.</summary>
    public async Task HandleLineAsync(string line)
    {
        JsonNode? id = null;
        try
        {
            JsonObject request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject ?? throw new RpcException(RpcException.InvalidRequest, "A request is a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new RpcException(RpcException.ParseError, "Not JSON: " + exception.Message);
            }

            id = request["id"]?.DeepClone();
            string method = request["method"]?.GetValue<string>() ?? throw new RpcException(RpcException.InvalidRequest, "The request has no method.");
            JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();
            JsonNode? result = await DispatchAsync(method, parameters).ConfigureAwait(false);
            if (id != null)
            {
                Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
        }
        catch (Exception exception)
        {
            (int code, string message) = Describe(exception);
            if (id != null || code == RpcException.ParseError)
            {
                Write(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
                });
            }
        }
    }

    /// <summary>Calls one method. Tests use this directly; the protocol loop goes through <see cref="HandleLineAsync"/>.</summary>
    public Task<JsonNode?> DispatchAsync(string method, JsonObject parameters)
    {
        return method switch
        {
            "app.info" => Sync(DesktopProject.AppInfo()),
            "project.open" => Sync(Project.Open(Required(parameters, "path"))),
            "project.state" => Sync(Project.State()),
            "project.init" => Sync(Project.Init(Required(parameters, "dir"), Bool(parameters, "sample"), parameters["unity"]?.GetValue<bool?>())),
            "project.close" => Sync(Project.Close()),
            "config.save" => Sync(Project.SaveConfig(parameters["config"] as JsonObject ?? throw new RpcException(RpcException.InvalidParams, "'config' must be an object."))),
            "run" => Wrap(Project.RunAsync(Bool(parameters, "commit"), Bool(parameters, "offline"), Strings(parameters, "only"), "user", Bool(parameters, "refreshGoogle"))),
            "tables.model" => Sync(Project.TableModel()),
            "tables.preview" => Sync(Project.Preview(Required(parameters, "table"), Optional(parameters, "sheet"))),
            "source.locate" => Sync(Project.LocateSource(Required(parameters, "workbook"), Optional(parameters, "sheet"))),
            "problem.locate" => Sync(Project.LocateProblem(Required(parameters, "location"), Optional(parameters, "workbook"), Optional(parameters, "sheet"))),
            "path.toProject" => Sync(Project.ToProjectPath(Required(parameters, "path"))),
            "signing.createKeyPair" => Sync(Project.CreateKeyPair()),
            "signing.ignoreKey" => Sync(Project.IgnoreKey()),
            "signing.stop" => Sync(Project.StopSigning()),
            "signing.verify" => Sync(Project.Verify(Optional(parameters, "dataDir"))),
            _ => throw new RpcException(RpcException.MethodNotFound, $"Unknown method '{method}'.")
        };

        static Task<JsonNode?> Sync(JsonNode? node) => Task.FromResult(node);

        static async Task<JsonNode?> Wrap(Task<JsonObject> task) => await task.ConfigureAwait(false);
    }

    private void Notify(string method, JsonNode? parameters)
    {
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
    }

    private void Write(JsonObject message)
    {
        string text = message.ToJsonString(Compact);
        lock (_writeLock)
        {
            _output.WriteLine(text);
            _output.Flush();
        }
    }

    private static (int Code, string Message) Describe(Exception exception)
    {
        return exception switch
        {
            RpcException rpc => (rpc.Code, rpc.Message),
            IOException or UnauthorizedAccessException or JsonFormatException or FormatException => (RpcException.ToolError, exception.Message),
            InvalidOperationException or JsonException => (RpcException.InvalidParams, exception.Message),
            _ => (RpcException.InternalError, exception.GetType().Name + ": " + exception.Message)
        };
    }

    private static string Required(JsonObject parameters, string name) =>
        Optional(parameters, name) ?? throw new RpcException(RpcException.InvalidParams, $"'{name}' is required.");

    private static string? Optional(JsonObject parameters, string name) =>
        parameters[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool Bool(JsonObject parameters, string name) =>
        parameters[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static IReadOnlyList<string> Strings(JsonObject parameters, string name) =>
        parameters[name] is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? "").Where(text => text.Length > 0).ToList() : [];
}
