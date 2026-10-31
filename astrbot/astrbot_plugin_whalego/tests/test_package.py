from __future__ import annotations

import json
import re
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parents[1]
WHALEGO_DIR = PLUGIN_DIR / "whalego"

REQUIRED_FILES = [
    "metadata.yaml",
    "_conf_schema.json",
    "requirements.txt",
    "README.md",
    "main.py",
    "pyproject.toml",
    "prompts/usage.md",
    "whalego/__init__.py",
    "whalego/config.py",
    "whalego/errors.py",
    "whalego/compat.py",
    "whalego/mcp_client.py",
    "whalego/client.py",
    "whalego/session.py",
    "whalego/policy.py",
    "whalego/convert.py",
    "whalego/tools.py",
    "whalego/contracts/whalego.tools.v1.json",
]


def test_required_deliverable_files_exist():
    missing = [name for name in REQUIRED_FILES if not (PLUGIN_DIR / name).is_file()]
    assert not missing, f"缺少交付文件：{missing}"


def test_l0_l2_modules_do_not_import_astrbot():
    boundaries = [
        "config.py",
        "errors.py",
        "compat.py",
        "mcp_client.py",
        "client.py",
        "session.py",
        "policy.py",
        "convert.py",
    ]
    pattern = re.compile(r"^\s*(?:from|import)\s+astrbot", re.MULTILINE)
    for name in boundaries:
        source = (WHALEGO_DIR / name).read_text(encoding="utf-8")
        assert not pattern.search(source), f"{name} 属于 L0-L2，不得 import astrbot"


def test_metadata_yaml_has_required_fields():
    text = (PLUGIN_DIR / "metadata.yaml").read_text(encoding="utf-8")
    fields = {}
    for line in text.splitlines():
        match = re.match(r"^([a-z_]+):\s*(.*)$", line)
        if match:
            fields[match.group(1)] = match.group(2).strip()
    assert fields.get("name") == "astrbot_plugin_whalego"
    assert fields.get("display_name") == "WhaleGo"
    for required in ("desc", "version", "author"):
        assert fields.get(required), f"metadata.yaml 缺少 {required}"
    if fields.get("version"):
        assert re.match(r"^\d+\.\d+\.\d+$", fields["version"])
    assert "astrbot_version" in fields


def test_conf_schema_keys_types_and_defaults():
    schema = json.loads((PLUGIN_DIR / "_conf_schema.json").read_text(encoding="utf-8"))
    expected = {
        "url": "string",
        "token": "string",
        "timeout": "int",
        "readonly": "bool",
        "admin_only": "bool",
        "allow_screenshot": "bool",
        "inject_usage_prompt": "bool",
        "allowed_tools": "list",
    }
    assert set(schema) == set(expected)
    supported_types = {"int", "float", "bool", "string", "text", "list", "file", "object"}
    for key, type_name in expected.items():
        assert schema[key]["type"] == type_name, key
        assert schema[key]["type"] in supported_types
        assert "default" in schema[key], key
    assert schema["url"]["default"] == "http://host.docker.internal:8765/mcp"
    assert schema["timeout"]["default"] == 45
    assert schema["admin_only"]["default"] is True
    assert schema["allow_screenshot"]["default"] is True
    assert schema["inject_usage_prompt"]["default"] is True
    assert schema["readonly"]["default"] is False
    assert schema["token"].get("secret") is True
    assert schema["allowed_tools"]["default"] == [
        "go_health",
        "go_apps",
        "go_windows",
        "go_observe",
        "go_act",
        "go_launch",
        "go_stop",
    ]


def test_requirements_pin_mcp_major_version():
    text = (PLUGIN_DIR / "requirements.txt").read_text(encoding="utf-8")
    assert "mcp>=1.30,<2" in text


def _handler_keyword_calls(path: Path) -> list[tuple[int, str]]:
    """AST 检查真实代码里是否出现 handler= 关键字调用（docstring 说明不算）。"""
    import ast

    tree = ast.parse(path.read_text(encoding="utf-8"))
    found: list[tuple[int, str]] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            for keyword in node.keywords:
                if keyword.arg == "handler":
                    found.append((node.lineno, ast.dump(node.func)))
    return found


def test_main_uses_subclass_tool_registration_and_commands():
    source = (PLUGIN_DIR / "main.py").read_text(encoding="utf-8")
    assert "add_llm_tools(" in source
    assert "TOOL_CLASSES" in source
    assert '@filter.command("whalego", alias={"wg"})' in source
    assert "@filter.on_llm_request()" in source
    assert "inject_usage_prompt" in source
    # B1：禁止 handler= 绑定插件方法（AST 检查真实调用，docstring 说明不算）
    assert _handler_keyword_calls(PLUGIN_DIR / "main.py") == []
    # resume 不得远程恢复
    assert "恢复只能在本机" in source or "恢复只能在 Windows 本机完成" in source


def test_tools_module_defines_seven_functiontool_subclasses():
    source = (WHALEGO_DIR / "tools.py").read_text(encoding="utf-8")
    assert source.count("(FunctionTool[AstrAgentContext])") >= 1
    assert source.count("(_WhaleGoToolBase)") == 7
    for name in (
        "GoHealthTool",
        "GoAppsTool",
        "GoWindowsTool",
        "GoObserveTool",
        "GoActTool",
        "GoLaunchTool",
        "GoStopTool",
    ):
        assert f"class {name}(" in source, name
    assert _handler_keyword_calls(WHALEGO_DIR / "tools.py") == []
    assert "handler: Any" not in source


def test_usage_prompt_file_is_short_and_mentions_privacy():
    text = (PLUGIN_DIR / "prompts" / "usage.md").read_text(encoding="utf-8")
    lines = [line for line in text.splitlines() if line.strip()]
    assert len(lines) <= 15
    assert "隐私" in text and "转发" in text


def test_readme_covers_operational_requirements():
    text = (PLUGIN_DIR / "README.md").read_text(encoding="utf-8")
    required_fragments = [
        "astrbot_plugin_whalego",
        "allow_screenshot",
        "admin_only",
        "WebUI",
        "defaultInclude=false",
        "隐私",
        "手动启动",
        "排障",
        "组合键",
        "是 WhaleGo 的品牌前缀",
        "mcp",
    ]
    for fragment in required_fragments:
        assert fragment in text, f"README 缺少关键内容：{fragment}"


def test_no_webui_or_webview_files_in_plugin():
    forbidden = {"webui", "webview"}
    names = {path.name.lower() for path in PLUGIN_DIR.rglob("*") if path.is_file()}
    assert not (names & forbidden)
