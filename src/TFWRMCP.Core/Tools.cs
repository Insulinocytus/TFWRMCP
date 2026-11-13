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
            "with its level and the cost of the next level (null at max level), world size, drone positions, " +
            "whether a Run is in progress (run.status), and the grid as tiles[y][x] (x grows east, y grows " +
            "north). Each tile has the ground type, plus the entity type unless the tile is empty and the " +
            "depth unless it is 0.",
            NoArguments(),
            async _ => await game.GetState(),
            Capabilities.State),
    };
}
