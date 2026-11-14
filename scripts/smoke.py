#!/usr/bin/env python3
"""Smoke test against the real game (stdlib only).

Launches the game through Steam unless MCP already answers, waits for the port, records the current Save, creates
two throwaway Saves (smoke-<timestamp> and smoke-<timestamp>-other), runs every check on them, loads the original
Save back at the very end (even if checks fail) and deletes the throwaway Saves. The original Save is never loaded
mid-run and its Unlocks do not matter. Prints PASS/FAIL per step. Exit code is 0 only if all steps pass.

Usage: python scripts/smoke.py [--port 47821] [--startup-timeout 240] [--no-launch]

Later tickets append checks to CHECKS; they run on the test Save, so checks may modify the Save freely.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

STEAM_URL = "steam://rungameid/2060160"  # launching the exe directly leaves Steam uninitialised


class Mcp:
    def __init__(self, port):
        self.url = f"http://127.0.0.1:{port}/"
        self.next_id = 1

    def post(self, message, timeout=120):
        req = urllib.request.Request(
            self.url, json.dumps(message).encode(), {"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read()
            return resp.status, (json.loads(body) if body else None)

    def rpc(self, method, params=None):
        message = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        self.next_id += 1
        if params is not None:
            message["params"] = params
        _, reply = self.post(message)
        if "error" in reply:
            raise AssertionError(f"{method} returned JSON-RPC error {reply['error']}")
        return reply["result"]

    def call(self, tool, **arguments):
        """Calls a tool; returns (is_error, parsed JSON or raw text)."""
        result = self.rpc("tools/call", {"name": tool, "arguments": arguments})
        text = result["content"][0]["text"]
        try:
            return result["isError"], json.loads(text)
        except json.JSONDecodeError:
            return result["isError"], text


def is_up(mcp):
    try:
        mcp.post({"jsonrpc": "2.0", "id": 0, "method": "ping"}, timeout=5)
        return True
    except (urllib.error.URLError, OSError):
        return False


def ensure_game(mcp, launch, timeout):
    if is_up(mcp):
        print("game already running, MCP is up")
        return
    if not launch:
        raise SystemExit("MCP is not reachable and --no-launch was given")
    print(f"launching game via {STEAM_URL}")
    if sys.platform == "win32":
        os.startfile(STEAM_URL)
    else:
        subprocess.Popen(["xdg-open", STEAM_URL])
    deadline = time.time() + timeout
    while time.time() < deadline:
        if is_up(mcp):
            print("MCP is up")
            return
        time.sleep(2)
    raise SystemExit(f"MCP did not come up within {timeout}s")


def check_handshake(mcp):
    info = mcp.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
    assert info["serverInfo"]["name"] == "tfwrmcp", info
    status, _ = mcp.post({"jsonrpc": "2.0", "method": "notifications/initialized"})
    assert status == 202, status
    assert mcp.rpc("ping") == {}


def check_tools_list(mcp):
    tools = {t["name"]: t for t in mcp.rpc("tools/list")["tools"]}
    assert "get_state" in tools, list(tools)
    assert tools["get_state"]["description"] and tools["get_state"]["inputSchema"]["type"] == "object"


def check_saves(mcp):
    test, original = mcp.test_save, mcp.original_save
    is_error, saves = mcp.call("list_saves")
    assert not is_error and saves["current"] == test and test in saves["saves"] and original in saves["saves"], saves

    for bad in ("bad/name", "CON", "", test, test.upper()):
        is_error, text = mcp.call("create_save", name=bad)
        assert is_error, f"create_save accepted {bad!r}"
        assert "Invalid Save name" in text or "already exists" in text, text
    is_error, text = mcp.call("load_save", name="no-such-save-" + test)
    assert is_error and test in text, text

    is_error, result = mcp.call("save_game")
    assert not is_error and result == {"saved": True}, result

    # Each switch reloads the scene; the call must only return once the other Save is loaded and playable.
    for target in (mcp.other_save, test):
        is_error, result = mcp.call("load_save", name=target)
        assert not is_error and result == {"save": target}, result
        is_error, state = mcp.call("get_state")
        assert not is_error and state["save"] == target, state
        assert len(mcp.rpc("tools/list")["tools"]) >= 5, "tools/list broken after scene reload"
    is_error, saves = mcp.call("list_saves")
    assert not is_error and saves["current"] == test, saves


def check_no_getting_started(mcp):
    # The test Save was loaded without data, where the game opens Getting Started unless the Mod (on by default) stops it.
    ok(mcp, "save_game")
    with open(os.path.join(SAVES_DIR, mcp.test_save, "save.json"), encoding="utf-8") as f:
        pages = [p["value"] for p in json.load(f)["openDocPages"]]
    assert "docs/getting_started.md" not in pages, pages


def check_get_state(mcp):
    is_error, state = mcp.call("get_state")
    assert not is_error, state
    for key in ("save", "inventory", "unlocks", "world_size", "drones", "tiles", "run"):
        assert key in state, f"missing {key}"
    assert state["save"] and isinstance(state["inventory"], dict)
    size = state["world_size"]
    assert size["x"] > 0 and size["y"] > 0, size
    assert len(state["tiles"]) == size["y"] and all(len(row) == size["x"] for row in state["tiles"]), "tiles != world size"
    assert all(tile["ground"] for row in state["tiles"] for tile in row)
    assert state["drones"], "no drone"
    assert all(0 <= d["x"] < size["x"] and 0 <= d["y"] < size["y"] for d in state["drones"])
    assert state["unlocks"] and all({"name", "level", "max_level", "next_cost"} <= u.keys() for u in state["unlocks"])
    assert state["run"]["status"] in ("idle", "running"), state["run"]
    print(f"    save={state['save']} world={size['x']}x{size['y']} unlocks={len(state['unlocks'])} run={state['run']['status']}")


SAVES_DIR = os.path.join(
    os.path.expanduser("~"), "AppData", "LocalLow", "TheFarmerWasReplaced", "TheFarmerWasReplaced", "Saves"
)


def script_on_disk(mcp, name):
    return os.path.join(SAVES_DIR, mcp.test_save, name + ".py")


def ok(mcp, tool, **arguments):
    is_error, result = mcp.call(tool, **arguments)
    assert not is_error, f"{tool}{arguments} failed: {result}"
    return result


def fails(mcp, tool, expect, **arguments):
    is_error, result = mcp.call(tool, **arguments)
    assert is_error and expect in result, f"{tool}{arguments} should fail with {expect!r}: {result}"


def check_scripts(mcp):
    code = "print('hello')\n"
    assert ok(mcp, "write_script", name="smoke_a", code=code) == {"name": "smoke_a", "created": True}
    assert "smoke_a" in ok(mcp, "list_scripts")["scripts"]
    assert ok(mcp, "read_script", name="smoke_a")["code"] == code
    # The window shows the new code and the file is on disk right after the call returns.
    if os.path.isdir(SAVES_DIR):
        with open(script_on_disk(mcp, "smoke_a"), encoding="utf-8") as f:
            assert f.read() == code

    assert ok(mcp, "write_script", name="smoke_a", code="print('v2')\n")["created"] is False
    assert ok(mcp, "read_script", name="smoke_a")["code"] == "print('v2')\n"
    if os.path.isdir(SAVES_DIR):
        with open(script_on_disk(mcp, "smoke_a"), encoding="utf-8") as f:
            assert f.read() == "print('v2')\n"

    fails(mcp, "write_script", "Invalid Script name", name="bad/name", code="")
    fails(mcp, "write_script", "Invalid Script name", name="__builtins__", code="")
    fails(mcp, "write_script", "clashes", name="SMOKE_A", code="")  # same name, different case
    fails(mcp, "read_script", "No Script named", name="SMOKE_A")
    fails(mcp, "delete_script", "No Script named", name="nope")
    assert "SMOKE_A" not in ok(mcp, "list_scripts")["scripts"]

    ok(mcp, "delete_script", name="smoke_a")
    assert "smoke_a" not in ok(mcp, "list_scripts")["scripts"]
    if os.path.isdir(SAVES_DIR):
        assert not os.path.exists(script_on_disk(mcp, "smoke_a")), "deleted Script is still on disk"


def run_wait(mcp, name, code, timeout=60):
    ok(mcp, "write_script", name=name, code=code)
    return ok(mcp, "run_script", name=name, timeout_seconds=timeout)


def check_run_finishes(mcp):
    run = run_wait(mcp, "smoke_run", "for i in range(3):\n    print(i)\n")
    assert run["status"] == "finished" and run["script"] == "smoke_run", run
    assert run["output"] == ["0", "1", "2"], run
    state = ok(mcp, "get_state")
    assert state["run"]["status"] == "finished" and "output" not in state["run"], state["run"]


def check_run_timeout_wait_stop(mcp):
    run = run_wait(mcp, "smoke_loop", "while True:\n    do_a_flip()\n", timeout=1)
    assert run["status"] == "running", run
    assert ok(mcp, "get_state")["run"]["status"] == "running"
    assert ok(mcp, "wait_run", timeout_seconds=1)["status"] == "running"

    # A second Run is refused and the first keeps going.
    ok(mcp, "write_script", name="smoke_other", code="print('x')\n")
    fails(mcp, "run_script", "already in progress", name="smoke_other")
    assert ok(mcp, "wait_run", timeout_seconds=0.5)["status"] == "running"
    fails(mcp, "write_script", "being run", name="smoke_loop", code="pass\n")
    fails(mcp, "delete_script", "being run", name="smoke_loop")

    stopped = ok(mcp, "stop_run")
    assert stopped["status"] == "stopped_by_tool", stopped
    assert ok(mcp, "get_state")["run"]["status"] == "stopped_by_tool"
    assert ok(mcp, "stop_run")["status"] == "stopped_by_tool"  # nothing to stop: reports the latest Run


def check_run_parse_error(mcp):
    run = run_wait(mcp, "smoke_syntax", "print('a')\nx = = 1\n")
    assert run["status"] == "parse_error", run
    assert run["error"]["line"] == 2 and run["error"]["column"] >= 1 and run["error"]["message"], run
    assert ok(mcp, "get_state")["run"]["status"] == "parse_error"
    # No Run was started, so the game takes the next one.
    assert ok(mcp, "run_script", name="smoke_run", timeout_seconds=30)["status"] == "finished"


def check_run_runtime_error(mcp):
    run = run_wait(mcp, "smoke_error", "print('before')\nno_such_function()\nprint('after')\n")
    assert run["status"] == "error", run
    assert run["error"]["line"] == 2 and run["error"]["message"] and run["error"]["stack"], run
    assert run["output"][0] == "before" and "after" not in run["output"], run
    # The game must not sit paused on the error: the next Run starts and ends normally.
    assert ok(mcp, "run_script", name="smoke_run", timeout_seconds=30)["status"] == "finished"
    assert ok(mcp, "get_state")["run"]["status"] == "finished"


def check_run_output_buffer(mcp):
    run = run_wait(mcp, "smoke_many", "for i in range(1100):\n    quick_print(i)\n", timeout=120)
    assert run["status"] == "finished", run
    assert len(run["output"]) == 1000 and run["output"][-1] == "1099" and run["output_dropped"] == 100, (
        len(run["output"]), run["output"][-1:], run.get("output_dropped"))


def grant_unlocks(mcp, unlocks):
    # Edit the test Save's Unlocks on disk: save.json lists names, with "<name>_<level>" past level 1.
    # A Save's file is rewritten when the game leaves it, so edit the test Save while the other throwaway Save is
    # loaded, then load it back.
    assert os.path.isdir(SAVES_DIR), f"Saves directory not found: {SAVES_DIR}"
    ok(mcp, "load_save", name=mcp.other_save)
    path = os.path.join(SAVES_DIR, mcp.test_save, "save.json")
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    data["unlocks"] = sorted(set(unlocks) | set(data["unlocks"]))
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f)
    ok(mcp, "load_save", name=mcp.test_save)


def check_grant_unlocks(mcp):
    # Everything the later Scripts use, so they pass whatever Save the smoke started from.
    grant_unlocks(mcp, {
        "debug", "costs", "simulation", "leaderboard", "dictionaries", "unlocks", "loops", "true", "expand_2",
        "operators", "variables", "plant", "entities", "items", "senses", "watering",
    })


def check_list_builtins(mcp):
    def unlock_levels(locked, name):
        return {(g["unlock"], g["level"]) for g in locked.get(name, [])}

    builtins = ok(mcp, "list_builtins")
    available, locked = builtins["available"], builtins["locked"]
    assert set(available) == {"functions", "keywords", "entities", "items", "grounds", "hats"}, list(available)
    assert "harvest" in available["functions"] and "harvest" not in locked, builtins
    assert "pass" in available["keywords"] and "Grass" in available["entities"] and "Hay" in available["items"], available
    assert ("expand", 2) in unlock_levels(locked, "for") and "for" not in available["keywords"], locked.get("for")
    assert "speed" in {u for u, _ in unlock_levels(locked, "can_harvest")} and "can_harvest" not in available["functions"]
    assert "operators" in locked and not {"and", "or", "not", "%"} & (set(locked) | set(available["keywords"])), locked.get("operators")
    assert "Carrot" in locked and "Carrot" not in available["entities"], locked.get("Carrot")
    measure = {u for u, _ in unlock_levels(locked, "measure")}
    assert len(measure) > 1 and "mazes" in measure, measure
    everything = set(locked) | {n for names in available.values() for n in names}
    assert "get_drone_id" not in everything, "get_drone_id is not callable from a Script"
    assert all(g["level"] >= 1 for grants in locked.values() for g in grants) and all(locked.values())
    print(f"    {sum(len(v) for v in available.values())} available, {len(locked)} locked")

    grant_unlocks(mcp, {"speed"})
    after = ok(mcp, "list_builtins")
    assert "can_harvest" in after["available"]["functions"] and "can_harvest" not in after["locked"], after
    assert "if" in after["available"]["keywords"], after["available"]["keywords"]


def check_leaderboard_run(mcp):
    ok(mcp, "write_script", name="smoke_lb_target", code="pass\n")
    # simulate() is not a Leaderboard Run: the Run just finishes, with no result attached.
    sim = run_wait(mcp, "smoke_sim", "print(simulate('smoke_lb_target', {}, {}, {}, -1, 1000))\nprint('after simulate')\n")
    assert sim["status"] == "finished" and "leaderboard" not in sim and sim["output"][-1] == "after simulate", sim

    # The goal is unreachable for a Script that does nothing: the result still arrives, the Run is finished.
    run = run_wait(mcp, "smoke_lb", "leaderboard_run(Leaderboards.Hay, 'smoke_lb_target', 1000)\n", timeout=120)
    assert run["status"] == "finished", run
    result = run["leaderboard"]
    assert result["reached_goal"] is False and result["average_seconds"] >= 0 and "rank" in result, run
    print(f"    leaderboard result: {result}")
    # The result page was closed by the Mod: the farm is usable and the next Run starts at once.
    assert ok(mcp, "run_script", name="smoke_run", timeout_seconds=30)["status"] == "finished"

    # Cancelling a Leaderboard Run: stopped by the tool.
    ok(mcp, "write_script", name="smoke_lb_long", code="while True:\n    do_a_flip()\n")
    started = run_wait(mcp, "smoke_lb2", "leaderboard_run(Leaderboards.Hay, 'smoke_lb_long', 1)\n", timeout=3)
    assert started["status"] == "running", started
    stopped = ok(mcp, "stop_run")
    assert stopped["status"] == "stopped_by_tool" and stopped["leaderboard"]["reached_goal"] is False, stopped
    assert ok(mcp, "run_script", name="smoke_run", timeout_seconds=30)["status"] == "finished"


def check_get_state_fields(mcp):
    state = ok(mcp, "get_state", fields=["inventory", "run"])
    assert sorted(state) == ["inventory", "run"], list(state)
    assert ok(mcp, "get_state", fields=["tiles"]).keys() == {"tiles"}
    assert sorted(ok(mcp, "get_state", fields=["save", "world_size", "drones", "unlocks"])) == [
        "drones", "save", "unlocks", "world_size"]
    fails(mcp, "get_state", "world_size", fields=["inventory", "nope"])

    assert all(d["hat"] == "Straw_Hat" for d in ok(mcp, "get_state", fields=["drones"])["drones"])
    # Wear a hat the test Save does not own yet: grant the Unlock that gives it, then change_hat in a looping Run.
    locked = ok(mcp, "list_builtins")["locked"]
    hat, grant = next((h, g[0]) for h, g in locked.items() if h.endswith("_Hat") and g[0]["level"] == 1)
    grant_unlocks(mcp, {grant["unlock"]})
    run_wait(mcp, "smoke_hat", f"change_hat(Hats.{hat})\nwhile True:\n    do_a_flip()\n", timeout=1)
    drones = ok(mcp, "get_state", fields=["drones"])["drones"]
    ok(mcp, "stop_run")
    assert drones[0]["hat"] == hat, (hat, drones)
    print(f"    hat {hat} via {grant['unlock']}")

    unlocks = ok(mcp, "get_state", fields=["unlocks"], unlocks=["loops", "speed"])["unlocks"]
    assert sorted(u["name"] for u in unlocks) == ["loops", "speed"], unlocks
    assert ok(mcp, "get_state", fields=["entity_counts"]).keys() == {"entity_counts"}
    fails(mcp, "get_state", "'nope'", unlocks=["nope"])


def check_until_inventory(mcp):
    props = {t["name"]: t for t in mcp.rpc("tools/list")["tools"]}["run_script"]["inputSchema"]["properties"]
    assert {"until_inventory", "stop_when_met"} <= props.keys() and "0" in props["timeout_seconds"]["description"], props

    fails(mcp, "run_script", "Unknown item", name="smoke_run", until_inventory={"no_such_item": 1})
    assert ok(mcp, "get_state")["run"]["status"] != "running"

    # timeout_seconds 0 returns as soon as the Run started; an unreachable target is not met.
    ok(mcp, "write_script", name="smoke_until", code="while True:\n    do_a_flip()\n")
    run = ok(mcp, "run_script", name="smoke_until", timeout_seconds=0, until_inventory={"hay": 1e12})
    assert run["status"] == "running" and run["met"] is False and "hay" in run["inventory"], run
    # An already-held target is met at once; stop_when_met then stops the Run.
    run = ok(mcp, "wait_run", timeout_seconds=30, until_inventory={"hay": 0}, stop_when_met=True)
    assert run["status"] == "stopped_by_tool" and run["met"] is True, run
    # The Run is over: a target that is not met is reported at once, not at the timeout.
    started = time.time()
    run = ok(mcp, "wait_run", timeout_seconds=30, until_inventory={"hay": 1e12})
    assert run["status"] == "stopped_by_tool" and run["met"] is False and time.time() - started < 10, run

    # Run until hay grows, then stop, in one call.
    hay = ok(mcp, "get_state", fields=["inventory"])["inventory"]["hay"]
    ok(mcp, "write_script", name="smoke_hay", code="while True:\n    if can_harvest():\n        harvest()\n")
    run = ok(mcp, "run_script", name="smoke_hay", timeout_seconds=120, stop_when_met=True, until_inventory={"hay": hay + 5})
    assert run["status"] == "stopped_by_tool" and run["met"] is True and run["inventory"]["hay"] >= hay + 5, run
    assert ok(mcp, "get_state")["run"]["status"] == "stopped_by_tool"
    print(f"    hay {hay} -> {run['inventory']['hay']}")


def check_tile_fields(mcp):
    # Plant a bush under the drone, water it and leave the Script looping: the tile must show water and growth, then maturity.
    ok(mcp, "write_script", name="smoke_tile", code=(
        "while num_items(Items.Water) < 1:\n    do_a_flip()\n"
        "clear()\nplant(Entities.Bush)\nuse_item(Items.Water)\nwhile True:\n    do_a_flip()\n"))
    assert ok(mcp, "run_script", name="smoke_tile", timeout_seconds=1)["status"] == "running"

    def drone_tile():
        state = ok(mcp, "get_state", fields=["drones", "tiles"])
        drone = state["drones"][0]
        return state["tiles"][drone["y"]][drone["x"]]

    def wait_tile(done):
        deadline = time.time() + 90
        while time.time() < deadline:
            tile = drone_tile()
            if tile.get("entity") == "bush" and done(tile):
                return tile
            time.sleep(0.1)
        raise AssertionError(f"tile never reached the expected state: {drone_tile()}")

    young = wait_tile(lambda t: t.get("water", 0) > 0)
    assert 0 <= young["grown"] < 1 and "harvestable" not in young, young
    mature = wait_tile(lambda t: t.get("harvestable"))
    assert mature["harvestable"] is True and "grown" not in mature, mature
    state = ok(mcp, "get_state", fields=["tiles", "entity_counts"])
    bushes = sum(t.get("entity") == "bush" for row in state["tiles"] for t in row)
    assert bushes >= 1 and state["entity_counts"].get("bush") == bushes, (state["entity_counts"], bushes)
    ok(mcp, "stop_run")


def check_save_switch_ends_run(mcp):
    run_wait(mcp, "smoke_loop", "while True:\n    do_a_flip()\n", timeout=1)
    ok(mcp, "load_save", name=mcp.other_save)
    assert ok(mcp, "get_state")["run"]["status"] == "idle"
    ok(mcp, "load_save", name=mcp.test_save)
    assert ok(mcp, "get_state")["run"]["status"] == "idle"


def check_unlock_failures(mcp):
    # A fresh Save owns only the starting Unlocks and no items, so every purchase fails (build-specific tree).
    _, before = mcp.call("get_state")
    for name, reason in (
        ("no-such-unlock", "No Unlock named"),
        ("hats", "requires 'loops'"),
        ("loops", "Not enough items for 'loops' level 1 (hay: need 5, have 0)"),
    ):
        is_error, text = mcp.call("unlock", name=name)
        assert is_error and reason in text, (name, text)
    _, after = mcp.call("get_state")
    assert before["inventory"] == after["inventory"] and before["unlocks"] == after["unlocks"], "failed unlock changed the state"


def check_docs(mcp):
    is_error, listing = mcp.call("list_docs")
    assert not is_error, listing
    pages = [p["page"] for p in listing["pages"]]
    assert len(pages) == len(set(pages)) and all(p["title"] for p in listing["pages"]), listing
    assert "docs/getting_started.md" in pages and "docs/scripting/for.md" not in pages, pages
    assert not any("locked" in p for p in listing["pages"]), listing

    is_error, full = mcp.call("list_docs", include_locked=True)
    assert not is_error, full
    locked = {p["page"] for p in full["pages"] if p.get("locked")}
    assert {"unlocks/megafarm", "docs/unlocks/megafarm.md", "docs/scripting/for.md"} <= locked, locked
    assert set(pages) <= {p["page"] for p in full["pages"] if not p.get("locked")}, full

    # Locked pages show the game's "you need to unlock X" page, which links to the tech tree; the others are real pages.
    for p in full["pages"]:
        is_error, doc = mcp.call("read_doc", page=p["page"])
        assert not is_error and doc["page"] == p["page"] and doc["content"].strip(), (p, doc)
        assert ("(tech_tree)" in doc["content"]) == bool(p.get("locked")), p
        assert "<noparse>" not in doc["content"], p

    # Locked pages (file and generated) show the game's "unlock X first" page, which links to the tech tree.
    for page in ("docs/scripting/for.md", "functions/move", "objects/tree"):
        is_error, doc = mcp.call("read_doc", page=page)
        assert not is_error and "(tech_tree)" in doc["content"], (page, doc)

    for page in ("functions/harvest", "items/hay", "objects/grass", "grounds/grassland", "unlocks/grass"):
        is_error, doc = mcp.call("read_doc", page=page)
        assert not is_error and doc["content"].strip(), (page, doc)

    for bad in ("docs/nope.md", "functions/nope", "items/nope", "docs/../../x.md"):
        is_error, text = mcp.call("read_doc", page=bad)
        assert is_error and bad in text, (bad, text)
    print(f"    {len(pages)} pages listed")


def check_unlock_plan(mcp):
    state = ok(mcp, "get_state")
    unlocks = {u["name"]: u for u in state["unlocks"]}
    iron, loops = unlocks["iron"], unlocks["loops"]
    assert iron["parent"] == "coal" and iron["also_requires"] == {"name": "expand", "level": 3}, iron
    assert "parent" not in loops and "also_requires" not in loops, loops

    plan = ok(mcp, "plan_unlock", name="iron")
    owned = {name: u["level"] for name, u in unlocks.items()}
    first_costs = {}
    for step in plan["steps"]:
        u = unlocks[step["unlock"]]
        assert step["level"] == owned[u["name"]] + 1, step
        assert not u.get("parent") or owned[u["parent"]] > 0, ("parent not bought before", step)
        also = u.get("also_requires")
        assert not also or owned[also["name"]] >= also["level"], ("extra requirement not met before", step)
        owned[u["name"]] = step["level"]
        first_costs.setdefault(u["name"], step)
    assert plan["steps"][-1]["unlock"] == "iron" and owned["coal"] > 0 and owned["expand"] >= 3, plan
    assert all(s["cost"] == unlocks[n]["next_cost"] for n, s in first_costs.items()), (first_costs, plan)
    total = {}
    for step in plan["steps"]:
        for item, amount in step["cost"].items():
            total[item] = total.get(item, 0) + amount
    assert plan["total_cost"] == total, plan
    assert plan["missing"] == {i: n - state["inventory"].get(i, 0) for i, n in total.items() if n > state["inventory"].get(i, 0)}, plan
    assert ok(mcp, "plan_unlock", name="grass", level=1) == {"steps": [], "total_cost": {}, "missing": {}}

    fails(mcp, "plan_unlock", "No Unlock named", name="no-such-unlock")
    fails(mcp, "plan_unlock", "maximum level", name="iron", level=iron["max_level"] + 1)
    fails(mcp, "unlock", "exactly one", name="loops", names=["loops"])
    fails(mcp, "unlock", "exactly one")

    # No items on a fresh Save: the first purchase fails, the rest is not tried, nothing changes.
    results = ok(mcp, "unlock", names=["loops", "hats"])["results"]
    assert "Not enough items" in results[0]["error"] and results[0]["unlock"] == "loops", results
    assert results[1] == {"unlock": "hats", "skipped": True}, results
    assert ok(mcp, "get_state")["unlocks"] == state["unlocks"]


def check_unlock_batch_and_costs(mcp):
    state = ok(mcp, "get_state")
    unlocks = {u["name"]: u for u in state["unlocks"]}

    # Every planned expand level costs what the game's get_cost says (levels past its cost table are extrapolated).
    expand = unlocks["expand"]
    steps = ok(mcp, "plan_unlock", name="expand", level=expand["max_level"])["steps"]
    assert [s["level"] for s in steps] == list(range(expand["level"] + 1, expand["max_level"] + 1)), steps
    lines = [
        f"print(get_cost(Unlocks.Expand, {s['level'] - 1})[Items.{item.title()}])" for s in steps for item in s["cost"]
    ]
    run = run_wait(mcp, "smoke_get_cost", "\n".join(lines) + "\n")
    assert run["status"] == "finished", run
    expected = [amount for s in steps for amount in s["cost"].values()]
    assert [float(o) for o in run["output"]] == expected, (run["output"], expected)

    levels = {name: u["level"] for name, u in unlocks.items()}
    buy = next((u for u in state["unlocks"] if u["next_cost"] is not None
                and (not u.get("parent") or levels[u["parent"]] > 0)
                and (not u.get("also_requires") or levels[u["also_requires"]["name"]] >= u["also_requires"]["level"])
                and all(state["inventory"].get(i, 0) >= n for i, n in u["next_cost"].items())), None)
    if buy is None:
        print("    nothing affordable on the test Save; batch purchase not checked")
        return
    name = buy["name"]
    results = ok(mcp, "unlock", names=[name, "no-such-unlock", name])["results"]
    assert results[0] == {"unlock": name, "level": buy["level"] + 1}, results
    assert results[1]["unlock"] == "no-such-unlock" and "No Unlock named" in results[1]["error"], results
    assert results[2] == {"unlock": name, "skipped": True}, results
    after = {u["name"]: u["level"] for u in ok(mcp, "get_state")["unlocks"]}
    assert after[name] == buy["level"] + 1, (name, after[name])
    print(f"    batch bought {name} to level {after[name]}")


def check_run_summary(mcp):
    run = run_wait(mcp, "smoke_run", "for i in range(3):\n    print(i)\n")
    assert run["ticks"] > 0 and run["seconds"] >= 0 and run["failed_calls"] == {}, run
    assert all(v != 0 for v in run["inventory_delta"].values()), run
    assert ok(mcp, "get_state", fields=["run"])["run"]["ticks"] == run["ticks"]

    # Planting on an occupied tile returns False: every plant after the first fails.
    run = run_wait(mcp, "smoke_fail_plant", "for i in range(20):\n    plant(Entities.Bush)\n")
    assert run["status"] == "finished" and run["failed_calls"].get("plant", 0) > 0, run
    print(f"    failed_calls {run['failed_calls']}, inventory_delta {run['inventory_delta']}")

    # harvest() of an unripe plant returns True in the game, yet counts as a failed harvest.
    run = run_wait(mcp, "smoke_fail_harvest", "harvest()\nfor i in range(5):\n    plant(Entities.Bush)\n    harvest()\n")
    assert run["status"] == "finished" and run["failed_calls"].get("harvest", 0) > 0, run

    # Actions on simulate()'s temporary farm are not counted.
    ok(mcp, "write_script", name="smoke_sim_target", code="for i in range(20):\n    plant(Entities.Bush)\n    harvest()\n")
    run = run_wait(mcp, "smoke_sim_summary", "simulate('smoke_sim_target', Unlocks, {}, {}, -1, 1000)\n")
    assert run["status"] == "finished" and run["failed_calls"] == {}, run

    # Ticks and seconds stop growing once the Run has ended.
    again = ok(mcp, "wait_run")
    time.sleep(1)
    assert ok(mcp, "wait_run")["seconds"] == again["seconds"] and ok(mcp, "get_state", fields=["run"])["run"]["ticks"] == again["ticks"]

    # stop_after_seconds stops the Run on the game clock after the call has returned; rates divide the delta by it.
    ok(mcp, "write_script", name="smoke_timed", code="while True:\n    if can_harvest():\n        harvest()\n")
    assert ok(mcp, "run_script", name="smoke_timed", stop_after_seconds=3, timeout_seconds=0)["status"] == "running"
    run = ok(mcp, "wait_run", timeout_seconds=20)
    assert run["status"] == "stopped_by_tool" and 3 <= run["game_seconds"] < 3.5, run
    assert all(abs(run["rates"][k] - v / run["game_seconds"]) < 1e-9 for k, v in run["inventory_delta"].items()), run
    fails(mcp, "run_script", "stop_after_seconds", name="smoke_timed", stop_after_seconds=0)
    print(f"    game_seconds {run['game_seconds']}, rates {run['rates']}")


def check_get_state_during_simulate(mcp):
    # The real farm differs from simulate()'s temporary one (1x1, drone at (0,0)): a bush under the drone, the test
    # Save's Unlocks, the world grown by expand.
    assert run_wait(mcp, "smoke_real_bush", "clear()\nplant(Entities.Bush)\n")["status"] == "finished"

    def snapshot():
        state = ok(mcp, "get_state", fields=["inventory", "unlocks", "world_size", "drones", "tiles"])
        state["tiles"] = [[(t["ground"], t.get("entity")) for t in row] for row in state["tiles"]]
        return state

    before = snapshot()
    # Refused (maxed or unaffordable) outside simulate(), for the simulation during it: a side-effect-free probe.
    probe = next(u["name"] for u in before["unlocks"] if u["next_cost"] is None
                 or any(before["inventory"].get(i, 0) < n for i, n in u["next_cost"].items()))

    ok(mcp, "write_script", name="smoke_sim_forever", code="while True:\n    do_a_flip()\n")
    run = run_wait(mcp, "smoke_sim_state", "simulate('smoke_sim_forever', {}, {}, {}, 0, 1)\n", timeout=0)
    assert run["status"] == "running", run
    try:
        deadline = time.time() + 30
        while "simulate()" not in mcp.call("unlock", name=probe)[1]:
            assert time.time() < deadline, "simulate() never started"
            time.sleep(0.1)
        during = snapshot()
    finally:
        ok(mcp, "stop_run")
    for key in before:
        assert during[key] == before[key], f"get_state {key} during simulate() is not the real farm's"


# Unlock/docs checks need the fresh test Save, so they run before check_grant_unlocks hands it more Unlocks.
CHECKS = [
    check_handshake, check_tools_list, check_no_getting_started, check_get_state, check_saves, check_unlock_failures,
    check_unlock_plan, check_docs,
    check_scripts, check_list_builtins, check_grant_unlocks, check_unlock_batch_and_costs, check_run_finishes,
    check_run_timeout_wait_stop, check_run_parse_error, check_run_runtime_error, check_run_output_buffer,
    check_leaderboard_run, check_get_state_fields, check_until_inventory, check_tile_fields, check_run_summary,
    check_get_state_during_simulate, check_save_switch_ends_run,
]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=47821)
    parser.add_argument("--startup-timeout", type=int, default=240)
    parser.add_argument("--no-launch", action="store_true")
    args = parser.parse_args()

    mcp = Mcp(args.port)
    ensure_game(mcp, not args.no_launch, args.startup_timeout)

    failed = 0
    total = 0

    def step(fn):
        nonlocal failed, total
        total += 1
        try:
            fn(mcp)
            print(f"PASS {fn.__name__}")
            return True
        except Exception as e:  # report and continue with the remaining steps
            failed += 1
            print(f"FAIL {fn.__name__}: {type(e).__name__}: {e}")
            return False

    def setup_test_save(mcp):
        is_error, saves = mcp.call("list_saves")  # also dismisses the title menu after a fresh start
        assert not is_error, saves
        mcp.original_save = saves["current"]
        mcp.test_save = time.strftime("smoke-%Y%m%d-%H%M%S")
        mcp.other_save = mcp.test_save + "-other"
        for name in (mcp.other_save, mcp.test_save):
            is_error, result = mcp.call("create_save", name=name)
            assert not is_error and result == {"save": name}, result
        is_error, state = mcp.call("get_state")
        assert not is_error and state["save"] == mcp.test_save, state
        print(f"    original Save={mcp.original_save} test Saves={mcp.test_save}, {mcp.other_save}")

    def restore_original_save(mcp):
        is_error, result = mcp.call("load_save", name=mcp.original_save)
        assert not is_error, result
        is_error, state = mcp.call("get_state")
        assert not is_error and state["save"] == mcp.original_save, state

    def delete_test_saves(mcp):  # the original Save is loaded by now, so these are inactive
        for name in (mcp.test_save, mcp.other_save):
            assert name.startswith("smoke-") and name != mcp.original_save, name
            shutil.rmtree(os.path.join(SAVES_DIR, name), ignore_errors=True)

    if step(setup_test_save):
        for check in CHECKS:
            step(check)
    if hasattr(mcp, "original_save") and step(restore_original_save):
        step(delete_test_saves)
    print(f"{total - failed}/{total} steps passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
