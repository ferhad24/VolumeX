# Measures the installed engine block by block while a known test tone plays,
# so a regression shows up as numbers instead of "it sounds a bit off".
#
# Checks, with Crescendo running and boost on:
#   * the UI watchdog stamp is fresh (the engine is not about to bypass);
#   * every processed block carries the same gain -- no block falls back to
#     1.0x while boost is on (the watchdog flap looked exactly like that);
#   * no gaps in the engine's block counter (dropouts).
#
# Needs no elevation: both shared pages are readable by everyone.

param([double]$Seconds = 3)

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Probe {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr OpenFileMappingW(uint a, bool i, string n);
  [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr h, uint a, uint hi, uint lo, UIntPtr s);
  [DllImport("kernel32.dll")] static extern uint GetTickCount();
  static IntPtr C, M;
  public static bool Open() {
    IntPtr a = OpenFileMappingW(4, false, @"Global\Crescendo.Config.v1");
    IntPtr b = OpenFileMappingW(4, false, @"Global\Crescendo.Meter.v1");
    if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
    C = MapViewOfFile(a, 4, 0, 0, UIntPtr.Zero); M = MapViewOfFile(b, 4, 0, 0, UIntPtr.Zero);
    return true;
  }
  static float F(IntPtr v, int o) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(v, o)), 0); }
  public static uint Flags() { return (uint)Marshal.ReadInt32(C, 12); }
  public static float Boost() { return F(C, 16); }
  // Signed age, as the fixed engine computes it (CrescendoAbi.h: stamp at 176).
  public static int StampAge() { uint s = (uint)Marshal.ReadInt32(C, 176); return s == 0 ? int.MinValue : (int)(GetTickCount() - s); }

  // Polls the meter as fast as possible and keeps one sample per engine block.
  public static double[][] Capture(int ms) {
    var rows = new System.Collections.Generic.List<double[]>();
    long last = -1; var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) {
      uint s1 = (uint)Marshal.ReadInt32(M, 8);
      long hb = Marshal.ReadInt64(M, 24);
      float inL = F(M, 32), outL = F(M, 64), gr = F(M, 96);
      uint s2 = (uint)Marshal.ReadInt32(M, 8);
      if ((s1 & 1) != 0 || s1 != s2 || hb == last) { System.Threading.Thread.SpinWait(200); continue; }
      rows.Add(new double[] { hb, inL, outL, gr }); last = hb;
    }
    return rows.ToArray();
  }
}
"@

$fail = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "  [ ok ] $what" } else { "  [FAIL] $what"; $script:fail++ } }

if (-not [Probe]::Open()) { "Crescendo is not running (no shared pages)."; exit 1 }
$flags = [Probe]::Flags(); $boost = [Probe]::Boost()
"engine: flags=0x{0:X}  gain={1:0.00}x" -f $flags, $boost
if (($flags -band 1) -eq 0 -or $boost -lt 1.2) { "Turn boost on and set it to 150% or more, then run again."; exit 2 }

# 440 Hz at -20 dBFS: even 500% of it stays under the ceiling, so the gain seen
# per block should be the configured gain, untouched by the limiter.
$rate = 48000; $n = [int]($rate * ($Seconds + 1))
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
$w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $n * 2))
$w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
$w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
$w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]($n * 2))
for ($i = 0; $i -lt $n; $i++) { $w.Write([int16](3277 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))) }
$ms.Position = 0
$player = New-Object Media.SoundPlayer $ms
$player.Play()
Start-Sleep -Milliseconds 500      # let the stream and the gain ramp settle

$ages = @()
$rows = [Probe]::Capture([int]($Seconds * 1000))
$ages += [Probe]::StampAge()
$player.Stop()

$audio = $rows | Where-Object { $_[1] -gt 0.02 }
$gains = $audio | ForEach-Object { $_[2] / $_[1] }
$bypassed = @($gains | Where-Object { $_ -lt 1.05 }).Count
$gaps = 0; for ($i = 1; $i -lt $rows.Count; $i++) { if ($rows[$i][0] - $rows[$i - 1][0] -gt 1) { $gaps++ } }
$mean = ($gains | Measure-Object -Average).Average
$spread = ($gains | Measure-Object -Maximum).Maximum - ($gains | Measure-Object -Minimum).Minimum

""
"blocks captured: $($rows.Count)   with tone: $($audio.Count)"
"per-block gain: mean {0:0.000}x  spread {1:0.000}" -f $mean, $spread
Check ($ages[0] -ge -50 -and $ages[0] -lt 1000) ("watchdog stamp is fresh ({0} ms)" -f $ages[0])
Check ($audio.Count -gt 50) "the tone reached the engine"
Check ($bypassed -eq 0) "no block fell back to bypass while boost was on ($bypassed of $($audio.Count) did)"
Check ($spread -lt 0.15) "gain is steady from block to block"
Check ($gaps -eq 0) "no missed blocks ($gaps gaps)"
""
if ($fail -eq 0) { "LIVE TEST PASSED" } else { "LIVE TEST FAILED ($fail)" }
exit $fail
