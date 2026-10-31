from __future__ import annotations

import json
from pathlib import Path

from astrbot_plugin_whalego.whalego import errors


def test_executor_retry_mapping_matches_contract(repo_root: Path):
    contract = json.loads((repo_root / "contracts" / "whalego.tools.v1.json").read_text(encoding="utf-8-sig"))
    contract_codes = contract["error_codes"]
    assert set(contract_codes) == set(errors.EXECUTOR_ERROR_RETRY)
    for code, entry in contract_codes.items():
        assert errors.EXECUTOR_ERROR_RETRY[code] == entry["retry"], code
        expected_dispatch = entry["dispatch"]
        assert errors.EXECUTOR_ERROR_DISPATCH[code] == expected_dispatch, code
    assert contract_codes["DISPATCH_UNCERTAIN"]["dispatch"] == "possibly_sent"


def test_plugin_error_codes_retry_semantics():
    expected = {
        "EXECUTOR_UNREACHABLE": "retry",
        "EXECUTOR_TIMEOUT": "reobserve",
        "AUTH_FAILED": "never",
        "PROTOCOL_MISMATCH": "never",
        "CONNECTION_LOST": "reobserve",
    }
    assert dict(errors.PLUGIN_ERROR_RETRY) == expected
    for code, retry in expected.items():
        assert errors.retry_for(code) == retry


def test_make_error_fills_schema_and_default_message():
    error = errors.make_error("STALE_STATE")
    payload = error.to_dict()
    assert payload["schema_version"] == "whalego-error-v1"
    assert payload["code"] == "STALE_STATE"
    assert payload["retry"] == "reobserve"
    assert payload["message"]
    assert payload["action_receipt"] is None


def test_plugin_side_error_messages_are_chinese_and_codes_english():
    for code in list(errors.PLUGIN_ERROR_RETRY) + ["READONLY_MODE", "INTERNAL"]:
        error = errors.make_error(code)
        assert any("\u4e00" <= char <= "\u9fff" for char in error.message)
        assert error.code == code
        assert error.to_text().startswith(f"ERROR[{code}]: ")


def test_action_uncertain_error_semantics():
    error = errors.action_uncertain_error(tool="go_act", reason="timeout", elapsed_ms=5012)
    assert error.code == "DISPATCH_UNCERTAIN"
    assert error.retry == "reobserve"
    assert error.message == "动作可能已发送，禁止重试，请先重新观察。"
    receipt = error.to_dict()["action_receipt"]
    assert receipt["dispatch_status"] == "possibly_sent"
    assert receipt["action_sent"] is True
    assert receipt["retry_action"] is False
    assert receipt["state_available"] is False


def test_permission_and_readonly_errors():
    permission = errors.permission_error("go_act")
    assert permission.code == "AUTH_FAILED"
    assert permission.retry == "never"
    readonly = errors.readonly_error("go_launch")
    assert readonly.code == "READONLY_MODE"
    assert readonly.retry == "never"
    whitelist = errors.whitelist_error("go_windows")
    assert whitelist.code == "INVALID_ARGUMENT"
    assert whitelist.retry == "never"


def test_go_error_to_dict_is_json_serializable():
    error = errors.action_uncertain_error(tool="go_act", reason="lost")
    encoded = json.dumps(error.to_dict(), ensure_ascii=False)
    assert "whalego-error-v1" in encoded
    assert errors.ERROR_SCHEMA_VERSION == "whalego-error-v1"
