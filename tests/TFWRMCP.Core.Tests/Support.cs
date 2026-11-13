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

public sealed partial class FakeGame : IGame
{
    public GameState State { get; set; } = SampleState();
    public Dictionary<string, string> Unavailable { get; } = new();
    public TimeSpan GetStateDelay { get; set; }
    public int MaxConcurrentCalls { get; private set; }
    public int Calls { get; private set; }
    int inFlight;

    public string? UnavailableReason(string capability) =>
        Unavailable.TryGetValue(capability, out var reason) ? reason : null;

    public GameScene Scene { get; set; } = GameScene.Playing;
    public List<string> Saves { get; } = new() { "Save0" };
    public string CurrentSave { get; set; } = "Save0";
    public bool SaveFails { get; set; }

    /// <summary>Calls to GetScene that still report Loading after a LoadSave, before the title menu shows up.</summary>
    public int ReloadPolls { get; set; } = 3;

    /// <summary>Everything the Game was asked to do that changes the world, in order.</summary>
    public List<string> Events { get; } = new();

    /// <summary>Calls to GetScene that still report Loading before the title menu shows up.</summary>
    public int LoadingPolls { get; set; }

    public int ScenePolls { get; private set; }

    public Task<GameScene> GetScene()
    {
        ScenePolls++;
        if (Scene == GameScene.Loading && LoadingPolls-- <= 0) Scene = GameScene.TitleMenu;
        return Task.FromResult(Scene);
    }

    public Task CloseTitleMenu()
    {
        Events.Add("close_menu");
        Scene = GameScene.Playing;
        return Task.CompletedTask;
    }

    public Task<SaveList> ListSaves()
    {
        var list = new SaveList { Current = CurrentSave };
        list.Saves.AddRange(Saves);
        return Task.FromResult(list);
    }

    public Task<bool> IsValidSaveName(string name) => Task.FromResult(!name.Contains('/') && name.Length > 0);

    public Task<string> GenerateSaveName() => Task.FromResult("Save" + Saves.Count);

    public Task CreateSave(string name)
    {
        Events.Add("create:" + name);
        Saves.Add(name);
        return Task.CompletedTask;
    }

    public Task LoadSave(string name, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (SaveFails) throw new ToolException("Saving the current Save failed.");
        Events.Add("save:" + CurrentSave);
        Events.Add("load:" + name);
        CurrentSave = name;
        Scene = GameScene.Loading;
        LoadingPolls = ReloadPolls;
        return Task.CompletedTask;
    }

    public Task SaveGame()
    {
        if (SaveFails) throw new ToolException("Saving failed.");
        Events.Add("save:" + CurrentSave);
        return Task.CompletedTask;
    }

    /// <summary>While set, GetState waits for it: a Tool stuck inside a game call.</summary>
    public TaskCompletionSource<bool>? GetStateBlocker { get; set; }

    public async Task<GameState> GetState()
    {
        if (Scene != GameScene.Playing) throw new InvalidOperationException("a Tool ran while the game was not playable");
        var now = Interlocked.Increment(ref inFlight);
        Calls++;
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, now);
        try
        {
            if (GetStateBlocker != null) await GetStateBlocker.Task;
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
        Run = new RunSnapshot { Status = RunStatus.Running },
    };
}

public sealed class TestServer : IDisposable
{
    public McpServer Server { get; }
    public HttpClient Http { get; } = new();
    public Uri Url { get; private set; }
    public List<string> Log { get; } = new();
    int nextId = 1;

    public TestServer(IGame game, params Tool[] extraTools)
    {
        var tools = new List<Tool>(Tools.Create(game, Log.Add));
        tools.AddRange(extraTools);

        // A port freed by FreePort can be taken by a parallel test before we bind it, so retry on another one.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var server = new McpServer(game, tools, port, Log.Add);
            try
            {
                server.Start();
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                server.Dispose();
                continue;
            }
            Server = server;
            Url = new Uri($"http://127.0.0.1:{port}/");
            return;
        }
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Restarts the server on a fresh port; Url follows it.</summary>
    public void Restart()
    {
        var port = FreePort();
        Server.Restart(port);
        Url = new Uri($"http://127.0.0.1:{port}/");
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
