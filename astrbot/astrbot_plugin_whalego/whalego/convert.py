"""WhaleGo AstrBot 插件的 L2 结果转换层（唯一转换点，ARCHITECTURE §4.5）。

本模块不得 import astrbot。

职责：
1. 执行器 `CallToolResult` → AstrBot 可消费的 `mcp.types.CallToolResult`；
2. 强制/修复文本前缀：成功 `OK:`，失败 `ERROR[<code>]:`（AstrBot 不读 isError）；
3. 图片保留与隐私提示：有图时首条文本以 `OK:` 开头，并在正文最前面插入固定隐私提示；
   三开关 AND 的插件侧（`allow_screenshot`）与 provider 图片能力（`image_supported`）在这里兜底；
4. 文本预算保护：截断非 JSON 文本，结构化 JSON 永不截断；
5. 插件侧错误 → `ERROR[CODE]: <中文>` + `whalego-error-v1` JSON。
"""

from __future__ import annotations

import json
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any, Final

from . import compat
from .errors import GoError

OK_PREFIX: Final = "OK:"
ERROR_PREFIX: Final = "ERROR["
PRIVACY_HINT: Final = "注意：桌面截图包含用户隐私，未经用户明确要求不得转发。"
IMAGE_UNSUPPORTED_NOTE: Final = (
    "（当前模型不支持图片输入，截图已省略；请使用 UIA 树 / 元素操作，不要做坐标点击。）"
)
IMAGE_DISABLED_NOTE: Final = "（插件已禁用截图，未附图片。）"
TRUNCATION_NOTE: Final = "\n…（输出过长，已截断）"
MAX_TEXT_CHARS: Final = 20000


@dataclass
class ToolCallOutcome:
    """一次工具调用的结果：执行器原始 result 或插件侧 GoError（二选一）。"""

    result: Any | None = None
    error: GoError | None = None

    @property
    def ok(self) -> bool:
        if self.error is not None:
            return False
        if self.result is None:
            return False
        if compat.result_is_error(self.result):
            return False
        texts = [text for text in _raw_texts(self.result) if text]
        return bool(texts) and texts[0].startswith(OK_PREFIX)


def ok_outcome(result: Any) -> ToolCallOutcome:
    return ToolCallOutcome(result=result)


def error_outcome(error: GoError) -> ToolCallOutcome:
    return ToolCallOutcome(error=error)


def _raw_texts(result: Any) -> list[str]:
    texts: list[str] = []
    for item in compat.iter_contents(result):
        text = compat.content_text(item)
        if text is not None:
            texts.append(text)
    return texts


def _raw_images(result: Any) -> list[tuple[str, str]]:
    images: list[tuple[str, str]] = []
    for item in compat.iter_contents(result):
        image = compat.content_image(item)
        if image is not None:
            images.append(image)
    return images


def error_result(error: GoError) -> Any:
    """构造失败通道：`ERROR[CODE]: 中文文案` + 错误对象 JSON（JSON 不截断）。"""
    payload = json.dumps(error.to_dict(), ensure_ascii=False, separators=(",", ":"))
    return compat.make_call_tool_result(
        [compat.make_text_content(error.to_text()), compat.make_text_content(payload)],
        is_error=True,
    )


def error_text(error: GoError) -> str:
    return error.to_text()


def _is_json_text(text: str) -> bool:
    stripped = text.lstrip()
    return stripped.startswith("{") or stripped.startswith("[")


def _repair_prefix(texts: list[str], *, is_error: bool) -> tuple[list[str], bool]:
    """修复首条文本前缀；返回 (texts, canonical_is_error)。"""
    repaired = list(texts)
    if not repaired:
        repaired = [""]
    first = repaired[0]
    text_says_error = first.startswith(ERROR_PREFIX)
    canonical_error = is_error or text_says_error
    if canonical_error:
        if not text_says_error:
            repaired[0] = (
                f"{ERROR_PREFIX}INTERNAL]: {first}"
                if first
                else (f"{ERROR_PREFIX}INTERNAL]: 执行器返回了未标注错误码的失败。")
            )
    elif not first.startswith(OK_PREFIX):
        repaired[0] = f"{OK_PREFIX} {first}".rstrip() if first else f"{OK_PREFIX} 完成。"
    return repaired, canonical_error


def with_privacy_hint(text: str) -> str:
    """有截图时在 OK 摘要行正文最前面插入固定隐私提示（不重复插入）。"""
    if PRIVACY_HINT in text:
        return text
    if text.startswith(OK_PREFIX):
        body = text[len(OK_PREFIX) :].lstrip()
        return f"{OK_PREFIX} {PRIVACY_HINT} {body}".rstrip()
    return f"{OK_PREFIX} {PRIVACY_HINT} {text}".rstrip()


def _truncate_texts(texts: list[str], max_chars: int) -> list[str]:
    """按总字符预算截断非 JSON 文本；结构化 JSON 始终保留完整。"""
    if max_chars <= 0:
        return texts
    total = sum(len(text) for text in texts)
    if total <= max_chars:
        return texts
    budget = max_chars
    truncated: list[str] = []
    for text in texts:
        if _is_json_text(text):
            truncated.append(text)
            continue
        if budget <= 0:
            continue
        if len(text) <= budget:
            truncated.append(text)
            budget -= len(text)
            continue
        keep = max(budget - len(TRUNCATION_NOTE), 0)
        truncated.append(text[:keep] + TRUNCATION_NOTE)
        budget = 0
    return truncated or texts


def to_tool_result(
    outcome: ToolCallOutcome,
    *,
    allow_screenshot: bool = True,
    image_supported: bool = True,
    max_text_chars: int = MAX_TEXT_CHARS,
) -> Any:
    """把 ToolCallOutcome 转换成 AstrBot 可直接返回的 `mcp.types.CallToolResult`。"""
    if outcome.error is not None:
        return error_result(outcome.error)
    result = outcome.result
    if result is None:
        return error_result(_internal_error("模型可见结果缺失", "empty_result"))

    texts = _raw_texts(result)
    images = _raw_images(result)
    is_error = compat.result_is_error(result)
    texts, canonical_error = _repair_prefix(texts, is_error=is_error)

    keep_image = bool(images) and not canonical_error
    if images and not keep_image:
        images = []
    elif keep_image and not allow_screenshot:
        images = []
        texts.append(IMAGE_DISABLED_NOTE)
    elif keep_image and not image_supported:
        images = []
        texts.append(IMAGE_UNSUPPORTED_NOTE)

    if images:
        texts[0] = with_privacy_hint(texts[0])

    texts = _truncate_texts(texts, max_text_chars)
    content: list[Any] = []
    for data, mime in images:
        content.append(compat.make_image_content(data, mime))
    for text in texts:
        content.append(compat.make_text_content(text))
    return compat.make_call_tool_result(content, is_error=canonical_error)


def _internal_error(message: str, reason: str) -> GoError:
    from .errors import make_error

    return make_error("INTERNAL", message, details={"reason": reason})


def parse_json_payload(result: Any) -> dict[str, Any] | None:
    """从结果文本中提取第一个结构化 JSON（执行器固定把 JSON 放在文本 content 里）。"""
    for text in _raw_texts(result):
        stripped = text.strip()
        if not stripped.startswith("{"):
            continue
        try:
            payload = json.loads(stripped)
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict):
            return payload
    return None


def contains_image(result: Any) -> bool:
    return bool(_raw_images(result))


def first_text(result: Any) -> str:
    """返回结果的第一条文本（命令/日志展示用；无文本返回空串）。"""
    texts = _raw_texts(result)
    return texts[0] if texts else ""


def outcome_failed(outcome: ToolCallOutcome) -> bool:
    """判断 outcome 是否失败（插件侧 GoError，或执行器返回的 isError/ERROR[...] 文本）。"""
    if outcome.error is not None:
        return True
    if outcome.result is None:
        return True
    if compat.result_is_error(outcome.result):
        return True
    return first_text(outcome.result).startswith(ERROR_PREFIX)


def describe(result: Any) -> Mapping[str, Any]:
    """调试/测试辅助：结果形状摘要（不包含图片字节）。"""
    return {
        "is_error": compat.result_is_error(result),
        "texts": _raw_texts(result),
        "images": [mime for _, mime in _raw_images(result)],
    }
