# tests —— 可重跑的验证脚本

这一层解决“改完代码怎么证明它真的好了”。四个脚本分层验证，全部走**生产同款启动方式**
（WMI 创建进程，才能拿到挂起所需的 `SeShutdownPrivilege`），测试期间自动停掉生产守护、
结束后自动恢复。

```powershell
# 一次跑完（构建 + 输入 + 关屏；不会睡眠）
.\tests\run-full-selftest.ps1

# 连“真睡眠 + 自唤醒”也测（会真的睡过去，先存盘）
.\tests\run-full-selftest.ps1 -Force
```

## 五个脚本

| 脚本 | 会做什么 | 安全性 | 通过判据 |
|---|---|---|---|
| `run-input-test.ps1` | 起一个动作全关的守护（`--display 0 --sleep 0 --hibernate 0`），注入一次点击、一格滚轮、三个按键 | **安全**，不关屏不睡眠 | 日志出现 `real input detected`（或 `real raw input #`） |
| `run-display-test.ps1` | 用 `--display 15` 起守护 | 屏幕会真的黑几秒 | 日志出现 `turning monitors off` |
| `run-monitor-test.ps1` | 用 `--display 15` 起守护，等 `turning monitors off` 后读 DDC 电源状态，保持 6 s，再注入真实鼠标 | 屏幕会真的黑十几秒（结束时强制 `MonitorPower on`） | `power=standby` 保持 ≥6 s，且输入后日志出现 `monitors woken on real input`、状态回到 `power=on` |
| `run-sleep-test.ps1 -Force` | 用 `--display 15 --sleep 20 --wake 25` 起守护 | **真的进 S3 并自唤醒**，约 2 分钟 | `requesting suspend (S3)` + `resumed from suspend` + 内核事件 187/42/131 齐全 + 无崩溃记录 |
| `run-full-selftest.ps1` | 依次跑 构建 → 输入 → 关屏 → 睡眠，汇总 PASS/FAIL | 取决于参数 | 所有执行到的阶段 PASS |

`run-sleep-test.ps1` 不加 `-Force` 时不会做任何事，只打印提示并以退出码 2 结束（`run-full-selftest`
把它记作 SKIP）。

## 辅助函数（`_common.ps1`）

不要直接运行它，用点源引入：

```powershell
. "$PSScriptRoot\_common.ps1"
$state = Stop-SGProductionGuard        # 停任务 + 杀掉现有守护，记录原状态
$guard = Start-SGTestGuard -GuardArgs @('--display','15','--sleep','0','--hibernate','0') -LogPath 'C:\temp\g.log'
Send-SGTestInput                        # 注入鼠标/按键
Wait-SGLogMatch -Path $guard.Log -Pattern 'turning monitors off' -TimeoutSec 30
Stop-SGTestGuard -Guards @($guard)
Restore-SGProductionGuard               # 恢复到测试前状态
```

| 函数 | 作用 |
|---|---|
| `Stop-SGProductionGuard` / `Restore-SGProductionGuard` | 停/恢复计划任务与守护进程，保证测试后机器仍有保护 |
| `Start-SGTestGuard -GuardArgs -LogPath` | 经 WMI 启动一个测试守护，等它写出 `ready v` 横幅（起不来会直接报错并附带日志尾部） |
| `Stop-SGTestGuard` | 杀掉本次测试启动的守护 |
| `Wait-SGLogMatch -Path -Pattern -TimeoutSec` | 轮询日志直到匹配（睡眠时轮询会自然暂停，恢复后继续） |
| `Send-SGTestInput [-KeysOnly\|-MouseOnly]` | 注入移动/点击/滚轮/按键 |
| `Get-SGKernelPowerEvents` | 取最近的 42（进睡）、107（唤醒）、187（调用挂起）、131（固件 S3 计时）事件 |
| `Write-SGResult -Name -Pass -Detail` | 统一输出 `[PASS]/[FAIL]` |

## 产物

- 每次运行的守护日志写到 `tests\artifacts\*.log`（已被 `.gitignore` 忽略）。
- 生产日志在 `%LOCALAPPDATA%\SleepGuard\sleep-guard.log`。
- 睡眠阶段的内核事件由脚本现场打印，也可以用 README 里的 `Get-WinEvent` 命令复核。

## 已知时间偏差

- **自唤醒比设定晚约 40 秒**（设 25 秒，实测 64–66 秒恢复；固件记录的 `FullResume` 只有约 2.8 秒）。
  所以 `--hib-wake` 实际会让“转休眠”晚 30–40 秒发生，测试里的等待窗口也相应放宽。
- 关屏/睡眠阈值精度约 ±1 秒（策略 tick 为 1 秒）。

更多背景见 `docs\testing.md` 与 `docs\troubleshooting.md`。
