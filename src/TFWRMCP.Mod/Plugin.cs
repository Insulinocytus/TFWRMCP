using System;
using BepInEx;
using BepInEx.Configuration;

namespace TFWRMCP.Mod;

[BepInPlugin("com.insulinocytus.tfwrmcp", "TFWRMCP", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    McpServer? server;

    public ConfigEntry<int> Port { get; private set; } = null!;

    public ConfigEntry<bool> SuppressUnlockDocs { get; private set; } = null!;

    public ConfigEntry<bool> ResourceBarAtBottom { get; private set; } = null!;

    /// <summary>Set when the server is not listening: why.</summary>
    public string? ServerError { get; private set; }

    public int ListeningPort { get; private set; }

    void Awake()
    {
        Port = Config.Bind("Server", "Port", 47821, new ConfigDescription(
            "Localhost port the MCP server listens on. Changes apply when the server is restarted from the game's MCP options.",
            new AcceptableValueRange<int>(1, 65535)));
        SuppressUnlockDocs = Config.Bind("Game", "SuppressUnlockDocs", true,
            "Do not open an Unlock's documentation automatically when it is bought.");
        ResourceBarAtBottom = Config.Bind("Layout", "ResourceBarAtBottom", true,
            "Anchor the resource bar at the bottom center of the screen instead of the game's top left.");

        var game = new GameAdapter(Logger.LogWarning, Logger.LogInfo, gameObject.AddComponent<InputLock>());
        server = new McpServer(game, Tools.Create(game, Logger.LogError), Port.Value, Logger.LogError);
        Listen(server.Start, Port.Value);

        Patching.Configure(Logger.LogWarning);
        gameObject.AddComponent<UnlockToastOverlay>().Install(this);
        gameObject.AddComponent<ResourceBarPlacement>().Install(this);
        McpOptions.Install(this);
    }

    /// <summary>Interrupts the Tool in progress and listens on the configured port again.</summary>
    public void RestartServer()
    {
        Logger.LogInfo("restarting the MCP server");
        Listen(() => server!.Restart(Port.Value), Port.Value);
    }

    void Listen(Action start, int on)
    {
        try
        {
            start();
            ListeningPort = on;
            ServerError = null;
            Logger.LogInfo($"MCP server listening on http://127.0.0.1:{on}/");
        }
        catch (Exception e)
        {
            ServerError = e.Message;
            Logger.LogError($"MCP disabled: cannot listen on 127.0.0.1:{on} ({e.Message}). Change the port in the game's MCP options and restart the server.");
        }
    }

    void Start() => InputLock.WarnAboutF10Bindings(Logger.LogWarning);

    void Update() => MainThread.Drain();

    void OnDestroy() => server?.Dispose();
}
