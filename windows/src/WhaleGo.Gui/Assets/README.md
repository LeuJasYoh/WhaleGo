# WhaleGo.Gui 托盘图标资源

本目录的 10 个 `.ico` 是库内嵌资源（`EmbeddedResource`），由 `TrayIconProvider` 在运行时按
「5 状态 × 浅/深任务栏」加载；WinForms `NotifyIcon` 不支持矢量，所以这里是**预生成的位图图标**。

| 文件名 | 状态 | 任务栏 |
|---|---|---|
| `running-connected.light-taskbar.ico` | 运行中 · 已连接（蓝） | 浅色任务栏（深色图形） |
| `running-connected.dark-taskbar.ico` | 运行中 · 已连接（蓝） | 深色任务栏（浅色图形） |
| `running-idle.light-taskbar.ico` / `.dark-taskbar.ico` | 运行中 · 暂无客户端（灰） | 同上 |
| `paused.light-taskbar.ico` / `.dark-taskbar.ico` | 已暂停（琥珀，角标 II） | 同上 |
| `stopped.light-taskbar.ico` / `.dark-taskbar.ico` | 已停止（红，角标 ■） | 同上 |
| `error.light-taskbar.ico` / `.dark-taskbar.ico` | 异常（红，角标 !） | 同上 |

- 每个 `.ico` 内含多尺寸帧：**16 / 20 / 24 / 32 / 48 / 256**；16–48 为 32bpp DIB 帧（含 AND mask），
  256 为 PNG 帧（Vista+ 支持）。测试 `TrayIconAssetsAreEmbeddedMultiSizeIcons` 会校验资源数量、尺寸集合与
  `System.Drawing.Icon` 可加载性。
- 选择逻辑：`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`
  + `SystemParameters.HighContrast`（高对比度使用浅色图形），见 `Tray/TrayState.cs` 的 `TrayIconSelector`。
- 生成脚本**不提交**（一次生成后作为二进制资源维护）：`.tmp/tg-gui/gen_icons.py`（Pillow，按目标尺寸逐帧绘制
  + 4× 超采样抗锯齿，然后手工封装 ICO 容器）。修改图标的流程：改脚本 → 重新生成 → 跑 `WhaleGo.Gui.Tests` 的图标测试。
- 颜色取自主题 token 的语义色（蓝 `#3F5BEA` / 灰 `#6E7686` / 琥珀 `#B36F00` / 红 `#C92F2F` 的深色变体
  与提亮变体），保证浅/深任务栏上都清晰；不做运行时 GDI+ 拼图（docs/GUI.md 5.1）。
