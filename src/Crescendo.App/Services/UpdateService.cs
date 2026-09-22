using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Crescendo.Services;

public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer Crescendo and installs it in place.
/// </summary>
/// <remarks>
/// <para>
/// Same model as McLauncher: the latest release carries a <c>*Setup.exe</c>
/// asset; it is downloaded, run silently, and the installer closes the old copy,
/// refreshes the engine and starts the new one.
/// </para>
/// <para>
/// Crescendo runs elevated and the setup it launches inherits that token, so a
/// downloaded file is only ever run if it carries Ferhad's code-signing
/// certificate. A tampered or substituted asset is refused rather than executed
/// with administrator rights.
/// </para>
/// </remarks>
public static class UpdateService
{
    public const string Owner = "ferhad24";
    public const string Repo = "VolumeX";

    // Ferhad's self-signed Authenticode certificate. The thumbprint pins the key
    // itself; nobody without the private key can produce a matching signature.
    private const string TrustedThumbprint = "21874BCDC82C01DA6B124E53C85A02F6C2815D36";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("User-Agent", "VolumeX-Updater");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return http;
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    // The setup's file name on every release, from 1.2.0 on.
    private const string SetupAssetName = "VolumeX-Setup.exe";

    // Redirects are followed by hand here, so the latest tag can be read off
    // the Location header without downloading the page it points to.
    private static readonly HttpClient NoRedirectHttp = CreateNoRedirectHttp();

    private static HttpClient CreateNoRedirectHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.Add("User-Agent", "VolumeX-Updater");
        return http;
    }

    /// <summary>
    /// Returns the newer release, or null when there is none. The flag tells
    /// "up to date" apart from "could not ask" (offline, or GitHub refused).
    /// </summary>
    /// <remarks>
    /// The check goes through github.com, not api.github.com. The API allows
    /// 60 anonymous requests per hour per IP address; behind one shared address
    /// (an office, a school, a mobile carrier) that runs out and the update
    /// would silently never arrive. The web endpoint has no such budget. The
    /// API is kept only as a fallback.
    /// </remarks>
    public static async Task<(UpdateInfo? Update, bool Reachable)> CheckAsync(CancellationToken cancellationToken)
    {
        (UpdateInfo? Update, bool Reachable)? viaWeb = await CheckViaWebAsync(cancellationToken);
        if (viaWeb is { } result) return result;
        return await CheckViaApiAsync(cancellationToken);
    }

    /// <summary>
    /// github.com/{owner}/{repo}/releases/latest redirects to .../releases/tag/vX.Y.Z.
    /// Returns null (not a result) when the page did not answer as expected,
    /// so the caller can fall back to the API.
    /// </summary>
    private static async Task<(UpdateInfo?, bool)?> CheckViaWebAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A renamed repository answers with a 301 to its new path first.
            var url = new Uri($"https://github.com/{Owner}/{Repo}/releases/latest");
            for (int hop = 0; hop < 5; hop++)
            {
                using HttpResponseMessage response = await NoRedirectHttp.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null)
                    return null;

                url = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(url, response.Headers.Location);

                const string marker = "/releases/tag/";
                int at = url.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                string tag = Uri.UnescapeDataString(url.AbsolutePath[(at + marker.Length)..]).TrimStart('v', 'V');
                if (!IsNewer(tag, CurrentVersion)) return (null, true);

                // The release page can exist a moment before its setup has
                // finished uploading; only offer it once the file is there.
                string repoPath = url.AbsolutePath[..at];
                var asset = new Uri(url, $"{repoPath}/releases/download/v{tag}/{SetupAssetName}");
                using var probe = new HttpRequestMessage(HttpMethod.Head, asset);
                using HttpResponseMessage assetResponse = await NoRedirectHttp.SendAsync(probe, cancellationToken);
                bool present = (int)assetResponse.StatusCode is >= 200 and < 400;

                return (present ? new UpdateInfo(tag, asset.ToString(), string.Empty) : null, true);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<(UpdateInfo? Update, bool Reachable)> CheckViaApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return (null, false);

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement root = document.RootElement;

            string? tag = (root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null)?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(tag) || !IsNewer(tag, CurrentVersion)) return (null, true);

            string? asset = null;
            if (root.TryGetProperty("assets", out JsonElement assets))
            {
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    if (name is not null && name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                        break;
                    }
                }
            }
            // A release without a setup (e.g. still uploading) is not offered yet.
            if (string.IsNullOrEmpty(asset)) return (null, true);

            string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
            return (new UpdateInfo(tag, asset, notes), true);
        }
        catch (Exception)
        {
            return (null, false);   // offline: try again at the next check
        }
    }

    /// <summary>
    /// Downloads the setup, verifies its signature and starts it silently.
    /// The caller exits right after, so the installer can replace the files.
    /// </summary>
    public static async Task DownloadAndStartAsync(UpdateInfo info, IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        string destination = Path.Combine(Path.GetTempPath(), $"VolumeX-{info.Version}-Setup.exe");

        using (HttpResponseMessage response = await Http.GetAsync(info.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream target = File.Create(destination);

            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                read += count;
                if (total > 0) progress.Report(read * 100.0 / total);
            }
        }

        if (!IsSignedByFerhad(destination))
        {
            File.Delete(destination);
            throw new InvalidOperationException(
                "The downloaded update is not signed with VolumeX's certificate and was not installed.");
        }

        Process.Start(new ProcessStartInfo(destination)
        {
            // /SILENT still shows a small progress window, so the user can see
            // that the update is happening; no questions are asked.
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Two independent checks, both required: the signer is Ferhad's key, and
    /// the file still hashes to what was signed. Reading the certificate alone
    /// would accept a file whose contents were altered after signing.
    /// </summary>
    internal static bool IsSignedByFerhad(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // The loader API does not read Authenticode signatures.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!string.Equals(certificate.Thumbprint, TrustedThumbprint, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception)
        {
            return false;   // unsigned file
        }

        // The certificate is self-signed, so the chain ends at an untrusted
        // root; that verdict still means the digest matched. A tampered file
        // fails earlier with TRUST_E_BAD_DIGEST instead.
        int result = Authenticode.Verify(path);
        return result == 0 || result == Authenticode.CertEUntrustedRoot;
    }

    private static class Authenticode
    {
        public const int CertEUntrustedRoot = unchecked((int)0x800B0109);

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static int Verify(string path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path
            };

            IntPtr filePointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(
                System.Runtime.InteropServices.Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(file, filePointer, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE: offline-safe
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = filePointer,
                    dwStateAction = 0,
                    dwProvFlags = 0x00000040     // WTD_SAFER_FLAG
                };

                Guid action = GenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            finally
            {
                // Releases the marshalled path string before the block itself.
                System.Runtime.InteropServices.Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(filePointer);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [System.Runtime.InteropServices.DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    }

    private static bool IsNewer(string remote, string current)
    {
        static Version Parse(string s)
        {
            string[] parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            int Get(int i) => i < parts.Length && int.TryParse(parts[i], out int v) ? v : 0;
            return new Version(Get(0), Get(1), Get(2));
        }

        try { return Parse(remote) > Parse(current); }
        catch (Exception) { return false; }
    }
}
