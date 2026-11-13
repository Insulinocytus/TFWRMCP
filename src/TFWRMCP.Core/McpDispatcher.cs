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

    event Action Interrupted = delegate { };
    int restarts;

    /// <summary>
    /// Abandons the Tool in progress like F10 does, except that a Run keeps going (the server restarts); calls still
    /// waiting their turn are dropped.
    /// </summary>
    public void Interrupt()
    {
        Interlocked.Increment(ref restarts);
        Interrupted();
    }

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

        var epoch = Volatile.Read(ref restarts);
        await toolGate.WaitAsync();
        Task? abandoned = null;
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

            var (result, left) = await RunLocked(name, tool, args, epoch);
            abandoned = left;
            if (abandoned != null)
                throw new ToolException("Interrupted by the user: the human pressed F10 and took back control. " +
                    "Any Run in progress was stopped (status stopped_by_user); the Tool's effects may be incomplete.");
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
            // An abandoned Tool may still be touching the game, so the next call waits for it.
            if (abandoned == null) toolGate.Release();
            else _ = abandoned.ContinueWith(_ => toolGate.Release(), TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    /// <summary>
    /// Runs the Tool under the Input Lock. F10 cancels it, stops the Run and abandons it (the game has released the lock
    /// by then); the returned task completes once the abandoned work has stopped.
    /// </summary>
    async Task<(object? Result, Task? Abandoned)> RunLocked(string name, Tool tool, JObject args, int epoch)
    {
        var cancel = new CancellationTokenSource();
        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTakeover()
        {
            stop.TrySetResult(true);
            cancel.Cancel();
        }
        void OnInterrupt()
        {
            stop.TrySetResult(false);
            cancel.Cancel();
        }
        game.UserTookOver += OnTakeover;
        Interrupted += OnInterrupt;
        try
        {
            // Subscribed first, so a restart either shows here or interrupts the work below.
            if (epoch != Volatile.Read(ref restarts)) throw new ToolException("Dropped: the MCP server was restarted.");
            await game.SetInputLock(name);
            var work = Work(cancel.Token);
            await Task.WhenAny(work, stop.Task);
            if (!stop.Task.IsCompleted) return (await work, null);

            var settled = Settle(work);
            if (await stop.Task)
            {
                try { await game.StopRun(); }
                catch (Exception e) { log($"Stopping the Run after the user interrupted '{name}' failed: {e}"); }
            }
            return (null, settled);
        }
        finally
        {
            game.UserTookOver -= OnTakeover;
            Interrupted -= OnInterrupt;
            await game.SetInputLock(null);
        }

        async Task<object?> Work(CancellationToken token)
        {
            await Readiness.Ensure(game, token);
            return await tool.Handler(args, token);
        }

        async Task Settle(Task work)
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (Exception e) { log($"Tool '{name}' failed after the user interrupted it: {e}"); }
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
