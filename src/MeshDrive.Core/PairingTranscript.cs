using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeshDrive.Core;

public sealed record PairingSide(string DeviceId, string Fingerprint, string Nonce);

public sealed record PairingTranscript(PairingSide Left, PairingSide Right)
{
    public const string VersionPrefix = "MESHDRIVE-PAIR-1";

    public static PairingTranscript Create(PairingSide local, PairingSide remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (string.CompareOrdinal(local.DeviceId, remote.DeviceId) <= 0)
        {
            return new PairingTranscript(local, remote);
        }

        return new PairingTranscript(remote, local);
    }

    public string CanonicalText()
    {
        var builder = new StringBuilder();
        builder.Append(VersionPrefix).Append('\n');
        AppendSide(builder, Left);
        AppendSide(builder, Right);
        return builder.ToString();
    }

    public byte[] CanonicalBytes() => Encoding.UTF8.GetBytes(CanonicalText());

    private static void AppendSide(StringBuilder builder, PairingSide side)
    {
        builder.Append(side.DeviceId).Append('\n');
        builder.Append(side.Fingerprint).Append('\n');
        builder.Append(side.Nonce).Append('\n');
    }
}

public static class PairingNonce
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

public static class SasCalculator
{
    public static string Compute(PairingTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var hash = SHA256.HashData(transcript.CanonicalBytes());
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static string FormatDisplay(string sas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sas);
        return sas.Length == 6 ? $"{sas[..3]} {sas[3..]}" : sas;
    }
}
