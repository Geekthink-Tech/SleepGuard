# SleepGuard

**A "phantom-input aware" idle watchdog for Windows.** It turns the monitor off, suspends and
hibernates on a *real* user-idle clock, ignoring HID devices that keep spamming input reports.

No SDK required — it is a single C# file compiled with the `csc.exe` that ships with every
Windows 10/11 installation.

```
离开 5 分钟 → 关显示器（有音视频在放则推迟） → 10 分钟 → 睡眠 S3 → 30 分钟 → 休眠 S4
```

---

## Why this exists

Some USB HID devices send an input report several times per second even when nobody touches
anything:

- water-cooling / AIO LCD controllers that stream telemetry to the PC,
- motherboard / GPU vendor dashboards, RGB and fan controllers,
- macro pads, KVM switches, virtual HID emulators (`VID_1D6B` = Linux Foundation virtual HID),
- some vendor utilities that "keep the device alive" through a virtual consumer-control device.

Windows counts **every** of those reports as user activity. `GetLastInputInfo()` therefore never
ages, and the built-in timers (screen off, sleep, hibernate) **never fire** — the monitor stays on
all night and the PC never sleeps, no matter what you configure in Power Options.

`powercfg /requests` does **not** show this: it is input activity, not a power request. That is why
the usual "something is holding a power request" advice does not help here.

SleepGuard solves it by keeping its own idle clock:

| Signal | Counted as real input? |
|---|---|
| Mouse / keyboard / pen / touch / precision touchpad raw input from a normal device | ✅ yes, resets the clock |
| Raw input from a device whose name matches a phantom pattern (default `VID_1D6B&PID_0122`) | ❌ ignored |
| Anything else (vendor HID, sensor, consumer control) | ❌ ignored — it never reaches `WM_INPUT` as mouse/keyboard |
| Audio / video currently playing | ⏸ monitor-off is postponed (sleep/hibernate still happen) |

## Behaviour

| Real idle | Action | Notes |
|---|---|---|
| ≥ `--display` (default 300 s) | Turn the monitor off (`SC_MONITORPOWER`) | Postponed while audio/video is playing |
| ≥ `--sleep` (default 600 s) | Suspend to RAM (S3) | Any real input wakes it instantly |
| ≥ `--hibernate` (default 1800 s) | Total idle → hibernate (S4) | Implemented as *sleep first, then hibernate*: a resume timer wakes the machine at `--hibernate` and it hibernates |
| any time | Real keyboard/mouse | Clock resets, display wakes |

Audio playing does **not** stop sleep/hibernate by design (music alone should not keep the PC
awake). Only the monitor-off step is postponed — this is the "watching a video / listening to a
podcast, screen would otherwise go black" exception.

## Quick start

```powershell
git clone https://github.com/<you>/SleepGuard.git
cd SleepGuard
powershell -ExecutionPolicy Bypass -File .\build.ps1

# elevated PowerShell (registers a logon task that runs the watchdog):
.\install.ps1 -Display 300 -Sleep 600 -Hibernate 1800
```

`install.ps1` builds if needed, registers the scheduled task `SleepGuard` (at logon, interactive
session, highest privileges, no time limit, restart on failure) and starts it.

Log file: `%LOCALAPPDATA%\SleepGuard\sleep-guard.log`

```powershell
Get-Content "$env:LOCALAPPDATA\SleepGuard\sleep-guard.log" -Tail 30
```

## Options

| Argument | Default | Meaning |
|---|---|---|
| `--display <sec>` | `300` | Turn the monitor off after N seconds of real idle (`0` = never) |
| `--sleep <sec>` | `600` | Suspend to RAM after N seconds of real idle (`0` = never) |
| `--hibernate <sec>` | `0` | Hibernate after N seconds of **total** idle; must be `> --sleep` (`0` = never) |
| `--phantom <a,b>` | `VID_1D6B&PID_0122` | Comma-separated device-name substrings to ignore (case-insensitive, matches anywhere in the raw-input device name) |
| `--log <path>` | `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` | Log file (rotated to `.old` at 1 MB) |
| `--wake <sec>` | `0` | **Self-test only**: arm a resume timer before suspending so the machine wakes itself |
| `--hib-wake <sec>` | `0` | **Self-test only**: same for the hibernate step |

## Self-tests (they really sleep the machine — pass the wake argument!)

```powershell
# 15 s of real idle -> monitor off; 20 s -> S3; wake itself after 30 s
.\dist\SleepGuard.exe --display 15 --sleep 20 --wake 30 --log .\selftest.log

# full chain: monitor off 5 s, S3 at 15 s, hibernate at 40 s, wake from hibernation after 30 s
.\dist\SleepGuard.exe --display 5 --sleep 15 --hibernate 40 --hib-wake 30 --log .\selftest.log
```

Verify with the event log:

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107,187,131} -MaxEvents 10 |
  Select-Object TimeCreated, Id, @{n='msg';e={($_.Message -split "`n")[0]}}
powercfg /lastwake
(Get-Item C:\hiberfil.sys).LastWriteTime   # hibernate really wrote the hiberfile
```

## Test the audio/video exception

```powershell
.\dist\AudioTest.exe 45          # loops a system WAV for 45 s (creates a real audio session)
```

Watch the log: `audio/video is playing -> display-off postponed` while it plays, then
`turning monitors off` right after the audio stops (within one tick, ≤ 1 s).

## How it works (short version)

1. Hidden message-only window + `RegisterRawInputDevices(..., RIDEV_INPUTSINK)` for mouse,
   keyboard, pen, touch and precision touchpad. `WM_INPUT` carries the device name; matching the
   phantom list marks the event as "not real". Only real events reset the clock.
2. A 1 s timer tick compares the clock against the thresholds. The clock uses `DateTime.UtcNow`
   (wall clock), because `Stopwatch`/QPC may not advance across S3/S4 — that also tells the process
   whether it really slept when it resumes.
3. Monitor off: `SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)`.
4. Suspend: `SetSuspendState(FALSE, FALSE, FALSE)` after enabling `SeShutdownPrivilege`.
   `SetSuspendState` can silently return success while nothing happens (a power request blocked
   it); SleepGuard measures how long the call took and retries every 30 s if it returned in < 2 s
   without sleeping.
5. Hibernate chain: before suspending it arms a waitable timer for `--hibernate - --sleep` seconds
   with `fResume = TRUE`. The machine wakes, sees the elapsed wall time has reached the total idle
   target and calls `SetSuspendState(TRUE, ...)`. If real input arrived in the meantime, the
   hibernate step is cancelled.
6. Audio exception: CoreAudio `IAudioSessionManager2` session enumeration on the default render
   endpoint; any `AudioSessionStateActive` session postpones monitor-off.
7. Log lines are written to `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` (rotated at 1 MB).

## If you only want a quick one-off diagnosis

Before installing anything, run the probe — it tells you *which device* is refreshing the idle
timer:

```powershell
powershell -ExecutionPolicy Bypass -Command "Invoke-Expression (Get-Content -Raw .\tools\quiet-probe.ps1)"
Get-Content .\tools\quiet-probe-log.txt
```

It installs low-level hooks (to prove no synthetic input is injected) and registers every raw-input
class (mouse, keyboard, gamepad, consumer control, pen, touch, precision touchpad), then prints the
devices that produced events while you were not touching anything. Any device listed there with a
steady stream (e.g. ~3 events/second) is your culprit — add it to `--phantom`.

`tools/sleep-watch.ps1` is a longer monitor (15 min, 5 s samples) that writes idle/cursor deltas —
handy to prove the problem exists before/after.

## Limitations

- Runs in the interactive desktop session only (that is where raw input and `SC_MONITORPOWER` live).
- Some fullscreen-exclusive games ignore the monitor-off message. Sleep/hibernate still work.
- Silent video (no audio stream) is **not** detected by the audio exception; only audio sessions.
- The hibernate resume timer may fire 30–80 s late on some firmware. Harmless in normal use
  (you wake it with the keyboard).
- Tested on Windows 11 (build 26300), x64, .NET Framework 4.8. Windows 10 should work; ARM64 is
  untested.
- Suspend requires the process to run as administrator (`SeShutdownPrivilege`); monitor-off and
  counting work without it.

## Uninstall

```powershell
Unregister-ScheduledTask -TaskName 'SleepGuard' -Confirm:$false
Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:LOCALAPPDATA\SleepGuard" -Recurse -Force
```

Or run `.\uninstall.ps1 -Purge`.

> Note: after uninstalling you are back to the OS behaviour — and if a phantom HID device is still
> present, that means the monitor stays on again. The two permanent alternatives are: disable the
> offending device in Device Manager, or switch off the "prevent sleep / keep device alive" option
> in the vendor app.

## 中文速览

**问题**：某些 USB HID 设备（水冷屏控制器、主板/显卡仪表盘、RGB/风扇控制器、宏键盘、虚拟 HID 等）
每秒上报数次输入报告，Windows 认为"用户一直在操作"，导致「关闭显示器 / 睡眠 / 休眠」的计时器永远
无法到期 —— 人走了屏幕彻夜亮着、电脑也不睡。`powercfg /requests` 看不到它，因为这不是电源请求。

**解决**：SleepGuard 用 Raw Input 自己维护"真实输入时钟"，只认真正的键鼠/笔/触控事件，每个空闲
阈值到了就执行：5 分钟关屏 → 10 分钟睡眠 S3 → 30 分钟转休眠 S4；正在播放音视频时只推迟关屏，
不影响睡眠/休眠。

**安装**（管理员 PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\install.ps1 -Display 300 -Sleep 600 -Hibernate 1800
```

**查日志**：`Get-Content "$env:LOCALAPPDATA\SleepGuard\sleep-guard.log" -Tail 30`

**先诊断到底是谁在捣乱**：`.\tools\quiet-probe.ps1`（见上文），把抓到的设备名加进 `--phantom`。

## License

MIT — see [LICENSE](LICENSE).
