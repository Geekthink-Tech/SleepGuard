# SleepGuard

**A phantom-input aware idle watchdog for Windows.** It turns the monitor off, suspends and
hibernates on a *real* user-idle clock, ignoring HID devices that keep spamming input reports.

No SDK required — it is a single C# file compiled with the `csc.exe` that ships with every
Windows 10/11 installation.

```
离开 5 分钟 → 关显示器（有音视频在放则推迟）→ 10 分钟 → 睡眠 S3 → 30 分钟 → 休眠 S4
```

Status: **v1.1.2**, verified 2026-10-28 on Windows 11 build 26300 (x64, .NET Framework 4.8):
monitor off at 15 s, S3 at 20 s, self-wake after ~65 s, idle clock reset, process alive throughout.

---

## Why this exists

Some USB HID devices send an input report several times per second even when nobody touches
anything:

- water-cooling / AIO LCD controllers that stream telemetry to the PC,
- motherboard / GPU vendor dashboards, RGB and fan controllers,
- macro pads, KVM switches, virtual HID emulators (`VID_1D6B` = Linux Foundation virtual HID),
- some vendor utilities that "keep the device alive" through a virtual consumer-control device.

Windows counts **every** one of those reports as user activity. `GetLastInputInfo()` therefore never
ages, and the built-in timers (screen off, sleep, hibernate) **never fire** — the monitor stays on
all night and the PC never sleeps, no matter what you configure in Power Options.

`powercfg /requests` does **not** show this: it is input activity, not a power request. That is why
the usual "something is holding a power request" advice does not help here.

SleepGuard solves it by keeping its own idle clock:

| Signal | Counted as real input? |
|---|---|
| Mouse / keyboard / pen / touch / precision touchpad raw input from a normal device | yes, resets the clock |
| Raw input from a device whose name matches a phantom pattern (default `VID_1D6B&PID_0122`) | no — ignored |
| Anything else (vendor HID, sensor, consumer control) | no — it never reaches `WM_INPUT` as mouse/keyboard |
| Audio / video currently playing | monitor-off is postponed (sleep/hibernate still happen) |

## Behaviour

| Real idle | Action | Notes |
|---|---|---|
| ≥ `--display` (default 300 s) | Turn the monitor off (`SC_MONITORPOWER`) | Postponed while audio/video is playing; re-sent every 45 s while still idle |
| ≥ `--sleep` (default 600 s) | Suspend to RAM (S3) | Any real input wakes it instantly |
| ≥ `--hibernate` (default 1800 s) | Hibernate (S4) | Implemented as *sleep first, then hibernate*: a resume timer wakes the machine at `--hibernate` and it hibernates |
| any time | Real keyboard/mouse | Clock resets, display wakes |

Audio playing does **not** stop sleep/hibernate by design (music alone should not keep the PC
awake). Only the monitor-off step is postponed — the "watching a video / listening to a podcast,
the screen would otherwise go black" exception.

## Quick start

```powershell
git clone https://github.com/arthur-aicoding/SleepGuard.git
cd SleepGuard
powershell -ExecutionPolicy Bypass -File .\build.ps1

# elevated PowerShell (registers a logon task that runs the watchdog):
.\install.ps1 -Display 300 -Sleep 600 -Hibernate 1800
```

`install.ps1` builds if needed, registers the scheduled task `SleepGuard` (at logon, interactive
session, highest privileges, no time limit, restart on failure) and starts it through
`launch-sleepguard.ps1` — the launcher re-creates the guard via WMI, which is required for
`SetSuspendState` to work (see *Gotchas* below).

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
| `--phantom-audio <a,b>` | `网易UU远程,UU远程` | Comma-separated **audio-session display names** that must not count as "media is playing" |
| `--audio-debug` | off | Log every audio session state and a 30 s heartbeat (noisy; diagnosis only) |
| `--log <path>` | `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` | Log file (rotated to `.old` at 1 MB) |
| `--wake <sec>` | `0` | **Test only**: arm a resume timer before suspending so the machine wakes itself |
| `--hib-wake <sec>` | `0` | **Test only**: same for the hibernate step |

## Project layout

```
build.ps1                 builds src\SleepGuard.cs -> dist\SleepGuard.exe  (/target:exe, see gotchas)
install.ps1               registers + starts the logon task (elevated)
uninstall.ps1             removes the task / files
launch-sleepguard.ps1     task action: re-creates the guard through WMI so it can suspend

src\SleepGuard.cs         the watchdog (single file, no dependencies)
dist\                     build output (dist\tools\ for the probes)

tools\                    standalone diagnostic probes + build-tools.ps1 + README.md
  RawProbe.cs             raw-input counters per device / per usage
  KeyProbe.cs             GetAsyncKeyState polling, per launch context
  HookProbe.cs            low-level hook delivery test
  AllocProbe.cs           does AllocConsole() rescue raw input in a winexe build
  SendMove.cs             injects absolute cursor moves
  AudioSessions.cs        dumps CoreAudio sessions per endpoint
  AudioTest.cs            loops a system WAV so a real audio session exists
  PlaySound.cs            plays one short WAV
  SuspendTest.cs          token privileges + SetSuspendState, the task-vs-WMI experiment
  inject-*.ps1            input injection helpers
  quiet-probe.ps1         one-off "who is refreshing the idle timer" probe
  sleep-watch.ps1         long sampler (idle, cursor, power requests)

tests\                    reproducible end-to-end scripts + README.md
  _common.ps1             helpers: stop/restore production guard, start test guard via WMI
  run-input-test.ps1      injected input must reset the idle clock   (safe)
  run-display-test.ps1    monitor really goes dark                  (screen flashes)
  run-sleep-test.ps1      real S3 + self wake                        (-Force, machine sleeps)
  run-full-selftest.ps1   build + the three stages, PASS/FAIL summary

docs\                     testing.md, troubleshooting.md, changelog.md, case-study.md, ci-workflow.yml
```

## Testing

```powershell
.\tests\run-full-selftest.ps1          # build + input + monitor-off (never sleeps)
.\tests\run-full-selftest.ps1 -Force   # also suspend/resume: the machine really sleeps
.\tests\run-monitor-test.ps1           # panel really stays dark, real input wakes it (DDC/CI)
```

Each stage stops the production guard, starts a throw-away one with short thresholds, checks the
log (and Kernel-Power events for the suspend stage), then restores the production guard. Details,
expected timings and failure triage: [docs/testing.md](docs/testing.md). Repro commands for the
individual tools: [tools/README.md](tools/README.md).

Quick manual smoke test (does not install anything):

```powershell
.\dist\SleepGuard.exe --display 15 --sleep 0 --hibernate 0 --audio-debug --log .\selftest.log
# screen goes dark after ~15 s; Ctrl+C / any input to stop
```

## How it works (short version)

1. Hidden window + `RegisterRawInputDevices(..., RIDEV_INPUTSINK)` for mouse, keyboard, pen, touch
   and precision touchpad. `WM_INPUT` carries the device name; a device matching `--phantom` is
   ignored. Only real events reset the clock. A 20 ms `GetCursorPos` poll is the second channel.
2. A 1 s timer tick compares the clock against the thresholds. The clock uses `DateTime.UtcNow`
   (wall clock), because `Stopwatch`/QPC may not advance across S3/S4 — that is also how the
   process knows whether it really slept when it resumes.
3. Monitor off: `SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)` **plus** a
   DDC/CI DPMS-off (`MCCS VCP 0xD6 = 4`) on every physical monitor. The `SC_MONITORPOWER` blank
   alone is undone by the next input event — and the phantom HID fires ~3 reports/s — so the panel
   lit up again a moment after going dark. DDC standby is handled by the monitor itself and stays
   until the guard asks for `DPMS on` (which happens on the next real input, after resume from
   sleep, and from the crash/exit paths so the screen can never be left dark). `--no-ddc` falls
   back to the OS-only behaviour; monitors with DDC/CI disabled in their OSD report
   `power=unreadable` and are simply left to `SC_MONITORPOWER`.
4. Suspend: `SetSuspendState(FALSE, FALSE, FALSE)` after enabling `SeShutdownPrivilege`.
   `SetSuspendState` can silently return success while nothing happens (a power request blocked
   it), so SleepGuard measures how long the call took and retries every 30 s if it returned in
   under 2 s without sleeping.
5. Hibernate chain: before suspending it arms a waitable timer for `--hibernate - --sleep` seconds
   with `fResume = TRUE`. The machine wakes, sees the elapsed wall time has reached the total idle
   target and calls `SetSuspendState(TRUE, ...)`. Real input in the meantime cancels the hibernate.
6. Audio exception: CoreAudio `IAudioSessionManager2` session enumeration on the default render
   endpoint; any `AudioSessionStateActive` session postpones monitor-off. Sessions whose display
   name matches `--phantom-audio` are skipped. Sleep/hibernate timing is untouched.
7. Log lines go to `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` (rotated at 1 MB).

## Gotchas that cost the most time

Full write-ups with evidence: [docs/troubleshooting.md](docs/troubleshooting.md). The five that
matter if you are adapting this code:

1. **`WM_INPUT` lParam is an `HRAWINPUT` handle, not a `RAWINPUTHEADER*`.** Dereferencing it kills
   the process with `0xC0000005` inside `VCRUNTIME140_CLR0400.dll` on the first event. Call
   `GetRawInputData` twice (size, then copy) — as `tools/RawProbe.cs` does.
2. **A task-started process cannot suspend the machine.** Its token has `SeShutdownPrivilege`
   present but `0x0`; `AdjustTokenPrivileges` reports success and `SetSuspendState` still fails
   with `err=1314`. The launch path must be `task -> launcher -> WMI Win32_Process.Create`
   (token attribute `0x2`).
3. **`BOOLEAN` returns must be marshalled as `byte`.** Declaring `SetSuspendState` as returning
   `bool` reads the whole `EAX`; garbage in the upper bytes makes failures look like successes.
4. **Build as `/target:exe`, not `/target:winexe`.** On the reference machine a winexe build
   received zero raw-input events for mouse/keyboard while byte-identical `/target:exe` code
   received everything (same session, desktop, elevation, registrations). `AllocConsole()` did not
   help. The guard hides its console via `ShowWindow(GetConsoleWindow(), SW_HIDE)`.
5. **`SC_MONITORPOWER` blanks the output, it does not power the panel down.** Any input event
   relights it — including the phantom HID of the very device this tool exists for — which looks
   like "the screen goes dark and comes back on immediately" and repeats every retry. Only DDC/CI
   standby (VCP `0xD6`) holds. Verified with `tools/MonitorPower.cs`: `list` shows
   `power=standby` that survives minutes of phantom reports, whereas after `SC_MONITORPOWER` the
   VCP state still reads `power=on`.

## Limitations

- Runs in the interactive desktop session only (that is where raw input and `SC_MONITORPOWER` live).
- Some fullscreen-exclusive games ignore the monitor-off message. Sleep/hibernate still work.
- Silent video (no audio stream) is **not** detected by the audio exception; only audio sessions.
- A remote-desktop tool can keep a render session `Active` with nothing audible — add its display
  name to `--phantom-audio` (`网易UU远程` is pre-configured).
- The hibernate resume timer may fire 30–40 s late on some firmware. Harmless in normal use.
- Tested on Windows 11 (build 26300), x64, .NET Framework 4.8. Windows 10 should work; ARM64 is
  untested.
- Suspend requires administrator rights (`SeShutdownPrivilege`); monitor-off and idle counting work
  without it.

## If it ever dies

`Get-ScheduledTaskInfo SleepGuard` → `LastTaskResult` `3221225477` (`0xC0000005`) means an access
violation; Event Viewer → Windows Logs → Application → "Application Error" names the faulting
module. For a dump, enable local dumps once:

```powershell
$k = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\SleepGuard.exe'
New-Item $k -Force | Out-Null
New-ItemProperty $k -Name DumpFolder -Value 'C:\ProgramData\SleepGuard\dumps' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty $k -Name DumpType   -Value 1 -PropertyType DWord -Force | Out-Null
```

The registered task restarts the binary up to 3 times at 1-minute intervals, so a crash does not
leave the machine unprotected for long.

## Uninstall

```powershell
Unregister-ScheduledTask -TaskName 'SleepGuard' -Confirm:$false
Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:LOCALAPPDATA\SleepGuard" -Recurse -Force
```

Or run `.\uninstall.ps1 -Purge`.

> Note: after uninstalling you are back to the OS behaviour — and if a phantom HID device is still
> present, the monitor stays on again. The two permanent alternatives are: disable the offending
> device in Device Manager, or switch off the "prevent sleep / keep device alive" option in the
> vendor app.

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

**测试**：`.\tests\run-full-selftest.ps1`（安全，不睡眠）；加 `-Force` 才是真睡眠测试。

**先诊断到底是谁在捣乱**：`.\tools\quiet-probe.ps1`，把抓到的设备名加进 `--phantom`。
排查手册见 [docs/troubleshooting.md](docs/troubleshooting.md)，测试细节见 [docs/testing.md](docs/testing.md)。

## CI

`docs/ci-workflow.yml` is a ready-made GitHub Actions workflow: on a Windows runner it builds both
binaries with the in-box compiler, runs a 4-second smoke test (`--display 0 --sleep 0`, so it never
touches the display) and uploads the exes as an artifact.

GitHub requires the `workflow` OAuth scope to push files under `.github/workflows/`, so the file is
shipped as a template. To enable it:

```powershell
gh auth refresh -s workflow
New-Item -ItemType Directory -Force .github\workflows | Out-Null
Copy-Item docs\ci-workflow.yml .github\workflows\build.yml
git add .github/workflows/build.yml
git commit -m 'ci: add build workflow'
git push
```

## License

MIT — see [LICENSE](LICENSE).
