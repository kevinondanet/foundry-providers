namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>The CRC-32 (IEEE 802.3, as used by ZIP) of a byte sequence; the BCL exposes no CRC-32 outside a package.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Continues a running checksum (<paramref name="crc"/> starts at 0) over <paramref name="data"/>.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var value = ~crc;
        foreach (var b in data)
        {
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
