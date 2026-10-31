"""WhaleGo AstrBot 插件的内部实现包。

分层（ARCHITECTURE §2.2 / §4.1）：
L0 config/errors → L1 compat/mcp_client → L2 session/policy/convert/client → L3 tools → L4 main。
L0-L2 不 import astrbot，可在 uv venv + pytest 下脱离宿主测试。
"""

from __future__ import annotations

__all__ = ["config", "errors", "compat", "mcp_client", "client", "session", "policy", "convert", "tools"]
