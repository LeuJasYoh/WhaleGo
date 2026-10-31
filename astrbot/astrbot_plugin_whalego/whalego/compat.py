"""WhaleGo AstrBot 插件的 L1 协议兼容层（mcp SDK 版本差异适配）。

本模块不得 import astrbot。

必须同时支持：
- 容器 mcp 1.30.0 的 legacy `streamablehttp_client(...) -> (read, write, get_session_id)`（3 元组）；
- 新版 `streamable_http_client(url, http_client=httpx.AsyncClient(...)) -> (read, write)`（2 元组）。

同时屏蔽 mcp 结果模型可能的字段差异（`isError`/`is_error`、`mimeType`/`mime_type`），
并提供异常分类（auth / protocol / timeout / unreachable / lost）。
"""

from __future__ import annotations

import importlib
import inspect
from collections.abc import AsyncIterator, Mapping
from contextlib import AsyncExitStack, asynccontextmanager
from dataclasses import dataclass
from datetime import timedelta
from functools import lru_cache
from typing import Any, Final

import httpx

from .config import ConnectionConfig

TRANSPORT_MODULE: Final = "mcp.client.streamable_http"

AUTH_ERROR: Final = "auth"
PROTOCOL_ERROR: Final = "protocol"
TIMEOUT_ERROR: Final = "timeout"
UNREACHABLE_ERROR: Final = "unreachable"
LOST_ERROR: Final = "lost"
CONNECTION_ERROR_KINDS: Final[tuple[str, ...]] = (
    AUTH_ERROR,
    PROTOCOL_ERROR,
    TIMEOUT_ERROR,
    UNREACHABLE_ERROR,
    LOST_ERROR,
)


class UnsupportedTransportError(RuntimeError):
    """当前 mcp SDK 既没有 legacy 也没有新版 Streamable HTTP 客户端。"""


@dataclass(frozen=True)
class TransportStreams:
    """统一后的 Streamable HTTP 传输三元组；新版 SDK 无 get_session_id 时为 None。"""

    read: Any
    write: Any
    get_session_id: Any | None = None


def normalize_streams(value: Any) -> TransportStreams:
    """接受 2 元组 (read, write) 或 3 元组 (read, write, get_session_id)。"""
    if isinstance(value, (tuple, list)):
        if len(value) == 2:
            return TransportStreams(read=value[0], write=value[1], get_session_id=None)
        if len(value) == 3:
            return TransportStreams(read=value[0], write=value[1], get_session_id=value[2])
    raise UnsupportedTransportError(
        f"Streamable HTTP transport 返回值形状不受支持：{type(value)!r}（期望 2/3 元组）"
    )


def coerce_streams(value: Any) -> TransportStreams:
    """把 transport 返回值统一成 TransportStreams（已归一化对象或 2/3 元组）。"""
    if isinstance(value, TransportStreams):
        return value
    return normalize_streams(value)


def _accepts_keyword(func: Any, keyword: str) -> bool:
    try:
        parameters = inspect.signature(func).parameters
    except (TypeError, ValueError):
        return False
    return keyword in parameters or any(
        parameter.kind is inspect.Parameter.VAR_KEYWORD for parameter in parameters.values()
    )


@lru_cache(maxsize=8)
def load_transport_module(module_name: str = TRANSPORT_MODULE) -> Any:
    """导入并缓存 mcp Streamable HTTP 模块（测试可注入 fake module 绕开本函数）。"""
    return importlib.import_module(module_name)


def resolve_transport_kind(module: Any | None = None) -> str:
    """返回 "modern"（streamable_http_client）或 "legacy"（streamablehttp_client）。"""
    module = module or load_transport_module()
    modern = getattr(module, "streamable_http_client", None)
    legacy = getattr(module, "streamablehttp_client", None)
    if modern is not None and _accepts_keyword(modern, "http_client"):
        return "modern"
    if legacy is not None:
        return "legacy"
    if modern is not None:
        return "modern"
    raise UnsupportedTransportError(
        "当前 mcp SDK 不提供 Streamable HTTP 客户端（需要 streamable_http_client 或 streamablehttp_client）"
    )


def _build_http_client(config: ConnectionConfig, http_client_factory: Any | None) -> Any:
    if http_client_factory is not None:
        return http_client_factory(config)
    return httpx.AsyncClient(
        headers=config.headers(),
        timeout=httpx.Timeout(config.timeout, read=300.0),
        follow_redirects=True,
    )


@asynccontextmanager
async def open_transport(
    config: ConnectionConfig,
    *,
    module: Any | None = None,
    http_client_factory: Any | None = None,
) -> AsyncIterator[TransportStreams]:
    """建立 Streamable HTTP 传输，产出归一化的 (read, write, get_session_id)。

    用法：`async with open_transport(cfg) as streams:`。
    所有 anyio cancel scope 的进入/退出都在同一个 async context manager 内完成
    （调用方需在同一个 asyncio Task 中 connect/close，见 mcp_client 的 owner-task 方案）。
    """
    module = module or load_transport_module()
    kind = resolve_transport_kind(module)
    async with AsyncExitStack() as stack:
        if kind == "modern":
            modern = module.streamable_http_client
            client = _build_http_client(config, http_client_factory)
            await stack.enter_async_context(client)
            raw = await stack.enter_async_context(modern(config.url, http_client=client))
            yield normalize_streams(raw)
            return
        legacy = module.streamablehttp_client
        raw = await stack.enter_async_context(
            legacy(
                config.url,
                headers=config.headers(),
                timeout=timedelta(seconds=config.timeout),
                sse_read_timeout=timedelta(seconds=300),
                terminate_on_close=True,
            )
        )
        yield normalize_streams(raw)


def create_session(read: Any, write: Any) -> Any:
    """创建 mcp.ClientSession（async context manager），兼容参数名差异。"""
    from mcp import ClientSession

    kwargs: dict[str, Any] = {}
    try:
        parameters = inspect.signature(ClientSession.__init__).parameters
    except (TypeError, ValueError):
        parameters = {}
    if "read_timeout_seconds" in parameters:
        kwargs["read_timeout_seconds"] = None
    if "read_stream" in parameters and "write_stream" in parameters:
        return ClientSession(read_stream=read, write_stream=write, **kwargs)
    return ClientSession(read, write, **kwargs)


# ---------------------------------------------------------------------------
# CallToolResult / Content 字段差异读取（camel / snake）
# ---------------------------------------------------------------------------


def _camel_to_snake(name: str) -> str:
    result: list[str] = []
    for index, char in enumerate(name):
        if char.isupper():
            if index:
                result.append("_")
            result.append(char.lower())
        else:
            result.append(char)
    return "".join(result)


def _attr(item: Any, name: str, default: Any = None) -> Any:
    if isinstance(item, Mapping):
        if name in item:
            return item[name]
        snake = _camel_to_snake(name)
        if snake in item:
            return item[snake]
        return default
    sentinel = object()
    value = getattr(item, name, sentinel)
    if value is not sentinel:
        return value
    value = getattr(item, _camel_to_snake(name), sentinel)
    return default if value is sentinel else value


def result_is_error(result: Any) -> bool:
    return bool(_attr(result, "isError", _attr(result, "is_error", False)))


def iter_contents(result: Any) -> list[Any]:
    content = _attr(result, "content", None)
    if isinstance(content, (list, tuple)):
        return list(content)
    return []


def content_kind(item: Any) -> str:
    kind = _attr(item, "type", "")
    return kind if isinstance(kind, str) else ""


def content_text(item: Any) -> str | None:
    if content_kind(item) not in {"", "text"}:
        return None
    text = _attr(item, "text", None)
    return text if isinstance(text, str) else None


def content_image(item: Any) -> tuple[str, str] | None:
    if content_kind(item) != "image":
        return None
    data = _attr(item, "data", None)
    mime = _attr(item, "mimeType", _attr(item, "mime_type", "image/png"))
    if not isinstance(data, str) or not data:
        return None
    if not isinstance(mime, str) or not mime:
        mime = "image/png"
    return data, mime


def make_text_content(text: str) -> Any:
    from mcp import types

    return types.TextContent(type="text", text=text)


def make_image_content(data: str, mime_type: str) -> Any:
    from mcp import types

    return types.ImageContent(type="image", data=data, mimeType=mime_type)


def make_call_tool_result(content: list[Any], *, is_error: bool) -> Any:
    from mcp import types

    return types.CallToolResult(content=content, isError=is_error)


# ---------------------------------------------------------------------------
# 异常分类
# ---------------------------------------------------------------------------


def _status_code(exc: BaseException) -> int | None:
    response = getattr(exc, "response", None)
    status = getattr(response, "status_code", None)
    return status if isinstance(status, int) else None


def _walk_exception_chain(exc: BaseException) -> list[BaseException]:
    chain: list[BaseException] = []
    seen: set[int] = set()
    current: BaseException | None = exc
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        chain.append(current)
        current = current.__cause__ or current.__context__
    return chain


def classify_exception(exc: BaseException) -> str:
    """把 mcp/httpx/anyio 异常映射为插件侧错误类别（PROTOCOL §8.1）。"""
    import anyio
    from mcp.shared.exceptions import McpError

    # anyio TaskGroup 会把子任务异常包成 ExceptionGroup（连接失败常见）。
    if isinstance(exc, BaseExceptionGroup):
        kinds = [classify_exception(sub) for sub in exc.exceptions]
        for kind in kinds:
            if kind != LOST_ERROR:
                return kind
        return LOST_ERROR

    for item in _walk_exception_chain(exc):
        status = _status_code(item)
        if status in (401, 403):
            return AUTH_ERROR
        if isinstance(item, httpx.HTTPStatusError):
            if status is not None and status >= 500:
                return LOST_ERROR
            return AUTH_ERROR
        if isinstance(item, (httpx.ConnectError, httpx.ConnectTimeout)):
            return UNREACHABLE_ERROR
        if isinstance(item, (httpx.ReadTimeout, httpx.WriteTimeout, httpx.PoolTimeout)):
            return TIMEOUT_ERROR
        if isinstance(item, TimeoutError):
            return TIMEOUT_ERROR
        if isinstance(item, McpError):
            message = str(item).lower()
            if "401" in message or "unauthorized" in message:
                return AUTH_ERROR
            if "protocol" in message or "initialize" in message:
                return PROTOCOL_ERROR
            return LOST_ERROR
        if isinstance(
            item,
            (anyio.ClosedResourceError, anyio.BrokenResourceError, anyio.EndOfStream),
        ):
            return LOST_ERROR
    if isinstance(exc, TimeoutError):
        return TIMEOUT_ERROR
    return LOST_ERROR
