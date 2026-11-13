#!/usr/bin/env python3
"""Smoke test against the real game (stdlib only).

Launches the game through Steam unless MCP already answers, waits for the port, records the current Save, creates
two throwaway Saves (smoke-<timestamp> and smoke-<timestamp>-other), runs every check on them, loads the original
Save back at the very end (even if checks fail) and deletes the throwaway Saves. The original Save is never loaded
mid-run; its Unlocks are read from its save.json. Prints PASS/FAIL per step. Exit code is 0 only if all steps pass.

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


def check_grant_unlocks(mcp):
    # A fresh Save lacks print/range/simulate/leaderboards. Give the throwaway test Save the original Save's Unlocks
    # (plus the Leaderboard) on disk. The original Save is only read, never loaded. A Save's file is rewritten when
    # the game leaves it, so edit the test Save while the other throwaway Save is loaded, then load it back.
    assert os.path.isdir(SAVES_DIR), f"Saves directory not found: {SAVES_DIR}"
    ok(mcp, "load_save", name=mcp.other_save)
    with open(os.path.join(SAVES_DIR, mcp.original_save, "save.json"), encoding="utf-8") as f:
        unlocks = json.load(f)["unlocks"]
    path = os.path.join(SAVES_DIR, mcp.test_save, "save.json")
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    data["unlocks"] = sorted(set(unlocks) | set(data["unlocks"]) | {"simulation", "leaderboard"})
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f)
    ok(mcp, "load_save", name=mcp.test_save)


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

    for page in pages:  # everything listed is unlocked, so it reads as a real page, not the placeholder
        is_error, doc = mcp.call("read_doc", page=page)
        assert not is_error and doc["page"] == page and doc["content"].strip(), (page, doc)
        assert "(tech_tree)" not in doc["content"] and "<noparse>" not in doc["content"], page

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


# Unlock/docs checks need the fresh test Save, so they run before check_grant_unlocks hands it the original Save's Unlocks.
CHECKS = [
    check_handshake, check_tools_list, check_get_state, check_saves, check_unlock_failures, check_docs,
    check_scripts, check_grant_unlocks, check_run_finishes, check_run_timeout_wait_stop, check_run_parse_error,
    check_run_runtime_error, check_run_output_buffer, check_leaderboard_run, check_get_state_fields, check_until_inventory,
    check_tile_fields, check_save_switch_ends_run,
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
