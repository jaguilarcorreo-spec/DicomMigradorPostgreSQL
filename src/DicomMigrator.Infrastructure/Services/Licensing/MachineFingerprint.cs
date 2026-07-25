using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DicomMigrator.Core.Interfaces;

namespace DicomMigrator.Infrastructure.Services.Licensing;

/// <summary>
/// Fingerprint estable de la máquina. Formato: 20 caracteres base32 agrupados
/// XXXX-XXXX-XXXX-XXXX-XXXX (100 bits derivados por SHA-256).
///
/// Semilla, en orden de preferencia:
///   1. Windows — ANCLAJE DE FIRMWARE (preferente): UUID de sistema SMBIOS
///      (Win32_ComputerSystemProduct.UUID) + número de serie de la placa base
///      (Win32_BaseBoard.SerialNumber), vía WMI. No se editan con regedit y
///      SOBREVIVEN a reinstalar Windows; solo cambian si se cambia la placa base.
///   2. Windows — respaldo: MachineGuid del registro, si el firmware no da valores
///      útiles (algunos equipos rellenan mal el SMBIOS).
///   3. Linux/otros: /etc/machine-id (para desarrollo en no-Windows).
///   4. Fallback final: nombre de máquina + versión de SO.
///
/// La semilla se mezcla con el nombre de máquina y un prefijo de dominio y se
/// hashea, de modo que el valor publicado no es ningún identificador en claro.
/// </summary>
public sealed class MachineFingerprint : IMachineFingerprintProvider
{
    public string GetFingerprint() => Compute();

    /// <summary>Estático para poder resolverlo desde la CLI (--fingerprint) sin DI.</summary>
    public static string Compute()
    {
        var seed = ReadSeed();
        var raw  = SHA256.HashData(Encoding.UTF8.GetBytes("DMFP1|" + seed + "|" + Environment.MachineName));

        // 13 bytes = 104 bits → 21 chars base32; nos quedamos con 20 (100 bits).
        var b32 = Base32.Encode(raw[..13]);
        var fp  = b32[..20];

        var sb = new StringBuilder(24);
        for (int i = 0; i < fp.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(fp[i]);
        }
        return sb.ToString();   // p. ej. K3M7-QP2A-...-....
    }

    private static string ReadSeed()
    {
        if (OperatingSystem.IsWindows())
        {
            var fw = ReadWindowsFirmwareIds();
            if (!string.IsNullOrEmpty(fw)) return fw;            // 1) firmware (preferente)

            var guid = ReadWindowsMachineGuid();
            if (!string.IsNullOrWhiteSpace(guid)) return "win:" + guid;   // 2) respaldo
        }
        else
        {
            try
            {
                foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (File.Exists(p))
                    {
                        var id = File.ReadAllText(p).Trim();
                        if (!string.IsNullOrWhiteSpace(id)) return "mid:" + id;   // 3) Linux/dev
                    }
                }
            }
            catch { /* cae al fallback */ }
        }

        // 4) Fallback final (menos robusto, pero estable en la misma máquina).
        return "fb:" + Environment.MachineName + "|" + Environment.OSVersion.VersionString;
    }

    /// <summary>Valores basura típicos de un SMBIOS mal rellenado por el fabricante:
    /// se descartan para no anclar a algo no único.</summary>
    private static readonly HashSet<string> JunkValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "none", "n/a", "na", "default string", "to be filled by o.e.m.",
        "to be filled by o.e.m", "not specified", "system serial number",
        "not applicable", "00000000", "ffffffff",
        "00000000-0000-0000-0000-000000000000",
        "ffffffff-ffff-ffff-ffff-ffffffffffff",
        "03000200-0400-0500-0006-000700080009",   // UUID falso conocido de placas baratas
    };

    private static bool IsGood(string? v)
        => !string.IsNullOrWhiteSpace(v) && !JunkValues.Contains(v.Trim());

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsFirmwareIds()
    {
        var parts  = new List<string>(2);
        var uuid   = QueryWmi("Win32_ComputerSystemProduct", "UUID");
        var baseSn = QueryWmi("Win32_BaseBoard", "SerialNumber");
        if (IsGood(uuid))   parts.Add("uuid:" + uuid!.Trim());
        if (IsGood(baseSn)) parts.Add("baseboard:" + baseSn!.Trim());
        return parts.Count > 0 ? "fw|" + string.Join("|", parts) : "";
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryWmi(string wmiClass, string property)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT {property} FROM {wmiClass}");
            foreach (var o in searcher.Get())
            {
                using var mo = (System.Management.ManagementBaseObject)o;
                var val = mo[property]?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { /* WMI no disponible → cae al respaldo */ }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
