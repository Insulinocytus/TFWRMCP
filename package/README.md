# TFWRMCP

A BepInEx 5 mod for The Farmer Was Replaced that runs an MCP server inside the game, so an AI agent can play it
the way a human does: write a script, run it, read the output and state, improve the script.

## Install

Use r2modman (import the zip as a local mod), or copy the `TFWRMCP` folder into `BepInEx/plugins` of a
BepInEx 5.4.23.x install.

## Connect

Point any MCP client (Streamable HTTP) at `http://127.0.0.1:47821`, for example:

```
claude mcp add --transport http tfwr http://127.0.0.1:47821
```

The port can be changed in `BepInEx/config/com.insulinocytus.tfwrmcp.cfg`. If it is already taken, the mod logs an
error and leaves MCP disabled; the game itself runs normally. The server only listens on localhost.

## Tools

`get_state` returns the Save name, inventory, Unlock levels and next costs, world size, drone positions, every
tile's entity and ground, and the Run status.

`list_saves`, `create_save`, `load_save` and `save_game` manage Saves. The title menu is closed automatically before
any tool runs, and tools called while the scene reloads wait until the game is playable again.

## Input Lock

While a tool call runs, the game ignores your mouse, keyboard, hotkeys and scroll wheel, and a lock icon shows in
the bottom-right corner (hover it to see the running tool). Input comes back as soon as the call returns, even if a
Run is still going. Press **F10** during the lock to take control back: the Run is stopped (`stopped_by_user`) and the
tool call returns "Interrupted by the user". F10 is reserved for this; if one of your key bindings uses it, the mod
logs a warning at startup.
