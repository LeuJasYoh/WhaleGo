from __future__ import annotations

import copy
import json
from pathlib import Path

import pytest
from astrbot_plugin_whalego.whalego import config


def test_defaults_from_empty_config():
    cfg = config.WhaleGoConfig.from_astrbot_config({})
    assert cfg.connection.url == config.DEFAULT_URL
    assert cfg.connection.timeout == 45
    assert cfg.readonly is False
    assert cfg.admin_only is True
    assert cfg.allow_screenshot is True
    assert cfg.inject_usage_prompt is True
    assert cfg.allowed_tools == config.TOOL_NAMES
    assert any("token" in warning for warning in cfg.warnings)


def test_contract_bundle_matches_repo_contract_and_version(repo_root: Path):
    bundled = config.load_contract()
    repo_contract_path = repo_root / "contracts" / "whalego.tools.v1.json"
    assert repo_contract_path.is_file(), "仓库 contracts/whalego.tools.v1.json 缺失"
    repo_contract = json.loads(repo_contract_path.read_text(encoding="utf-8-sig"))
    assert bundled == repo_contract, "插件包内契约副本与仓库 contracts 不同步（需重新 cp）"
    assert bundled["contract_version"] == config.CONTRACT_VERSION == "1.3"
    assert bundled["protocol_version"] == config.PROTOCOL_VERSION == 1
    names = [tool["name"] for tool in bundled["tools"]]
    assert names == list(config.TOOL_NAMES)


def test_timeout_clamped_to_astrbot_tool_call_timeout():
    cfg = config.WhaleGoConfig.from_astrbot_config(
        {"url": "http://127.0.0.1:8765/mcp", "token": "wg_test", "timeout": 100},
        astr_tool_call_timeout=60,
    )
    assert cfg.connection.timeout == 55
    assert any("clamp" in warning for warning in cfg.warnings)


def test_timeout_at_or_below_executor_budget_warns():
    cfg = config.WhaleGoConfig.from_astrbot_config(
        {"token": "wg_test", "timeout": 45}, astr_tool_call_timeout=45
    )
    assert cfg.connection.timeout == 40
    assert any("执行器整体预算" in warning for warning in cfg.warnings)


def test_timeout_invalid_type_falls_back_with_warning():
    cfg = config.WhaleGoConfig.from_astrbot_config({"token": "wg_test", "timeout": "abc"})
    assert cfg.connection.timeout == float(config.DEFAULT_TIMEOUT)
    assert any("timeout" in warning for warning in cfg.warnings)


@pytest.mark.parametrize(
    ("raw_value", "expected"),
    [
        (True, True),
        (False, False),
        ("true", True),
        ("off", False),
        ("1", True),
        (0, False),
    ],
)
def test_bool_coercion(raw_value, expected):
    cfg = config.WhaleGoConfig.from_astrbot_config(
        {"token": "wg_test", "readonly": raw_value, "admin_only": raw_value}
    )
    assert cfg.readonly is expected
    assert cfg.admin_only is expected


def test_invalid_url_falls_back():
    cfg = config.WhaleGoConfig.from_astrbot_config({"url": "ftp://nope", "token": "wg_test"})
    assert cfg.connection.url == config.DEFAULT_URL
    assert any("url" in warning for warning in cfg.warnings)


def test_url_without_mcp_suffix_kept_but_warns():
    cfg = config.WhaleGoConfig.from_astrbot_config({"url": "http://127.0.0.1:8765/other", "token": "wg_test"})
    assert cfg.connection.url == "http://127.0.0.1:8765/other"
    assert any("/mcp" in warning for warning in cfg.warnings)


def test_allowed_tools_filters_unknown_and_empty_falls_back():
    cfg = config.WhaleGoConfig.from_astrbot_config(
        {"token": "wg_test", "allowed_tools": ["go_health", "go_nope", "go_observe"]}
    )
    assert cfg.allowed_tools == ("go_health", "go_observe")
    empty = config.WhaleGoConfig.from_astrbot_config({"token": "wg_test", "allowed_tools": []})
    assert empty.allowed_tools == config.TOOL_NAMES


def test_read_astrbot_tool_call_timeout_paths():
    assert (
        config.read_astrbot_tool_call_timeout({"agent_runner": {"config": {"misc": {"tool_call_timeout": 90}}}})
        == 90
    )
    assert config.read_astrbot_tool_call_timeout({"provider_settings": {"tool_call_timeout": 77}}) == 77
    assert config.read_astrbot_tool_call_timeout({}) == 120
    assert config.read_astrbot_tool_call_timeout(None) == 120


def test_masked_token_never_leaks_plaintext():
    masked = config.masked_token("wg_abcdefghijklmnopqrstuvwxyz")
    assert "abcdefghijklmnopqrstuvwxyz" not in masked
    assert masked.startswith("wg_ab")
    assert config.masked_token("") == "(未配置)"
    assert config.masked_token("short") == "***"


def test_connection_headers_bearer_only_when_token():
    assert config.ConnectionConfig(token="wg_x").headers() == {"Authorization": "Bearer wg_x"}
    assert config.ConnectionConfig(token="").headers() == {}
    assert "wg_secret" not in repr(config.ConnectionConfig(token="wg_secret"))


# ---------------------------------------------------------------------------
# 契约 → 模型面 schema（内部参数过滤 / 单一事实源）
# ---------------------------------------------------------------------------


def _walk_keys(schema, keys=None):
    found = set()
    if isinstance(schema, dict):
        for key, value in schema.items():
            if key == "properties" and isinstance(value, dict):
                found |= set(value.keys())
            found |= _walk_keys(value)
        return found
    if isinstance(schema, list):
        for item in schema:
            found |= _walk_keys(item)
    return found


@pytest.mark.parametrize("tool_name", list(config.TOOL_NAMES))
def test_model_parameters_have_no_internal_params(tool_name: str):
    contract = config.load_contract()
    schema = config.model_parameters(contract, tool_name)
    keys = _walk_keys(schema)
    for internal in config.INTERNAL_PARAMS:
        assert internal not in keys, f"{tool_name} 模型面 schema 泄漏内部参数 {internal}"
        text = json.dumps(schema)
        assert f'"{internal}"' not in text
    assert schema.get("type") == "object"


def test_go_stop_scope_removed_from_model_schema():
    contract = config.load_contract()
    raw = config.contract_tool(contract, "go_stop")["inputSchema"]
    assert "scope" in raw["properties"], "契约中 go_stop.scope 必须存在且标 internal"
    assert raw["properties"]["scope"].get("x-internal") is True
    model_schema = config.model_parameters(contract, "go_stop")
    assert "scope" not in model_schema.get("properties", {})
    assert "reason" in model_schema.get("properties", {})


def test_model_parameters_only_strip_internal_fields():
    contract = config.load_contract()
    for tool_name in config.TOOL_NAMES:
        model_schema = config.model_parameters(contract, tool_name)
        cloned = copy.deepcopy(model_schema)
        assert cloned == config.model_parameters(contract, tool_name)
        raw_properties = config.contract_tool(contract, tool_name)["inputSchema"].get("properties", {})
        expected = {key: value for key, value in raw_properties.items() if value.get("x-internal") is not True}
        assert set(model_schema.get("properties", {})) == set(expected)


# ---------------------------------------------------------------------------
# 使用说明（prompts/usage.md）与 PROTOCOL §12 同步
# ---------------------------------------------------------------------------


def test_usage_prompt_length_and_marker():
    prompt = config.load_usage_prompt()
    lines = [line for line in prompt.splitlines() if line.strip()]
    assert 2 <= len(lines) <= 15, f"usage prompt 行数 {len(lines)} 超出 ≤15 行约束"
    assert "WhaleGo" in lines[0]


#: PROTOCOL §12 十条规则 → usage.md 必须覆盖的关键词（每个规则一组“或”关系）。
RULE_TOKENS = {
    "rule1_find_target": ("go_apps", "go_windows"),
    "rule2_observe_before_act": ("go_observe", "state_id"),
    "rule3_state_invalidated": ("失效", "重新"),
    "rule4_prefixes": ("OK:", "ERROR["),
    "rule5_possibly_sent": ("possibly_sent", "禁止重试"),
    "rule6_controller_busy": ("CONTROLLER_BUSY", "retry_after_ms"),
    "rule7_hotkey_limits": ("hotkey", "快捷键", "右键", "双击", "拖拽", "粘贴"),
    "rule8_screenshot_privacy": ("截图", "隐私", "include_screenshot", "转发"),
    "rule9_paused_stopped": ("PAUSED", "SESSION_STOPPED", "托盘"),
    "rule10_read_long": ("max_value_chars", "max_elements"),
}


def test_usage_prompt_covers_every_protocol_rule():
    prompt = config.load_usage_prompt()
    missing: list[str] = []
    for rule, tokens in RULE_TOKENS.items():
        missing.extend(f"{rule}:{token}" for token in tokens if token not in prompt)
    assert not missing, f"usage prompt 缺少 PROTOCOL §12 规则关键词：{missing}"


def test_protocol_section12_still_has_ten_rules(repo_root: Path):
    protocol = (repo_root / "docs" / "PROTOCOL.md").read_text(encoding="utf-8")
    section = protocol.split("## 12. 模型使用规则", 1)
    assert len(section) == 2, "PROTOCOL.md 缺少 §12 模型使用规则"
    body = section[1]
    numbered = [line for line in body.splitlines() if line[:3].strip().rstrip(".").isdigit()]
    assert len(numbered) >= 10, f"PROTOCOL §12 规则数量异常：{len(numbered)}"
