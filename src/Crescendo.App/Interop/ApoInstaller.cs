using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Crescendo.Interop;

/// <summary>Where in the audio graph the effect is inserted.</summary>
public enum EffectSlot
{
    /// <summary>Per-stream, before mixing. Rarely what a booster wants.</summary>
    Stream = 5,

    /// <summary>After the engine mixes every application stream. The default.</summary>
    Mode = 6,

    /// <summary>Endpoint stage, last before the driver. Fallback for drivers that
    /// do not build a mode graph.</summary>
    Endpoint = 7
}

internal sealed record ApoState(
    bool EngineRegistered,
    bool SignatureCheckDisabled,
    bool AttachedToEndpoint,
    EffectSlot? AttachedSlot,
    string? DllPath,
    string? ReplacedClsid);

/// <summary>
/// Installs, inspects and completely removes the Crescendo APO.
/// </summary>
/// <remarks>
/// <para>
/// Every write this class performs is recorded under
/// <c>HKLM\SOFTWARE\Crescendo\Backup</c> before it happens, so
/// <see cref="DetachFromEndpoint"/> can put the endpoint back exactly as the
/// audio driver left it — including restoring a vendor APO that Crescendo
/// displaced.
/// </para>
/// <para>
/// Windows will not load an unsigned APO into audiodg.exe unless
/// <c>DisableProtectedAudioDG</c> is set. That weakens the protected audio path
/// used by DRM playback, which is why it is a separate, explicitly reversible
/// step rather than something bundled into the install.
/// </para>
/// </remarks>
internal sealed class ApoInstaller
{
    public static readonly Guid ApoClsid = new("8B3F5D2A-7C14-4E9B-A6D3-2F81C0E5B740");
    private static readonly Guid AudioSystemEffectsIid = new("FD7F2B29-24D0-4B5C-B177-592C39F9CA10");

    // PKEY_FX_* all share this format GUID; the property id selects the slot.
    private const string FxPropertyFormatId = "{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D}";

    private const string ClsidKey = @"SOFTWARE\Classes\CLSID";
    private const string AudioEngineKey = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects";
    private const string AudioPolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
    private const string RenderDevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string BackupKey = @"SOFTWARE\Crescendo\Backup";

    private const string FriendlyName = "VolumeX Audio Engine";
    private const uint ApoFlagDefault = 0x2 | 0x4 | 0x8;   // APO_FLAG_DEFAULT

    private static string ClsidText => ApoClsid.ToString("B").ToUpperInvariant();

    // ---------------------------------------------------------------- status

    public ApoState GetState(string? endpointId)
    {
        string? dllPath = null;
        using (var inproc = Registry.LocalMachine.OpenSubKey($@"{ClsidKey}\{ClsidText}\InprocServer32"))
            dllPath = inproc?.GetValue(null) as string;

        bool engineRegistered = dllPath is not null && File.Exists(dllPath);
        using (var engine = Registry.LocalMachine.OpenSubKey($@"{AudioEngineKey}\{ClsidText}"))
            engineRegistered &= engine is not null;

        bool signatureCheckDisabled;
        using (var audio = Registry.LocalMachine.OpenSubKey(AudioPolicyKey))
            signatureCheckDisabled = (audio?.GetValue("DisableProtectedAudioDG") as int?) == 1;

        EffectSlot? attachedSlot = null;
        string? replaced = null;

        if (!string.IsNullOrEmpty(endpointId))
        {
            string guid = ExtractEndpointGuid(endpointId);
            using var fx = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{guid}\FxProperties");
            if (fx is not null)
            {
                foreach (EffectSlot slot in Enum.GetValues<EffectSlot>())
                {
                    if (IsOurs(fx.GetValue(FxValueName(slot))) || IsOurs(fx.GetValue(CompositeValueName(slot))))
                    {
                        attachedSlot = slot;
                        break;
                    }
                }
            }

            using var backup = Registry.LocalMachine.OpenSubKey($@"{BackupKey}\{guid}");
            replaced = backup?.GetValue("OriginalClsid") as string;
        }

        return new ApoState(
            engineRegistered,
            signatureCheckDisabled,
            attachedSlot is not null,
            attachedSlot,
            dllPath,
            replaced);
    }

    // --------------------------------------------------------------- install

    /// <summary>
    /// Registers the COM class and the audio-engine entry. Does not touch any
    /// endpoint and does not change the signature policy.
    /// </summary>
    public void RegisterEngine(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("The VolumeX engine library is missing.", dllPath);

        using (var clsid = Registry.LocalMachine.CreateSubKey($@"{ClsidKey}\{ClsidText}", true))
        {
            clsid.SetValue(null, FriendlyName, RegistryValueKind.String);
            using var inproc = clsid.CreateSubKey("InprocServer32", true);
            inproc.SetValue(null, dllPath, RegistryValueKind.String);
            // "Both" lets the audio engine create the object on whichever
            // apartment its graph-building thread happens to be in.
            inproc.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
        }

        using var apo = Registry.LocalMachine.CreateSubKey($@"{AudioEngineKey}\{ClsidText}", true);
        apo.SetValue("FriendlyName", FriendlyName, RegistryValueKind.String);
        apo.SetValue("Copyright", "VolumeX", RegistryValueKind.String);
        apo.SetValue("MajorVersion", 1, RegistryValueKind.DWord);
        apo.SetValue("MinorVersion", 0, RegistryValueKind.DWord);
        apo.SetValue("Flags", (int)ApoFlagDefault, RegistryValueKind.DWord);
        apo.SetValue("MinInputConnections", 1, RegistryValueKind.DWord);
        apo.SetValue("MaxInputConnections", 1, RegistryValueKind.DWord);
        apo.SetValue("MinOutputConnections", 1, RegistryValueKind.DWord);
        apo.SetValue("MaxOutputConnections", 1, RegistryValueKind.DWord);
        apo.SetValue("MaxInstances", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
        apo.SetValue("NumAPOInterfaces", 1, RegistryValueKind.DWord);
        apo.SetValue("APOInterface0", AudioSystemEffectsIid.ToString("B").ToUpperInvariant(), RegistryValueKind.String);
    }

    public void UnregisterEngine()
    {
        TryDeleteTree($@"{ClsidKey}\{ClsidText}");
        TryDeleteTree($@"{AudioEngineKey}\{ClsidText}");
    }

    /// <summary>
    /// Allows unsigned APOs to load into audiodg.exe.
    /// </summary>
    /// <param name="enabled">
    /// <c>false</c> restores the Windows default. Takes effect when the audio
    /// service next restarts.
    /// </param>
    public void SetSignatureCheckDisabled(bool enabled)
    {
        using var audio = Registry.LocalMachine.CreateSubKey(AudioPolicyKey, true);
        if (enabled)
            audio.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
        else
            audio.DeleteValue("DisableProtectedAudioDG", throwOnMissingValue: false);
    }

    // -------------------------------------------------------------- endpoint

    /// <summary>
    /// Inserts Crescendo into one playback endpoint, remembering whatever was
    /// there before.
    /// </summary>
    public void AttachToEndpoint(string endpointId, EffectSlot slot)
    {
        string guid = ExtractEndpointGuid(endpointId);
        string devicePath = $@"{RenderDevicesKey}\{guid}";

        using RegistryKey fx = OpenFxForValues(devicePath) ?? CreateFxProperties(devicePath);

        using var backup = Registry.LocalMachine.CreateSubKey($@"{BackupKey}\{guid}", true);
        // Never overwrite an existing backup: re-attaching must not lose the
        // state the driver originally shipped with.
        bool firstAttach = backup.GetValue("Captured") is not int;

        // Windows 11 drivers (Realtek among them) describe their effects as
        // CompositeFX chains. When one exists, the legacy single-CLSID value is
        // ignored, so Crescendo joins the end of the chain instead -- the
        // driver's own APOs keep running and the limiter still has the last word.
        string compositeName = CompositeValueName(slot);
        if (fx.GetValue(compositeName) is string[] chain)
        {
            if (firstAttach)
            {
                backup.SetValue("Captured", 1, RegistryValueKind.DWord);
                backup.SetValue("Kind", "Composite", RegistryValueKind.String);
                backup.SetValue("ValueName", compositeName, RegistryValueKind.String);
                backup.SetValue("OriginalChain", chain, RegistryValueKind.MultiString);
                backup.SetValue("EndpointId", endpointId, RegistryValueKind.String);
            }

            if (!IsOurs(chain))
                fx.SetValue(compositeName, chain.Append(ClsidText).ToArray(), RegistryValueKind.MultiString);
            return;
        }

        string valueName = FxValueName(slot);
        string? existing = fx.GetValue(valueName) as string;

        if (firstAttach)
        {
            backup.SetValue("Captured", 1, RegistryValueKind.DWord);
            backup.SetValue("Kind", "Legacy", RegistryValueKind.String);
            backup.SetValue("Slot", (int)slot, RegistryValueKind.DWord);
            backup.SetValue("HadValue", existing is not null ? 1 : 0, RegistryValueKind.DWord);
            if (existing is not null)
                backup.SetValue("OriginalClsid", existing, RegistryValueKind.String);
            backup.SetValue("EndpointId", endpointId, RegistryValueKind.String);
        }

        fx.SetValue(valueName, ClsidText, RegistryValueKind.String);
    }

    /// <summary>
    /// Removes Crescendo from an endpoint and restores the driver's original APO.
    /// </summary>
    public void DetachFromEndpoint(string endpointId)
    {
        string guid = ExtractEndpointGuid(endpointId);
        string devicePath = $@"{RenderDevicesKey}\{guid}";

        using var backup = Registry.LocalMachine.OpenSubKey($@"{BackupKey}\{guid}", true);
        using RegistryKey? fx = OpenFxForValues(devicePath);

        if (fx is not null)
        {
            // Clear our CLSID from every slot, not just the recorded one: a user
            // may have switched slots between sessions.
            foreach (EffectSlot slot in Enum.GetValues<EffectSlot>())
            {
                string name = FxValueName(slot);
                if (IsOurs(fx.GetValue(name)))
                    fx.DeleteValue(name, throwOnMissingValue: false);

                // In a chain, take out only our entry and leave the driver's.
                string compositeName = CompositeValueName(slot);
                if (fx.GetValue(compositeName) is string[] chain && IsOurs(chain))
                {
                    string[] remaining = chain
                        .Where(c => !string.Equals(c, ClsidText, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    fx.SetValue(compositeName, remaining, RegistryValueKind.MultiString);
                }
            }

            if (backup is not null && backup.GetValue("Kind") as string == "Composite")
            {
                // Put the chain back exactly as the driver shipped it.
                if (backup.GetValue("ValueName") is string name && backup.GetValue("OriginalChain") is string[] original)
                    fx.SetValue(name, original, RegistryValueKind.MultiString);
            }
            else if (backup is not null && (backup.GetValue("HadValue") as int?) == 1)
            {
                var slot = (EffectSlot)(backup.GetValue("Slot") as int? ?? (int)EffectSlot.Mode);
                if (backup.GetValue("OriginalClsid") is string original)
                    fx.SetValue(FxValueName(slot), original, RegistryValueKind.String);
            }
        }

        TryDeleteTree($@"{BackupKey}\{guid}");
    }

    /// <summary>Detaches from every endpoint Crescendo has ever touched.</summary>
    public IReadOnlyList<string> DetachFromAllEndpoints()
    {
        var touched = new List<string>();

        using var backupRoot = Registry.LocalMachine.OpenSubKey(BackupKey);
        if (backupRoot is null) return touched;

        foreach (string guid in backupRoot.GetSubKeyNames())
        {
            using var entry = backupRoot.OpenSubKey(guid);
            string endpointId = entry?.GetValue("EndpointId") as string ?? guid;
            try
            {
                DetachFromEndpoint(endpointId);
                touched.Add(endpointId);
            }
            catch (Exception)
            {
                // One stale endpoint must not block cleaning up the rest.
            }
        }
        return touched;
    }

    /// <summary>
    /// Full removal: endpoints restored, COM registration gone, signature policy
    /// back to the Windows default.
    /// </summary>
    public void RemoveEverything()
    {
        DetachFromAllEndpoints();
        UnregisterEngine();
        SetSignatureCheckDisabled(false);
        TryDeleteTree(@"SOFTWARE\Crescendo\Backup");
    }

    // ---------------------------------------------------------------- helpers

    private static string FxValueName(EffectSlot slot) => $"{FxPropertyFormatId},{(int)slot}";

    /// <summary>
    /// PKEY_CompositeFX_StreamEffectClsid / ModeEffectClsid / EndpointEffectClsid
    /// are ids 13, 14 and 15 -- the legacy id plus eight.
    /// </summary>
    private static string CompositeValueName(EffectSlot slot) => $"{FxPropertyFormatId},{(int)slot + 8}";

    private static bool IsOurs(object? value) => value switch
    {
        string s => string.Equals(s, ClsidText, StringComparison.OrdinalIgnoreCase),
        string[] chain => chain.Any(c => string.Equals(c, ClsidText, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    /// <summary>
    /// Turns <c>{0.0.0.00000000}.{guid}</c> into the bare <c>{guid}</c> that the
    /// MMDevices tree uses as a key name.
    /// </summary>
    public static string ExtractEndpointGuid(string endpointId)
    {
        int lastBrace = endpointId.LastIndexOf('{');
        return lastBrace >= 0 ? endpointId[lastBrace..] : endpointId;
    }

    /// <summary>
    /// Opens FxProperties asking only for what Administrators are actually
    /// granted there: read plus SetValue. <c>OpenSubKey(writable: true)</c> asks
    /// for KEY_WRITE, which includes CreateSubKey, and is refused -- no
    /// ownership change is needed to set or delete values.
    /// </summary>
    private static RegistryKey? OpenFxForValues(string devicePath) =>
        Registry.LocalMachine.OpenSubKey($@"{devicePath}\FxProperties",
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.SetValue);

    /// <summary>
    /// Only for drivers that ship no FxProperties key at all: creating a subkey
    /// is the one thing Administrators may not do there, so this is the single
    /// place ownership of an MMDevices key is ever taken.
    /// </summary>
    private static RegistryKey CreateFxProperties(string devicePath)
    {
        TakeOwnership(devicePath);
        using RegistryKey device = Registry.LocalMachine.OpenSubKey(devicePath, writable: true)
            ?? throw new InvalidOperationException("This playback device has no registry entry; it may have been removed.");
        return device.CreateSubKey("FxProperties", true)
            ?? throw new InvalidOperationException("Windows would not allow writing the effect properties for this device.");
    }

    /// <summary>
    /// Makes the local Administrators group the owner of a key and grants it
    /// full control. Used only when Windows refuses a legitimate write.
    /// </summary>
    private static void TakeOwnership(string path)
    {
        Privileges.EnableOrThrow(Privileges.TakeOwnership);
        Privileges.TryEnable(Privileges.Restore);

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        using (var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.TakeOwnership))
        {
            if (key is null) return;
            var security = key.GetAccessControl(AccessControlSections.None);
            security.SetOwner(administrators);
            key.SetAccessControl(security);
        }

        using (var key = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ChangePermissions))
        {
            if (key is null) return;
            var security = key.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new RegistryAccessRule(
                administrators,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            key.SetAccessControl(security);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            TakeOwnership(path);
            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
