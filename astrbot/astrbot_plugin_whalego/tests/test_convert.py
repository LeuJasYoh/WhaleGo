from __future__ import annotations

import json

import mcp.types as mcp_types
from _helpers import image_result, ok, outcome_error, outcome_ok, text_result
from astrbot_plugin_whalego.whalego import compat, convert, errors


def _texts(result: mcp_types.CallToolResult) -> list[str]:
    return [
        item.text  # type: ignore[attr-defined]
        for item in result.content
        if isinstance(item, mcp_types.TextContent)
    ]


def _images(result: mcp_types.CallToolResult) -> list[mcp_types.ImageContent]:
    return [item for item in result.content if isinstance(item, mcp_types.ImageContent)]


def test_success_text_keeps_ok_prefix_and_order():
    result = convert.to_tool_result(outcome_ok())
    assert result.isError is False
    texts = _texts(result)
    assert texts[0].startswith("OK:")
    assert convert.PRIVACY_HINT not in texts[0]


def test_missing_ok_prefix_repaired():
    outcome = convert.ok_outcome(text_result("观察完成"))
    result = convert.to_tool_result(outcome)
    assert _texts(result)[0].startswith("OK:")


def test_executor_error_prefix_and_is_error_preserved():
    raw = text_result(
        "ERROR[STALE_STATE]: 观察状态已过期，请先重新调用 go_observe。", '{"code":"STALE_STATE"}', is_error=True
    )
    result = convert.to_tool_result(convert.ok_outcome(raw))
    assert result.isError is True
    texts = _texts(result)
    assert texts[0] == "ERROR[STALE_STATE]: 观察状态已过期，请先重新调用 go_observe。"
    assert texts[1] == '{"code":"STALE_STATE"}'


def test_error_without_prefix_repaired_to_internal():
    result = convert.to_tool_result(convert.ok_outcome(text_result("炸了", is_error=True)))
    assert result.isError is True
    assert _texts(result)[0].startswith("ERROR[INTERNAL]:")


def test_text_error_without_iserror_still_treated_as_error():
    raw = text_result("ERROR[PAUSED]: 用户已在本机暂停", is_error=False)
    result = convert.to_tool_result(convert.ok_outcome(raw))
    assert result.isError is True


def test_plugin_error_result_shape():
    outcome = convert.error_outcome(errors.action_uncertain_error(tool="go_act", reason="timeout"))
    result = convert.to_tool_result(outcome)
    assert result.isError is True
    texts = _texts(result)
    assert texts[0] == "ERROR[DISPATCH_UNCERTAIN]: 动作可能已发送，禁止重试，请先重新观察。"
    payload = json.loads(texts[1])
    assert payload["schema_version"] == "whalego-error-v1"
    assert payload["code"] == "DISPATCH_UNCERTAIN"
    assert payload["retry"] == "reobserve"
    assert payload["action_receipt"]["dispatch_status"] == "possibly_sent"


def test_image_preserved_with_privacy_hint_at_front_of_ok_line():
    raw = image_result("OK: 观察完成 window=w-1 state_id=st-1 elements=3 screenshot=yes")
    result = convert.to_tool_result(convert.ok_outcome(raw), allow_screenshot=True, image_supported=True)
    images = _images(result)
    assert len(images) == 1
    assert isinstance(result.content[0], mcp_types.ImageContent), "图片必须在 content 最前（协议约定）"
    first_text = _texts(result)[0]
    assert first_text.startswith("OK:")
    assert convert.PRIVACY_HINT in first_text
    assert first_text == (
        "OK: " + convert.PRIVACY_HINT + " 观察完成 window=w-1 state_id=st-1 elements=3 screenshot=yes"
    )


def test_image_hint_not_duplicated():
    raw = image_result("OK: " + convert.PRIVACY_HINT + " 观察完成")
    result = convert.to_tool_result(convert.ok_outcome(raw))
    assert _texts(result)[0].count(convert.PRIVACY_HINT) == 1


def test_image_dropped_when_model_does_not_support_image():
    raw = image_result("OK: 观察完成 screenshot=yes")
    result = convert.to_tool_result(convert.ok_outcome(raw), allow_screenshot=True, image_supported=False)
    assert _images(result) == []
    texts = _texts(result)
    assert texts[-1] == convert.IMAGE_UNSUPPORTED_NOTE
    assert convert.PRIVACY_HINT not in texts[0]


def test_image_dropped_when_plugin_disallows_screenshot():
    raw = image_result("OK: 观察完成 screenshot=yes")
    result = convert.to_tool_result(convert.ok_outcome(raw), allow_screenshot=False, image_supported=True)
    assert _images(result) == []
    assert _texts(result)[-1] == convert.IMAGE_DISABLED_NOTE


def test_image_dropped_on_error_result():
    raw = image_result("ERROR[STALE_STATE]: 过期", is_error=True)
    result = convert.to_tool_result(convert.ok_outcome(raw))
    assert _images(result) == []
    assert result.isError is True


def test_json_text_is_never_truncated():
    big_json = json.dumps({"schema_version": "whalego-observe-v1", "elements": ["x" * 500] * 60})
    raw = text_result("OK: 摘要", big_json, "树" * 10000)
    result = convert.to_tool_result(convert.ok_outcome(raw), max_text_chars=2000)
    texts = _texts(result)
    assert texts[1] == big_json
    assert len(texts[0]) <= 2000
    assert texts[2].endswith(convert.TRUNCATION_NOTE)
    assert len(texts[0]) + len(texts[2]) <= 2000 + len(convert.TRUNCATION_NOTE) + 10


def test_parse_json_payload_and_contains_image():
    raw = image_result("OK: 摘要", '{"state_id": "st-1", "elements": []}')
    assert convert.contains_image(raw) is True
    assert convert.parse_json_payload(raw) == {"state_id": "st-1", "elements": []}
    assert convert.parse_json_payload(ok()) is None


def test_outcome_ok_property():
    assert convert.ok_outcome(ok()).ok is True
    assert convert.ok_outcome(text_result("ERROR[PAUSED]: x", is_error=True)).ok is False
    assert outcome_error("PAUSED").ok is False
    assert convert.ToolCallOutcome().ok is False


def test_error_text_helper():
    error = outcome_error("EXECUTOR_TIMEOUT").error
    assert error is not None
    assert convert.error_text(error).startswith("ERROR[EXECUTOR_TIMEOUT]: ")


def test_converted_result_is_mcp_call_tool_result():
    result = convert.to_tool_result(outcome_ok())
    assert isinstance(result, mcp_types.CallToolResult)
    assert all(isinstance(item, (mcp_types.TextContent, mcp_types.ImageContent)) for item in result.content)
    assert compat.result_is_error(result) is False


def test_first_text_and_outcome_failed_helpers():
    ok_result = text_result("OK: 完成", "{}")
    assert convert.first_text(ok_result) == "OK: 完成"
    assert convert.first_text(text_result()) == ""
    assert convert.outcome_failed(convert.ok_outcome(ok_result)) is False
    executor_error = text_result("ERROR[STALE_STATE]: 过期", "{}")
    assert convert.outcome_failed(convert.ok_outcome(executor_error)) is True
    assert convert.outcome_failed(outcome_error("EXECUTOR_TIMEOUT")) is True
    assert convert.outcome_failed(convert.ToolCallOutcome()) is True
