# 版本记录

时间均为 2026 年 10 月，机器为本仓库的开发机（Windows 11 build 26300，双显示器，水冷屏 HID，网易UU远程）。

## v1.2.1 — 2026-10-30

修"暂停代理悄悄死掉，于是屏幕又关不住、机器还会在屏幕亮着时直接睡眠"。

- 现场：代理进程 2026-10-28 23:52 启动，扛过一次 44 小时的休眠，**2026-10-30 21:04:39 之后再没写过一行日志**（死了）。
  21:14:51 守护关屏并写 pause 请求，没人执行 → HiMOS 继续每 ~0.3 s 写 consumer-control 报文 → 面板被点亮 →
  守护每 45 s 重关一次（"turning monitors off again"），屏幕上看到的是"根本没熄屏"；
  21:19:51 守护按 10 分钟空闲请求 S3，21:21:13 用户按电源键唤醒（睡了 81 s）。用户看到的就是"没有熄屏过程、直接睡了"。
- 守护侧（`src\SleepGuard.cs`）：
  - `pauseAttempts` / `pauseRequestedMs` / `pauseConfirmed` 三个状态；
  - `CheckPauseState()` 每秒读 `pause-state.txt`：`paused:<pids>` 或 `no-app` 才算确认，日志 `pause-app: confirmed by agent (state=...)`；
  - 15 s 内没确认就**重发**请求（第 3 行 `attempt N` 变化，代理按内容而不是按命令字去重），最多 6 次；
  - **请求没确认时不允许睡眠/休眠**（最多等 4 轮），避免"屏幕还亮着机器就睡了"这种最吓人的表现；
  - 新常量 `PauseRetryMs = 15000`。
- 代理侧（`tests\sleepguard-agent.ps1`）：
  - 请求去重从"命令字"改为**内容签名**（否则重发的 pause 会被当成重复请求忽略）；
  - 同一 pid **不会重复挂起**（避免挂起计数加两次、恢复一次后仍冻结）；
  - 每 2 s 写 `agent-heartbeat.txt`，外部可判断代理是否活着。
- 任务侧（`install.ps1`）：`SleepGuardAgent` 增加**每 10 分钟重复触发**（`IgnoreNew` 保持单实例）——代理被系统杀掉或自己崩掉后最多 10 分钟自动复活，不再需要人管。
- **代理第二次死亡（21:36:46）**：这次连日志都没留，任务记录 `lastResult = 0xC000013A`（`STATUS_CONTROL_C_EXIT`，进程因“控制台被关/Ctrl+C”结束）；且死掉的实例让任务卡在 Running，`IgnoreNew` 的重复触发不会真正重启它。因此新增：
  - `tests\start-sleepguard-agent.ps1`（幂等启动器）：读 `agent-heartbeat.txt`，新鲜（<45 s）就直接退出；过期/缺失就用 WMI 起代理并记 `agent-launcher.log`。任务的动作改成跑这个启动器，重复触发即变成“存活检查”。
  - 守护内置看门狗：每秒检查心跳，超过 60 s 未更新就自己调用启动器（每分钟最多一次），日志 `pause-app: agent heartbeat is ... -> starting ...`。实测 21:47:30 用“心跳文件缺失”的场景触发成功。
  - 光标日志带上位移 `cursor x,y (dx=.. dy=..)`，便于区分“真人移动”与面板电源切换时的指针跳变。
- 实测：`--test-pause` 路径 21:32:03 代理挂起 5 个 HiMOS、21:32:05 恢复（日志齐全）；
  关屏路径 21:28:21 触发后守护按 15 s 间隔重发请求（`attempt` 递增到 6）并持续重关屏，直到真实鼠标输入唤醒。

## v1.2.0 — 2026-10-28

关屏期间**暂停伪造输入的进程**（本机是水冷屏软件 HiMOS），彻底解决"屏幕刚黑就自己亮"。

- 背景：v1.1.3 的 DDC 深度 off 在 ASUS XG27UCG 上会断 DisplayPort 链路（系统里只剩 AOC，显示器
  只能按物理电源键唤醒），这是不可接受的，用户明确要求不要物理断电。
- 方案：SC_MONITORPOWER 关屏 + 关屏时暂停 --pause-app 指定的进程（本机 HiMOS 每秒约 3 条
  consumer-control HID 报文，正是它让 Windows 认为"用户一直在操作"），第一个真实输入到来时恢复。
- 为什么必须独立代理：Task Scheduler / WMI 启动的 .NET 进程在参考机上进程视图被过滤——守护只能
  看到 102 个进程，同会话的 Windows PowerShell 能看到 424 个，HiMOS 在 Toolhelp、
  WTSEnumerateProcesses、NtQuerySystemInformation、Win32_Process 里全部缺席；即使启用
  SeDebugPrivilege，对拿到的 pid 调 OpenProcess(PROCESS_SUSPEND_RESUME) 仍是 err=5。
  于是守护只写请求文件 pause-request.txt，由 SleepGuardAgent 登录任务（普通 Windows
  PowerShell）执行 NtSuspendProcess / NtResumeProcess，真实 pid 写入 pause-state.txt。
- 请求只在状态变化时写一次（否则 20 ms 的输入 tick 每秒重写几十次、两个日志都被刷爆）。
- 崩溃安全：暂停之后若守护消失 5 秒，代理自动恢复进程；代理用 Global\SleepGuardAgent mutex
  保证单实例；launcher 发现代理没在跑会 Start-ScheduledTask SleepGuardAgent。
- DDC 改为 opt-in：默认关闭，只有显式 --ddc 才启用（--no-ddc 已移除）。
- install.ps1 新增 -PauseApp（默认 HiMOS）并注册 SleepGuardAgent；uninstall.ps1 一并清理。
- 新增 	ests\run-pause-test.ps1：5 项断言（代理在跑 → 守护写 pause 请求 → 代理挂起 5 个
  HiMOS → 代理恢复 → HiMOS 仍在运行），2026-10-28 23:40 与 23:41 两次实测 PASS。
- 清理：删掉守护里已被证明不可行的"自己枚举+挂起进程"代码（Toolhelp / WMI / pid 文件三条
  回退路径，约 140 行），dist\SleepGuard.exe 从 37,376 B 降到 33,792 B，banner 升至 v1.2.0。
## v1.1.3 — 2026-10-28

修“息屏后马上自动亮”。

- 根因：`SC_MONITORPOWER` 只让 Windows 停止输出画面，任何输入事件（包括水冷屏 HID 每秒 ~3 次的
  consumer-control 报文）都会把它重新点亮。用户看到的就是“黑一下又亮、每 45 秒重复一次”。
- 改为 **DDC/CI 硬件 standby**（MCCS VCP `0xD6`：1=on / 2=standby / 3=suspend / 4=off），
  由显示器自己保持，输入事件无法把它叫醒：
  - 关屏：`SC_MONITORPOWER(2)` + 所有物理显示器 `SetVCPFeature(0xD6, 4)`（工作线程执行，不阻塞消息循环）；
  - 唤醒：真实输入、从 S3 恢复、崩溃/退出路径都会 `SetVCPFeature(0xD6, 1)`，保证屏不会留在暗着；
  - `--no-ddc` 可退回纯 `SC_MONITORPOWER`；显示器 OSD 关闭 DDC/CI 时读取失败（`power=unreadable`），
    自动降级不影响睡眠逻辑。
- 新增 `tools\MonitorPower.cs`（`list` / `off [--hard]` / `on` / `watch <sec>`）与
  `tests\run-monitor-test.ps1`（要求：关屏后 panel 真进入 standby 并保持 ≥6 s，真实输入后回到 on）。

实测（2026-10-28 22:53）：15 s 触发关屏 → `power=standby` 保持 6 s（期间幻影 HID 持续发报文）→
注入真实鼠标 → 日志 `monitors woken on real input (ddc)` → `power=on`。

## v1.1.2 — 2026-10-28

三个会导致“用户在用电脑却被熄屏/睡眠/黑屏”的真 BUG 一起修掉，这是本项目的转折点。

- **崩溃**：`WM_INPUT` 的 `lParam` 是 `HRAWINPUT` 句柄，旧代码直接 `Marshal.PtrToStructure` 当结构体指针用，
  第一条 raw input 就 `0xC0000005`（`VCRUNTIME140_CLR0400.dll` + `0x1416`）。改成 `GetRawInputData`
  两次（先查长度、再拷进 `AllocHGlobal`）。
- **`SetSuspendState` 假成功**：`BOOLEAN` 返回值声明成 `bool`，CLR 读整个 EAX，失败也报成功
  （`ok=True err=1314`）。改成 `byte` 并按 `!= 0` 判定。
- **GUI 子系统收不到 raw input**：本机同一份代码 `/target:exe` 收 mouse=12/kb=8，`/target:winexe` 收 0/0；
  `AllocConsole()` 也救不回来。守护改为 `/target:exe` 编译，`Main` 开头 `ShowWindow(GetConsoleWindow(), 0)`。
- 移除在本机从不触发的低层钩子，改 raw input + 20 ms `GetCursorPos` / `GetAsyncKeyState` 轮询双通道。
- 新增 `tests\` 四个可重跑脚本与 `tools\build-tools.ps1`。

实测（2026-10-28 22:10）：15 s 关屏 → 20 s 进 S3（内核事件 187/42/131）→ 约 65 s 自唤醒 → 空闲计时归零，进程全程存活。

## v1.1.1 — 2026-10-28

- 修 `WM_INPUT` 句柄误用导致的崩溃（v1.1.0 首次出现，10:22 那次“屏幕崩溃”即此）。详见 `docs\case-study.md`。
- 设备名查询改用与 `RawProbe` 一致的写法（`size*2+16`、清零、`pcbSize=size+4`）。
- 诊断日志：每条 raw input 打印 `type` / `name` / `phantom`。

## v1.1.0 — 2026-10-28

- 用 raw input 取代低层钩子（后者在这台机器上装了但从不触发）。
- 注册 consumer control（`0x0C/0x01`）以识别幻影设备——也正因为改了 `WM_INPUT` 处理才暴露 v1.1.1 的崩溃。

## v1.0.4 — 2026-10-28

- **改用计划任务 → PowerShell launcher → WMI** 启动守护：任务直接 spawn 的进程拿不到可用的
  `SeShutdownPrivilege`（属性 `0x0`，`SetSuspendState` 返回 `err=1314`），WMI 子进程是 `0x2`。
- 关屏后每 45 s 重发 `SC_MONITORPOWER`（水冷屏 HID 会重新点亮显示器）。
- 新增 `launch-sleepguard.ps1`。

## v1.0.3 — 2026-10-27

- 过滤常驻 Active 的幻影音频会话（网易UU远程），此前它让 `display-off postponed` 永远成立。
- 新增 `--audio-debug`（每会话状态 + 30 s 心跳）、`--phantom-audio`。

## v1.0.2 — 2026-10-27

- 音视频播放时只推迟关屏，睡眠/休眠照常（10 s grace）。

## v1.0.1 — 2026-10-27

- 修堆损坏崩溃：`GetRawInputDeviceInfo` 返回的设备名长度可能不含 NUL，按精确尺寸 `AllocHGlobal`
  导致每次 `WM_INPUT` 越界 2 字节（幽灵设备 3 次/秒 → 堆被踩坏，`LastTaskResult=0xC0000005`）。
- 缓冲区加 slack 并清零、设备分类按 `hDevice` 缓存、`WndProc` 全程 try/catch。

## v1.0.0 — 2026-10-27

- 首版：隐藏窗口 + raw input 监听，自维护空闲计时，5 分钟关屏、10 分钟睡眠、30 分钟休眠，
  `waitable timer` 自唤醒后按墙钟判断是否该转休眠。
