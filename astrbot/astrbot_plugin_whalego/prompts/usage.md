## WhaleGo 桌面工具使用规则（容器内模型，必读）
1. 找目标：先 `go_apps`/`go_windows`，不要猜 `app_id`/`window_id`；标题只用于人读。
2. 动作前必须 `go_observe`：元素用 `state_id+index`；坐标用最近一次带截图的图片像素（`include_screenshot=true`）。
3. 任何动作后 state/frame 立即失效；下一步先重新 `go_observe`，不要复用旧 `state_id`。
4. `OK:` = 动作已发送/查询成功（不等于生效）；`ERROR[...]` = 失败，按 `retry` 提示处理。
5. “动作可能已发送”（`possibly_sent`）时**禁止重试**，只能重新观察。
6. `CONTROLLER_BUSY` 按 `retry_after_ms` 等待后重试；不要连续重复同一个调用。
7. 需要快捷键（保存/撤销/查找等）用 go_act op=hotkey：元素目标先 observe，keys 例如 ctrl+s、alt+f4；快捷键已完全放开；仍不支持右键/双击/拖拽/粘贴。
8. 截图默认可用（含隐私提示）：未经用户明确要求不得转发；需要看图/坐标操作时传 `include_screenshot=true`；模型看不到图（provider 不支持 image）时不要做坐标动作。
9. 用户暂停/停止后（`PAUSED`/`SESSION_STOPPED`）不要重试，提示用户到 Windows 托盘/设置窗口恢复；恢复只能在本机完成。
10. 读长文用 `go_observe(max_value_chars=…, max_elements=…)`；树被裁剪/跳号是正常现象。
