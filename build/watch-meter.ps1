# Live view of what the APO is doing: input vs output peak and the gain between
# them. Used to confirm that exiting Crescendo drops the engine into bypass
# (gain returns to ~1.0x) instead of leaving the boost running.
param([int]$Seconds = 90)

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Watch {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr OpenFileMappingW(uint a, bool i, string n);
  [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr h, uint a, uint hi, uint lo, UIntPtr s);
  [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  static float F(IntPtr v, int o) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(v, o)), 0); }
  // heartbeat, peakIn L, peakOut L, gain reduction; null when the page is gone.
  public static double[] Read() {
    IntPtr h = OpenFileMappingW(4, false, @"Global\Crescendo.Meter.v1");
    if (h == IntPtr.Zero) return null;
    IntPtr v = MapViewOfFile(h, 4, 0, 0, (UIntPtr)128);
    double[] r = { Marshal.ReadInt64(v, 24), F(v, 32), F(v, 64), F(v, 96) };
    UnmapViewOfFile(v); CloseHandle(h);
    return r;
  }
}
"@

$end = (Get-Date).AddSeconds($Seconds)
$lastState = ''
while ((Get-Date) -lt $end) {
    $m = [Watch]::Read()
    $app = [bool](Get-Process Crescendo -ErrorAction SilentlyContinue)
    if ($null -eq $m) { $state = "no meter page (engine not attached yet)" }
    elseif ($m[1] -lt 0.01) { $state = "silence" }
    else {
        $gain = $m[2] / $m[1]
        $mode = if ($gain -gt 1.15 -or $m[3] -gt 0.5) { 'BOOSTING' } else { 'bypass' }
        $state = "{0}  gain {1:0.00}x" -f $mode, $gain
    }
    $line = "{0:HH:mm:ss}  app={1,-5}  {2}" -f (Get-Date), $app, $state
    $key = "$app|$($state.Split(' ')[0])"
    if ($key -ne $lastState) { Write-Output $line; $lastState = $key }
    Start-Sleep -Milliseconds 250
}
Write-Output "done"
