# tools —— 诊断探针

这里放的是排查 SleepGuard 时实际用到的独立小工具。它们不参与守护逻辑，用来回答
“到底是 Windows 不给我们输入、还是我们写错了”这类问题。**全部用 `/target:exe`（控制台子系统）
编译**——在这台机器上同样代码编译成 `/target:winexe` 时收不到任何 raw input，这本身就是
v1.1.2 修掉的那个坑，`RawProbe-winexe.exe` 是它的最小复现。

构建（需要 .NET Framework 4.x，Windows 自带）：

```powershell
.\tools\build-tools.ps1          # 输出到 dist\tools\
.\tools\build-tools.ps1 -Clean
```

> 启动方式会改变结果。同一个探针，从普通 shell / DSH 会话里启动可能什么都收不到，
> 用 WMI 创建就没问题：
> `Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '"C:\path\probe.exe" "C:\path\out.log" 10' }`
> 这跟计划任务进程没有 `SeShutdownPrivilege` 是同一类“上下文决定能力”的问题，见 `docs/troubleshooting.md`。

## 输入链路

| 工具 | 用法 | 看什么 |
|---|---|---|
| `RawProbe.exe` | `RawProbe.exe <log> [seconds] [mode]` | 统计 raw input：`mouse=` / `kb=` / `hid=`，逐条打印设备名。`mode=guard` 注册 5 台设备（鼠标、键盘、触控板/笔），其它值注册 3 台（额外含 consumer control `0x0C/0x01`，水冷屏那个幻影 HID 就走这个 usage）。`hid=` 只是 consumer-control 计数。 |
| `RawProbe-winexe.exe` | 同上 | GUI 子系统版本。**预期 `mouse=0 kb=0`**；如果它也有计数，说明本机没有 winexe 收不到 raw input 的毛病。 |
| `KeyProbe.exe` | `KeyProbe.exe <log> [console\|window] [seconds]` | 20 ms 轮询 `GetAsyncKeyState` + `GetCursorPos`，用来判断轮询通道是否可用（不依赖 raw input）。`window` 模式会建隐藏窗口 + `SetTimer`，复现守护进程的形态。 |
| `HookProbe.exe` | `HookProbe.exe <log> [seconds]` | 只装 `WH_MOUSE_LL` / `WH_KEYBOARD_LL`，看低层钩子是否收到事件、以及装钩子会不会让进程崩。 |
| `AllocProbe.exe` | `AllocProbe.exe <log> [seconds]` | 以 winexe 编译，运行时 `AllocConsole()`，验证“补一个控制台能不能救回 raw input”。结论：不能。 |
| `SendMove.exe` | `SendMove.exe <x> <y>` | 用 `SendInput` 把光标移到绝对坐标，给探针喂输入。 |
| `inject-*.ps1` | `powershell -File tools\inject-abs.ps1` | 注入脚本：`inject-abs` 绝对移动 + 左键 + 滚轮 + 按键；`inject-keys` / `inject-mouse` / `inject-all` 分别只做键盘 / 相对移动 / 混合。全部用 `keybd_event` / `mouse_event`。 |
| `kbd-probe.ps1` / `kp-run.ps1` | `kbd-probe.ps1 <out>` | 纯 PowerShell 的按键轮询探针，用来在“不改代码”的前提下确认 `GetAsyncKeyState` 在当前会话能看到注入按键。 |

## 音频、令牌、电源

| 工具 | 用法 | 看什么 |
|---|---|---|
| `AudioSessions.exe` | `AudioSessions.exe` | 列出每个 CoreAudio 端点的会话：`state` / 显示名 / peak。常驻 `state=1 name=""` 的就是“僵尸会话”，会让守护误判在放音。 |
| `AudioTest.exe` | `AudioTest.exe 45` | 循环播放系统 WAV 45 秒，制造真实音频会话，用来测“播放时推迟关屏”。 |
| `PlaySound.exe` | `PlaySound.exe <wav>` | 播一个短 WAV。 |
| `SuspendTest.exe` | `SuspendTest.exe <log> [wakeSeconds]` | 打印进程令牌里的 `SeShutdownPrivilege` 属性，arm 一个 `fResume` 唤醒计时器，然后 `SetSuspendState`。**必须按 BOOLEAN 封送**（`byte` 返回），否则失败也会显示成功。用它对照计划任务 / 普通进程 / WMI 三种上下文。 |
| `MonitorPower.exe` | `MonitorPower.exe list`（= `get`）<br>`MonitorPower.exe off [--hard]`<br>`MonitorPower.exe on`<br>`MonitorPower.exe watch 30` | DDC/CI 面板电源（MCCS VCP `0xD6`：1=on / 2=standby / 3=suspend / 4=off / 5=hard off）。`list` 打印每个物理显示器的当前值；`watch` 每秒采样、变化才打印。用它区分“真关屏”（`power=standby` 并保持）和“只是 Windows 停了输出”（VCP 仍是 `power=on`，一有输入就亮）。`unreadable (err ...)` 表示该显示器 OSD 关了 DDC/CI 或中间有转接器。 |
| `wmi-launch-test.ps1` | `wmi-launch-test.ps1` | 示范用 WMI 创建进程并拿令牌权限。 |

## 观察类脚本

| 脚本 | 用途 |
|---|---|
| `sleep-watch.ps1` | 长时间采样 `GetLastInputInfo`、光标轨迹、显示器状态、`powercfg /requests`，输出到日志。判断“到底是谁在刷新空闲计时”。 |
| `quiet-probe.ps1` | 40 秒静默探测：用 raw input + 低层钩子统计每类输入来源，配合禁用某个 HID 节点来定位幻影设备（水冷屏 `VID_1D6B&PID_0122` 就是这样查出来的）。 |
| `kp-run.ps1` | 起 `KeyProbe` 并在固定时间点注入按键，一把跑完“探针 + 注入”的对照。 |
