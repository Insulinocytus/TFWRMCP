using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace TFWRMCP;

/// <summary>JSON-RPC / MCP method handling, independent of the HTTP transport. Tool calls run strictly one at a time.</summary>
public sealed class McpDispatcher
{
    const int ParseError = -32700;
    const int InvalidRequest = -32600;
    const int MethodNotFound = -32601;
    const int InvalidParams = -32602;

    // Newest first; the first entry is offered when the client asks for something we do not know.
    static readonly string[] ProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Include,
    });

    readonly IGame game;
    readonly Dictionary<string, Tool> tools;
    readonly Action<string> log;
    readonly SemaphoreSlim toolGate = new(1, 1);

    public McpDispatcher(IGame game, IEnumerable<Tool> tools, Action<string> log)
    {
        this.game = game;
        this.tools = tools.ToDictionary(t => t.Name);
        this.log = log;
    }

    /// <summary>Handles one POST body. Json is null when no response body is due (notifications).</summary>
    public async Task<(int Status, string? Json)> Handle(string body)
    {
        JToken parsed;
        try
        {
            parsed = JToken.Parse(body);
        }
        catch (JsonReaderException)
        {
            return (400, Error(null, ParseError, "Parse error"));
        }

        if (parsed is not JObject request)
            return (400, Error(null, InvalidRequest, "Batches and non-object requests are not supported"));

        var id = request["id"];
        if (request["method"] is not JValue { Value: string method})
        {
            // A client response to a server request: we never send any, so just acknowledge.
            return request["result"] != null || request["error"] != null
                ? (202, null)
                : (400, Error(id, InvalidRequest, "Missing method"));
        }

        if (id == null) return (202, null);

        var parameters = request["params"] as JObject ?? new JObject();
        switch (method)
        {
            case "initialize":
                return Ok(id, Initialize(parameters));
            case "ping":
                return Ok(id, new JObject());
            case "tools/list":
                return Ok(id, new JObject { ["tools"] = new JArray(tools.Values.Select(Describe)) });
            case "tools/call":
                return await CallTool(id, parameters);
            default:
                return (200, Error(id, MethodNotFound, $"Method not found: {method}"));
        }
    }

    static JObject Initialize(JObject parameters)
    {
        var requested = (string?)parameters["protocolVersion"];
        var version = requested != null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0];
        return new JObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
            ["serverInfo"] = new JObject
            {
                ["name"] = "tfwrmcp",
                ["version"] = typeof(McpDispatcher).Assembly.GetName().Version!.ToString(3),
            },
        };
    }

    static JObject Describe(Tool tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.InputSchema,
    };

    async Task<(int, string?)> CallTool(JToken id, JObject parameters)
    {
        if (parameters["name"] is not JValue { Value: string name } || !tools.TryGetValue(name, out var tool))
            return (200, Error(id, InvalidParams, "Unknown tool"));
        if (parameters["arguments"] is not (null or JObject))
            return (200, Error(id, InvalidParams, "arguments must be an object"));
        var args = parameters["arguments"] as JObject ?? new JObject();

        await toolGate.WaitAsync();
        try
        {
            foreach (var capability in tool.Capabilities)
            {
                var reason = game.UnavailableReason(capability);
                if (reason != null)
                    return Ok(id, ToolResult(true,
                        $"Tool '{name}' is unavailable: capability '{capability}' failed to resolve ({reason}). " +
                        "The game may have been updated; other tools are unaffected."));
            }

            await Readiness.Ensure(game);
            var result = await tool.Handler(args);
            return Ok(id, ToolResult(false, JToken.FromObject(result ?? new JObject(), Serializer).ToString(Formatting.None)));
        }
        catch (ToolException e)
        {
            return Ok(id, ToolResult(true, e.Message));
        }
        catch (Exception e)
        {
            log($"Tool '{name}' failed: {e}");
            return Ok(id, ToolResult(true, $"Internal error in tool '{name}': {e.GetType().Name}: {e.Message}"));
        }
        finally
        {
            toolGate.Release();
        }
    }

    static JObject ToolResult(bool isError, string text) => new()
    {
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static (int, string?) Ok(JToken id, JObject result) =>
        (200, new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToString(Formatting.None));

    static string Error(JToken? id, int code, string message) =>
        new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        }.ToString(Formatting.None);
}
