using System.Text;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>
/// Port of <c>_util/hash.py</c> <c>mm3_hash</c>: MurmurHash3 x64/128 (seed 0) of the UTF-8 text, formatted as
/// the two unsigned 64-bit halves in hex. Kept bit-exact with Python so the bridge recognises the
/// <c>attachment://&lt;hash&gt;</c> references Inspect's transcript condensation produces for long messages.
/// </summary>
internal static class Mm3Hash
{
    private const ulong C1 = 0x87c37b91114253d5UL;

    private const ulong C2 = 0x4cf5ad432745937fUL;

    public static string Hash(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bytes = Utf8WithBackslashReplace(message);
        var (h1, h2) = Hash128(bytes);
        return $"{h1:x16}{h2:x16}";
    }

    /// <summary>Python's <c>encode("utf-8", errors="backslashreplace")</c>: a lone surrogate becomes the literal <c>\udXXX</c> escape rather than U+FFFD.</summary>
    private static byte[] Utf8WithBackslashReplace(string text)
    {
        var hasLoneSurrogate = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                hasLoneSurrogate = true;
                break;
            }
        }

        if (!hasLoneSurrogate)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(text[i]).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                sb.Append("\\u").Append(((int)text[i]).ToString("x4"));
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static (ulong H1, ulong H2) Hash128(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        ulong h1 = 0;
        ulong h2 = 0;
        var blocks = length / 16;
        for (var i = 0; i < blocks; i++)
        {
            var k1 = BitConverter.ToUInt64(data.Slice(i * 16, 8));
            var k2 = BitConverter.ToUInt64(data.Slice(i * 16 + 8, 8));

            k1 *= C1;
            k1 = ulong.RotateLeft(k1, 31);
            k1 *= C2;
            h1 ^= k1;
            h1 = ulong.RotateLeft(h1, 27);
            h1 += h2;
            h1 = h1 * 5 + 0x52dce729;

            k2 *= C2;
            k2 = ulong.RotateLeft(k2, 33);
            k2 *= C1;
            h2 ^= k2;
            h2 = ulong.RotateLeft(h2, 31);
            h2 += h1;
            h2 = h2 * 5 + 0x38495ab5;
        }

        var tail = data[(blocks * 16)..];
        ulong t1 = 0;
        ulong t2 = 0;
        var rem = length & 15;
        if (rem > 8)
        {
            for (var i = rem - 1; i >= 8; i--)
            {
                t2 ^= (ulong)tail[i] << ((i - 8) * 8);
            }

            t2 *= C2;
            t2 = ulong.RotateLeft(t2, 33);
            t2 *= C1;
            h2 ^= t2;
        }

        if (rem > 0)
        {
            for (var i = Math.Min(rem, 8) - 1; i >= 0; i--)
            {
                t1 ^= (ulong)tail[i] << (i * 8);
            }

            t1 *= C1;
            t1 = ulong.RotateLeft(t1, 31);
            t1 *= C2;
            h1 ^= t1;
        }

        h1 ^= (ulong)length;
        h2 ^= (ulong)length;
        h1 += h2;
        h2 += h1;
        h1 = FMix(h1);
        h2 = FMix(h2);
        h1 += h2;
        h2 += h1;
        return (h1, h2);
    }

    private static ulong FMix(ulong k)
    {
        k ^= k >> 33;
        k *= 0xff51afd7ed558ccdUL;
        k ^= k >> 33;
        k *= 0xc4ceb9fe1a85ec53UL;
        k ^= k >> 33;
        return k;
    }
}
