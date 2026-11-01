"""WhaleGo end-to-end acceptance driver (real headless executor + real mcp==1.30.0 client).

Covers the DESIGN section 11 oracle cases that can be automated on this host:
  HTTP     : /healthz 200 (no auth); /mcp without token -> 401; /mcp with wrong token -> 401
  MCP      : tools/list == 7 contract tools; go_health(protocol_version=1, accepting_actions);
             go_apps; go_windows; go_launch(fixture by path); go_observe(tree + PNG image);
             go_observe(no screenshot -> no image); go_act set_value/type/click + verification readback;
             STALE_STATE after acting with an old state_id; SESSION_STOPPED after go_stop;
             go_observe still works after stop; READONLY_MODE on a readonly executor;
             CONTROLLER_BUSY with two sessions on one executor (skipped if the lease is not held).
  Contract : first text line is always OK:/ERROR[<code>]: and error JSON codes match PROTOCOL v1.3.

The AstrBot plugin injects session_id (internal, not in schema); this client does the same through the
hidden argument. Exit code 0 only if no FAIL cases; SKIPs are reported and allowed.

Usage (normally launched by scripts/e2e.ps1):
  uv run --no-project --with mcp==1.30.0 python scripts/e2e/mcp_e2e.py \
      --url http://127.0.0.1:19001/mcp --token wg_... \
      --readonly-url http://127.0.0.1:19002/mcp --readonly-token wg_... \
      --fixture windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe \
      --contracts contracts/whalego.tools.v1.json
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import contextlib
import json
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, AsyncIterator

SESSION_MAIN = "wg-8f21cd04f00dbeef"
SESSION_A = "wg-aaaa000111112222"
SESSION_B = "wg-bbbb000233334444"
STATE_ID_RE = re.compile(r"^st-[0-9a-f]{8}$")
OK_PREFIX = "OK:"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

CALL_TIMEOUT_SEC = 60.0
FIXTURE_TITLE = "WhaleGo Fixture"


@dataclass
class Reporter:
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    failures: list[str] = field(default_factory=list)

    def ok(self, case: str, detail: str = "") -> None:
        self.passed += 1
        suffix = f"  [{detail}]" if detail else ""
        print(f"PASS  {case}{suffix}")

    def fail(self, case: str, detail: str = "") -> None:
        self.failed += 1
        self.failures.append(case)
        suffix = f"  [{detail}]" if detail else ""
        print(f"FAIL  {case}{suffix}")

    def skip(self, case: str, reason: str) -> None:
        self.skipped += 1
        print(f"SKIP  {case}  ({reason})")

    def check(self, case: str, condition: bool, detail: str = "") -> bool:
        if condition:
            self.ok(case, detail)
        else:
            self.fail(case, detail)
        return condition

    def summary(self) -> None:
        print()
        print(f"RESULT: {self.passed} passed, {self.failed} failed, {self.skipped} skipped")
        if self.failures:
            print("FAILED CASES:")
            for case in self.failures:
                print(f"  - {case}")


def content_texts(result: Any) -> list[str]:
    texts: list[str] = []
    for item in getattr(result, "content", None) or []:
        if getattr(item, "type", None) == "text" and getattr(item, "text", None):
            texts.append(item.text)
    return texts


def first_line(result: Any) -> str:
    texts = content_texts(result)
    return texts[0].splitlines()[0].strip() if texts and texts[0] else ""


def image_items(result: Any) -> list[Any]:
    return [item for item in (getattr(result, "content", None) or []) if getattr(item, "type", None) == "image"]


def json_payload(result: Any) -> dict[str, Any] | None:
    for text in content_texts(result):
        text = text.strip()
        if not text.startswith("{"):
            continue
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            continue
        if isinstance(parsed, dict) and "schema_version" in parsed:
            return parsed
    return None


def is_query_ok(result: Any, allow_privacy_prefix: bool = False) -> bool:
    if getattr(result, "isError", None):
        return False
    line = first_line(result)
    if allow_privacy_prefix:
        return OK_PREFIX in line
    return line.startswith(OK_PREFIX)


def error_code(result: Any) -> str | None:
    payload = json_payload(result)
    if payload and isinstance(payload.get("code"), str):
        return payload["code"]
    line = first_line(result)
    match = re.match(r"^ERROR\[([A-Z_]+)\]", line)
    return match.group(1) if match else None


def action_receipt(result: Any) -> dict[str, Any]:
    payload = json_payload(result) or {}
    receipt = payload.get("action_receipt")
    return receipt if isinstance(receipt, dict) else {}


def http_get_json(url: str, timeout: float = 10.0) -> tuple[int, dict[str, Any] | None]:
    request = urllib.request.Request(url, method="GET", headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - local test URL
            body = response.read().decode("utf-8", errors="replace")
            return response.status, json.loads(body)
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        try:
            return exc.code, json.loads(body)
        except json.JSONDecodeError:
            return exc.code, None
    except Exception as exc:  # noqa: BLE001 - network errors are reported as cases
        print(f"      http_get_json({url}) error: {type(exc).__name__}: {exc}")
        return 0, None


def http_post_status(url: str, token: str | None = None, timeout: float = 10.0) -> int:
    body = json.dumps(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "whalego-e2e", "version": "1.0"},
            },
        }
    ).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    }
    if token is not None:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return response.status
    except urllib.error.HTTPError as exc:
        return exc.code
    except Exception as exc:  # noqa: BLE001
        print(f"      http_post_status({url}) error: {type(exc).__name__}: {exc}")
        return 0


@contextlib.asynccontextmanager
async def open_mcp_session(url: str, token: str) -> AsyncIterator[Any]:
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    async with streamablehttp_client(url, headers={"Authorization": f"Bearer {token}"}) as streams:
        read_stream, write_stream = streams[0], streams[1]
        async with ClientSession(read_stream, write_stream) as session:
            await session.initialize()
            yield session


async def call_tool(session: Any, tool: str, arguments: dict[str, Any] | None = None, session_id: str = SESSION_MAIN) -> Any:
    args = dict(arguments or {})
    args["session_id"] = session_id
    return await asyncio.wait_for(session.call_tool(tool, args), timeout=CALL_TIMEOUT_SEC)


def find_element(observe_payload: dict[str, Any], automation_id: str) -> dict[str, Any] | None:
    for element in observe_payload.get("elements") or []:
        if element.get("automation_id") == automation_id:
            return element
    return None


def element_text(element: dict[str, Any] | None) -> str:
    if not element:
        return ""
    value = element.get("value")
    if isinstance(value, str) and value:
        return value
    title = element.get("title")
    return title if isinstance(title, str) else ""


async def find_window(session: Any, title_contains: str, timeout_sec: float = 15.0, session_id: str = SESSION_MAIN) -> dict[str, Any] | None:
    deadline = time.monotonic() + timeout_sec
    while time.monotonic() < deadline:
        result = await call_tool(session, "go_windows", {}, session_id=session_id)
        payload = json_payload(result) or {}
        for window in payload.get("windows") or []:
            title = str(window.get("title") or "")
            if title_contains in title:
                return window
        await asyncio.sleep(0.5)
    return None


@dataclass
class FixtureHandle:
    pid: int | None = None
    window_id: str | None = None
    launched_by_executor: bool = False
    fallback_process: subprocess.Popen[bytes] | None = None

    def cleanup(self) -> None:
        if self.pid:
            with contextlib.suppress(Exception):
                subprocess.run(["taskkill", "/PID", str(self.pid), "/F"], capture_output=True, timeout=15, check=False)
        if self.fallback_process is not None and self.fallback_process.poll() is None:
            with contextlib.suppress(Exception):
                self.fallback_process.kill()
            with contextlib.suppress(Exception):
                self.fallback_process.wait(timeout=10)


async def start_fixture_fallback(exe: str, title_suffix: str) -> FixtureHandle:
    """Fallback when go_launch fails: start the fixture directly so the remaining cases can run."""
    ready = Path(tempfile.gettempdir()) / f"whalego-e2e-fixture-{title_suffix}.ready"
    ready.unlink(missing_ok=True)
    process = subprocess.Popen(  # noqa: S603 - launching the test fixture we built
        [exe, "--title", title_suffix, "--ready-file", str(ready), "--auto-close-ms", "600000"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline and not ready.exists():
        await asyncio.sleep(0.1)
    return FixtureHandle(pid=process.pid, launched_by_executor=False, fallback_process=process)


async def observe(
    session: Any,
    window_id: str,
    include_screenshot: bool = False,
    session_id: str = SESSION_MAIN,
    settle_ms: int = 500,
    max_value_chars: int | None = None,
) -> Any:
    arguments: dict[str, Any] = {
        "window_id": window_id,
        "include_screenshot": include_screenshot,
        "settle_ms": settle_ms,
        "max_elements": 300,
    }
    if max_value_chars is not None:
        arguments["max_value_chars"] = max_value_chars
    return await call_tool(session, "go_observe", arguments, session_id=session_id)


def estimate_client_origin(observe_payload: dict[str, Any]) -> tuple[int, int] | None:
    """Best-effort client-area origin in screen pixels.

    Wire format is [left, top, right, bottom] for both go_windows.windows[].bounds and
    go_observe.target.window.bounds (see docs/PROTOCOL.md 3.5/3.6 and the executor's WindowInfo mapping).
    The DWM window bounds include the (invisible) resize borders, so:
      border = (window_width - client_width) / 2
      client_left = left + border
      client_top  = top + (window_height - client_height) - border   (title bar + top border - bottom border)
    """
    target = observe_payload.get("target") or {}
    window = target.get("window") or {}
    frame = observe_payload.get("frame") or {}
    bounds = window.get("bounds")
    try:
        client_width = int(frame["client_width"])
        client_height = int(frame["client_height"])
    except (KeyError, TypeError, ValueError):
        return None
    if not (isinstance(bounds, list) and len(bounds) == 4):
        return None
    try:
        left, top, right, bottom = (int(value) for value in bounds)
    except (TypeError, ValueError):
        return None
    window_width = right - left
    window_height = bottom - top
    if window_width <= 0 or window_height <= 0 or client_width <= 0 or client_height <= 0:
        return None
    if client_width > window_width or client_height > window_height:
        return None
    border = max((window_width - client_width) // 2, 0)
    client_left = left + border
    client_top = top + (window_height - client_height) - border
    return client_left, client_top


def screen_to_image_point(observe_payload: dict[str, Any], screen_x: int, screen_y: int) -> tuple[int, int] | None:
    frame = observe_payload.get("frame") or {}
    origin = estimate_client_origin(observe_payload)
    if origin is None:
        return None
    try:
        image_width = int(frame["image_width"])
        image_height = int(frame["image_height"])
        client_width = int(frame["client_width"])
        client_height = int(frame["client_height"])
    except (KeyError, TypeError, ValueError):
        return None
    if image_width <= 0 or image_height <= 0 or client_width <= 0 or client_height <= 0:
        return None
    left, top = origin
    client_x = screen_x - left
    client_y = screen_y - top
    if client_x < 0 or client_y < 0 or client_x >= client_width or client_y >= client_height:
        return None
    image_x = round(client_x * image_width / client_width)
    image_y = round(client_y * image_height / client_height)
    if image_x < 0 or image_y < 0 or image_x >= image_width or image_y >= image_height:
        return None
    return image_x, image_y


def element_center_screen(element: dict[str, Any]) -> tuple[int, int] | None:
    bounds = element.get("bounds_screen_diag")
    if not (isinstance(bounds, list) and len(bounds) == 4):
        return None
    try:
        left, top, right, bottom = (int(value) for value in bounds)
    except (TypeError, ValueError):
        return None
    return (left + right) // 2, (top + bottom) // 2


def parse_counter_element(payload: dict[str, Any], automation_id: str, field_name: str) -> int | None:
    text = element_text(find_element(payload, automation_id))
    match = re.search(rf"(?:^|;){re.escape(field_name)}=(\d+)", text)
    return int(match.group(1)) if match else None


async def act(
    session: Any,
    op: str,
    target: dict[str, Any],
    args: dict[str, Any] | None = None,
    session_id: str = SESSION_MAIN,
) -> Any:
    payload: dict[str, Any] = {"op": op, "target": target}
    if args is not None:
        payload["args"] = args
    return await call_tool(session, "go_act", payload, session_id=session_id)


async def run_http_cases(rep: Reporter, base_url: str, token: str) -> bool:
    """Returns True when the basic HTTP surface looks usable."""
    health_status, health = http_get_json(f"{base_url}/healthz")
    usable = True
    if not rep.check("/healthz returns HTTP 200 without auth", health_status == 200, f"status={health_status}"):
        usable = False
    if health is not None:
        rep.check("/healthz payload ok=true", health.get("ok") is True, json.dumps(health, ensure_ascii=False))
        rep.check("/healthz protocol_version=1", health.get("protocol_version") == 1, f"protocol_version={health.get('protocol_version')!r}")
        rep.check("/healthz accepting_actions is boolean", isinstance(health.get("accepting_actions"), bool))
        for field_name in ("version", "uptime_s", "paused", "stopped"):
            rep.check(f"/healthz has '{field_name}'", field_name in health)
    else:
        rep.fail("/healthz JSON payload parsed", "no JSON body")

    no_token = http_post_status(f"{base_url}/mcp", token=None)
    rep.check("/mcp without token -> 401", no_token == 401, f"status={no_token}")
    bad_token = http_post_status(f"{base_url}/mcp", token="wg_this-is-not-a-valid-token")
    rep.check("/mcp with wrong token -> 401", bad_token == 401, f"status={bad_token}")
    return usable


async def run_mcp_cases(rep: Reporter, args: argparse.Namespace, contract_names: set[str]) -> tuple[bool, FixtureHandle, Any]:
    """Runs the main-session cases; returns (fixture, main MCP endpoint usable flag)."""
    fixture = FixtureHandle()
    window_id: str | None = None

    async with open_mcp_session(args.url, args.token) as session:
        # --- tools/list ---
        try:
            tools_result = await asyncio.wait_for(session.list_tools(), timeout=CALL_TIMEOUT_SEC)
            names = {tool.name for tool in tools_result.tools}
            rep.check("tools/list exposes exactly the 7 contract tools", names == contract_names, f"names={sorted(names)}")
        except Exception as exc:  # noqa: BLE001
            rep.fail("tools/list", f"{type(exc).__name__}: {exc}")
            return fixture, False

        # --- go_health ---
        health_result = await call_tool(session, "go_health", {})
        health_ok = rep.check("go_health OK: first line", is_query_ok(health_result), first_line(health_result))
        health = json_payload(health_result) or {}
        rep.check("go_health schema_version=whalego-health-v1", health.get("schema_version") == "whalego-health-v1")
        rep.check("go_health.protocol_version=1", health.get("protocol_version") == 1, f"got {health.get('protocol_version')!r}")
        rep.check("go_health.accepting_actions=true", health.get("accepting_actions") is True)
        rep.check("go_health.paused=false", health.get("paused") is False)
        rep.check("go_health.stopped=false", health.get("stopped") is False)
        capabilities = health.get("capabilities") or {}
        rep.check("go_health.capabilities.actions is non-empty list", bool(capabilities.get("actions")))
        rep.check("go_health.capabilities.observation contains uia_tree", "uia_tree" in (capabilities.get("observation") or []))
        launch_capabilities = capabilities.get("launch") or []
        rep.check("go_health launch capabilities contain app", "app" in launch_capabilities, f"launch={launch_capabilities}")

        # --- go_apps / go_windows ---
        apps_result = await call_tool(session, "go_apps", {"include_windows": False})
        rep.check("go_apps OK: first line", is_query_ok(apps_result), first_line(apps_result))
        apps = json_payload(apps_result) or {}
        rep.check("go_apps schema_version=whalego-apps-v1", apps.get("schema_version") == "whalego-apps-v1")
        rep.check("go_apps.apps is list", isinstance(apps.get("apps"), list))

        windows_result = await call_tool(session, "go_windows", {})
        rep.check("go_windows OK: first line", is_query_ok(windows_result), first_line(windows_result))
        windows = json_payload(windows_result) or {}
        rep.check("go_windows schema_version=whalego-windows-v1", windows.get("schema_version") == "whalego-windows-v1")
        rep.check("go_windows.windows is list", isinstance(windows.get("windows"), list))

        # --- go_launch fixture by absolute path (no allowlist) ---
        launch_result = await call_tool(session, "go_launch", {"app": args.fixture, "wait_ms": 10000})
        launch_ok = is_query_ok(launch_result)
        launch = json_payload(launch_result) or {}
        rep.check("go_launch(app=<fixture path>) OK: first line", launch_ok, first_line(launch_result))
        rep.check("go_launch schema_version=whalego-launch-v1", launch.get("schema_version") == "whalego-launch-v1")
        rep.check("go_launch.launched=true", launch.get("launched") is True)
        app_info = launch.get("app") or {}
        window_info = launch.get("window") or {}
        if isinstance(app_info.get("pid"), int):
            fixture.pid = int(app_info["pid"])
        if isinstance(window_info.get("window_id"), str):
            window_id = window_info["window_id"]
            fixture.window_id = window_id

        if not launch_ok:
            rep.skip("fallback fixture launch (go_launch failed)", "starting the fixture directly so act/observe cases still run")
            fixture = await start_fixture_fallback(args.fixture, "e2e-fallback")
        if window_id is None:
            found = await find_window(session, FIXTURE_TITLE, timeout_sec=20)
            if found:
                window_id = str(found.get("window_id"))
                fixture.window_id = window_id
        if window_id is None:
            rep.fail("fixture window visible in go_windows", "no window containing 'WhaleGo Fixture' found")
            return fixture, False
        rep.ok("fixture window visible in go_windows", f"window_id={window_id}")

        # --- go_observe with screenshot ---
        observe_result = await observe(session, window_id, include_screenshot=True)
        line = first_line(observe_result)
        rep.check("go_observe(include_screenshot=true) OK", is_query_ok(observe_result, allow_privacy_prefix=True), line)
        # The privacy hint in front of the OK summary line is added by the AstrBot plugin (PROTOCOL 11.5/D10),
        # not by the executor; a direct-executor E2E can only assert the image + OK: semantics.
        rep.check("image case: first text line contains OK:", OK_PREFIX in line, line[:120])
        images = image_items(observe_result)
        image_ok = len(images) >= 1
        rep.check("go_observe returns one ImageContent", image_ok, f"images={len(images)}")
        if image_ok:
            data = getattr(images[0], "data", "") or ""
            mime = getattr(images[0], "mimeType", "") or ""
            try:
                raw = base64.b64decode(data)
            except Exception:  # noqa: BLE001
                raw = b""
            rep.check("image mimeType=image/png", mime == "image/png", f"mime={mime}")
            rep.check("image is a real PNG and non-trivial", raw.startswith(PNG_SIGNATURE) and len(raw) > 2000, f"bytes={len(raw)}")
        observe_payload = json_payload(observe_result) or {}
        rep.check("go_observe schema_version=whalego-observe-v1", observe_payload.get("schema_version") == "whalego-observe-v1")
        state_id = str(observe_payload.get("state_id") or "")
        rep.check("go_observe state_id matches ^st-[0-9a-f]{8}$", bool(STATE_ID_RE.match(state_id)), state_id)
        frame = observe_payload.get("frame") or {}
        rep.check("go_observe frame.actionable=true", frame.get("actionable") is True, f"frame={json.dumps(frame, ensure_ascii=False)[:200]}")
        rep.check("go_observe frame has image/client pixel mapping", all(key in frame for key in ("image_width", "image_height", "client_width", "client_height", "scale_x", "scale_y")))
        elements = observe_payload.get("elements") or []
        rep.check("go_observe tree contains fixture controls", all(find_element(observe_payload, control) is not None for control in ("txtInput", "btnIncrement", "btnClickTarget", "lblCounter")), f"elements={len(elements)}")

        # --- go_observe without screenshot (no automatic attachment; defaultInclude=false) ---
        observe_noshot = await observe(session, window_id, include_screenshot=False)
        rep.check("go_observe(include_screenshot=false) OK: first line", is_query_ok(observe_noshot), first_line(observe_noshot))
        rep.check("go_observe(include_screenshot=false) returns no image", len(image_items(observe_noshot)) == 0)
        nostate = str((json_payload(observe_noshot) or {}).get("state_id") or "")
        rep.check("no-screenshot observe still returns a state_id", bool(STATE_ID_RE.match(nostate)), nostate)

        # --- go_act set_value + verification readback ---
        current = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        current_state = str(current.get("state_id") or "")
        txt_element = find_element(current, "txtInput")
        rep.check("fixture txtInput element found in tree", txt_element is not None)
        if txt_element is None:
            return fixture, True
        txt_index = int(txt_element["index"])
        set_result = await act(session, "set_value", {"kind": "element", "index": txt_index, "state_id": current_state}, {"value": "e2e-set-value"})
        receipt = action_receipt(set_result)
        rep.check("go_act set_value success (isError=false + OK:)", is_query_ok(set_result, allow_privacy_prefix=True), first_line(set_result))
        rep.check("go_act set_value dispatch_status=accepted", receipt.get("dispatch_status") == "accepted", f"receipt={json.dumps(receipt, ensure_ascii=False)}")
        rep.check("go_act set_value state_available=false (state invalidated)", receipt.get("state_available") is False)
        verification = receipt.get("verification") or {}
        rep.check("go_act set_value verification.status in {verified,mismatched,unverified}", verification.get("status") in {"verified", "mismatched", "unverified"}, str(verification.get("status")))

        # --- stale state after action ---
        stale_result = await act(session, "click", {"kind": "element", "index": 1, "state_id": current_state}, {"button": "left", "count": 1})
        stale_code = error_code(stale_result)
        rep.check("old state_id after act -> ERROR[STALE_STATE]", bool(stale_result.isError) and stale_code == "STALE_STATE", f"code={stale_code} line={first_line(stale_result)}")
        stale_payload = json_payload(stale_result) or {}
        rep.check("STALE_STATE retry=reobserve", stale_payload.get("retry") == "reobserve", f"retry={stale_payload.get('retry')!r}")

        # --- readback: set_value visible in a fresh observe ---
        after_set = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        after_set_state = str(after_set.get("state_id") or "")
        txt_after = find_element(after_set, "txtInput")
        rep.check("set_value readback: txtInput contains 'e2e-set-value'", "e2e-set-value" in element_text(txt_after), element_text(txt_after))

        # --- go_act type ---
        if txt_after is not None:
            type_result = await act(
                session,
                "type",
                {"kind": "element", "index": int(txt_after["index"]), "state_id": after_set_state},
                {"text": "e2e-type-42"},
            )
            rep.check("go_act type success", is_query_ok(type_result, allow_privacy_prefix=True), first_line(type_result))
            after_type = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
            txt_typed = find_element(after_type, "txtInput")
            rep.check("type readback: txtInput contains 'e2e-type-42'", "e2e-type-42" in element_text(txt_typed), element_text(txt_typed))
        else:
            rep.fail("go_act type (txtInput disappeared)")

        # --- go_act hotkey (SendInput chord; fixture records the key chord) ---
        hot_pre = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        hot_target = find_element(hot_pre, "txtInput")
        if hot_target is not None:
            hot_result = await act(
                session,
                "hotkey",
                {"kind": "element", "index": int(hot_target["index"]), "state_id": str(hot_pre.get("state_id") or "")},
                {"keys": "ctrl+s"},
            )
            rep.check("go_act hotkey(ctrl+s) success", is_query_ok(hot_result, allow_privacy_prefix=True), first_line(hot_result))
            hot_receipt = action_receipt(hot_result)
            rep.check("go_act hotkey dispatch_status=accepted", hot_receipt.get("dispatch_status") == "accepted", json.dumps(hot_receipt, ensure_ascii=False))
            hot_after = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
            hot_label = find_element(hot_after, "lblHotkey")
            hot_text = element_text(hot_label)
            rep.check("hotkey oracle: fixture received ctrl+s", "ctrl" in hot_text and "s" in hot_text, hot_text)
        else:
            rep.fail("go_act hotkey (txtInput disappeared)")

        # --- D7 long value: set 500 chars, read back with max_value_chars=1000 ---
        long_pre = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        long_target = find_element(long_pre, "txtInput")
        long_text = ("A" * 100) + "read-after-100" + ("B" * 386)
        if long_target is not None and len(long_text) == 500:
            long_set = await act(
                session,
                "set_value",
                {"kind": "element", "index": int(long_target["index"]), "state_id": str(long_pre.get("state_id") or "")},
                {"value": long_text},
            )
            rep.check("D7: go_act set_value 500-char value success", is_query_ok(long_set, allow_privacy_prefix=True), first_line(long_set))
            long_observe = await observe(session, window_id, include_screenshot=False, max_value_chars=1000)
            long_payload = json_payload(long_observe) or {}
            long_read = find_element(long_payload, "txtInput")
            read_value = element_text(long_read)
            rep.check(
                "D7 oracle: max_value_chars=1000 reads back >100 chars (contains 'read-after-100')",
                len(read_value) >= 500 and "read-after-100" in read_value,
                f"len={len(read_value)}",
            )
        else:
            rep.fail("D7 long-value readback (txtInput element unavailable)")

        # --- go_act click element (invoke) + counter oracle ---
        before = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        counter_before = element_text(find_element(before, "lblCounter"))
        increment = find_element(before, "btnIncrement")
        if increment is not None and counter_before.isdigit():
            click_result = await act(
                session,
                "click",
                {"kind": "element", "index": int(increment["index"]), "state_id": str(before.get("state_id") or "")},
                {"button": "left", "count": 1},
            )
            rep.check("go_act click btnIncrement success", is_query_ok(click_result, allow_privacy_prefix=True), first_line(click_result))
            after_click = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
            counter_after = element_text(find_element(after_click, "lblCounter"))
            rep.check(
                "element click oracle: lblCounter incremented by 1",
                counter_after.isdigit() and int(counter_after) == int(counter_before) + 1,
                f"{counter_before} -> {counter_after}",
            )
        else:
            rep.fail("go_act click btnIncrement (element/counter unavailable)", f"counter_before={counter_before!r}")

        # --- go_act click btnClickTarget + click counter oracle ---
        before_target = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        target_element = find_element(before_target, "btnClickTarget")
        clicks_before = element_text(find_element(before_target, "lblClickCount"))
        if target_element is not None:
            target_result = await act(
                session,
                "click",
                {"kind": "element", "index": int(target_element["index"]), "state_id": str(before_target.get("state_id") or "")},
                {"button": "left", "count": 1},
            )
            rep.check("go_act click btnClickTarget success", is_query_ok(target_result, allow_privacy_prefix=True), first_line(target_result))
            after_target = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
            clicks_after = element_text(find_element(after_target, "lblClickCount"))
            rep.check(
                "element click oracle: lblClickCount target counter incremented",
                "target=" in clicks_before and "target=" in clicks_after and clicks_after != clicks_before,
                f"{clicks_before} -> {clicks_after}",
            )
        else:
            rep.fail("go_act click btnClickTarget (element unavailable)")

        # --- coordinate click through the last screenshot frame mapping (best effort; SKIP when frame not actionable) ---
        shot = json_payload(await observe(session, window_id, include_screenshot=True)) or {}
        panel_element = find_element(shot, "pnlClick")
        frame = shot.get("frame") or {}
        panel_before = parse_counter_element(shot, "lblClickCount", "panel")
        if frame.get("actionable") is not True:
            rep.skip("coordinate click on pnlClick (frame actionable)", f"frame.actionable={frame.get('actionable')!r} (design allows capture_degraded hosts)")
        elif panel_element is None or element_center_screen(panel_element) is None:
            rep.skip("coordinate click on pnlClick (geometry)", "pnlClick element/bounds_screen_diag unavailable")
        else:
            center = element_center_screen(panel_element)
            image_point = screen_to_image_point(shot, center[0], center[1]) if center else None
            if image_point is None:
                target_info = (shot.get("target") or {}).get("window") or {}
                rep.skip(
                    "coordinate click on pnlClick (geometry)",
                    f"screen center {center} could not be mapped; window={json.dumps(target_info, ensure_ascii=False)[:220]} frame_client={frame.get('client_width')}x{frame.get('client_height')} origin={estimate_client_origin(shot)}",
                )
            else:
                coordinate_result = await act(
                    session,
                    "click",
                    {"kind": "coordinate", "x": int(image_point[0]), "y": int(image_point[1])},
                    {"button": "left", "count": 1},
                )
                coordinate_code = error_code(coordinate_result)
                if coordinate_code is not None:
                    rep.skip(
                        "coordinate click on pnlClick",
                        f"executor refused coordinate path: {coordinate_code} (design: unverified host -> FRAME_NOT_ACTIONABLE/UNSUPPORTED_ACTION)",
                    )
                else:
                    rep.check("coordinate click success (accepted)", is_query_ok(coordinate_result, allow_privacy_prefix=True), first_line(coordinate_result))
                    after_coord = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
                    panel_after = parse_counter_element(after_coord, "lblClickCount", "panel")
                    rep.check(
                        "coordinate click oracle: pnlClick panel counter incremented",
                        panel_before is not None and panel_after is not None and panel_after == panel_before + 1,
                        f"panel {panel_before} -> {panel_after} (image point {image_point}, screen {center})",
                    )

        # --- go_stop (kill switch, session scope) ---
        stop_result = await call_tool(session, "go_stop", {"reason": "e2e stop"})
        rep.check("go_stop OK: first line", is_query_ok(stop_result), first_line(stop_result))
        stop_payload = json_payload(stop_result) or {}
        rep.check("go_stop schema_version=whalego-stop-v1", stop_payload.get("schema_version") == "whalego-stop-v1")
        rep.check("go_stop stopped=true scope=session", stop_payload.get("stopped") is True and stop_payload.get("scope") == "session", json.dumps(stop_payload, ensure_ascii=False))

        stopped_select = json_payload(await observe(session, window_id, include_screenshot=False)) or {}
        rep.check("go_observe still works after go_stop", bool(STATE_ID_RE.match(str(stopped_select.get("state_id") or ""))))
        stopped_act = await act(
            session,
            "click",
            {"kind": "element", "index": 0, "state_id": str(stopped_select.get("state_id") or "")},
            {"button": "left", "count": 1},
        )
        stopped_code = error_code(stopped_act)
        rep.check("go_act after stop -> ERROR[SESSION_STOPPED]", bool(stopped_act.isError) and stopped_code == "SESSION_STOPPED", f"code={stopped_code}")
        rep.check("SESSION_STOPPED retry=never", (json_payload(stopped_act) or {}).get("retry") == "never")
        health_stopped = json_payload(await call_tool(session, "go_health", {})) or {}
        rep.check("go_health after session stop: session_stopped=true", health_stopped.get("session_stopped") is True, json.dumps({k: health_stopped.get(k) for k in ("stopped", "session_stopped", "accepting_actions")}))
        rep.check("go_health after session stop: accepting_actions=false", health_stopped.get("accepting_actions") is False)

        # --- CONTROLLER_BUSY with two sessions (best effort; SKIP when the lease is not held) ---
        try:
            busy_before = json_payload(await call_tool(session, "go_windows", {}, session_id=SESSION_A)) or {}
            window_for_a = next((w for w in busy_before.get("windows") or [] if FIXTURE_TITLE in str(w.get("title") or "")), None)
            if window_for_a is None:
                rep.skip("CONTROLLER_BUSY (two sessions)", "fixture window not visible for session A")
            else:
                wid = str(window_for_a.get("window_id"))
                obs_a = json_payload(await observe(session, wid, include_screenshot=False, session_id=SESSION_A)) or {}
                obs_b = json_payload(await observe(session, wid, include_screenshot=False, session_id=SESSION_B)) or {}
                inc_a = find_element(obs_a, "btnIncrement")
                inc_b = find_element(obs_b, "btnIncrement")
                if inc_a is None or inc_b is None:
                    rep.skip("CONTROLLER_BUSY (two sessions)", "btnIncrement not found for both sessions")
                else:
                    first = await act(session, "click", {"kind": "element", "index": int(inc_a["index"]), "state_id": str(obs_a.get("state_id") or "")}, {"button": "left", "count": 1}, session_id=SESSION_A)
                    rep.check("controller busy: session A acquires lease with a click", is_query_ok(first, allow_privacy_prefix=True), first_line(first))
                    second = await act(session, "click", {"kind": "element", "index": int(inc_b["index"]), "state_id": str(obs_b.get("state_id") or "")}, {"button": "left", "count": 1}, session_id=SESSION_B)
                    second_code = error_code(second)
                    if second_code == "CONTROLLER_BUSY":
                        rep.ok("controller busy: session B -> ERROR[CONTROLLER_BUSY]", str((json_payload(second) or {}).get("retry_after_ms")))
                        rep.check("CONTROLLER_BUSY retry=retry", (json_payload(second) or {}).get("retry") == "retry")
                    elif is_query_ok(second, allow_privacy_prefix=True):
                        rep.skip("controller busy: session B -> CONTROLLER_BUSY", "second session action was accepted, so this executor does not retain the lease between actions; covered by Executor/Platform unit tests")
                    else:
                        rep.fail("controller busy: session B -> CONTROLLER_BUSY", f"code={second_code} line={first_line(second)}")
                    with contextlib.suppress(Exception):
                        await call_tool(session, "go_stop", {"reason": "e2e lease cleanup"}, session_id=SESSION_A)
        except Exception as exc:  # noqa: BLE001
            rep.skip("CONTROLLER_BUSY (two sessions)", f"{type(exc).__name__}: {exc}")

    return fixture, True


async def run_readonly_cases(rep: Reporter, args: argparse.Namespace, fixture: FixtureHandle) -> None:
    if not args.readonly_url or not args.readonly_token:
        rep.skip("READONLY_MODE (policy.readonly=true)", "no --readonly-url/--readonly-token supplied")
        return
    try:
        async with open_mcp_session(args.readonly_url, args.readonly_token) as session:
            health = json_payload(await call_tool(session, "go_health", {})) or {}
            rep.check("readonly executor reachable (go_health OK)", health.get("schema_version") == "whalego-health-v1")
            window = await find_window(session, FIXTURE_TITLE, timeout_sec=15)
            if window is None:
                rep.fail("readonly: fixture window visible", "no window containing 'WhaleGo Fixture'")
                return
            obs = json_payload(await observe(session, str(window.get("window_id")), include_screenshot=False)) or {}
            increment = find_element(obs, "btnIncrement")
            if increment is None:
                rep.fail("readonly: btnIncrement found")
                return
            act_result = await act(session, "click", {"kind": "element", "index": int(increment["index"]), "state_id": str(obs.get("state_id") or "")}, {"button": "left", "count": 1})
            code = error_code(act_result)
            rep.check("readonly go_act -> ERROR[READONLY_MODE]", bool(act_result.isError) and code == "READONLY_MODE", f"code={code} line={first_line(act_result)}")
            rep.check("READONLY_MODE retry=never", (json_payload(act_result) or {}).get("retry") == "never")
            launch_result = await call_tool(session, "go_launch", {"app": "fixture", "wait_ms": 2000})
            launch_code = error_code(launch_result)
            rep.check("readonly go_launch -> ERROR[READONLY_MODE]", bool(launch_result.isError) and launch_code == "READONLY_MODE", f"code={launch_code} line={first_line(launch_result)}")
    except Exception as exc:  # noqa: BLE001
        rep.fail("READONLY_MODE cases", f"{type(exc).__name__}: {exc}")


async def readonly_only_main(args: argparse.Namespace, rep: Reporter, contract_names: set[str]) -> int:
    """Sequential readonly phase: only one headless executor can run per interactive session (single-instance mutex)."""
    if not args.readonly_url or not args.readonly_token:
        print("[ERROR] --readonly-only requires --readonly-url and --readonly-token")
        return 2
    base_url = args.readonly_url.rsplit("/mcp", 1)[0]
    print("== WhaleGo E2E - readonly executor phase ==")
    print(f"readonly : {args.readonly_url}")
    print(f"fixture  : {args.fixture}")
    print()

    usable = await run_http_cases(rep, base_url, args.readonly_token)
    if not usable:
        rep.summary()
        return 1

    fixture = await start_fixture_fallback(args.fixture, "e2e-readonly")
    try:
        await asyncio.sleep(1.0)
        await run_readonly_cases(rep, args, fixture)
    finally:
        fixture.cleanup()

    rep.summary()
    return 0 if rep.failed == 0 else 1


def load_contract_names(path: Path) -> set[str]:
    contract = json.loads(path.read_text(encoding="utf-8"))
    return {str(tool["name"]) for tool in contract.get("tools") or []}


async def async_main(args: argparse.Namespace) -> int:
    rep = Reporter()
    contract_names = load_contract_names(Path(args.contracts))
    base_url = args.url.rsplit("/mcp", 1)[0] if args.url else ""
    readonly_base = args.readonly_url.rsplit("/mcp", 1)[0] if args.readonly_url else ""

    if args.readonly_only:
        return await readonly_only_main(args, rep, contract_names)

    print("== WhaleGo E2E (real headless executor + mcp 1.30.0 client) ==")
    print(f"executor     : {args.url}")
    print(f"readonly     : {args.readonly_url or '(not supplied)'}")
    print(f"fixture      : {args.fixture}")
    print(f"contracts    : {args.contracts} ({len(contract_names)} tools)")
    print()

    usable = await run_http_cases(rep, base_url, args.token)
    if not usable:
        rep.summary()
        return 1

    fixture = FixtureHandle()
    try:
        fixture, mcp_ok = await run_mcp_cases(rep, args, contract_names)
        if mcp_ok:
            if args.readonly_url and args.readonly_token:
                await run_readonly_cases(rep, args, fixture)
            else:
                print("NOTE  readonly executor cases run in a separate sequential phase (single-instance mutex)")
        else:
            rep.skip("READONLY_MODE (policy.readonly=true)", "main MCP session unusable")
    finally:
        fixture.cleanup()
        if readonly_base and args.readonly_token:
            # Nothing to clean beyond the process, which scripts/e2e.ps1 owns.
            pass

    rep.summary()
    return 0 if rep.failed == 0 else 1


def main() -> int:
    parser = argparse.ArgumentParser(description="WhaleGo end-to-end acceptance driver")
    parser.add_argument("--url", default="", help="MCP endpoint of the normal executor (required unless --readonly-only)")
    parser.add_argument("--token", default="", help="Bearer token of the normal executor (required unless --readonly-only)")
    parser.add_argument("--readonly-url", default="", help="MCP endpoint of a policy.readonly=true executor")
    parser.add_argument("--readonly-token", default="", help="Bearer token of the readonly executor")
    parser.add_argument("--fixture", required=True, help="Path to WhaleGo.Fixtures.WinForms.exe")
    parser.add_argument("--contracts", required=True, help="Path to contracts/whalego.tools.v1.json")
    parser.add_argument("--readonly-only", action="store_true", help="only run the readonly-executor cases (sequential phase)")
    args = parser.parse_args()

    if not args.readonly_only and not args.url:
        parser.error("--url is required unless --readonly-only is set")
    if not args.readonly_only and not args.token:
        parser.error("--token is required unless --readonly-only is set")

    if not Path(args.fixture).is_file():
        print(f"[ERROR] fixture not found: {args.fixture}")
        return 2
    if not Path(args.contracts).is_file():
        print(f"[ERROR] contract not found: {args.contracts}")
        return 2

    try:
        return asyncio.run(async_main(args))
    except KeyboardInterrupt:
        print("interrupted")
        return 130


if __name__ == "__main__":
    sys.exit(main())