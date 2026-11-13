#!/usr/bin/env python3
"""Smoke test against the real game (stdlib only).

Launches the game through Steam unless MCP already answers, waits for the port, then runs every check against
the live Mod and prints PASS/FAIL per check. Exit code is 0 only if all checks pass.

Usage: python scripts/smoke.py [--port 47821] [--startup-timeout 240] [--no-launch]

Later tickets append checks to CHECKS; checks that modify the Save must run on the test Save.
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

    def post(self, message, timeout=30):
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


def check_get_state(mcp):
    is_error, state = mcp.call("get_state")
    assert not is_error, state
    for key in ("save", "inventory", "unlocks", "world_size", "drones", "tiles", "run"):
        assert key in state, f"missing {key}"
    assert state["save"] and isinstance(state["inventory"], dict)
    size = state["world_size"]
    assert size["x"] > 0 and size["y"] > 0, size
    assert len(state["tiles"]) == size["x"] * size["y"], "tile count != world size"
    assert all("x" in t and "y" in t and t["ground"] for t in state["tiles"])
    assert state["drones"], "no drone"
    assert all(0 <= d["x"] < size["x"] and 0 <= d["y"] < size["y"] for d in state["drones"])
    assert state["unlocks"] and all({"name", "level", "max_level", "next_cost"} <= u.keys() for u in state["unlocks"])
    assert state["run"]["status"] in ("idle", "running"), state["run"]
    print(f"    save={state['save']} world={size['x']}x{size['y']} unlocks={len(state['unlocks'])} run={state['run']['status']}")


CHECKS = [check_handshake, check_tools_list, check_get_state]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=47821)
    parser.add_argument("--startup-timeout", type=int, default=240)
    parser.add_argument("--no-launch", action="store_true")
    args = parser.parse_args()

    mcp = Mcp(args.port)
    ensure_game(mcp, not args.no_launch, args.startup_timeout)

    failed = 0
    for check in CHECKS:
        try:
            check(mcp)
            print(f"PASS {check.__name__}")
        except Exception as e:  # report and continue with the remaining checks
            failed += 1
            print(f"FAIL {check.__name__}: {type(e).__name__}: {e}")
    print(f"{len(CHECKS) - failed}/{len(CHECKS)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
