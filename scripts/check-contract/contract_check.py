"""WhaleGo contract gate: fetch MCP tools/list from a running executor and diff it against the
machine-readable contract (contracts/whalego.tools.v1.json = single source of truth).

Normalization rules (see docs/ARCHITECTURE.md 3.10 and docs/PROTOCOL.md):
  * properties marked "x-internal": true are stripped from the *expected* contract before compare
    (they are injected by the AstrBot plugin and must NOT be model-facing);
  * all remaining "x-internal" keys are dropped;
  * object key order is irrelevant (structural compare of sorted JSON).

Asserts:
  1. exactly the 7 contract tool names are exposed (order-insensitive);
  2. tool descriptions match exactly (whitespace-trimmed);
  3. inputSchema is structurally equal after normalization;
  4. no internal parameter name (session_id / request_id / scope) appears in the model-facing schema;
  5. static: contract_version == "1.2", protocol_version == 1;
  6. optional --check-health: go_health.protocol_version == 1 and accepting_actions present.

Exit codes: 0 = PASS, 1 = contract mismatch, 2 = setup/connection error.
"""

from __future__ import annotations

import argparse
import asyncio
import copy
import difflib
import json
import sys
from pathlib import Path
from typing import Any

INTERNAL_PARAM_NAMES = frozenset({"session_id", "request_id", "scope"})
EXPECTED_CONTRACT_VERSION = "1.2"
EXPECTED_PROTOCOL_VERSION = 1
EXPECTED_TOOL_COUNT = 7
DEFAULT_SESSION_ID = "wg-8f21cd04f00dbeef"  # wg- + 16 hex (sha256(umo)[:16])

_failures: list[str] = []


def _fail(message: str) -> None:
    _failures.append(message)
    print(f"[FAIL] {message}")


def _pass(message: str) -> None:
    print(f"[PASS] {message}")


def strip_internal(node: Any, removed: list[str] | None = None) -> Any:
    """Deep-copy node, drop properties marked x-internal and any x-internal keys."""
    if removed is None:
        removed = []
    if isinstance(node, dict):
        result: dict[str, Any] = {}
        properties = node.get("properties")
        for key, value in node.items():
            if key == "x-internal":
                continue
            if key == "properties" and isinstance(value, dict):
                cleaned_props: dict[str, Any] = {}
                for prop_name, prop_schema in value.items():
                    if isinstance(prop_schema, dict) and prop_schema.get("x-internal") is True:
                        removed.append(prop_name)
                        continue
                    cleaned_props[prop_name] = strip_internal(prop_schema, removed)
                result[key] = cleaned_props
                continue
            result[key] = strip_internal(value, removed)
        return result
    if isinstance(node, list):
        return [strip_internal(item, removed) for item in node]
    return node


def find_internal_param_names(node: Any, path: str = "$") -> list[str]:
    """Return property-name paths that use an internal parameter name (model-facing violation)."""
    found: list[str] = []
    if isinstance(node, dict):
        properties = node.get("properties")
        if isinstance(properties, dict):
            for name in properties:
                if name in INTERNAL_PARAM_NAMES:
                    found.append(f"{path}.properties.{name}")
        for key, value in node.items():
            found.extend(find_internal_param_names(value, f"{path}.{key}"))
    elif isinstance(node, list):
        for index, item in enumerate(node):
            found.extend(find_internal_param_names(item, f"{path}[{index}]"))
    return found


def canonical(value: Any) -> Any:
    """Order-insensitive canonical form (dict keys sorted, JSON primitives normalized)."""
    if isinstance(value, dict):
        return {key: canonical(value[key]) for key in sorted(value)}
    if isinstance(value, list):
        return [canonical(item) for item in value]
    return value


def unified_json_diff(expected: Any, actual: Any, label: str) -> str:
    expected_text = json.dumps(canonical(expected), indent=2, ensure_ascii=False).splitlines()
    actual_text = json.dumps(canonical(actual), indent=2, ensure_ascii=False).splitlines()
    return "\n".join(
        difflib.unified_diff(expected_text, actual_text, fromfile=f"contract:{label}", tofile=f"executor:{label}", lineterm="")
    )


def compare_tool_schema(expected: dict[str, Any], actual: dict[str, Any]) -> list[str]:
    name = expected.get("name")
    problems: list[str] = []
    expected_description = str(expected.get("description", "")).strip()
    actual_description = str(actual.get("description", "")).strip()
    if expected_description != actual_description:
        problems.append(f"tool {name}: description mismatch")
        problems.append(
            "\n".join(
                difflib.unified_diff(
                    expected_description.splitlines(),
                    actual_description.splitlines(),
                    fromfile="contract:description",
                    tofile="executor:description",
                    lineterm="",
                )
            )
        )
    expected_schema = strip_internal(copy.deepcopy(expected.get("inputSchema", {})))
    actual_schema = copy.deepcopy(actual.get("inputSchema", {}))
    if canonical(expected_schema) != canonical(actual_schema):
        problems.append(f"tool {name}: inputSchema mismatch")
        problems.append(unified_json_diff(expected_schema, actual_schema, f"{name}.inputSchema"))
    return problems


async def fetch_contract_live(url: str, token: str) -> tuple[list[dict[str, Any]], dict[str, Any] | None]:
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    headers = {"Authorization": f"Bearer {token}"}
    tools: list[dict[str, Any]] = []
    health_payload: dict[str, Any] | None = None
    async with streamablehttp_client(url, headers=headers) as streams:
        if len(streams) == 3:
            read_stream, write_stream, _ = streams
        else:  # pragma: no cover - older/newer mcp API shape
            read_stream, write_stream = streams[:2]
        async with ClientSession(read_stream, write_stream) as session:
            await session.initialize()
            result = await session.list_tools()
            for tool in result.tools:
                schema = getattr(tool, "inputSchema", None)
                if schema is None:
                    schema = getattr(tool, "input_schema", None)
                tools.append(
                    {
                        "name": getattr(tool, "name", None),
                        "description": getattr(tool, "description", None),
                        "inputSchema": schema,
                    }
                )
            if CHECK_HEALTH:
                health = await session.call_tool("go_health", {"session_id": DEFAULT_SESSION_ID})
                for item in health.content or []:
                    text = getattr(item, "text", None)
                    if not text:
                        continue
                    try:
                        parsed = json.loads(text)
                    except json.JSONDecodeError:
                        continue
                    if isinstance(parsed, dict) and parsed.get("schema_version") == "whalego-health-v1":
                        health_payload = parsed
                        break
    return tools, health_payload


CHECK_HEALTH = False


def main() -> int:
    global CHECK_HEALTH
    parser = argparse.ArgumentParser(description="WhaleGo tools/list vs contracts/whalego.tools.v1.json gate")
    parser.add_argument("--url", required=True, help="MCP endpoint, e.g. http://127.0.0.1:18765/mcp")
    parser.add_argument("--token", required=True, help="Bearer token (wg_...)")
    parser.add_argument("--contracts", required=True, help="Path to contracts/whalego.tools.v1.json")
    parser.add_argument("--check-health", action="store_true", help="also assert go_health.protocol_version == 1")
    args = parser.parse_args()
    CHECK_HEALTH = args.check_health

    contract_path = Path(args.contracts)
    if not contract_path.is_file():
        print(f"[ERROR] contract file not found: {contract_path}")
        return 2
    try:
        contract = json.loads(contract_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"[ERROR] cannot read contract JSON: {exc}")
        return 2

    print("== WhaleGo contract check ==")
    print(f"executor : {args.url}")
    print(f"contract : {contract_path} (contract_version={contract.get('contract_version')})")

    contract_version = str(contract.get("contract_version", ""))
    if contract_version == EXPECTED_CONTRACT_VERSION:
        _pass(f"contract_version == {EXPECTED_CONTRACT_VERSION}")
    else:
        _fail(f"contract_version expected {EXPECTED_CONTRACT_VERSION}, contract has {contract_version!r}")
    protocol_version = contract.get("protocol_version")
    if protocol_version == EXPECTED_PROTOCOL_VERSION:
        _pass(f"protocol_version == {EXPECTED_PROTOCOL_VERSION}")
    else:
        _fail(f"protocol_version expected {EXPECTED_PROTOCOL_VERSION}, contract has {protocol_version!r}")

    expected_tools = contract.get("tools") or []
    if len(expected_tools) != EXPECTED_TOOL_COUNT:
        _fail(f"contract declares {len(expected_tools)} tools, expected {EXPECTED_TOOL_COUNT}")
    expected_by_name = {str(tool.get("name")): tool for tool in expected_tools}
    print(f"expected tools ({len(expected_by_name)}): {', '.join(sorted(expected_by_name))}")

    try:
        actual_tools, health_payload = asyncio.run(fetch_contract_live(args.url, args.token))
    except Exception as exc:  # noqa: BLE001 - connection/setup errors are reported, not raised
        print(f"[ERROR] MCP connection/tools-list failed: {type(exc).__name__}: {exc}")
        return 2

    actual_by_name = {str(tool.get("name")): tool for tool in actual_tools}
    print(f"actual tools   ({len(actual_by_name)}): {', '.join(sorted(actual_by_name))}")

    if set(actual_by_name) == set(expected_by_name):
        _pass(f"tool names exactly match the {EXPECTED_TOOL_COUNT} contract tools")
    else:
        missing = sorted(set(expected_by_name) - set(actual_by_name))
        extra = sorted(set(actual_by_name) - set(expected_by_name))
        _fail(f"tool name mismatch: missing={missing} extra={extra}")

    problems: list[str] = []
    for name in sorted(expected_by_name):
        if name not in actual_by_name:
            continue
        problems.extend(compare_tool_schema(expected_by_name[name], actual_by_name[name]))
    if problems:
        _fail("tool descriptions / inputSchema drift detected:")
        for problem in problems:
            print(problem)
            print()
    else:
        _pass("all tool descriptions match the contract")
        _pass("all inputSchemas match the contract (x-internal stripped, order-insensitive)")

    violations: list[str] = []
    for name, tool in actual_by_name.items():
        for violation in find_internal_param_names(tool.get("inputSchema") or {}):
            violations.append(f"{name}: {violation}")
    if violations:
        _fail("model-facing schema exposes internal parameters: " + "; ".join(violations))
    else:
        _pass("no internal params (session_id/request_id/scope) in model-facing schemas")

    if CHECK_HEALTH:
        if health_payload is None:
            _fail("go_health returned no whalego-health-v1 JSON payload")
        else:
            if health_payload.get("protocol_version") == EXPECTED_PROTOCOL_VERSION:
                _pass("go_health.protocol_version == 1")
            else:
                _fail(f"go_health.protocol_version expected 1, got {health_payload.get('protocol_version')!r}")
            if "accepting_actions" in health_payload:
                _pass("go_health.accepting_actions present")
            else:
                _fail("go_health.accepting_actions missing")

    print()
    if _failures:
        print(f"RESULT: FAIL ({len(_failures)} failure(s))")
        return 1
    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())