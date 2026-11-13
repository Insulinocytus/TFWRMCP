using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>The Tool catalogue; each ticket adds its Tools here.</summary>
public static class Tools
{
    static JObject NoArguments() => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject(),
        ["additionalProperties"] = false,
    };

    public static IReadOnlyList<Tool> Create(IGame game) => new[]
    {
        new Tool(
            "get_state",
            "Returns a snapshot of the game as the player sees it: current Save name, inventory, every Unlock " +
            "with its level and the cost of the next level, world size, drone positions, the entity and ground " +
            "type of every tile, and whether a Run is in progress.",
            NoArguments(),
            async _ => await game.GetState(),
            Capabilities.State),
    };
}
