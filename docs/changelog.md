# 版本记录

时间均为 2026 年 10 月，机器为本仓库的开发机（Windows 11 build 26300，双显示器，水冷屏 HID，网易UU远程）。

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
