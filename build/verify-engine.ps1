# Verifies, from outside the app, that the Crescendo engine is really live:
#   1. registry: COM class, audio-engine entry, unsigned-APO policy, endpoint slot
#   2. runtime: plays a test tone and watches the meter heartbeat the APO writes
#      from inside audiodg.exe. A moving heartbeat is proof the DSP is in the path.
# Needs no elevation: every check is a read.

$ErrorActionPreference = 'Stop'
$clsid = '{8B3F5D2A-7C14-4E9B-A6D3-2F81C0E5B740}'
$fx    = '{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D}'
$ok = 0; $bad = 0

function Report([bool]$cond, [string]$what) {
    if ($cond) { Write-Output "  [ ok ] $what"; $script:ok++ }
    else       { Write-Output "  [FAIL] $what"; $script:bad++ }
}

Write-Output "Registry"
$inproc = Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue
Report ($null -ne $inproc) "COM class registered"
if ($inproc) { Report (Test-Path $inproc.'(default)') "DLL exists at $($inproc.'(default)')" }
Report (Test-Path "HKLM:\SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\$clsid") "audio engine entry present"
$pol = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' -ErrorAction SilentlyContinue
Report ($pol.DisableProtectedAudioDG -eq 1) "unsigned APOs allowed (DisableProtectedAudioDG=1)"

$attached = @()
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render' | ForEach-Object {
    $p = Get-ItemProperty "$($_.PSPath)\FxProperties" -ErrorAction SilentlyContinue
    if ($p) {
        # 5/6/7 legacy single-CLSID slots, 13/14/15 CompositeFX chains.
        foreach ($slot in 5, 6, 7, 13, 14, 15) {
            if (@($p."$fx,$slot") -contains $clsid) { $attached += "$($_.PSChildName) slot $slot" }
        }
    }
}
Report ($attached.Count -gt 0) ("attached to: " + ($(if ($attached) { $attached -join ', ' } else { 'nothing' })))

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Meter {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // Layout of CrescendoMeter (CrescendoAbi.h): heartbeat at 24, peakOut[0] at 64, GR at 96.
    public static double[] Read() {
        IntPtr h = OpenFileMappingW(4, false, @"Global\Crescendo.Meter.v1");
        if (h == IntPtr.Zero) return null;
        IntPtr v = MapViewOfFile(h, 4, 0, 0, (UIntPtr)128);
        if (v == IntPtr.Zero) { CloseHandle(h); return null; }
        double[] r = {
            (double)Marshal.ReadInt64(v, 24),
            Marshal.ReadInt32(v, 16),
            BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(v, 64)), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(v, 96)), 0)
        };
        UnmapViewOfFile(v); CloseHandle(h);
        return r;
    }
}
"@

Write-Output ""
Write-Output "Runtime"
$before = [Meter]::Read()
Report ($null -ne $before) "meter block exists (Crescendo app has run)"

if ($before) {
    # 2 s of 440 Hz at -10 dBFS, built in memory so nothing touches disk.
    $rate = 48000; $n = $rate * 2
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $n * 2))
    $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]($n * 2))
    for ($i = 0; $i -lt $n; $i++) { $w.Write([int16](10362 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))) }
    $ms.Position = 0
    $player = New-Object Media.SoundPlayer $ms
    $player.Play()
    Start-Sleep -Milliseconds 1200
    $during = [Meter]::Read()
    Start-Sleep -Milliseconds 1200

    $beats = $during[0] - $before[0]
    Report ($beats -gt 10) "heartbeat advanced by $beats blocks while the tone played -> APO is processing inside audiodg"
    if ($during[1] -gt 0) { Write-Output "         stream: $($during[1]) Hz, output peak $([Math]::Round($during[2], 3)), limiter GR $([Math]::Round($during[3], 2)) dB" }
}

Write-Output ""
Write-Output "$ok ok, $bad failed"
if ($bad -gt 0) { exit 1 }
