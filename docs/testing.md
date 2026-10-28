# 测试手册

测试分三层：**纯逻辑/输入** → **关屏** → **睡眠/休眠**。越往后副作用越大，所以脚本默认
只做前两层，第三层要显式加 `-Force`。

生产日志在 `%LOCALAPPDATA%\SleepGuard\sleep-guard.log`；测试日志在 `tests\artifacts\`。

## 一、启动方式决定成败（先读这段）

这台机器上，“同一个 exe 用什么方式启动”会改变它能做什么：

| 启动方式 | `SeShutdownPrivilege` | `SetSuspendState` | raw input |
|---|---|---|---|
| 计划任务直接启动 | 属性存在但 `0x0`，`AdjustTokenPrivileges` 返回成功却启用不了 | `ret=0 err=1314 ERROR_PRIVILEGE_NOT_HELD` | 收不到（本机实测） |
| 计划任务 → powershell launcher → **WMI** | `0x2`（enabled） | `ret=1`，机器真的睡 | 正常 |
| 普通用户进程 / 提权 shell | `0x2` | 可以 | 取决于进程形态 |

结论：**所有测试都用 `Start-SGTestGuard`（内部走 WMI `Win32_Process.Create`）**，否则睡眠测试会
“什么都没发生却也不报错”。同理，`SleepGuard.exe` 必须用 `/target:exe`（控制台子系统）编译——
`/target:winexe` 版本在本机永远收不到 raw input，这是 v1.1.2 的结论。

## 二、输入层：`run-input-test.ps1`

```powershell
.\tests\run-input-test.ps1
```

守护以 `--display 0 --sleep 0 --hibernate 0 --audio-debug` 启动，脚本注入点击/滚轮/按键，然后等日志出现：

```
real raw input #1 type=0 name=<synthetic>
real input detected (mouse <synthetic>) -> idle clock reset
```

失败时的排查顺序：

1. 日志里有没有 `ready v1.1.2 (console-subsystem build)`？没有 → 二进制没换成控制台子系统版。
2. `--audio-debug` 心跳里的 `rawEvents` 是否一直 0？是 → 又回到了“收不到 raw input”的坑。
3. 心跳里 `keyHits` 是否增长？`GetAsyncKeyState` 轮询是第二条通道，20 ms 一次。
4. 注入是否真的发生了？单独跑 `.\dist\tools\SendMove.exe 400 300`，看光标是否移动。

## 三、关屏层：`run-display-test.ps1`

```powershell
.\tests\run-display-test.ps1
```

`--display 15`，预期日志：

```
IDLE 15s >= display threshold 15s -> turning monitors off
```

之后守护会在 `DisplayRetryMs`（45 s）内重复 `SC_MONITORPOWER`——因为水冷屏那个幻影 HID 可能把
显示器重新点亮，一次性关屏并不可靠。

屏幕真的黑了但脚本说失败，通常是日志被 `--log` 覆盖或路径写错；屏幕没黑但日志有这行，说明
`SC_MONITORPOWER` 被驱动/显示器 DPMS 忽略（换 `powercfg /change monitor-timeout-ac 1` 做对照）。

## 四、睡眠层：`run-sleep-test.ps1 -Force`

```powershell
.\tests\run-sleep-test.ps1 -Force
```

会依次发生（各阶段时间可调）：

| 时刻 | 现象 | 证据 |
|---|---|---|
| +15 s 空闲 | 屏幕关闭 | 日志 `turning monitors off` |
| +20 s 空闲 | 调用 `SetSuspendState` | 日志 `requesting suspend (S3)`；内核事件 **187** |
| 随即 | 进入 S3 | 内核事件 **42**（“系统正在进入睡眠状态”） |
| +25 s（实际约 +65 s） | 自唤醒 | 内核事件 **131**（`ResumeCount` 递增、`FullResume`≈2800）；日志 `resumed from suspend after Ns` |
| 唤醒后 | 空闲计时归零 | 日志 `idle clock reset` |

人工复核：

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107,187,131} -MaxEvents 10 |
  Sort-Object TimeCreated | Select-Object TimeCreated, Id, @{n='msg';e={($_.Message -split "`n")[0]}}
powercfg /lastwake
(Get-Item C:\hiberfil.sys).LastWriteTime    # 只有休眠那次会更新
```

### 休眠链

`--sleep 15 --hibernate 40 --hib-wake 30` 测的是“睡 25 秒后转休眠 → 再 30 秒自唤醒”。判据是
`hiberfil.sys` 时间戳更新 + 日志 `slept Ns -> HIBERNATE`。这条链代价大（要真睡+真休眠），
一般只在改了休眠相关代码时跑。

## 五、音视频例外

```powershell
.\dist\tools\AudioTest.exe 45          # 循环播放系统 WAV 45 秒，制造真实音频会话
```

同时用 `--display 15 --audio-debug` 起守护，应看到：

```
IDLE 15s ... but audio/video is playing -> display-off postponed
```

播放结束 10 秒 grace 之后出现 `turning monitors off`。若发现“没放声音也一直 postponed”，看
`audio-debug: session[i] state=1 name=...`——常驻 `state=1` 且名字为空/为远程软件的会话属于幻影会话，
应当加进 `--phantom-audio`。

## 六、失败时最该看的三处

1. 守护日志里最后一次 `config:` 与 `ready v...`，确认版本与参数。
2. `powercfg /requests`：SYSTEM 一栏若有程序的名字（如 `GameViewer.exe`），它会阻止睡眠，与代码无关。
3. 事件日志 `Application` 里有没有 `SleepGuard.exe` 的 `Application Error`——有就是崩溃，先看 WER 的
   异常代码与偏移（`0xc0000005` + `VCRUNTIME140_CLR0400.dll` 曾经对应 `WM_INPUT` 句柄误用）。

## 七、清理

测试脚本都会在 `finally` 里停掉测试守护并调用 `Restore-SGProductionGuard`。如果中途被打断
（比如按了 Ctrl+C），手动收尾：

```powershell
Get-Process SleepGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName SleepGuard     # 或者重跑 .\launch-sleepguard.ps1
```
