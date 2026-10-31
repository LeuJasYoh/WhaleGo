from __future__ import annotations

from contextlib import asynccontextmanager
from types import SimpleNamespace

import anyio
import httpx
import mcp.types as mcp_types
import pytest
from astrbot_plugin_whalego.whalego import compat
from astrbot_plugin_whalego.whalego.config import ConnectionConfig

CFG = ConnectionConfig(url="http://127.0.0.1:8765/mcp", token="wg_test", timeout=45)


# ---------------------------------------------------------------------------
# 传输元组形状 & 客户端选择
# ---------------------------------------------------------------------------


def test_normalize_streams_accepts_3_and_2_tuples():
    read, write = object(), object()
    callback = lambda: "sid"  # noqa: E731
    three = compat.normalize_streams((read, write, callback))
    assert three.read is read and three.write is write
    assert three.get_session_id is callback
    two = compat.normalize_streams((read, write))
    assert two.get_session_id is None
    with pytest.raises(compat.UnsupportedTransportError):
        compat.normalize_streams((read,))
    with pytest.raises(compat.UnsupportedTransportError):
        compat.normalize_streams("nope")


def _fake_legacy_module() -> SimpleNamespace:
    recorded: dict[str, object] = {}

    @asynccontextmanager
    async def streamablehttp_client(
        url, headers=None, timeout=None, sse_read_timeout=None, terminate_on_close=True
    ):
        recorded.update(
            {
                "url": url,
                "headers": headers,
                "timeout": timeout,
                "sse_read_timeout": sse_read_timeout,
                "terminate_on_close": terminate_on_close,
            }
        )
        yield (object(), object(), lambda: "legacy-session")

    return SimpleNamespace(streamablehttp_client=streamablehttp_client, _recorded=recorded)


def _fake_modern_module() -> SimpleNamespace:
    recorded: dict[str, object] = {}

    @asynccontextmanager
    async def streamable_http_client(url, *, http_client=None, terminate_on_close=True):
        recorded.update({"url": url, "http_client": http_client})
        yield (object(), object())  # 新版 API 返回 2 元组

    return SimpleNamespace(streamable_http_client=streamable_http_client, _recorded=recorded)


def test_resolve_transport_kind_prefers_modern_with_http_client():
    assert compat.resolve_transport_kind(_fake_modern_module()) == "modern"
    assert compat.resolve_transport_kind(_fake_legacy_module()) == "legacy"
    both = SimpleNamespace(
        streamable_http_client=_fake_modern_module().streamable_http_client,
        streamablehttp_client=_fake_legacy_module().streamablehttp_client,
    )
    assert compat.resolve_transport_kind(both) == "modern"
    with pytest.raises(compat.UnsupportedTransportError):
        compat.resolve_transport_kind(SimpleNamespace())


async def test_open_transport_legacy_3_tuple():
    module = _fake_legacy_module()
    async with compat.open_transport(CFG, module=module) as streams:
        assert streams.get_session_id() == "legacy-session"
    recorded = module._recorded
    assert recorded["url"] == CFG.url
    assert recorded["headers"] == {"Authorization": "Bearer wg_test"}
    assert recorded["terminate_on_close"] is True


def _fake_http_client_records(recorded: dict[str, object]):
    class FakeClient:
        async def __aenter__(self):
            recorded["entered"] = True
            return self

        async def __aexit__(self, exc_type, exc, tb):
            recorded["exited"] = True
            return False

    return FakeClient()


async def test_open_transport_modern_2_tuple_with_headers_on_http_client():
    module = _fake_modern_module()
    recorded: dict[str, object] = {}

    def factory(config: ConnectionConfig):
        recorded["config"] = config
        return _fake_http_client_records(recorded)

    async with compat.open_transport(CFG, module=module, http_client_factory=factory) as streams:
        assert streams.get_session_id is None
    assert module._recorded["http_client"] is recorded or recorded["entered"] is True
    assert recorded["config"].headers() == {"Authorization": "Bearer wg_test"}
    assert recorded["exited"] is True


# ---------------------------------------------------------------------------
# 结果/内容字段兼容（camel / snake）
# ---------------------------------------------------------------------------


def test_content_helpers_with_real_mcp_types():
    result = mcp_types.CallToolResult(
        content=[
            mcp_types.ImageContent(type="image", data="aGVsbG8=", mimeType="image/png"),
            mcp_types.TextContent(type="text", text="OK: 完成"),
        ],
        isError=False,
    )
    assert compat.result_is_error(result) is False
    contents = compat.iter_contents(result)
    assert len(contents) == 2
    assert compat.content_image(contents[0]) == ("aGVsbG8=", "image/png")
    assert compat.content_text(contents[1]) == "OK: 完成"


def test_content_helpers_with_dict_snake_case():
    result = {"is_error": True, "content": [{"type": "text", "text": "ERROR[X]: 失败"}]}
    assert compat.result_is_error(result) is True
    assert compat.content_text(compat.iter_contents(result)[0]) == "ERROR[X]: 失败"
    image = {"type": "image", "data": "aaa", "mime_type": "image/webp"}
    assert compat.content_image(image) == ("aaa", "image/webp")


def test_make_result_roundtrip():
    result = compat.make_call_tool_result(
        [compat.make_text_content("OK: x"), compat.make_image_content("aaa", "image/png")],
        is_error=False,
    )
    assert isinstance(result, mcp_types.CallToolResult)
    assert compat.result_is_error(result) is False
    assert compat.content_text(compat.iter_contents(result)[0]) == "OK: x"


# ---------------------------------------------------------------------------
# 异常分类
# ---------------------------------------------------------------------------


def _http_status_error(status: int) -> httpx.HTTPStatusError:
    request = httpx.Request("POST", "http://127.0.0.1:8765/mcp")
    response = httpx.Response(status, request=request)
    return httpx.HTTPStatusError("boom", request=request, response=response)


def test_classify_auth_errors():
    assert compat.classify_exception(_http_status_error(401)) == compat.AUTH_ERROR
    assert compat.classify_exception(_http_status_error(403)) == compat.AUTH_ERROR


def test_classify_transport_errors():
    assert compat.classify_exception(httpx.ConnectError("refused")) == compat.UNREACHABLE_ERROR
    assert compat.classify_exception(httpx.ConnectTimeout("timeout")) == compat.UNREACHABLE_ERROR
    assert compat.classify_exception(httpx.ReadTimeout("timeout")) == compat.TIMEOUT_ERROR
    assert compat.classify_exception(TimeoutError()) == compat.TIMEOUT_ERROR
    assert compat.classify_exception(anyio.ClosedResourceError()) == compat.LOST_ERROR
    assert compat.classify_exception(anyio.BrokenResourceError()) == compat.LOST_ERROR
    assert compat.classify_exception(_http_status_error(500)) == compat.LOST_ERROR


def test_classify_unknown_is_lost():
    assert compat.classify_exception(RuntimeError("weird")) == compat.LOST_ERROR


def test_classify_exception_group_uses_child_kind():
    group = ExceptionGroup("task group", [httpx.ConnectError("refused")])
    assert compat.classify_exception(group) == compat.UNREACHABLE_ERROR
    mixed = BaseExceptionGroup("task group", [RuntimeError("x"), httpx.ReadTimeout("slow")])
    assert compat.classify_exception(mixed) == compat.TIMEOUT_ERROR
    nested = ExceptionGroup("outer", [ExceptionGroup("inner", [httpx.ConnectError("no")])])
    assert compat.classify_exception(nested) == compat.UNREACHABLE_ERROR
