using Crescendo.Services;

// Usage: UpdaterTests <signed file>
// Builds a tampered and an unsigned copy next to it and checks all three.
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? " ok " : "FAIL")}] {what}"); if (!ok) failures++; }

string signed = args[0];
string dir = Path.Combine(Path.GetTempPath(), "crescendo-updater-tests");
Directory.CreateDirectory(dir);

// Tampered: signature block intact, one byte of the code changed.
string tampered = Path.Combine(dir, "tampered.exe");
byte[] bytes = File.ReadAllBytes(signed);
bytes[4096] ^= 0xFF;
File.WriteAllBytes(tampered, bytes);

// Unsigned: a fresh file with no signature at all.
string unsigned = Path.Combine(dir, "unsigned.exe");
File.Copy(Path.Combine(AppContext.BaseDirectory, "UpdaterTests.dll"), unsigned, true);

Console.WriteLine("Crescendo updater signature tests");
Check(UpdateService.IsSignedByFerhad(signed), "a genuine signed build is accepted");
Check(!UpdateService.IsSignedByFerhad(tampered), "a signed build with altered contents is refused");
Check(!UpdateService.IsSignedByFerhad(unsigned), "an unsigned file is refused");
Console.WriteLine($"{3 - failures}/3 passed");
return failures == 0 ? 0 : 1;
