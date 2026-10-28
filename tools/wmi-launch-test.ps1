$ErrorActionPreference = 'Stop'
$exe = 'C:\Users\Administrator\Documents\deepseek-harness\default-workspace\sleep-guard\diagnostics\SuspendTest.exe'
$log = 'C:\Users\Administrator\Documents\deepseek-harness\default-workspace\sleep-guard\diagnostics\suspend-test-taskwmi.log'
$cmdline = '"' + $exe + '" "' + $log + '" 6'
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdline }
$out = 'C:\Users\Administrator\Documents\deepseek-harness\default-workspace\sleep-guard\diagnostics\taskwmi-launch.txt'
Set-Content -Path $out -Value ("return=" + $r.ReturnValue + " pid=" + $r.ProcessId + " time=" + (Get-Date -Format 'HH:mm:ss.fff')) -Encoding UTF8
