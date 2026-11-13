using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TFWRMCP;

namespace TFWRMCP.Core.Tests;

public sealed class FakeGame : IGame
{
    public GameState State { get; set; } = SampleState();
    public Dictionary<string, string> Unavailable { get; } = new();
    public TimeSpan GetStateDelay { get; set; }
    public int MaxConcurrentCalls { get; private set; }
    public int Calls { get; private set; }
    int inFlight;

    public string? UnavailableReason(string capability) =>
        Unavailable.TryGetValue(capability, out var reason) ? reason : null;

    public async Task<GameState> GetState()
    {
        var now = Interlocked.Increment(ref inFlight);
        Calls++;
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, now);
        try
        {
            if (GetStateDelay > TimeSpan.Zero) await Task.Delay(GetStateDelay);
            return State;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    public static GameState SampleState() => new()
    {
        Save = "Save0",
        Inventory = new Dictionary<string, double> { ["hay"] = 12.5, ["wood"] = 0 },
        Unlocks =
        {
            new UnlockState { Name = "grass", Level = 1, MaxLevel = 1 },
            new UnlockState
            {
                Name = "expand", Level = 1, MaxLevel = 9,
                NextCost = new Dictionary<string, double> { ["wood"] = 20 },
            },
        },
        WorldSize = new Vec2 { X = 2, Y = 2 },
        Drones = { new DroneState { Id = 0, X = 1, Y = 0, Depth = 0 } },
        Tiles =
        {
            new List<TileState>
            {
                new TileState { Entity = "grass", Ground = "grassland" },
                new TileState { Entity = null, Ground = "soil" },
            },
            new List<TileState>
            {
                new TileState { Entity = "tree", Ground = "grassland" },
                new TileState { Entity = null, Ground = "dirt", Depth = 2 },
            },
        },
        Run = new RunState { Status = RunState.Running },
    };
}

public sealed class TestServer : IDisposable
{
    public McpServer Server { get; }
    public HttpClient Http { get; } = new();
    public Uri Url { get; }
    public List<string> Log { get; } = new();
    int nextId = 1;

    public TestServer(IGame game, params Tool[] extraTools)
    {
        var port = FreePort();
        Url = new Uri($"http://127.0.0.1:{port}/");
        var tools = new List<Tool>(Tools.Create(game));
        tools.AddRange(extraTools);
        Server = new McpServer(game, tools, port, Log.Add);
        Server.Start();
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task<HttpResponseMessage> PostRaw(string body, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (origin != null) req.Headers.Add("Origin", origin);
        return await Http.SendAsync(req);
    }

    public async Task<JObject> Rpc(string method, JObject? parameters = null)
    {
        var req = new JObject { ["jsonrpc"] = "2.0", ["id"] = nextId++, ["method"] = method };
        if (parameters != null) req["params"] = parameters;
        using var resp = await PostRaw(req.ToString());
        return JObject.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Calls a tool; returns (isError, text of the first content block).</summary>
    public async Task<(bool IsError, string Text)> Call(string tool, JObject? args = null)
    {
        var resp = await Rpc("tools/call", new JObject { ["name"] = tool, ["arguments"] = args ?? new JObject() });
        var result = (JObject)resp["result"]!;
        return ((bool)result["isError"]!, (string)result["content"]![0]!["text"]!);
    }

    public void Dispose()
    {
        Server.Dispose();
        Http.Dispose();
    }
}
