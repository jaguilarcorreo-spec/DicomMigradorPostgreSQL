using System.Text;

namespace DicomMigrator.Infrastructure.Services.Licensing;

/// <summary>base64url SIN relleno (RFC 4648 §5). Igual que el b64url del generador Python.</summary>
public static class B64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
            case 1: throw new FormatException("Longitud base64url inválida.");
        }
        return Convert.FromBase64String(s);
    }
}

/// <summary>base32 RFC 4648 (alfabeto A–Z 2–7), sin relleno. Se usa para el fingerprint.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}
