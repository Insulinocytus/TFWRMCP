#!/usr/bin/env python3
"""Smoke test against the real game (stdlib only).

Launches the game through Steam unless MCP already answers, waits for the port, records the current Save, creates
a test Save, runs every check on it and finally loads the original Save back (even if checks fail). Prints PASS/FAIL
per step. Exit code is 0 only if all steps pass. Test Saves (smoke-<timestamp>) stay in the game's Saves folder.

Usage: python scripts/smoke.py [--port 47821] [--startup-timeout 240] [--no-launch]

Later tickets append checks to CHECKS; they run on the test Save, so checks may modify the Save freely.
"""
import argparse
import json
import os
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
    for target in (original, test):
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


CHECKS = [check_handshake, check_tools_list, check_get_state, check_saves, check_unlock_failures, check_docs]


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
        is_error, result = mcp.call("create_save", name=mcp.test_save)
        assert not is_error and result == {"save": mcp.test_save}, result
        is_error, state = mcp.call("get_state")
        assert not is_error and state["save"] == mcp.test_save, state
        print(f"    original Save={mcp.original_save} test Save={mcp.test_save}")

    def restore_original_save(mcp):
        is_error, result = mcp.call("load_save", name=mcp.original_save)
        assert not is_error, result
        is_error, state = mcp.call("get_state")
        assert not is_error and state["save"] == mcp.original_save, state

    if step(setup_test_save):
        for check in CHECKS:
            step(check)
    if hasattr(mcp, "original_save"):
        step(restore_original_save)
    print(f"{total - failed}/{total} steps passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
