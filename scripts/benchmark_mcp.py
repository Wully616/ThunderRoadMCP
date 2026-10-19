#!/usr/bin/env python3
"""Matched offline MCP subprocess benchmark for ThunderRoadMcp.

The optional OMP config output points at benchmark_mcp_readonly.py, a benchmark-only
stdio proxy that exposes a fixed read-only tool allowlist. OMP --tools is not a
per-MCP-tool filter; the proxy enforces the allowlist in tools/list and tools/call.
"""

from __future__ import annotations

import argparse
import asyncio
import ctypes
import hashlib
import json
import math
import os
import platform
import shutil
import subprocess
import tempfile
import sys
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

PROTOCOL_VERSION = "2025-06-18"
BASELINE_REVISION = "05381981d8ec9362b371a2226aa2b98166048808"
MAX_RESPONSE_LINE = 32 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 120
API_LOOKUP_TASKS = (
    ("spawn_item_near_player", "How do I spawn an item near the player?"),
    ("critter_trails_player", "How can I make a critter trail the player?"),
    ("damage_event_cleanup", "Which collision or damage event should a mod use, and how can it unsubscribe during cleanup?"),
    ("runtime_counter", "How can I safely inspect the current value of a named runtime counter?"),
    ("negative_spawn_async", "Does Creature.SpawnAsync exist in the installed API?"),
)


class McpSession:
    def __init__(self, host: Path, launch_config: Path, cache_dir: Path, diagnostics: bool) -> None:
        self.host = host.resolve()
        self.launch_config = launch_config.resolve()
        self.cache_dir = cache_dir.resolve()
        self.diagnostics = diagnostics
        self.process: asyncio.subprocess.Process | None = None
        self.stderr_task: asyncio.Task[None] | None = None
        self.stderr_diagnostics: asyncio.Queue[dict[str, Any]] = asyncio.Queue()
        self.request_id = 0
        self.startup_ms = 0.0
        self.tools: list[str] = []
        self.tool_schemas: list[dict[str, Any]] = []
        self.negotiated_protocol_version: str | None = None

    async def start(self) -> None:
        env = os.environ.copy()
        env["THUNDERROAD_MCP_LAUNCH_CONFIG"] = str(self.launch_config)
        env["THUNDERROAD_MCP_CACHE_DIR"] = str(self.cache_dir)
        env["THUNDERROAD_MCP_DIAGNOSTICS"] = "1" if self.diagnostics else "0"
        started = time.perf_counter_ns()
        self.process = await asyncio.create_subprocess_exec(
            "dotnet", str(self.host),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env,
            limit=MAX_RESPONSE_LINE,
        )
        self.stderr_task = asyncio.create_task(self._drain_stderr())
        initialized, _, _, _ = await self.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "ThunderRoadMcpBenchmark", "version": "1"},
        })
        self.negotiated_protocol_version = initialized.get("result", {}).get("protocolVersion")
        if not isinstance(self.negotiated_protocol_version, str):
            raise RuntimeError("MCP initialize response omitted negotiated protocolVersion")
        await self.notify("notifications/initialized", {})
        listed, _, _, _ = await self.request("tools/list", {})
        self.tool_schemas = listed.get("result", {}).get("tools", [])
        self.tools = [tool.get("name", "") for tool in self.tool_schemas]
        self.startup_ms = (time.perf_counter_ns() - started) / 1_000_000

    async def request(self, method: str, params: dict[str, Any]) -> tuple[dict[str, Any], float, int, int]:
        if self.process is None or self.process.stdin is None or self.process.stdout is None:
            raise RuntimeError("MCP process is not started")
        self.request_id += 1
        request_id = self.request_id
        line = json.dumps({
            "jsonrpc": "2.0", "id": request_id, "method": method, "params": params,
        }, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
        started = time.perf_counter_ns()
        self.process.stdin.write(line)
        await self.process.stdin.drain()
        try:
            response_line = await asyncio.wait_for(
                self.process.stdout.readline(), timeout=REQUEST_TIMEOUT_SECONDS)
        except asyncio.TimeoutError as error:
            raise TimeoutError(
                f"MCP request {method} exceeded the {REQUEST_TIMEOUT_SECONDS}s benchmark timeout") from error
        elapsed_ms = (time.perf_counter_ns() - started) / 1_000_000
        if not response_line:
            raise RuntimeError(f"MCP server exited during {method} (exit={self.process.returncode})")
        response = json.loads(response_line)
        if response.get("id") != request_id:
            raise RuntimeError(f"MCP response ID mismatch during {method}")
        if "error" in response:
            raise RuntimeError(f"MCP request failed: {method}")
        return response, elapsed_ms, len(line), len(response_line)

    async def notify(self, method: str, params: dict[str, Any]) -> None:
        if self.process is None or self.process.stdin is None:
            raise RuntimeError("MCP process is not started")
        line = json.dumps({"jsonrpc": "2.0", "method": method, "params": params},
                          separators=(",", ":")).encode("utf-8") + b"\n"
        self.process.stdin.write(line)
        await self.process.stdin.drain()

    async def tool(self, name: str, arguments: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
        response, elapsed_ms, request_bytes, response_bytes = await self.request(
            "tools/call", {"name": name, "arguments": arguments})
        result = response.get("result", {})
        if result.get("isError"):
            blocks = result.get("content", [])
            detail = blocks[0].get("text", "") if blocks else ""
            raise RuntimeError(f"MCP tool failed: {name}: {detail[:2000]}")
        blocks = result.get("content", [])
        text = blocks[0].get("text", "") if blocks else ""
        try:
            domain = json.loads(text)
        except (TypeError, json.JSONDecodeError):
            domain = None
        meta = result.get("_meta", {})
        timings = meta.get("timings", {}) if isinstance(meta, dict) else {}
        counts: dict[str, int] = {}
        gauges: dict[str, int] = {}
        diagnostic = None
        if self.diagnostics:
            try:
                diagnostic = await asyncio.wait_for(self.stderr_diagnostics.get(), timeout=2)
            except asyncio.TimeoutError:
                pass
        if isinstance(diagnostic, dict):
            timings = diagnostic.get("timings", timings)
            counts = diagnostic.get("counts", {})
            gauges = diagnostic.get("gauges", {})
        current_working_set, peak_working_set = process_memory(self.process.pid if self.process else None)
        sample = {
            "latency_ms": elapsed_ms,
            "request_wire_bytes": request_bytes,
            "response_wire_bytes": response_bytes,
            "wire_bytes": request_bytes + response_bytes,
            "domain_text_bytes": len(text.encode("utf-8")),
            "domain_text_chars": len(text.encode("utf-16-le")) // 2,
            "process_working_set_bytes": current_working_set,
            "process_peak_working_set_bytes": peak_working_set,
            "cache_disk_bytes": directory_size(self.cache_dir),
            "server_timings_ms": timings if isinstance(timings, dict) else {},
            "work_counters": counts if isinstance(counts, dict) else {},
            "cache_gauges_bytes": gauges if isinstance(gauges, dict) else {},
        }
        return domain if isinstance(domain, dict) else {}, sample

    async def _drain_stderr(self) -> None:
        if self.process is None or self.process.stderr is None:
            return
        while line := await self.process.stderr.readline():
            try:
                value = json.loads(line)
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
            if isinstance(value, dict) and isinstance(value.get("requestId"), str) and \
                    isinstance(value.get("timings"), dict) and isinstance(value.get("counts"), dict):
                await self.stderr_diagnostics.put(value)

    async def close(self) -> None:
        if self.process is None:
            return
        if self.process.stdin is not None:
            self.process.stdin.close()
        try:
            await asyncio.wait_for(self.process.wait(), timeout=5)
        except asyncio.TimeoutError:
            self.process.kill()
            await self.process.wait()
        if self.stderr_task is not None:
            try:
                await asyncio.wait_for(self.stderr_task, timeout=1)
            except asyncio.TimeoutError:
                self.stderr_task.cancel()

    async def __aenter__(self) -> "McpSession":
        await self.start()
        return self

    async def __aexit__(self, *_: object) -> None:
        await self.close()


def process_memory(pid: int | None) -> tuple[int | None, int | None]:
    if pid is None:
        return None, None
    if os.name == "nt":
        class Counters(ctypes.Structure):
            _fields_ = [
                ("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t),
            ]
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel32.OpenProcess.argtypes = (ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
        psapi.GetProcessMemoryInfo.argtypes = (ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong)
        psapi.GetProcessMemoryInfo.restype = ctypes.c_int
        handle = kernel32.OpenProcess(0x0410, False, pid)
        if not handle:
            return None, None
        try:
            counters = Counters()
            counters.cb = ctypes.sizeof(counters)
            if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                return None, None
            return int(counters.WorkingSetSize), int(counters.PeakWorkingSetSize)
        finally:
            kernel32.CloseHandle(handle)
    status = Path(f"/proc/{pid}/status")
    if status.exists():
        values = {}
        for line in status.read_text(errors="replace").splitlines():
            if line.startswith(("VmRSS:", "VmHWM:")):
                key, value, *_ = line.split()
                values[key] = int(value) * 1024
        return values.get("VmRSS:"), values.get("VmHWM:")
    return None, None


def directory_size(path: Path) -> int:
    if not path.exists():
        return 0
    size = 0
    for root, directories, files in os.walk(path):
        directories[:] = [name for name in directories if not (Path(root) / name).is_symlink()]
        for name in files:
            file = Path(root) / name
            try:
                if not file.is_symlink():
                    size += file.stat().st_size
            except OSError:
                pass
    return size


def percentile(values: list[float | int], p: float) -> float | int | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(p * len(ordered)) - 1)]


def summarize(samples: list[dict[str, Any]]) -> dict[str, Any]:
    numeric_fields = (
        "latency_ms", "time_to_first_lookup_ms", "request_wire_bytes", "response_wire_bytes", "wire_bytes",
        "domain_text_bytes", "domain_text_chars", "process_working_set_bytes", "process_peak_working_set_bytes",
        "cache_disk_bytes",
    )
    summary: dict[str, Any] = {"sample_count": len(samples)}
    for field in numeric_fields:
        values = [sample[field] for sample in samples if isinstance(sample.get(field), (int, float))]
        if values:
            summary[field] = {"p50": percentile(values, 0.50), "p95": percentile(values, 0.95)}
    for field in ("server_timings_ms", "work_counters", "cache_gauges_bytes"):
        names = sorted({name for sample in samples for name, value in sample.get(field, {}).items()
                        if isinstance(value, (int, float))})
        if names:
            summary[field] = {
                name: {"p50": percentile([sample[field][name] for sample in samples
                                           if isinstance(sample.get(field, {}).get(name), (int, float))], 0.50),
                       "p95": percentile([sample[field][name] for sample in samples
                                           if isinstance(sample.get(field, {}).get(name), (int, float))], 0.95)}
                for name in names
            }
    return summary


def domain(response: dict[str, Any], tool_name: str) -> dict[str, Any]:
    if not isinstance(response, dict):
        raise RuntimeError(f"Unexpected {tool_name} response")
    return response


def select_assembly(data: dict[str, Any], requested_name: str | None = None) -> dict[str, Any]:
    assemblies = data.get("assemblies", [])
    if not assemblies:
        raise RuntimeError("api_assemblies returned no matching assembly; check --assembly-name")
    if requested_name is None:
        return assemblies[0]
    return next((assembly for assembly in assemblies
                 if assembly.get("name", "").casefold() == requested_name.casefold()), assemblies[0])


def write_launch_config(path: Path, game_exe: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"executablePath": str(game_exe.resolve()), "arguments": []}), encoding="utf-8")


async def measured_tool(session: McpSession, name: str, arguments: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    data, sample = await session.tool(name, arguments)
    return domain(data, name), sample

def summarize_api_lookup(response: dict[str, Any], sample: dict[str, Any]) -> dict[str, Any]:
    result_field = next((name for name in ("results", "matches", "hits", "candidates")
                         if isinstance(response.get(name), list)), None)
    results = response.get(result_field, []) if result_field else []

    def strings(value: Any, limit: int = 16) -> list[str]:
        if not isinstance(value, list):
            return []
        return [item for item in value[:limit] if isinstance(item, str)]

    def identity(hit: Any) -> dict[str, Any] | None:
        if not isinstance(hit, dict):
            return None
        fields = ("assemblyName", "qualifiedName", "symbolId", "kind", "declaringType")
        item = {field: hit[field] for field in fields if isinstance(hit.get(field), str)}
        relevance = hit.get("relevance")
        if isinstance(relevance, dict):
            item["relevance"] = {
                field: relevance[field] for field in (
                    "score", "matchedTokens", "queryTokens", "coveragePercent",
                    "directMatch", "confidence", "exactName",
                ) if isinstance(relevance.get(field), (str, int, float, bool))
            }
        elif isinstance(hit.get("score"), (int, float)):
            item["score"] = hit["score"]
        context = hit.get("typeContext")
        if isinstance(context, dict):
            members = context.get("relevantMembers")
            members = members if isinstance(members, list) else []
            member_ids = [identity(member) for member in members[:8]]
            item["type_context"] = {
                "return_type_present": isinstance(context.get("returnType"), str),
                "parameter_count": len(context.get("parameters", []))
                if isinstance(context.get("parameters"), list) else 0,
                "related_type_names": strings(context.get("relatedTypeNames")),
                "related_type_count": len(context.get("relatedTypeNames", []))
                if isinstance(context.get("relatedTypeNames"), list) else 0,
                "related_type_id_count": len(context.get("relatedTypeIds", []))
                if isinstance(context.get("relatedTypeIds"), list) else 0,
                "unresolved_type_names": strings(context.get("unresolvedTypeNames")),
                "relevant_member_count": len(members),
                "relevant_member_identities": [item for item in member_ids if item is not None],
                "truncated": bool(context.get("contextTruncated", False)),
            }
        return item

    identities = [item for item in (identity(result) for result in results) if item is not None]
    related_types = response.get("relatedTypes")
    related_types = related_types if isinstance(related_types, list) else []
    related_summary = []
    for related in related_types[:20]:
        item = identity(related)
        if item is not None:
            related_to = related.get("relatedTo")
            item["related_to_count"] = len(related_to) if isinstance(related_to, list) else 0
            related_summary.append(item)

    catalog = response.get("catalog")
    catalog = catalog if isinstance(catalog, dict) else {}
    catalog_records = catalog.get("records")
    catalog_records = catalog_records if isinstance(catalog_records, list) else []
    catalog_summary = [
        {field: record[field] for field in ("entryId", "id", "recordType", "declaredApiType", "evidence")
         if isinstance(record, dict) and isinstance(record.get(field), (str, int, float, bool))}
        for record in catalog_records[:20]
    ]
    examples = response.get("examples")
    examples = examples if isinstance(examples, list) else []
    example_summary = []
    for example in examples[:8]:
        if not isinstance(example, dict):
            continue
        evidence_identity = example.get("evidenceIdentity")
        evidence_identity = evidence_identity if isinstance(evidence_identity, dict) else {}
        example_summary.append({
            "id": example.get("id") if isinstance(example.get("id"), str) else None,
            "evidence": example.get("evidence") if isinstance(example.get("evidence"), str) else None,
            "symbol_count": len(example.get("symbols", [])) if isinstance(example.get("symbols"), list) else 0,
            "resolved_symbol_count": len(evidence_identity.get("symbolIds", []))
            if isinstance(evidence_identity.get("symbolIds"), list) else 0,
        })
    missing = response.get("missingInformation", response.get("missing", []))
    if not isinstance(missing, list):
        missing = [missing] if isinstance(missing, str) else []
    logical_chars = response.get("logicalChars")
    logical_chars_source = "response.logicalChars"
    if not isinstance(logical_chars, int):
        logical_chars = sample.get("domain_text_chars")
        logical_chars_source = "first-text UTF-16 character count"
    return {
        "top_level_fields": sorted(response),
        "result_list_field": result_field,
        "found": response.get("found"),
        "total": response.get("total", len(results)),
        "result_count": len(results),
        "result_identities": identities[:20],
        "confidence": response.get("confidence"),
        "missing_information": [str(item)[:256] for item in missing[:16]],
        "complete": response.get("complete"),
        "truncated": response.get("truncated"),
        "has_more": response.get("hasMore"),
        "logical_chars": logical_chars,
        "logical_chars_source": logical_chars_source,
        "context": {
            "related_type_count": len(related_types),
            "related_type_identities": related_summary,
            "catalog_record_count": len(catalog_records),
            "catalog_complete": catalog.get("complete"),
            "catalog_records": catalog_summary,
            "example_count": len(examples),
            "examples": example_summary,
            "warning_count": len(response.get("warnings", []))
            if isinstance(response.get("warnings"), list) else 0,
        },
    }


async def profile_host(label: str, host: Path, game_exe: Path, root: Path, args: argparse.Namespace,
                       fixture_sources: tuple[Path, Path]) -> dict[str, Any]:
    host_root = root / label
    host_root.mkdir(parents=True, exist_ok=True)
    launch = host_root / "launch.json"
    write_launch_config(launch, game_exe)
    groups: dict[str, list[dict[str, Any]]] = {}
    startups: dict[str, list[float]] = {
        "cold_inventory": [], "persisted_restart_inventory": [],
        "cold_no_cache_first_api_lookup": [], "persisted_restart_first_api_lookup": [],
    }
    api_lookup_tasks: dict[str, Any] = {}
    first_lookup_task_id, first_lookup_query = API_LOOKUP_TASKS[0]
    first_lookup_observations: dict[str, list[dict[str, Any]]] = {
        "cold_no_cache": [], "process_restart_persisted": [],
    }

    for index in range(args.samples):
        cache = host_root / "cold" / str(index)
        async with McpSession(host, launch, cache, diagnostics=(label != "baseline")) as session:
            startups["cold_inventory"].append(session.startup_ms)
            _, sample = await measured_tool(session, "api_assemblies", {"name": args.assembly_name, "limit": 100})
            groups.setdefault("cold_no_cache_inventory", []).append(sample)

    persistent = host_root / "persisted-cache"
    async with McpSession(host, launch, persistent, diagnostics=(label != "baseline")) as seed:
        await measured_tool(seed, "api_assemblies", {"name": args.assembly_name, "limit": 100})
    for _ in range(args.samples):
        async with McpSession(host, launch, persistent, diagnostics=(label != "baseline")) as session:
            startups["persisted_restart_inventory"].append(session.startup_ms)
            _, sample = await measured_tool(session, "api_assemblies", {"name": args.assembly_name, "limit": 100})
            groups.setdefault("process_restart_persisted_inventory", []).append(sample)

    for index in range(args.samples):
        cache = host_root / "cold-first-api-lookup" / str(index)
        async with McpSession(host, launch, cache, diagnostics=(label != "baseline")) as session:
            startups["cold_no_cache_first_api_lookup"].append(session.startup_ms)
            response, sample = await measured_tool(
                session, "api_lookup", {"query": first_lookup_query})
            sample["time_to_first_lookup_ms"] = session.startup_ms + sample["latency_ms"]
            groups.setdefault("cold_no_cache_first_api_lookup", []).append(sample)
            first_lookup_observations["cold_no_cache"].append(summarize_api_lookup(response, sample))

    for index in range(args.samples):
        cache = host_root / "persisted-first-api-lookup" / str(index)
        async with McpSession(host, launch, cache, diagnostics=(label != "baseline")) as seed:
            await measured_tool(seed, "api_lookup", {"query": first_lookup_query})
        async with McpSession(host, launch, cache, diagnostics=(label != "baseline")) as session:
            startups["persisted_restart_first_api_lookup"].append(session.startup_ms)
            response, sample = await measured_tool(
                session, "api_lookup", {"query": first_lookup_query})
            sample["time_to_first_lookup_ms"] = session.startup_ms + sample["latency_ms"]
            groups.setdefault("process_restart_persisted_first_api_lookup", []).append(sample)
            first_lookup_observations["process_restart_persisted"].append(
                summarize_api_lookup(response, sample))

    warm_cache = host_root / "persisted-cache"
    async with McpSession(host, launch, warm_cache, diagnostics=(label != "baseline")) as session:
        inventory_data, _ = await measured_tool(session, "api_assemblies", {"name": args.assembly_name, "limit": 100})
        search_args = {"query": args.type_query, "kind": "type", "limit": 100}
        search_response, _ = await measured_tool(session, "api_search", search_args)
        hits = search_response.get("results", [])
        type_hits = [hit for hit in hits if hit.get("kind") in {
            "class", "struct", "interface", "enum", "delegate", "type"}]
        type_hit = next((hit for hit in type_hits
                         if hit.get("qualifiedName", "").rsplit(".", 1)[-1].casefold() ==
                         args.type_query.casefold()), type_hits[0] if type_hits else None)
        if type_hit is None:
            raise RuntimeError(f"api_search returned no type for query {args.type_query!r}")
        assembly_id = type_hit["assemblyId"]
        symbol_id = type_hit["symbolId"]
        snapshot_id = search_response["snapshotId"]
        api_type_args = {"assemblyId": assembly_id, "typeSymbolId": symbol_id, "snapshotId": snapshot_id,
                         "includeInherited": False, "limit": 100}
        await measured_tool(session, "api_type", api_type_args)
        source_args = {"assemblyId": assembly_id, "symbolId": symbol_id, "startLine": 1,
                       "lineCount": 10, "snapshotId": snapshot_id}
        first_source, _ = await measured_tool(session, "api_decompile", source_args)
        next_line = first_source.get("nextLine")
        if next_line is None:
            raise RuntimeError("selected API type has no subsequent source page; use a broader --type-query")
        continuation_args = dict(source_args, startLine=next_line)
        catalog_search_args = {"query": args.catalog_query, "catalogType": args.catalog_type, "limit": 25}
        catalog_search, _ = await measured_tool(session, "catalog_search", catalog_search_args)
        catalog_entries = catalog_search.get("results", [])
        if not catalog_entries:
            raise RuntimeError("catalog_search returned no records; adjust --catalog-query/--catalog-type")
        entry = catalog_entries[0]
        catalog_read_args = {"entryId": entry["entryId"], "snapshotId": catalog_search["snapshotId"],
                             "startLine": 1, "lineCount": 25}
        await measured_tool(session, "catalog_read", catalog_read_args)

        workloads = [
            ("warm_api_search", "api_search", search_args),
            ("exact_type_retrieval", "api_type", api_type_args),
            ("first_source_page", "api_decompile", source_args),
            ("subsequent_source_page", "api_decompile", continuation_args),
            ("warm_catalog_search", "catalog_search", catalog_search_args),
            ("catalog_read", "catalog_read", catalog_read_args),
        ]
        sizes = {
            "assembly_name": type_hit.get("assemblyName"),
            "api_assemblies_total": inventory_data.get("total"),
            "api_query_result_count": len(hits),
            "selected_type": type_hit.get("qualifiedName"),
            "catalog_archive_bytes": (game_exe.parent / "BladeAndSorcery_Data" / "StreamingAssets" /
                                      "Default" / "bas.jsondb").stat().st_size,
            "catalog_search_result_count": len(catalog_entries),
            "managed_dll_count": sum(1 for path in (game_exe.parent / "BladeAndSorcery_Data" / "Managed").rglob("*.dll")),
        }
        for group, name, tool_args in workloads:
            for _ in range(args.warm_repeats):
                _, sample = await measured_tool(session, name, tool_args)
                groups.setdefault(group, []).append(sample)
        for task_id, query in API_LOOKUP_TASKS:
            task_samples = []
            observations = []
            for _ in range(args.warm_repeats):
                response, sample = await measured_tool(session, "api_lookup", {"query": query})
                task_samples.append(sample)
                observations.append(summarize_api_lookup(response, sample))
            groups[f"api_lookup_{task_id}"] = task_samples
            variants = {json.dumps(observation, sort_keys=True, separators=(",", ":"))
                        for observation in observations}
            api_lookup_tasks[task_id] = {
                "query": query,
                "result": observations[0],
                "result_stable_across_repeats": len(variants) == 1,
            }


    invalidation = await run_invalidation_fixture(
        host, fixture_sources, host_root / "invalidation", args.samples,
        diagnostics=(label != "baseline"))
    first_lookup_results = {}
    for scenario, observations in first_lookup_observations.items():
        variants = {json.dumps(observation, sort_keys=True, separators=(",", ":"))
                    for observation in observations}
        first_lookup_results[scenario] = {
            "result": observations[0],
            "result_stable_across_samples": len(variants) == 1,
        }
    return {
        "host_file": host.name,
        "host_sha256": sha256_file(host),
        "negotiated_protocol_version": session.negotiated_protocol_version,
        "available_tools": session.tools,
        "tool_schema_sha256": hashlib.sha256(json.dumps(
            session.tool_schemas, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest(),
        "tool_schemas": session.tool_schemas,
        "startup_ms": {name: summarize([{"latency_ms": value} for value in values])["latency_ms"]
                       for name, values in startups.items()},
        "workloads": {name: summarize(samples) for name, samples in groups.items()},
        "api_lookup_tasks": api_lookup_tasks,
        "api_lookup_first_runs": {
            "task_id": first_lookup_task_id,
            "query": first_lookup_query,
            **first_lookup_results,
        },
        "sizes": sizes,
        "invalidation_fixture": invalidation,
    }




def write_catalog_fixture(path: Path, marker: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    record = {
        "$type": "ThunderRoad.ItemData, ThunderRoad",
        "id": f"fixture-{marker}",
        "name": marker,
        "displayName": marker,
    }
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("Items/Fixture.json", json.dumps(record, separators=(",", ":")))


async def run_invalidation_fixture(host: Path, sources: tuple[Path, Path], root: Path,
                                   samples: int, diagnostics: bool) -> dict[str, Any]:
    root.mkdir(parents=True, exist_ok=True)
    game_dir = root / "fixture-game"
    game_exe = game_dir / "BladeAndSorcery.exe"
    managed = game_dir / "BladeAndSorcery_Data" / "Managed"
    mods = game_dir / "BladeAndSorcery_Data" / "StreamingAssets" / "Mods"
    catalog = game_dir / "BladeAndSorcery_Data" / "StreamingAssets" / "Default" / "bas.jsondb"
    managed.mkdir(parents=True, exist_ok=True)
    mods.mkdir(parents=True, exist_ok=True)
    game_exe.parent.mkdir(parents=True, exist_ok=True)
    game_exe.write_bytes(b"")
    fixture_assembly = managed / "Fixture.dll"
    fixed_time_ns = 1_700_000_000_000_000_000
    fixture_assembly.write_bytes(sources[0].read_bytes())
    os.utime(fixture_assembly, ns=(fixed_time_ns, fixed_time_ns))
    write_catalog_fixture(catalog, "FixtureOriginalSentinel")
    os.utime(catalog, ns=(fixed_time_ns, fixed_time_ns))
    launch = root / "launch.json"
    write_launch_config(launch, game_exe)

    cache = root / "cache"
    assembly_samples: list[dict[str, Any]] = []
    catalog_samples: list[dict[str, Any]] = []
    current = 0

    async with McpSession(host, launch, cache, diagnostics) as session:
        for index in range(samples):
            if index:
                fixture_assembly.write_bytes(sources[current].read_bytes())
                os.utime(fixture_assembly, ns=(fixed_time_ns, fixed_time_ns))
                marker = "FixtureOriginalSentinel" if current == 0 else "FixtureUpdatedSentinel"
                write_catalog_fixture(catalog, marker)
                os.utime(catalog, ns=(fixed_time_ns, fixed_time_ns))
            before_api, _ = await measured_tool(session, "api_assemblies", {"limit": 100})
            before_id = select_assembly(before_api)["assemblyId"]
            before_marker = "FixtureOriginalSentinel" if current == 0 else "FixtureUpdatedSentinel"
            await measured_tool(session, "catalog_search", {"query": before_marker, "limit": 25})

            changed = 1 - current
            fixture_assembly.write_bytes(sources[changed].read_bytes())
            os.utime(fixture_assembly, ns=(fixed_time_ns, fixed_time_ns))
            changed_marker = "FixtureOriginalSentinel" if changed == 0 else "FixtureUpdatedSentinel"
            write_catalog_fixture(catalog, changed_marker)
            os.utime(catalog, ns=(fixed_time_ns, fixed_time_ns))

            after_api, assembly_sample = await measured_tool(session, "api_assemblies", {"limit": 100})
            after_id = select_assembly(after_api)["assemblyId"]
            after_catalog, catalog_sample = await measured_tool(
                session, "catalog_search", {"query": changed_marker, "limit": 25})
            found = any(changed_marker in json.dumps(item) for item in after_catalog.get("results", []))
            if before_id == after_id or not found:
                raise RuntimeError("fixture invalidation did not observe changed assembly/catalog content")
            assembly_sample["fixture_content_changed"] = before_id != after_id
            catalog_sample["fixture_content_changed"] = found
            assembly_samples.append(assembly_sample)
            catalog_samples.append(catalog_sample)
            current = changed

    return {
        "scope": "temporary copy of two installed managed DLLs and generated bas.jsondb; shipped files are never written",
        "same_timestamp_replacement": True,
        "assembly_change": summarize(assembly_samples),
        "catalog_change": summarize(catalog_samples),
    }


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def system_memory_bytes() -> int | None:
    if os.name == "nt":
        class MemoryStatus(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]
        status = MemoryStatus()
        status.dwLength = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.ullTotalPhys)
    try:
        return int(os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES"))
    except (ValueError, OSError, AttributeError):
        return None


def environment(game_exe: Path) -> dict[str, Any]:
    dotnet = shutil.which("dotnet") or "dotnet"
    dotnet_version = subprocess.run([dotnet, "--version"], capture_output=True, text=True, check=False)
    runtime_list = subprocess.run([dotnet, "--list-runtimes"], capture_output=True, text=True, check=False)
    volume = os.path.splitdrive(str(game_exe.resolve()))[0] or None
    return {
        "os": platform.platform(),
        "architecture": platform.machine(),
        "cpu": platform.processor() or None,
        "logical_cpu_count": os.cpu_count(),
        "total_memory_bytes": system_memory_bytes(),
        "python": platform.python_version(),
        "dotnet_sdk": dotnet_version.stdout.strip() if dotnet_version.returncode == 0 else None,
        "dotnet_runtimes": [line.strip() for line in runtime_list.stdout.splitlines()],
        "game_volume": volume,
        "storage_device_mapping": "not inferred; use the observed physical-disk inventory for this machine",
        "filesystem_cache_policy": "normal OS filesystem cache; no cache flush or direct-I/O mode",
    }


def create_omp_config(config_path: Path, host: Path, game_exe: Path) -> tuple[Path, Path]:
    if config_path.name != "mcp.json" or config_path.parent.name != ".omp":
        raise ValueError("OMP config output must be an isolated workspace's .omp/mcp.json")
    config_path.parent.mkdir(parents=True, exist_ok=True)
    workdir = config_path.parent.parent
    launch_path = workdir / "launch.json"
    cache_path = workdir / "cache"
    write_launch_config(launch_path, game_exe)
    proxy_path = Path(__file__).with_name("benchmark_mcp_readonly.py").resolve()
    value = {
        "mcpServers": {
            "ThunderRoadMcp": {
                "type": "stdio",
                "command": sys.executable,
                "args": [str(proxy_path), "--host", str(host.resolve())],
                "env": {
                    "THUNDERROAD_MCP_LAUNCH_CONFIG": str(launch_path.resolve()),
                    "THUNDERROAD_MCP_CACHE_DIR": str(cache_path.resolve()),
                    "THUNDERROAD_MCP_DIAGNOSTICS": "0",
                },
                "enabled": True,
            }
        }
    }
    config_path.write_text(json.dumps(value, indent=2), encoding="utf-8")
    return launch_path, cache_path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True, help="Preserved baseline host DLL")
    parser.add_argument("--candidate", type=Path, help="Final built host DLL; omit for baseline-only measurement")
    parser.add_argument("--game-exe", type=Path, required=True, help="Installed BladeAndSorcery.exe (never launched)")
    parser.add_argument("--samples", type=int, default=5)
    parser.add_argument("--warm-repeats", type=int, default=10)
    parser.add_argument("--assembly-name", default="ThunderRoad")
    parser.add_argument("--type-query", default="ItemData")
    parser.add_argument("--catalog-query", default="ItemData")
    parser.add_argument("--catalog-type", default="ItemData")
    parser.add_argument("--baseline-revision", default=BASELINE_REVISION)
    parser.add_argument("--candidate-revision")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--omp-config-out", type=Path,
                        help="Write <isolated-workspace>/.omp/mcp.json with the benchmark read-only proxy")
    parser.add_argument("--omp-baseline-config-out", type=Path,
                        help="Write the baseline's separate <isolated-workspace>/.omp/mcp.json")
    args = parser.parse_args()
    if args.samples < 1 or args.warm_repeats < 1:
        parser.error("--samples and --warm-repeats must be at least 1")
    for path, label in ((args.baseline, "--baseline"), (args.candidate, "--candidate"),
                        (args.game_exe, "--game-exe")):
        if path is not None and not path.is_file():
            parser.error(f"{label} does not exist: {path}")
    if args.omp_baseline_config_out is not None and args.omp_config_out is not None and \
            args.omp_baseline_config_out.resolve() == args.omp_config_out.resolve():
        parser.error("baseline and candidate OMP workspaces must use separate config paths")
    if args.omp_baseline_config_out is not None and args.omp_config_out is not None and \
            args.candidate is None:
        parser.error("--omp-config-out is the candidate config and requires --candidate")
    for path in (args.omp_config_out, args.omp_baseline_config_out):
        if path is not None and (path.name != "mcp.json" or path.parent.name != ".omp"):
            parser.error("OMP config outputs must be <isolated-workspace>/.omp/mcp.json")
    if shutil.which("dotnet") is None:
        parser.error("dotnet was not found on PATH")
    return args


async def main_async(args: argparse.Namespace) -> None:
    managed = args.game_exe.resolve().parent / "BladeAndSorcery_Data" / "Managed"
    source_a = managed / "ThunderRoad.dll"
    source_b = managed / "ThunderRoad.ArtTools.dll"
    if not source_a.is_file() or not source_b.is_file():
        raise RuntimeError("invalidation fixture requires installed ThunderRoad.dll and ThunderRoad.ArtTools.dll")
    fixtures = (source_a, source_b)
    output: dict[str, Any] = {
        "schema_version": 1,
        "measured_at_utc": datetime.now(timezone.utc).isoformat(),
        "baseline_revision": args.baseline_revision,
        "candidate_revision": args.candidate_revision,
        "protocol": {
            "transport": "MCP stdio JSON-RPC subprocess",
            "client_protocol_version": PROTOCOL_VERSION,
            "samples": args.samples,
            "request_timeout_seconds": REQUEST_TIMEOUT_SECONDS,
            "warm_repeats_per_workload": args.warm_repeats,
            "freshness_policy": "full content hashing is retained for every request; cache metadata never replaces validation",
            "model_tokens": "not measured by this transport benchmark",
        },
        "environment": environment(args.game_exe),
        "installation": {
            "managed_dll_count": sum(1 for path in managed.rglob("*.dll")),
            "catalog_archive_bytes": (args.game_exe.resolve().parent / "BladeAndSorcery_Data" /
                                       "StreamingAssets" / "Default" / "bas.jsondb").stat().st_size,
            "game_volume": os.path.splitdrive(str(args.game_exe.resolve()))[0] or None,
        },
        "hosts": {},
    }
    omp_configs = {}
    config_outputs = [
        ("baseline", args.omp_baseline_config_out, args.baseline),
        ("candidate" if args.candidate is not None else "baseline",
         args.omp_config_out, args.candidate or args.baseline),
    ]
    for label, config_path, selected_host in config_outputs:
        if config_path is None:
            continue
        if label in omp_configs:
            raise ValueError("baseline and candidate OMP configs require distinct host labels")
        launch_path, cache_path = create_omp_config(config_path, selected_host, args.game_exe)
        omp_configs[label] = {
            "mcp_config_file": config_path.name,
            "launch_config_file": launch_path.name,
            "cache_directory_name": cache_path.name,
            "proxy": "benchmark_mcp_readonly.py",
            "allowlist_enforced_by": "proxy tools/list filtering and tools/call rejection",
            "allowed_tool_count": 8,
            "note": "OMP must use --cwd <workspace>; this config is not a --config settings overlay.",
        }
    if omp_configs:
        output["omp_readonly_configs"] = omp_configs
    with tempfile.TemporaryDirectory(prefix="thunderroad-mcp-benchmark-") as temporary:
        root = Path(temporary)

        for label, host in (("baseline", args.baseline), ("candidate", args.candidate)):
            if host is None:
                continue
            output["hosts"][label] = await profile_host(
                label, host, args.game_exe, root, args, fixtures)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2), encoding="utf-8")
    print(json.dumps({
        "output": str(args.output),
        "hosts": {label: {name: result["workloads"][name]["latency_ms"]
                          for name in result["workloads"]}
                  for label, result in output["hosts"].items()},
        "omp_configs": [str(path) for path in
                        (args.omp_baseline_config_out, args.omp_config_out) if path is not None],
    }, indent=2))


def main() -> None:
    args = parse_args()
    asyncio.run(main_async(args))


if __name__ == "__main__":
    main()
