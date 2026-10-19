#!/usr/bin/env python3
"""Benchmark-only MCP stdio proxy exposing a fixed read-only tool allowlist."""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

ALLOWED_TOOLS = (
    "api_lookup",
    "api_search",
    "api_type",
    "api_decompile",
    "api_assemblies",
    "catalog_search",
    "catalog_read",
    "game_guide",
)
ALLOWED_TOOL_SET = frozenset(ALLOWED_TOOLS)
MAX_JSON_LINE = 32 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 120
CHILD_SHUTDOWN_SECONDS = 5


def write_message(message: dict[str, Any]) -> None:
    sys.stdout.buffer.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
    sys.stdout.buffer.flush()


def json_error(request_id: Any, code: int, message: str) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id,
            "error": {"code": code, "message": message}}


async def read_client_line() -> bytes:
    return await asyncio.to_thread(sys.stdin.buffer.readline, MAX_JSON_LINE + 1)


async def discard_client_line_remainder(line: bytes) -> None:
    while line and not line.endswith(b"\n"):
        line = await asyncio.to_thread(sys.stdin.buffer.readline, MAX_JSON_LINE + 1)


async def drain_child_stderr(stream: asyncio.StreamReader) -> None:
    # Drain but do not relay arbitrary host logs, which may contain local paths or exception text.
    while await stream.read(4096):
        pass


async def stop_child(process: asyncio.subprocess.Process | None,
                     stderr_task: asyncio.Task[None] | None) -> None:
    if process is not None:
        if process.stdin is not None:
            process.stdin.close()
        try:
            await asyncio.wait_for(process.wait(), timeout=CHILD_SHUTDOWN_SECONDS)
        except asyncio.TimeoutError:
            process.kill()
            await process.wait()
    if stderr_task is not None:
        try:
            await asyncio.wait_for(stderr_task, timeout=1)
        except asyncio.TimeoutError:
            stderr_task.cancel()


async def forward_request(process: asyncio.subprocess.Process,
                          request: dict[str, Any]) -> dict[str, Any]:
    if process.stdin is None or process.stdout is None:
        raise RuntimeError("MCP backend streams are unavailable")
    line = json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
    async def exchange() -> dict[str, Any]:
        process.stdin.write(line)
        await process.stdin.drain()
        response_line = await process.stdout.readline()
        if not response_line:
            raise RuntimeError("MCP backend exited before returning a response")
        response = json.loads(response_line)
        if not isinstance(response, dict) or response.get("id") != request.get("id"):
            raise RuntimeError("MCP backend response ID mismatch")
        return response

    try:
        return await asyncio.wait_for(exchange(), timeout=REQUEST_TIMEOUT_SECONDS)
    except asyncio.TimeoutError as error:
        raise TimeoutError("MCP backend request exceeded the benchmark proxy timeout") from error


async def run_proxy(host: Path | None, smoke_backend: bool) -> int:
    process: asyncio.subprocess.Process | None = None
    stderr_task: asyncio.Task[None] | None = None

    async def ensure_backend() -> asyncio.subprocess.Process:
        nonlocal process, stderr_task
        if process is not None:
            return process
        if smoke_backend:
            command = [sys.executable, str(Path(__file__).resolve()), "--fake-backend"]
        else:
            if host is None or not host.is_file():
                raise RuntimeError("MCP host assembly does not exist")
            command = ["dotnet", str(host.resolve())]
        process = await asyncio.create_subprocess_exec(
            *command,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            limit=MAX_JSON_LINE,
        )
        if process.stderr is not None:
            stderr_task = asyncio.create_task(drain_child_stderr(process.stderr))
        return process

    try:
        while line := await read_client_line():
            if len(line) > MAX_JSON_LINE:
                await discard_client_line_remainder(line)
                write_message(json_error(None, -32700, "MCP request exceeds the proxy line limit"))
                continue
            try:
                message = json.loads(line)
            except (UnicodeDecodeError, json.JSONDecodeError):
                write_message(json_error(None, -32700, "Invalid JSON-RPC request"))
                continue
            if not isinstance(message, dict) or message.get("jsonrpc") != "2.0" or \
                    not isinstance(message.get("method"), str):
                write_message(json_error(message.get("id") if isinstance(message, dict) else None,
                                         -32600, "Invalid JSON-RPC request"))
                continue

            method = message["method"]
            has_id = "id" in message
            request_id = message.get("id")
            if not has_id:
                if method == "notifications/initialized" and process is not None and process.stdin is not None:
                    process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
                    await process.stdin.drain()
                # Unknown notifications are discarded and can never trigger backend work.
                continue

            if method == "tools/call":
                params = message.get("params")
                name = params.get("name") if isinstance(params, dict) else None
                if name not in ALLOWED_TOOL_SET:
                    write_message(json_error(request_id, -32601,
                                             "Tool is not enabled by the benchmark read-only proxy"))
                    continue
            elif method not in {"initialize", "tools/list", "ping"}:
                write_message(json_error(request_id, -32601,
                                         "Method is not enabled by the benchmark read-only proxy"))
                continue

            try:
                backend = await ensure_backend()
                response = await forward_request(backend, message)
                if method == "tools/list" and "result" in response:
                    result = response.get("result")
                    tools = result.get("tools") if isinstance(result, dict) else None
                    if not isinstance(tools, list):
                        write_message(json_error(request_id, -32603,
                                                 "MCP backend returned an invalid tools/list result"))
                        continue
                    result["tools"] = [tool for tool in tools
                                       if isinstance(tool, dict) and tool.get("name") in ALLOWED_TOOL_SET]
                    result.pop("nextCursor", None)
                write_message(response)
            except Exception:
                write_message(json_error(request_id, -32603,
                                         "Read-only MCP backend request failed"))
    finally:
        await stop_child(process, stderr_task)
    return 0


def fake_backend() -> int:
    log_path = os.environ.get("THUNDERROAD_MCP_PROXY_SMOKE_LOG")
    if not log_path:
        return 2
    for line in sys.stdin.buffer:
        try:
            request = json.loads(line)
        except (UnicodeDecodeError, json.JSONDecodeError):
            continue
        if not isinstance(request, dict):
            continue
        with Path(log_path).open("a", encoding="utf-8") as log:
            log.write(json.dumps({"method": request.get("method"),
                                  "tool": request.get("params", {}).get("name")
                                  if isinstance(request.get("params"), dict) else None}) + "\n")
        if "id" not in request:
            continue
        method = request.get("method")
        if method == "initialize":
            result = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                      "serverInfo": {"name": "fixture", "version": "1"}}
        elif method == "tools/list":
            names = (*ALLOWED_TOOLS, "evaluate_csharp", "game_spawn_item")
            result = {"tools": [{"name": name, "inputSchema": {"type": "object"}}
                                for name in names], "nextCursor": "fixture-secret-cursor"}
        elif method == "tools/call":
            result = {"content": [{"type": "text", "text": "{}"}]}
        elif method == "ping":
            result = {}
        else:
            continue
        response = {"jsonrpc": "2.0", "id": request["id"], "result": result}
        sys.stdout.buffer.write(json.dumps(response, separators=(",", ":")).encode("utf-8") + b"\n")
        sys.stdout.buffer.flush()
    return 0


def smoke_test() -> int:
    with tempfile.TemporaryDirectory(prefix="thunderroad-mcp-proxy-smoke-") as temporary:
        root = Path(temporary)
        log_path = root / "forwarded.jsonl"
        env = os.environ.copy()
        env["THUNDERROAD_MCP_PROXY_SMOKE_LOG"] = str(log_path)
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
            {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
             "params": {"name": "evaluate_csharp", "arguments": {"code": "must_not_forward"}}},
            {"jsonrpc": "2.0", "id": 4, "method": "tools/call",
             "params": {"name": "game_spawn_item", "arguments": {"itemId": "must_not_forward"}}},
            {"jsonrpc": "2.0", "id": 5, "method": "tools/call",
             "params": {"name": "api_lookup", "arguments": {"query": "fixture"}}},
        ]
        command = [sys.executable, str(Path(__file__).resolve()), "--use-fake-backend"]
        completed = subprocess.run(command, input="".join(json.dumps(item) + "\n" for item in requests),
                                   text=True, capture_output=True, timeout=15, env=env, check=False)
        if completed.returncode != 0:
            raise RuntimeError("read-only MCP proxy subprocess failed its smoke run")
        responses = [json.loads(line) for line in completed.stdout.splitlines() if line]
        by_id = {response.get("id"): response for response in responses}
        listed = by_id.get(2, {}).get("result", {}).get("tools", [])
        names = [tool.get("name") for tool in listed]
        if names != list(ALLOWED_TOOLS):
            raise RuntimeError("read-only MCP proxy did not expose exactly the configured eight tools")
        if "nextCursor" in by_id.get(2, {}).get("result", {}):
            raise RuntimeError("read-only MCP proxy leaked the backend tools/list cursor")
        for request_id in (3, 4):
            if by_id.get(request_id, {}).get("error", {}).get("code") != -32601:
                raise RuntimeError("read-only MCP proxy did not reject a forbidden tool call")
        if "result" not in by_id.get(5, {}):
            raise RuntimeError("read-only MCP proxy did not forward an allowed tool call")
        forwarded = [json.loads(line) for line in log_path.read_text(encoding="utf-8").splitlines()]
        if any(item.get("tool") in {"evaluate_csharp", "game_spawn_item"} for item in forwarded):
            raise RuntimeError("read-only MCP proxy forwarded a forbidden tool call")
        print(json.dumps({"exposed_tools": len(names), "exposed_names": names,
                          "rejected_not_forwarded": ["evaluate_csharp", "game_spawn_item"],
                          "allowed_call_forwarded": "api_lookup"}, indent=2))
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, help="Host assembly to run behind the read-only allowlist")
    parser.add_argument("--smoke-test", action="store_true", help="Run an offline proxy-filtering self-check")
    parser.add_argument("--use-fake-backend", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--fake-backend", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    return args


def main() -> None:
    args = parse_args()
    if args.smoke_test:
        raise SystemExit(smoke_test())
    if args.fake_backend:
        raise SystemExit(fake_backend())
    if args.host is None and not args.use_fake_backend:
        raise SystemExit("--host is required")
    raise SystemExit(asyncio.run(run_proxy(args.host, smoke_backend=args.use_fake_backend)))


if __name__ == "__main__":
    main()
