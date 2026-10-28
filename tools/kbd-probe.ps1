# kbd-probe.ps1 - does GetAsyncKeyState see injected keys in this session?
Add-Type -Namespace P -Name K -MemberDefinition @'
[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
public struct POINT { public int X; public int Y; }
'@

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$hits = 0
$cursorChanges = 0
$lastX = -1; $lastY = -1
while ($sw.Elapsed.TotalSeconds -lt 22) {
    for ($vk = 1; $vk -lt 255; $vk++) {
        if ([P.K]::GetAsyncKeyState($vk) -band 0x8000) {
            Write-Output ("{0:F1}s  KEY DOWN vk=0x{1:X2}" -f $sw.Elapsed.TotalSeconds, $vk)
            $hits++
            break
        }
    }
    $p = New-Object P.POINT
    if ([P.K]::GetCursorPos([ref]$p)) {
        if ($lastX -ge 0 -and ($p.X -ne $lastX -or $p.Y -ne $lastY)) { $cursorChanges++ }
        $lastX = $p.X; $lastY = $p.Y
    }
    Start-Sleep -Milliseconds 20
}
Write-Output "done: keyHits=$hits cursorChanges=$cursorChanges"
