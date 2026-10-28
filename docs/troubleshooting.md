# 故障排查

按“你观察到的现象”检索。每条都是这台机器上真实发生过、定位过、修掉的。

## 症状 → 原因 → 处理

| 现象 | 真正原因 | 处理 |
|---|---|---|
| 人离开后屏幕一直亮、电脑从不睡眠 | 多个 SYSTEM 电源请求常驻：GameViewer（网易UU远程）`PreventSleep=1`、AMD HDMI 音频活动流；同时水冷屏 HID 每 25 秒往系统灌 72 个输入事件，把空闲计时器一直刷新 | 关掉 GameViewer 的“防止睡眠”、退出不用的音频播放；用 SleepGuard 的幻影设备过滤（`--phantom "VID_1D6B&PID_0122"`） |
| 屏幕关了又自己亮，反复闪烁 | 水冷屏 HID（consumer control）会触发显示器重新上电；守护原本“关屏一次就锁存” | v1.0.4 起每 45 s 重发 `SC_MONITORPOWER`，直到出现真实输入 |
| 明明在打字，屏幕却黑了 | 守护只看到光标变化：键盘/按键/滚轮全部漏检（旧版只靠 `GetCursorPos` + 低层钩子，而钩子在这台机器上装了不触发） | v1.1.2 起改用 raw input + 20 ms 轮询双通道，并用**控制台子系统**编译（见下条） |
| 正在用电脑，屏幕突然黑掉而且再也点不亮 | `WM_INPUT` 的 `lParam` 被当成 `RAWINPUTHEADER*` 解引用（它其实是 `HRAWINPUT` 句柄）→ 原生层 `0xC0000005`（WER：`VCRUNTIME140_CLR0400.dll`，偏移 `0x1416`）。崩溃往往正好发生在关屏之后 | v1.1.1 修：`GetRawInputData` 两次调用（取长度 → 拷进 `AllocHGlobal`）再解析 |
| 进程是 GUI 子系统时，raw input 一条都收不到 | 本机实测：同一份代码 `/target:winexe` 收 0 条，`/target:exe` 正常；连运行时 `AllocConsole()` 也救不回来 | v1.1.2 起守护用 `/target:exe` 编译，`Main` 开头把控制台窗口隐藏 |
| 计划任务启动了守护，日志却一直 `suspend request returned after 0ms`，机器从不睡 | 计划任务直接 spawn 的进程令牌里 `SeShutdownPrivilege` 属性为 `0x0`；`AdjustTokenPrivileges` 返回 TRUE 但启用无效；`SetSuspendState` 返回 0、`err=1314` | v1.0.4 起改成**计划任务 → `launch-sleepguard.ps1` → WMI `Win32_Process.Create` → 守护**，令牌属性 `0x2` 后正常 |
| `SetSuspendState` 明明失败却打印 `ok=True` | P/Invoke 把 `BOOLEAN` 返回值声明成 `bool`，CLR 读整个 32 位 EAX，高位垃圾被当成 TRUE | v1.1.2 修：`private static extern byte SetSuspendState(byte,byte,byte)`，比较 `!= 0` |
| 放完视频/音乐后屏幕迟迟不关（或从没放音也一直 `display-off postponed`） | CoreAudio 里有常驻 `state=1` 的僵尸会话（如 `网易UU远程`），显示名为空或固定，永远算“在播放” | v1.0.3 起按会话显示名过滤（`--phantom-audio "网易UU远程,UU远程"`），并要求显示名非空 |
| 自唤醒时间比设定晚 30–40 秒 | 本机 S3 唤醒计时器对齐/固件延迟；固件记录的 `FullResume` 只约 2.8 s | 不是 bug。需要精确时把 `--hib-wake` 提前 60 s 补偿 |
| 进睡 1 秒后又醒（日志里 42 与 107 成对紧邻） | 唤醒源不是守护的计时器（`powercfg /lastwake` 会显示），常见是网卡/键鼠 `wake_armed`、或“固定超时休眠”链路 | 用 `powercfg /waketimers`、`powercfg /devicequery wake_armed` 逐个排除；`--wake` 只用于测试自唤醒 |

## 三分钟定位流程

```powershell
# 1. 谁在阻止睡眠（DISPLAY 段为关屏请求，SYSTEM 段为睡眠请求）
powercfg /requests

# 2. 守护当前参数与最近动作
Get-Content "$env:LOCALAPPDATA\SleepGuard\sleep-guard.log" -Tail 40

# 3. 守护进程与令牌权限
Get-CimInstance Win32_Process -Filter "Name='SleepGuard.exe'" | Select-Object ProcessId, CommandLine
Get-ScheduledTask -TaskName SleepGuard | Select-Object State

# 4. 唤醒来源
powercfg /lastwake
powercfg /waketimers

# 5. 系统层面的睡眠/唤醒历史
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107,187,131} -MaxEvents 10 |
  Sort-Object TimeCreated | Select-Object TimeCreated, Id, @{n='msg';e={($_.Message -split "`n")[0]}}
```

## 幻影设备的来龙去脉（背景）

`USB\VID_1D6B&PID_0122` 是七彩虹/COLORFIRE **HiMOS 水冷屏**的 HID 节点（`HID\VID_1D6B&PID_0122\9&5695AF1&0&0000`，
“符合 HID 标准的用户控制设备”）。HiMOS 每秒往这块屏推 `.osd` 数据，系统把它当成用户在按
consumer-control 键，于是 `GetLastInputInfo` 永远只有零点几秒。它同时会**重新点亮**已经关掉的显示器。

处理方式不是禁用设备（会让水冷屏停止显示），而是：

- 守护按设备名过滤它的 raw input（`--phantom "VID_1D6B&PID_0122"`）；
- 关屏后每 45 秒重发一次 `SC_MONITORPOWER`（v1.0.4）。

音频层有同构的问题：远程软件留下恒为 Active 的会话，同样只是过滤，不禁用设备。
