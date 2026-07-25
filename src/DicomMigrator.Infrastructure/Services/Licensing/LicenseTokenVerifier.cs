using System.Text;
using System.Text.Json;
using DicomMigrator.Core.Models;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace DicomMigrator.Infrastructure.Services.Licensing;

/// <summary>
/// Verifica la firma Ed25519 de un token DMLIC1 y decodifica su payload.
///
/// Token:  DMLIC1.&lt;payload_b64url&gt;.&lt;firma_b64url&gt;
/// Entrada de firma (ASCII):  "DMLIC1." + payload_b64url
///
/// Se verifica sobre los BYTES EXACTOS del payload que viajan en el token: no se
/// re-serializa el JSON. El payload se decodifica solo para leer sus campos.
///
/// La clave PÚBLICA va embebida (constante EmbeddedPublicKeyB64), pareja de la
/// privada (private_key.pem) que guarda el fabricante en el generador Python.
/// </summary>
public static class LicenseTokenVerifier
{
    public const string Magic   = "DMLIC1";
    public const string Product = "MOVE";

    /// <summary>Clave pública Ed25519 (32 bytes, base64 estándar).
    /// Generada con licensing/keygen.py — sustituir si se rota el par de claves.</summary>
    public const string EmbeddedPublicKeyB64 = "5R1++DQ/dm1Y9pTBzT3rx6g30R2AuQOpi32CJgddS4o=";

    /// <summary>Verifica forma + firma y devuelve el payload. NO comprueba ventana
    /// temporal, binding de máquina ni anti-rollback: de eso se ocupa LicenseService.</summary>
    public static bool TryVerify(string? token, out LicensePayload? payload, out LicenseVerdict verdict, out string error)
    {
        payload = null;
        verdict = LicenseVerdict.Malformed;
        error   = "";

        if (string.IsNullOrWhiteSpace(token))
        {
            verdict = LicenseVerdict.Missing;
            error   = "No hay token de licencia.";
            return false;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Magic)
        {
            verdict = LicenseVerdict.Malformed;
            error   = "El token no tiene el formato esperado (DMLIC1.<payload>.<firma>).";
            return false;
        }

        byte[] signature;
        try { signature = B64Url.Decode(parts[2]); }
        catch { verdict = LicenseVerdict.Malformed; error = "Firma no decodificable."; return false; }

        if (signature.Length != 64)
        {
            verdict = LicenseVerdict.Malformed;
            error   = "Longitud de firma Ed25519 inesperada.";
            return false;
        }

        // Verificación Ed25519 sobre la entrada de firma exacta.
        var signingInput = Encoding.ASCII.GetBytes(Magic + "." + parts[1]);
        try
        {
            var pub      = new Ed25519PublicKeyParameters(Convert.FromBase64String(EmbeddedPublicKeyB64), 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, pub);
            verifier.BlockUpdate(signingInput, 0, signingInput.Length);
            if (!verifier.VerifySignature(signature))
            {
                verdict = LicenseVerdict.BadSignature;
                error   = "La firma no verifica con la clave pública embebida.";
                return false;
            }
        }
        catch (Exception ex)
        {
            verdict = LicenseVerdict.BadSignature;
            error   = "Error verificando la firma: " + ex.Message;
            return false;
        }

        // Payload: se decodifica solo para leer.
        byte[] payloadBytes;
        try { payloadBytes = B64Url.Decode(parts[1]); }
        catch { verdict = LicenseVerdict.Malformed; error = "Payload no decodificable."; return false; }

        try { payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes); }
        catch (Exception ex) { verdict = LicenseVerdict.Malformed; error = "Payload JSON inválido: " + ex.Message; return false; }

        if (payload is null)
        {
            verdict = LicenseVerdict.Malformed;
            error   = "Payload vacío.";
            return false;
        }

        if (!string.Equals(payload.Product, Product, StringComparison.Ordinal))
        {
            verdict = LicenseVerdict.WrongProduct;
            error   = $"La licencia no es para este producto (product='{payload.Product}').";
            return false;
        }

        verdict = LicenseVerdict.Valid;   // firma + producto OK; el resto lo evalúa LicenseService
        return true;
    }
}
