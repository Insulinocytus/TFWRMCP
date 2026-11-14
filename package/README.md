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

The server only listens on localhost. Its settings are in the game's options menu, on the **MCP** tab (also stored in
`BepInEx/config/com.insulinocytus.tfwrmcp.cfg`):

- **Port**: saved right away, used after the next restart.
- **MCP server**: shows whether the server is listening; **Restart** listens again on the configured port. A tool
  call in progress is dropped and the Input Lock released, but a Run keeps going. If the port is taken, the server
  stays stopped and the reason shows here; the game itself runs normally.
- **Don't open docs after an unlock**: stops the game from opening an Unlock's documentation when it is bought. On by default.
- **Don't open Getting Started on a new save**: stops the game from opening the Getting Started docs when a Save that
  has never been saved is loaded. On by default.
- **Resource bar at bottom center**: anchors the resource bar at the bottom center of the screen and lets it grow
  from the middle; switching it off puts the bar back at the game's top left right away. On by default.
- **Auto-arrange code windows**: whenever a Run starts or ends, a Script joins the Run for the first time, a Script is
  created, deleted or renamed (by you or an agent), the farm changes size, or a window's line count changes (after you
  stop typing for half a second), the code windows line up beside the farm: running and focused ones open at their
  content size in the left column, the rest fold in the right column, each sorted by name, and glide into place.
  Windows docked together move as one unit sorted by their top window (a group holding a documentation window is left
  alone). Windows you moved by hand are overwritten at the next trigger; switching it off leaves them where they are.
  On by default.
- **Auto-center camera on farm**: keeps the farm at the center of the screen and zooms so that the farm and every code
  window fit (the resource bar's height is left out), within the game's own zoom limits. When the farm changes size
  (an expand, `set_world_size()`, a Run ending) or a `simulate()` / Leaderboard Run farm comes or goes, or the windows
  move, the camera glides to the new view over a second. While it is on it holds the camera; switching it off leaves
  the camera where it is. On by default.

## Tools

`get_state` returns the Save name, inventory, Unlock levels, next costs and prerequisites (`parent`, `also_requires`),
world size, drone positions and the hat each drone wears (spelled as in `Hats`, e.g. `Straw_Hat`), every tile as
`tiles[y][x]` (`tiles[0][0]` is (0, 0); y grows north, x grows east) with its ground, entity, whether it can be
harvested, water level and growth progress, `entity_counts` (entity type → number of tiles holding it, so leftovers
such as hedges are visible at a glance), and the Run status. It is large; pass `fields` (for example
`["inventory","run"]`) to get only those top-level fields, and `unlocks` (for example `["loops","speed"]`) to get only
those Unlocks; an unknown Unlock name is an error, and so is `unlocks` with `fields` that leave out `unlocks`.

`run_script` and `wait_run` end when the Run does, or after `timeout_seconds` (default 20, at most 600; `0` returns
right after the Run has started). The call blocks that long, so keep it below your MCP client's request timeout (30
seconds in many clients) and call `wait_run` again to keep waiting. Pass `until_inventory` (for example
`{"carrot": 600}`) to also end as soon as the inventory holds at least that much of every item; the result then has
`inventory` and `met`. With `stop_when_met` the Run is stopped at that moment, so "run until carrot ≥ 600, then stop"
is a single call. `run_script` also takes `stop_after_seconds`: the Run is stopped once the game clock (the one
`get_time()` reads) has advanced that far, even after the call has returned `running`; with `until_inventory` and
`stop_when_met`, whichever comes first stops it. To measure production, call
`run_script(stop_after_seconds=60, timeout_seconds=0)`, then `wait_run` until the Run has ended and read `rates`.

`write_script` refuses to overwrite a Script that is being run. Pass `stop_current: true` to first stop the Run in
progress (returned as `stopped`) and `run: true` to run the Script right after writing it (returned as `run`, taking
the same `timeout_seconds`, `until_inventory` and `stop_when_met` as `run_script`), so "stop, overwrite, rerun" is a
single call. `run` while a Run is in progress without `stop_current` fails and writes nothing.

Every Run (from `run_script`, `wait_run`, `stop_run` and `get_state`'s `run`) carries a summary so a script that spins
without progress shows: `inventory_delta` (net change per item since the Run began, non-zero items only, covering
everything: script actions, the game's own production and consumption, Unlocks bought), `failed_calls` (per function,
how often `plant`, `harvest`, `move`, `swap`, `use_item`, `place`, `dig`, `push`, `unlock` or `hover` returned
`False`; `harvest()` on an unripe entity counts too, `till()` is not counted), `ticks` (game steps), `seconds`,
`game_seconds` (on the game clock) and `rates` (`inventory_delta` per game second, absent when `game_seconds` is 0).
Actions on the temporary farm of `simulate()` and leaderboard runs do not count.

`list_saves`, `create_save`, `load_save` and `save_game` manage Saves. The title menu is closed automatically before
any tool runs, and tools called while the scene reloads wait until the game is playable again.

`list_builtins` tells which functions, keywords and members (Entities, Items, Grounds, Hats) a script can use right
now, and for every other name which Unlocks grant it and at what level (for example `for` needs `expand` level 2).

`unlock` buys one level of an Unlock, or the Unlocks in `names` in order (it stops at the first failure and keeps
earlier purchases). `plan_unlock` lists, without buying, every purchase needed to reach an Unlock level (parents and
required levels first), their total cost and the items you still lack.

`list_docs` lists the pages of the game's docs window; pass `include_locked: true` to also list pages for content
not unlocked yet, including `unlocks/<name>` for every locked Unlock (marked `locked: true`; `read_doc` shows them as
the game's "you need to unlock X" page). `read_doc` reads
a page. Page ids come in two kinds: `unlocks/<name>`, `functions/<name>`, `items/<name>`, `objects/<name>` and
`grounds/<name>` are generated from the game's texts (`unlocks/<name>` shows the effect and the next level's cost),
while `docs/....md` are tutorial pages whose names need not match an Unlock (the tutorial for `loops` is
`docs/scripting/while.md`). Take ids from `list_docs` rather than building them.

## Input Lock

While a tool call runs, the game ignores your mouse, keyboard, hotkeys and scroll wheel, and a lock icon shows in
the bottom-right corner (hover it to see the running tool). Input comes back as soon as the call returns, even if a
Run is still going. Press **F10** during the lock to take control back: the Run is stopped (`stopped_by_user`) and the
tool call returns "Interrupted by the user". F10 is reserved for this; if one of your key bindings uses it, the mod
logs a warning at startup.

## Unlock notices

Every Unlock bought on your farm (by a tool, a script's `unlock()` or the tech tree) shows a notice in the
bottom-left corner for a few seconds. Leaderboard runs and `simulate()` do not. The notices hide with the game's
"hide UI" hotkey.
