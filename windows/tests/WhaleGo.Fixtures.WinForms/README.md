# WhaleGo.Fixtures.WinForms —— UIA / 输入测试夹具（T2/T3/T4 共用）

> 用途：给 `WhaleGo.Platform.Tests` / `WhaleGo.Executor.Tests`（以及 E2E 脚本）提供一个**真实、可自动化、可重复**
> 的 WinForms 顶层窗口：UIA 树枚举、Invoke/SetValue/Toggle/Selection 模式、PrintWindow 截图、坐标点击命中测试、
> 密码控件脱敏、敏感窗口/自身窗口排除等测试都以它为目标。
> 自包含项目：**无包引用、无项目引用**，可脱离 solution 单独构建；不参与执行器发布。

## 构建

```powershell
dotnet build windows/tests/WhaleGo.Fixtures.WinForms -c Release
# 产物：windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe
```

## 启动

```powershell
$fixture = "windows/tests/WhaleGo.Fixtures.WinForms/bin/Release/net10.0-windows/WhaleGo.Fixtures.WinForms.exe"
# 基础：可见窗口，标题 "WhaleGo Fixture"
Start-Process $fixture

# 测试推荐：带标题后缀 + ready 文件 + 自动关闭（避免残留进程）
Start-Process $fixture -ArgumentList '--title','t2','--ready-file','.tmp\fixture-t2.ready','--auto-close-ms','60000'

# 帮助（退出码 0；GUI 子进程会 AttachConsole 到父控制台，重定向 stdout 也可用）
& $fixture --help
```

| 参数 | 说明 |
|---|---|
| `--title <suffix>` | 窗口标题变为 `WhaleGo Fixture - <suffix>`；同时出现在 `lblStatus` 的 `;suffix=<suffix>` 段（并行测试用它区分实例） |
| `--ready-file <path>` | 窗口显示 + 布局 + DPI 缩放完成后写入就绪文件（内容见下）；测试应轮询此文件而不是 sleep |
| `--auto-close-ms <n>` | `n` 毫秒后自动关闭，退出码 0 |
| `--click-log <path>` | 每次 `pnlClick` / `btnClickTarget` 被点击追加一行（ISO8601 UTC + pid + target + 计数） |
| `--help` / `-h` / `-?` | 打印用法并退出 0；未知参数退出 2 |

就绪文件示例（每次启动写到指定路径，写入为整文件覆盖）：

```text
ready=true
pid=12345
title=WhaleGo Fixture - t2
dpi=120
client=1125x813
hwnd=0x6A0B2C
```

## 控件与 automation id（测试契约）

**AutomationId = 控件 `Name`；UIA Name = 可读文本/当前值**（已用 FlaUI UIA3 实测，见文末）。
窗口本身：`Name = "WhaleGo Fixture[ - suffix]"`，`AutomationId = "frmFixture"`，ClassName `WindowsForms10.Window.8.*`。

| automation id | 控件 | 可读文本 / 行为 | 备注 |
|---|---|---|---|
| `btnIncrement` | Button | "Increment" | 点击后 `lblCounter` +1 |
| `lblCounter` | Label(Text) | 计数值 `"0"`、`"1"`… | 值在 UIA `Name` 上 |
| `lblStatus` | Label(Text) | `counter=0;check=off;text=;clicks=0[;suffix=x]` | 每 200 ms 刷新；`text=` 后为 `txtInput` 当前文本，`clicks=` 为 pnlClick+btnClickTarget 总数 |
| `txtInput` | Edit | 空；可 type / SetValue | Name="Input"；ValuePattern 可读写 |
| `btnSetText` | Button | "Set" | 点击后 `txtInput.Text = "set-by-button"` |
| `txtSecret` | Edit | `"secret123"`，`UseSystemPasswordChar=true` | UIA `IsPassword=true`，value/Name 不暴露明文（脱敏测试必测） |
| `chkToggle` | CheckBox | "Toggle" | TogglePattern；状态进 `lblStatus` 的 `check=on/off` |
| `lstItems` | List(ListBox) | items `A`/`B`/`C` | 子项为 ListItem |
| `cboChoice` | ComboBox | items `X`/`Y`/`Z`，默认选 `X` | SelectionPattern |
| `pnlClick` | Pane(Panel) | 240×140 逻辑尺寸（100% DPI），边框，**无子控件** | 仅响应 `Click` 事件 → `lblClickCount` 的 `panel=`；坐标 hit-test 用它的中心 |
| `btnClickTarget` | Button | "ClickTarget" | 点击后 `lblClickCount` 的 `target=` +1 |
| `lblClickCount` | Label(Text) | `panel=0;target=0` | 值在 UIA `Name` 上 |
| `btnDisabled` | Button | "Disabled" | `Enabled=false`（不可用元素测试） |

容器 id（UIA Pane/Group，同样 = 控件 Name，可供树断言使用）：
`frmFixture`（Window）、`tlpRoot`、`grpCounter`/`tlpCounter`、`grpInput`/`tlpInput`、`grpLists`/`tlpLists`、
`grpClick`/`tlpClick`/`tlpClickSide`。

## DPI

- 进程 **PerMonitorV2**：`app.manifest` 声明 `dpiAware=true/pm` + `dpiAwareness=PerMonitorV2, PerMonitor`，且 `Program.cs`
  调用 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)`（两者一致；WinForms 分析器 WFO0003 的“重复声明”告警已在
  csproj 里显式 `NoWarn` 并注明原因）。
- 逻辑设计尺寸 900×650 客户区 / 240×140 点击面板，在首次 `Shown` 的下一轮消息循环按 `DeviceDpi/96` 显式缩放（并 clamp 到
  工作区），布局用 TableLayoutPanel 自然回流；实测 120 DPI（125%）= 1125×813 客户区、300×175 面板。
- **测试进程注意**：如果你的测试宿主进程不是 DPI-aware，UIA 返回的 `BoundingRectangle` 会被 DWM 虚拟化（本机 125% 下
  约为实际值 ×0.8）。断言窗口尺寸/坐标前，要么把测试宿主设为 PerMonitorV2，要么用执行器返回的 frame 像素坐标。

## 实测证据（FlaUI UIA3 5.0.0，本机 Windows 11 / 120 DPI）

```text
window name='WhaleGo Fixture - interact' id='frmFixture' dpi=120 awareness=2 bounds={X=501,Y=221,Width=1143,Height=860}
root pane bounds={X=510,Y=259,Width=1125,Height=813}
pnlClick bounds={X=535,Y=733,Width=300,Height=175}
PASS btnIncrement Invoke -> lblCounter=1
PASS txtInput SetValue -> status text=hello-from-probe
PASS txtSecret IsPassword=true
PASS txtSecret value hidden from UIA
PASS chkToggle -> status check=on
PASS btnSetText Invoke -> txtInput value=set-by-button
PASS pnlClick real mouse click -> lblClickCount panel=1
PASS btnClickTarget Invoke -> target=1
PASS btnDisabled IsEnabled=false
PASS lstItems items A,B,C
PASS cboChoice selection X
```

## 已知注意点

- 窗口 `StartPosition=CenterScreen`，默认可见、可最小化/最大化；测试不要把它移出屏幕。
- `lblStatus`/`lblClickCount`/`lblCounter` 是 **Label**：值只在 UIA `Name` 上（无 ValuePattern），别用 `AsTextBox`。
- `pnlClick` 故意没有子控件：坐标点击其中心才会触发面板 `Click`；不要用 `Label` 覆盖它。
- 多实例并行：必须用 `--title` 后缀区分，并各自用独立 ready/click-log 路径。
- 出现残留进程时按标题/前缀清理（测试 teardown 用 `--auto-close-ms` 兜底）。
