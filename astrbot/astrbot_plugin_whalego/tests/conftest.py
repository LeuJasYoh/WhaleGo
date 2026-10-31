from __future__ import annotations

import sys
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
PLUGIN_ROOT = TESTS_DIR.parent
ASTRBOT_DIR = PLUGIN_ROOT.parent
REPO_ROOT = ASTRBOT_DIR.parent

for _path in (str(ASTRBOT_DIR), str(TESTS_DIR)):
    if _path not in sys.path:
        sys.path.insert(0, _path)

import pytest  # noqa: E402


@pytest.fixture(scope="session")
def plugin_root() -> Path:
    return PLUGIN_ROOT


@pytest.fixture(scope="session")
def repo_root() -> Path:
    return REPO_ROOT


@pytest.fixture(scope="session")
def astrbot_dir() -> Path:
    return ASTRBOT_DIR


def pytest_configure(config) -> None:  # noqa: ARG001 - pytest hook
    # mcp 1.30 在导入时会读取环境变量；这里不做宿主 AstrBot 相关初始化。
    import mcp  # noqa: F401
