# Case study: "my monitor never turns off and my PC never sleeps"

This is the real debugging trail that produced SleepGuard, written up so you can reuse the method
on your own machine. Machine-specific details (exact device ids, model names) are genericised.

## Symptom

- Power Options configured correctly: *turn off display after 5 minutes*, *sleep after 10 minutes*.
- Verified with `powercfg /query` that the active scheme really carries those values.
- The monitor stayed on all night. `powercfg /lastwake`, Kernel-Power events: no sleep event at all,
  for days.

## What did **not** explain it

| Check | Result |
|---|---|
| `powercfg /requests` | *nothing* (all six categories empty) |
| `powercfg /waketimers`, `/lastwake` | no active timers, no odd wake source |
| Group policy overrides (`HKLM\SOFTWARE\Policies\Microsoft\Power`) | none |
| `powercfg /energy`, WMI `WmiMonitorBasicDisplayParams` | monitors `Active=True`, no errors |
| Screen saver / lock screen | screen saver not configured |
| GPU / streaming / remote-desktop activity | no active session, no video decode |

So the machine was awake with *no power request and no wake timer* — which leaves only one
explanation: something keeps the **input idle timer** from ageing.

## Measuring it

`GetLastInputInfo()` should return a growing number of seconds while you do nothing. Instead,
sampled every few seconds:

```
idle=0.2s  cursor=1461,1155
idle=0.1s  cursor=1461,1155      <- nobody is moving anything
idle=0.3s  cursor=1461,1155
```

The idle counter is constantly reset, so the display-off timer (5 min) and sleep timer (10 min)
never expire.

## Finding *what* resets it

`tools/quiet-probe.ps1` in this repo is the refined version of what I used:

1. install low-level mouse/keyboard hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) — they count *real*
   input coming through the normal path (and would reveal synthetic `SendInput` injection);
2. register **every** raw-input class: mouse, keyboard, gamepad, consumer control, pen, touch,
   precision touchpad;
3. log the device name of every `WM_INPUT` event plus the idle counter every second.

Result on an untouched machine (25 s):

```
hooks: mouse=True keyboard=True   raw registration errors: 0
low-level hooks: mouse=0 keyboard=0
idle: 0.0..0.3s  (constant)  cursor: 1461,1155 (constant)
raw device events: 72 in 25s
  \??\HID#VID_1D6B&PID_0122#...   type=HID-other   ~3 events/second   (steady)
```

Zero hook events → the input is **not** synthetic. Zero mouse/keyboard raw input → nobody is
touching anything. But one HID device reports **three times per second**, forever.

`VID_1D6B` is the Linux Foundation (USB-IF) virtual/embedded vendor id; this particular device
exposed a *Consumer Control* HID usage — i.e. a device that claims to send media-key style reports.

## Confirming the cause

Toggle the device off in Device Manager and watch the idle counter:

```
disable device  -> idle: 2.3, 6.9, 11.5, ... 20.3 s   (it finally ages!)
enable  device  -> idle: 0.19, 0.11, 0.28, ...        (collapses again)
```

That is proof, not correlation: the phantom device *is* the reason the OS idle timer never fires.

## Root cause

A water-cooling LCD controller app was streaming telemetry to its screen through a USB HID
interface (via a driver that exposes it as a consumer-control device). Its "keep the link alive"
loop emitted an input report every ~330 ms. Windows counts every input report as user activity.

Nothing in Power Options, `powercfg /requests`, or the event log can reveal this, because no power
request is held — the *input* state is what is wrong.

## The three possible fixes

1. **Disable the device** (Device Manager → the HID device → Disable). Works, but you lose the
   device (LCD display, macro keys, whatever it is), and a driver update can re-enable it.
2. **Turn off the vendor app's keep-alive / "prevent sleep" option.** Only works if the vendor
   exposes such a switch; some do ("prevent system sleep while remote control is possible").
3. **Keep the device, fix the accounting** — the approach of this project: maintain a *real-input*
   idle clock that ignores the phantom device, and act on that clock.

SleepGuard is option 3, and it also replaces the OS timers entirely so the behaviour is
predictable (and testable — see `--wake` / `--hib-wake`).

## Design decisions worth knowing

| Decision | Why |
|---|---|
| Own clock from raw input instead of `GetLastInputInfo()` | the OS clock is exactly what is broken; raw input lets us filter by device |
| Ignore by device-name substring, not by device instance | names are stable across replugs; `VID_xxxx&PID_yyyy` is recognisable |
| Broadcast `WM_SYSCOMMAND(SC_MONITORPOWER, 2)` | same message Windows sends; instant, no flicker, works on multi-monitor |
| Wall clock (`DateTime.UtcNow`) instead of `Stopwatch` | QPC can stop advancing across S3/S4, so elapsed time after resume must come from wall clock |
| Verify suspend actually happened | `SetSuspendState` returns `TRUE` even when a power request blocked it; measure elapsed call time (< 2 s ⇒ blocked) and retry every 30 s |
| Sleep first, then hibernate via resume timer | `SetSuspendState(TRUE,...)` after an idle S3 is the most reliable way to reach S4; the waitable timer with `fResume=TRUE` wakes the box at the target time, and real input cancels the hibernate |
| Postpone monitor-off while audio plays | watching/listening is a deliberate activity; making the screen go black is bad UX. Sleep/hibernate stay on schedule |
| Log to `%LOCALAPPDATA%`, rotate at 1 MB | long-running background process, no log growth concerns |
| Run as a logon scheduled task (interactive, highest) | survives crashes/updates, no console window, has `SeShutdownPrivilege` |

## Reproduce the diagnosis on your machine

```powershell
# 1. prove the OS idle timer is stuck
1..10 | % { Start-Sleep 3; (Get-IdleInfo) }   # or just the probe below

# 2. find the phantom device
powershell -ExecutionPolicy Bypass -Command "Invoke-Expression (Get-Content -Raw .\tools\quiet-probe.ps1)"
Get-Content .\tools\quiet-probe-log.txt

# 3. optionally disable the device and re-run the probe to confirm the idle counter ages again
```

Then either uninstall, or use SleepGuard with `--phantom <that device name>`.
