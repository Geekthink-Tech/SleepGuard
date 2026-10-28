# SleepGuard

**A "phantom-input aware" idle watchdog for Windows.** It turns the monitor off, suspends and
hibernates on a *real* user-idle clock, ignoring HID devices that keep spamming input reports.

No SDK required 鈥?it is a single C# file compiled with the `csc.exe` that ships with every
Windows 10/11 installation.

```
绂诲紑 5 鍒嗛挓 鈫?鍏虫樉绀哄櫒锛堟湁闊宠棰戝湪鏀惧垯鎺ㄨ繜锛?鈫?10 鍒嗛挓 鈫?鐫＄湢 S3 鈫?30 鍒嗛挓 鈫?浼戠湢 S4
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
ages, and the built-in timers (screen off, sleep, hibernate) **never fire** 鈥?the monitor stays on
all night and the PC never sleeps, no matter what you configure in Power Options.

`powercfg /requests` does **not** show this: it is input activity, not a power request. That is why
the usual "something is holding a power request" advice does not help here.

SleepGuard solves it by keeping its own idle clock:

| Signal | Counted as real input? |
|---|---|
| Mouse / keyboard / pen / touch / precision touchpad raw input from a normal device | 鉁?yes, resets the clock |
| Raw input from a device whose name matches a phantom pattern (default `VID_1D6B&PID_0122`) | 鉂?ignored |
| Anything else (vendor HID, sensor, consumer control) | 鉂?ignored 鈥?it never reaches `WM_INPUT` as mouse/keyboard |
| Audio / video currently playing | 鈴?monitor-off is postponed (sleep/hibernate still happen) |

## Behaviour

| Real idle | Action | Notes |
|---|---|---|
| 鈮?`--display` (default 300 s) | Turn the monitor off (`SC_MONITORPOWER`) | Postponed while audio/video is playing |
| 鈮?`--sleep` (default 600 s) | Suspend to RAM (S3) | Any real input wakes it instantly |
| 鈮?`--hibernate` (default 1800 s) | Total idle 鈫?hibernate (S4) | Implemented as *sleep first, then hibernate*: a resume timer wakes the machine at `--hibernate` and it hibernates |
| any time | Real keyboard/mouse | Clock resets, display wakes |

Audio playing does **not** stop sleep/hibernate by design (music alone should not keep the PC
awake). Only the monitor-off step is postponed 鈥?this is the "watching a video / listening to a
podcast, screen would otherwise go black" exception.

## Quick start

```powershell
git clone https://github.com/arthur-aicoding/SleepGuard.git
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
| `--phantom-audio <a,b>` | `缃戞槗UU杩滅▼,UU杩滅▼` | Comma-separated **audio-session display names** that must not count as "media is playing" (see below) |
| `--audio-debug` | off | Log every audio session state and a 30 s heartbeat (noisy; for diagnosis only) |
| `--log <path>` | `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` | Log file (rotated to `.old` at 1 MB) |
| `--wake <sec>` | `0` | **Self-test only**: arm a resume timer before suspending so the machine wakes itself |
| `--hib-wake <sec>` | `0` | **Self-test only**: same for the hibernate step |

## Self-tests (they really sleep the machine 鈥?pass the wake argument!)

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
`turning monitors off` right after the audio stops (within one tick, 鈮?1 s).

### Beware of "phantom" audio sessions

Some remote-desktop / streaming tools (缃戞槗UU杩滅▼ on the development machine) keep an audio
session in `AudioSessionStateActive` **permanently**, even with nothing audible. Counting it would
keep the display on forever 鈥?the same class of bug as a phantom HID device, just at the audio layer.

If your log repeats `display-off postponed` while nothing is playing, run with `--audio-debug` and
look at the session list:

```
audio-debug: session[1] state=1 name=缃戞槗UU杩滅▼ -> ignored (phantom)
audio-debug: playing=False
```

Add the offending display name to `--phantom-audio` (default already covers 缃戞槗UU杩滅▼ / UU杩滅▼).
The engine binary build `SleepGuard.exe --audio-debug --display 15` is the quickest way to check.

## How it works (short version)

1. Hidden message-only window + `RegisterRawInputDevices(..., RIDEV_INPUTSINK)` for mouse,
   keyboard, pen, touch and precision touchpad. `WM_INPUT` carries the device name; matching the
   phantom list marks the event as "not real". Only real events reset the clock.
2. A 1 s timer tick compares the clock against the thresholds. The clock uses `DateTime.UtcNow`
   (wall clock), because `Stopwatch`/QPC may not advance across S3/S4 鈥?that also tells the process
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
   endpoint; any `AudioSessionStateActive` session postpones monitor-off. Sessions whose display name
   matches `--phantom-audio` are skipped, and the display comes back on the next tick after the last
   real sound (10 s grace) 鈥?sleep/hibernate timing is untouched.
7. Log lines are written to `%LOCALAPPDATA%\SleepGuard\sleep-guard.log` (rotated at 1 MB).

## If you only want a quick one-off diagnosis

Before installing anything, run the probe 鈥?it tells you *which device* is refreshing the idle
timer:

```powershell
powershell -ExecutionPolicy Bypass -Command "Invoke-Expression (Get-Content -Raw .\tools\quiet-probe.ps1)"
Get-Content .\tools\quiet-probe-log.txt
```

It installs low-level hooks (to prove no synthetic input is injected) and registers every raw-input
class (mouse, keyboard, gamepad, consumer control, pen, touch, precision touchpad), then prints the
devices that produced events while you were not touching anything. Any device listed there with a
steady stream (e.g. ~3 events/second) is your culprit 鈥?add it to `--phantom`.

`tools/sleep-watch.ps1` is a longer monitor (15 min, 5 s samples) that writes idle/cursor deltas 鈥?handy to prove the problem exists before/after.

## Limitations

- Runs in the interactive desktop session only (that is where raw input and `SC_MONITORPOWER` live).
- Some fullscreen-exclusive games ignore the monitor-off message. Sleep/hibernate still work.
- Silent video (no audio stream) is **not** detected by the audio exception; only audio sessions.
- A remote-desktop tool can keep a render session `Active` with nothing audible 鈥?add its display
  name to `--phantom-audio` (缃戞槗UU杩滅▼ is pre-configured). Otherwise the display never turns off.
- The hibernate resume timer may fire 30鈥?0 s late on some firmware. Harmless in normal use
  (you wake it with the keyboard).
- Tested on Windows 11 (build 26300), x64, .NET Framework 4.8. Windows 10 should work; ARM64 is
  untested.
- Suspend requires the process to run as administrator (`SeShutdownPrivilege`); monitor-off and
  counting work without it.

## Stability notes (v1.0.1)

v1.0.1 fixes a crash that showed up after ~5 minutes of normal use (the first version died silently,
leaving you unprotected):

- `GetRawInputDeviceInfo(RIDI_DEVICENAME)` can report a size that *excludes* the terminating NUL.
  The buffer was allocated at exactly that size, so every raw-input event wrote two bytes past the
  block. With a phantom HID firing ~3 events/s, the heap was corrupted within minutes and the
  process died with `0xC0000005` inside `VCRUNTIME140_CLR0400.dll`.
- Device-name resolution is now done once per device handle and cached, and `WM_INPUT` is forwarded
  to `DefWindowProc` (required, otherwise the raw-input buffer is never freed).
- Anything thrown inside the window procedure is caught and logged instead of killing the process,
  and an `AppDomain.UnhandledException` hook records unexpected exceptions in the log.
- The singleton mutex is kept in a static field so it cannot be collected early.

If it ever dies again, check `Get-ScheduledTaskInfo SleepGuard` 鈥?`LastTaskResult` `3221225477`
(`0xC0000005`) means an access violation; Event Viewer 鈫?Windows Logs 鈫?Application 鈫?"Application
Error" names the faulting module. For a dump, enable local dumps once:

```powershell
$k = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\SleepGuard.exe'
New-Item $k -Force | Out-Null
New-ItemProperty $k -Name DumpFolder -Value 'C:\ProgramData\SleepGuard\dumps' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty $k -Name DumpType   -Value 1 -PropertyType DWord -Force | Out-Null
```

The registered task restarts the binary up to 3 times at 1-minute intervals, so a crash does not
leave the machine unprotected for long.

## Task Scheduler steals the shutdown privilege (v1.0.4)

A process started **directly** by a scheduled task cannot suspend the machine. Measured on Windows 11
build 26300 with an elevated task (`LogonType Interactive`, `RunLevel Highest`):

| launch path | `SeShutdownPrivilege` in the token | `SetSuspendState` |
|---|---|---|
| Task Scheduler -> `SleepGuard.exe` | `0x0` (present, disabled) | `ret=0 err=1314` in 1 ms, machine stays awake |
| WMI `Win32_Process.Create` -> `SleepGuard.exe` | `0x2` (enabled) | `ret=1`, machine really enters S3 |

Inside the task process, `AdjustTokenPrivileges(SeShutdownPrivilege)` returns TRUE with
`GetLastError() == 0`, yet the token attribute remains `0x0` 鈥?so the guard's own privilege-enable step
"works" and the suspend call is still refused with `ERROR_PRIVILEGE_NOT_HELD` (`1314`). No
`Kernel-Power` event 187 is written, which is how you can tell the call never reached the power
manager. The very same binary launched through WMI gets the privilege enabled and suspends for real.

Therefore the logon task runs `launch-sleepguard.ps1`, which creates the guard with
`Invoke-CimMethod Win32_Process Create` and exits. Check `launcher.log` next to the scripts to see the
WMI return code and the guard pid.

To reproduce both cases, build `tools\SuspendTest.cs` (it arms a wake timer first, so a working
suspend lasts a few seconds, then wakes itself) and start it once from a task and once through WMI:

```powershell
# task context: expect "returned ret=0 err=1314", machine never sleeps
# WMI context:  expect "returned ret=1", machine sleeps and wakes itself
```

## v1.0.4: keep insisting on monitor-off

The phantom HID can bring the panel back up after `SC_MONITORPOWER` was sent. The guard no longer
assumes "off" is final: while nobody is here (`idle >= display` and no real input) it re-sends
`SC_MONITORPOWER` every 45 s, and any real input cancels that until the next idle period.

## v1.1.2: "it blanked my screen while I was typing"

Three separate defects were found after the watchdog blanked the display of a user who was typing.
All three are fixed in v1.1.2; the build is now `-target:exe` instead of `-target:winexe`.

1. **`WM_INPUT` lParam misuse — crash with 0xC0000005.** `lParam` is an `HRAWINPUT` *handle*, not a
   pointer to `RAWINPUTHEADER`. Calling `Marshal.PtrToStructure(lParam, …)` dereferenced invalid
   memory and killed the process inside `VCRUNTIME140_CLR0400.dll` (error offset `0x1416`) right after
   the first raw-input message. The monitor had already been blanked and no process was left alive to
   restore it — from the outside that looks exactly like "the screen crashed". Fixed by calling
   `GetRawInputData` twice (size query, then copy into an `AllocHGlobal` buffer), the same way
   `tools/RawProbe.cs` does it.

2. **`SetSuspendState` marshalling.** The prototype is
   `BOOLEAN SetSuspendState(BOOLEAN hibernate, BOOLEAN forceCritical, BOOLEAN disableWakeEvent)`.
   Declaring the return type as `bool` makes the CLR read the whole 32-bit `EAX`, whose upper bytes
   are undefined: a failed call can look successful (`ok=True err=1314`) and a successful one can look
   failed. Declare it `byte` and compare `!= 0`.

3. **GUI-subsystem processes can receive no raw input at all.** On Windows 11 26300 a process compiled
   `/target:winexe` received **zero** `WM_INPUT` for mouse and keyboard, while byte-identical code
   compiled `/target:exe`, launched the same way (WMI), in the same session and desktop (WinSta0\
   Default, both elevated) and registering the same five usage pairs, received every injected and real
   event. `AllocConsole()` at runtime does not change the outcome. SleepGuard therefore ships as a
   console-subsystem binary and hides its console immediately via
   `ShowWindow(GetConsoleWindow(), SW_HIDE)` in `Main`. Reproduce with the bundled probe:

   ```powershell
   csc /target:exe    /out:probe-exe.exe    tools\RawProbe.cs
   csc /target:winexe /out:probe-winexe.exe tools\RawProbe.cs
   # inject input, then compare the two log files
   ```

Low-level hooks were removed in v1.1.x: they installed successfully (`mouse=True keyboard=True`) but
never fired for this process, so the guard now relies on raw input plus a 20 ms `GetCursorPos` poll.

## Uninstall

```powershell
Unregister-ScheduledTask -TaskName 'SleepGuard' -Confirm:$false
Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:LOCALAPPDATA\SleepGuard" -Recurse -Force
```

Or run `.\uninstall.ps1 -Purge`.

> Note: after uninstalling you are back to the OS behaviour 鈥?and if a phantom HID device is still
> present, that means the monitor stays on again. The two permanent alternatives are: disable the
> offending device in Device Manager, or switch off the "prevent sleep / keep device alive" option
> in the vendor app.

## 涓枃閫熻

**闂**锛氭煇浜?USB HID 璁惧锛堟按鍐峰睆鎺у埗鍣ㄣ€佷富鏉?鏄惧崱浠〃鐩樸€丷GB/椋庢墖鎺у埗鍣ㄣ€佸畯閿洏銆佽櫄鎷?HID 绛夛級
姣忕涓婃姤鏁版杈撳叆鎶ュ憡锛學indows 璁や负"鐢ㄦ埛涓€鐩村湪鎿嶄綔"锛屽鑷淬€屽叧闂樉绀哄櫒 / 鐫＄湢 / 浼戠湢銆嶇殑璁℃椂鍣ㄦ案杩?鏃犳硶鍒版湡 鈥斺€?浜鸿蛋浜嗗睆骞曞交澶滀寒鐫€銆佺數鑴戜篃涓嶇潯銆俙powercfg /requests` 鐪嬩笉鍒板畠锛屽洜涓鸿繖涓嶆槸鐢垫簮璇锋眰銆?
**瑙ｅ喅**锛歋leepGuard 鐢?Raw Input 鑷繁缁存姢"鐪熷疄杈撳叆鏃堕挓"锛屽彧璁ょ湡姝ｇ殑閿紶/绗?瑙︽帶浜嬩欢锛屾瘡涓┖闂?闃堝€煎埌浜嗗氨鎵ц锛? 鍒嗛挓鍏冲睆 鈫?10 鍒嗛挓鐫＄湢 S3 鈫?30 鍒嗛挓杞紤鐪?S4锛涙鍦ㄦ挱鏀鹃煶瑙嗛鏃跺彧鎺ㄨ繜鍏冲睆锛?涓嶅奖鍝嶇潯鐪?浼戠湢銆?
**瀹夎**锛堢鐞嗗憳 PowerShell锛夛細

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\install.ps1 -Display 300 -Sleep 600 -Hibernate 1800
```

**鏌ユ棩蹇?*锛歚Get-Content "$env:LOCALAPPDATA\SleepGuard\sleep-guard.log" -Tail 30`

**鍏堣瘖鏂埌搴曟槸璋佸湪鎹ｄ贡**锛歚.\tools\quiet-probe.ps1`锛堣涓婃枃锛夛紝鎶婃姄鍒扮殑璁惧鍚嶅姞杩?`--phantom`銆?
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

MIT 鈥?see [LICENSE](LICENSE).
