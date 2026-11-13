using System;
using BepInEx;
using BepInEx.Configuration;

namespace TFWRMCP.Mod;

[BepInPlugin("com.insulinocytus.tfwrmcp", "TFWRMCP", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    ConfigEntry<int> port = null!;
    McpServer? server;

    void Awake()
    {
        port = Config.Bind("Server", "Port", 47821, new ConfigDescription(
            "Localhost port the MCP server listens on.", new AcceptableValueRange<int>(1, 65535)));

        var game = new GameAdapter(Logger.LogWarning);
        server = new McpServer(game, Tools.Create(game), port.Value, Logger.LogError);
        try
        {
            server.Start();
            Logger.LogInfo($"MCP server listening on http://127.0.0.1:{port.Value}/");
        }
        catch (Exception e)
        {
            Logger.LogError($"MCP disabled: cannot listen on 127.0.0.1:{port.Value} ({e.Message}). Change [Server] Port in the BepInEx config.");
            server.Dispose();
            server = null;
        }
    }

    void Update() => MainThread.Drain();

    void OnDestroy() => server?.Dispose();
}
