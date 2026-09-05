using System.Buffers.Binary;
using System.Numerics;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// A Zstandard (RFC 8878) decompressor for the members of Python-written <c>.eval</c> zips, which
/// <c>_util/zipfile.py</c> stores with ZIP method 93 (zstandard, level 3, possibly multi-frame). Decode only:
/// raw / RLE / compressed blocks, Huffman-coded literals (one or four streams, FSE- or directly-described
/// trees, treeless reuse), predefined / RLE / FSE / repeat sequence tables, repeat offsets, skippable frames
/// and concatenated frames. Dictionaries are not supported (Python never uses them) and the optional content
/// checksum is skipped rather than verified. The BCL has no zstd support and the port allows no packages.
/// </summary>
public static class ZstdDecoder
{
    private const uint FrameMagic = 0xFD2FB528;

    private const uint SkippableMagicMask = 0xFFFFFFF0;

    private const uint SkippableMagic = 0x184D2A50;

    private const int MaxLiteralsLength = 35;

    private const int MaxMatchLength = 52;

    private const int MaxOffsetCode = 31;

    private static readonly short[] LiteralsLengthDefault =
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
    ];

    private static readonly short[] MatchLengthDefault =
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        -1, -1, -1, -1, -1, -1, -1,
    ];

    private static readonly short[] OffsetDefault =
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ];

    private static readonly uint[] LiteralsLengthBaseline =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536,
    ];

    private static readonly byte[] LiteralsLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    private static readonly uint[] MatchLengthBaseline =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539,
    ];

    private static readonly byte[] MatchLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    /// <summary>Decompresses one or more concatenated zstd frames. <paramref name="expectedSize"/> (when known) pre-sizes the output.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, long expectedSize = -1)
    {
        var output = new OutputBuffer(expectedSize > 0 && expectedSize < int.MaxValue ? (int)expectedSize : Math.Min(Math.Max(input.Length * 4, 1024), 1 << 20));
        var position = 0;
        while (position < input.Length)
        {
            if (input.Length - position < 4)
            {
                throw new InvalidDataException("Truncated zstd frame header.");
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(input[position..]);
            if ((magic & SkippableMagicMask) == SkippableMagic)
            {
                if (input.Length - position < 8)
                {
                    throw new InvalidDataException("Truncated zstd skippable frame.");
                }

                var skip = BinaryPrimitives.ReadUInt32LittleEndian(input[(position + 4)..]);
                position = checked(position + 8 + (int)skip);
                continue;
            }

            if (magic != FrameMagic)
            {
                throw new InvalidDataException($"Not a zstd frame (magic 0x{magic:x8}).");
            }

            position = DecodeFrame(input, position + 4, output);
        }

        if (position != input.Length)
        {
            throw new InvalidDataException("zstd input has trailing bytes.");
        }

        return output.ToArray();
    }

    private static int DecodeFrame(ReadOnlySpan<byte> input, int position, OutputBuffer output)
    {
        var descriptor = input[position++];
        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var hasChecksum = (descriptor & 0x04) != 0;
        var dictionaryIdFlag = descriptor & 0x03;
        if ((descriptor & 0x08) != 0)
        {
            throw new InvalidDataException("zstd frame header has a reserved bit set.");
        }

        if (!singleSegment)
        {
            position++; // window descriptor: the whole frame is kept in memory, so the window size is irrelevant
        }

        var dictionaryIdSize = dictionaryIdFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        ulong dictionaryId = 0;
        for (var i = 0; i < dictionaryIdSize; i++)
        {
            dictionaryId |= (ulong)input[position++] << (8 * i);
        }

        if (dictionaryId != 0)
        {
            throw new NotSupportedException("zstd frames that use a dictionary are not supported.");
        }

        var contentSizeBytes = contentSizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        ulong contentSize = 0;
        for (var i = 0; i < contentSizeBytes; i++)
        {
            contentSize |= (ulong)input[position++] << (8 * i);
        }

        if (contentSizeBytes == 2)
        {
            contentSize += 256;
        }

        var frame = new FrameState(output.Length);
        var lastBlock = false;
        while (!lastBlock)
        {
            if (input.Length - position < 3)
            {
                throw new InvalidDataException("Truncated zstd block header.");
            }

            var header = input[position] | (input[position + 1] << 8) | (input[position + 2] << 16);
            position += 3;
            lastBlock = (header & 1) != 0;
            var blockType = (header >> 1) & 3;
            var blockSize = header >> 3;
            switch (blockType)
            {
                case 0:
                    Require(input, position, blockSize);
                    output.Append(input.Slice(position, blockSize));
                    position += blockSize;
                    break;
                case 1:
                    Require(input, position, 1);
                    output.AppendRepeated(input[position], blockSize);
                    position += 1;
                    break;
                case 2:
                    Require(input, position, blockSize);
                    DecodeCompressedBlock(input.Slice(position, blockSize), output, frame);
                    position += blockSize;
                    break;
                default:
                    throw new InvalidDataException("zstd block uses the reserved block type.");
            }
        }

        if (contentSizeBytes > 0 && (ulong)(output.Length - frame.FrameStart) != contentSize)
        {
            throw new InvalidDataException($"zstd frame decoded to {output.Length - frame.FrameStart} bytes, header declares {contentSize}.");
        }

        if (hasChecksum)
        {
            Require(input, position, 4);
            position += 4;
        }

        return position;
    }

    private static void Require(ReadOnlySpan<byte> input, int position, int count)
    {
        if (input.Length - position < count)
        {
            throw new InvalidDataException("Truncated zstd block.");
        }
    }

    private static void DecodeCompressedBlock(ReadOnlySpan<byte> block, OutputBuffer output, FrameState frame)
    {
        var literals = DecodeLiterals(block, frame, out var consumed);
        DecodeSequences(block[consumed..], literals, output, frame);
    }

    private static byte[] DecodeLiterals(ReadOnlySpan<byte> block, FrameState frame, out int consumed)
    {
        if (block.Length < 1)
        {
            throw new InvalidDataException("zstd block has no literals section.");
        }

        var b0 = block[0];
        var literalsType = b0 & 3;
        var sizeFormat = (b0 >> 2) & 3;
        int regeneratedSize;
        int headerSize;
        if (literalsType is 0 or 1)
        {
            switch (sizeFormat)
            {
                case 0:
                case 2:
                    regeneratedSize = b0 >> 3;
                    headerSize = 1;
                    break;
                case 1:
                    Require(block, 0, 2);
                    regeneratedSize = (b0 >> 4) | (block[1] << 4);
                    headerSize = 2;
                    break;
                default:
                    Require(block, 0, 3);
                    regeneratedSize = (b0 >> 4) | (block[1] << 4) | (block[2] << 12);
                    headerSize = 3;
                    break;
            }

            if (literalsType == 0)
            {
                Require(block, headerSize, regeneratedSize);
                consumed = headerSize + regeneratedSize;
                return block.Slice(headerSize, regeneratedSize).ToArray();
            }

            Require(block, headerSize, 1);
            consumed = headerSize + 1;
            var rle = new byte[regeneratedSize];
            Array.Fill(rle, block[headerSize]);
            return rle;
        }

        int compressedSize;
        int streams;
        switch (sizeFormat)
        {
            case 0:
            case 1:
                Require(block, 0, 3);
                streams = sizeFormat == 0 ? 1 : 4;
                regeneratedSize = (b0 >> 4) | ((block[1] & 0x3F) << 4);
                compressedSize = (block[1] >> 6) | (block[2] << 2);
                headerSize = 3;
                break;
            case 2:
                Require(block, 0, 4);
                streams = 4;
                regeneratedSize = (b0 >> 4) | (block[1] << 4) | ((block[2] & 3) << 12);
                compressedSize = (block[2] >> 2) | (block[3] << 6);
                headerSize = 4;
                break;
            default:
                Require(block, 0, 5);
                streams = 4;
                regeneratedSize = (b0 >> 4) | (block[1] << 4) | ((block[2] & 0x3F) << 12);
                compressedSize = (block[2] >> 6) | (block[3] << 2) | (block[4] << 10);
                headerSize = 5;
                break;
        }

        Require(block, headerSize, compressedSize);
        consumed = headerSize + compressedSize;
        var data = block.Slice(headerSize, compressedSize);
        if (literalsType == 2)
        {
            frame.HuffmanTable = HuffmanTable.Read(data, out var tableBytes);
            data = data[tableBytes..];
        }
        else if (frame.HuffmanTable is null)
        {
            throw new InvalidDataException("zstd treeless literals without a previous Huffman table.");
        }

        var table = frame.HuffmanTable;
        var literals = new byte[regeneratedSize];
        if (streams == 1)
        {
            table.Decode(data, literals);
            return literals;
        }

        if (data.Length < 6)
        {
            throw new InvalidDataException("zstd four-stream literals are missing the jump table.");
        }

        var size1 = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var size2 = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var size3 = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var segment = (regeneratedSize + 3) / 4;
        var lastSegment = regeneratedSize - 3 * segment;
        if (lastSegment < 0 || 6 + size1 + size2 + size3 > data.Length)
        {
            throw new InvalidDataException("zstd four-stream literals have inconsistent stream sizes.");
        }

        var offset = 6;
        table.Decode(data.Slice(offset, size1), literals.AsSpan(0, segment));
        offset += size1;
        table.Decode(data.Slice(offset, size2), literals.AsSpan(segment, segment));
        offset += size2;
        table.Decode(data.Slice(offset, size3), literals.AsSpan(2 * segment, segment));
        offset += size3;
        table.Decode(data[offset..], literals.AsSpan(3 * segment, lastSegment));
        return literals;
    }

    private static void DecodeSequences(ReadOnlySpan<byte> section, byte[] literals, OutputBuffer output, FrameState frame)
    {
        if (section.Length == 0)
        {
            throw new InvalidDataException("zstd block has no sequences section.");
        }

        var position = 0;
        int sequenceCount;
        var first = section[position++];
        if (first == 0)
        {
            sequenceCount = 0;
        }
        else if (first < 128)
        {
            sequenceCount = first;
        }
        else if (first < 255)
        {
            Require(section, position, 1);
            sequenceCount = ((first - 128) << 8) | section[position++];
        }
        else
        {
            Require(section, position, 2);
            sequenceCount = (section[position] | (section[position + 1] << 8)) + 0x7F00;
            position += 2;
        }

        if (sequenceCount == 0)
        {
            output.Append(literals);
            return;
        }

        Require(section, position, 1);
        var modes = section[position++];
        if ((modes & 3) != 0)
        {
            throw new InvalidDataException("zstd sequence compression modes have reserved bits set.");
        }

        frame.LiteralsLengthTable = ReadSequenceTable(section, ref position, modes >> 6, frame.LiteralsLengthTable, LiteralsLengthDefault, 6, 9, MaxLiteralsLength);
        frame.OffsetTable = ReadSequenceTable(section, ref position, (modes >> 4) & 3, frame.OffsetTable, OffsetDefault, 5, 8, MaxOffsetCode);
        frame.MatchLengthTable = ReadSequenceTable(section, ref position, (modes >> 2) & 3, frame.MatchLengthTable, MatchLengthDefault, 6, 9, MaxMatchLength);

        var llTable = frame.LiteralsLengthTable;
        var ofTable = frame.OffsetTable;
        var mlTable = frame.MatchLengthTable;
        var bits = new ReverseBitReader(section[position..]);
        var llState = (int)bits.Read(llTable.AccuracyLog);
        var ofState = (int)bits.Read(ofTable.AccuracyLog);
        var mlState = (int)bits.Read(mlTable.AccuracyLog);
        var literalPosition = 0;
        for (var i = 0; i < sequenceCount; i++)
        {
            var ofCode = ofTable.Symbols[ofState];
            var mlCode = mlTable.Symbols[mlState];
            var llCode = llTable.Symbols[llState];
            if (llCode > MaxLiteralsLength || mlCode > MaxMatchLength || ofCode > MaxOffsetCode)
            {
                throw new InvalidDataException("zstd sequence code out of range.");
            }

            var offsetValue = (1UL << ofCode) + bits.Read(ofCode);
            var matchLength = MatchLengthBaseline[mlCode] + (uint)bits.Read(MatchLengthBits[mlCode]);
            var literalLength = LiteralsLengthBaseline[llCode] + (uint)bits.Read(LiteralsLengthBits[llCode]);
            if (i < sequenceCount - 1)
            {
                llState = llTable.Baselines[llState] + (int)bits.Read(llTable.Bits[llState]);
                mlState = mlTable.Baselines[mlState] + (int)bits.Read(mlTable.Bits[mlState]);
                ofState = ofTable.Baselines[ofState] + (int)bits.Read(ofTable.Bits[ofState]);
            }

            if (bits.Overflow)
            {
                throw new InvalidDataException("zstd sequence bitstream is truncated.");
            }

            long offset;
            if (offsetValue > 3)
            {
                offset = (long)offsetValue - 3;
                frame.RepeatOffsets[2] = frame.RepeatOffsets[1];
                frame.RepeatOffsets[1] = frame.RepeatOffsets[0];
                frame.RepeatOffsets[0] = offset;
            }
            else
            {
                var index = (int)offsetValue - 1 + (literalLength == 0 ? 1 : 0);
                switch (index)
                {
                    case 0:
                        offset = frame.RepeatOffsets[0];
                        break;
                    case 1:
                        offset = frame.RepeatOffsets[1];
                        frame.RepeatOffsets[1] = frame.RepeatOffsets[0];
                        frame.RepeatOffsets[0] = offset;
                        break;
                    case 2:
                        offset = frame.RepeatOffsets[2];
                        frame.RepeatOffsets[2] = frame.RepeatOffsets[1];
                        frame.RepeatOffsets[1] = frame.RepeatOffsets[0];
                        frame.RepeatOffsets[0] = offset;
                        break;
                    default:
                        offset = frame.RepeatOffsets[0] - 1;
                        if (offset <= 0)
                        {
                            throw new InvalidDataException("zstd repeat offset underflow.");
                        }

                        frame.RepeatOffsets[2] = frame.RepeatOffsets[1];
                        frame.RepeatOffsets[1] = frame.RepeatOffsets[0];
                        frame.RepeatOffsets[0] = offset;
                        break;
                }
            }

            if (literalPosition + literalLength > (uint)literals.Length)
            {
                throw new InvalidDataException("zstd sequence references more literals than the block holds.");
            }

            output.Append(literals.AsSpan(literalPosition, (int)literalLength));
            literalPosition += (int)literalLength;
            if (offset > output.Length - frame.FrameStart)
            {
                throw new InvalidDataException("zstd match offset reaches before the start of the frame.");
            }

            output.CopyMatch((int)offset, (int)matchLength);
        }

        if (bits.Overflow)
        {
            throw new InvalidDataException("zstd sequence bitstream is truncated.");
        }

        output.Append(literals.AsSpan(literalPosition));
    }

    private static FseTable ReadSequenceTable(ReadOnlySpan<byte> section, ref int position, int mode, FseTable? previous, short[] defaultDistribution, int defaultAccuracyLog, int maxAccuracyLog, int maxSymbol)
    {
        switch (mode)
        {
            case 0:
                return FseTable.Build(defaultDistribution, defaultAccuracyLog);
            case 1:
                Require(section, position, 1);
                var symbol = section[position++];
                if (symbol > maxSymbol)
                {
                    throw new InvalidDataException("zstd RLE sequence symbol out of range.");
                }

                return FseTable.Rle(symbol);
            case 2:
                var distribution = FseTable.ReadDistribution(section[position..], maxAccuracyLog, maxSymbol, out var accuracyLog, out var consumed);
                position += consumed;
                return FseTable.Build(distribution, accuracyLog);
            default:
                return previous ?? throw new InvalidDataException("zstd repeat sequence table without a previous table.");
        }
    }

    /// <summary>Per-frame decoding state: the reusable Huffman and FSE tables and the repeat offsets.</summary>
    private sealed class FrameState(int frameStart)
    {
        public int FrameStart { get; } = frameStart;

        public HuffmanTable? HuffmanTable { get; set; }

        public FseTable? LiteralsLengthTable { get; set; }

        public FseTable? OffsetTable { get; set; }

        public FseTable? MatchLengthTable { get; set; }

        public long[] RepeatOffsets { get; } = [1, 4, 8];
    }

    /// <summary>An FSE decoding table: per state the symbol, the bits to read for the next state and its baseline.</summary>
    private sealed class FseTable
    {
        private FseTable(int accuracyLog, byte[] symbols, byte[] bits, int[] baselines)
        {
            AccuracyLog = accuracyLog;
            Symbols = symbols;
            Bits = bits;
            Baselines = baselines;
        }

        public int AccuracyLog { get; }

        public byte[] Symbols { get; }

        public byte[] Bits { get; }

        public int[] Baselines { get; }

        public static FseTable Rle(byte symbol) => new(0, [symbol], [0], [0]);

        /// <summary>Port of <c>FSE_buildDTable</c>: spreads the normalized counts over the state table.</summary>
        public static FseTable Build(short[] distribution, int accuracyLog)
        {
            var tableSize = 1 << accuracyLog;
            var symbols = new byte[tableSize];
            var bits = new byte[tableSize];
            var baselines = new int[tableSize];
            var symbolNext = new int[distribution.Length];
            var highThreshold = tableSize - 1;
            for (var s = 0; s < distribution.Length; s++)
            {
                if (distribution[s] == -1)
                {
                    symbols[highThreshold--] = (byte)s;
                    symbolNext[s] = 1;
                }
                else
                {
                    symbolNext[s] = distribution[s];
                }
            }

            var step = (tableSize >> 1) + (tableSize >> 3) + 3;
            var mask = tableSize - 1;
            var position = 0;
            for (var s = 0; s < distribution.Length; s++)
            {
                for (var i = 0; i < distribution[s]; i++)
                {
                    symbols[position] = (byte)s;
                    position = (position + step) & mask;
                    while (position > highThreshold)
                    {
                        position = (position + step) & mask;
                    }
                }
            }

            if (position != 0)
            {
                throw new InvalidDataException("zstd FSE distribution does not fill its table.");
            }

            for (var state = 0; state < tableSize; state++)
            {
                var symbol = symbols[state];
                var nextState = symbolNext[symbol]++;
                var nbBits = accuracyLog - (31 - BitOperations.LeadingZeroCount((uint)nextState));
                bits[state] = (byte)nbBits;
                baselines[state] = (nextState << nbBits) - tableSize;
            }

            return new FseTable(accuracyLog, symbols, bits, baselines);
        }

        /// <summary>Port of <c>FSE_readNCount</c>: the normalized counts of a compressed table description.</summary>
        public static short[] ReadDistribution(ReadOnlySpan<byte> data, int maxAccuracyLog, int maxSymbol, out int accuracyLog, out int consumed)
        {
            var bits = new ForwardBitReader(data);
            accuracyLog = (int)bits.Read(4) + 5;
            if (accuracyLog > maxAccuracyLog)
            {
                throw new InvalidDataException($"zstd FSE accuracy log {accuracyLog} exceeds the maximum {maxAccuracyLog}.");
            }

            var remaining = (1 << accuracyLog) + 1;
            var threshold = 1 << accuracyLog;
            var nbBits = accuracyLog + 1;
            var counts = new List<short>();
            var previousZero = false;
            while (remaining > 1 && counts.Count <= maxSymbol)
            {
                if (previousZero)
                {
                    var zeroRun = counts.Count;
                    while (bits.Peek(16) == 0xFFFF)
                    {
                        zeroRun += 24;
                        bits.Skip(16);
                    }

                    while (bits.Peek(2) == 3)
                    {
                        zeroRun += 3;
                        bits.Skip(2);
                    }

                    zeroRun += (int)bits.Read(2);
                    if (zeroRun > maxSymbol + 1)
                    {
                        throw new InvalidDataException("zstd FSE distribution has more symbols than allowed.");
                    }

                    while (counts.Count < zeroRun)
                    {
                        counts.Add(0);
                    }

                    if (counts.Count > maxSymbol)
                    {
                        break;
                    }
                }

                var max = (2 * threshold - 1) - remaining;
                int count;
                var value = (int)bits.Peek(nbBits);
                if ((value & (threshold - 1)) < max)
                {
                    count = value & (threshold - 1);
                    bits.Skip(nbBits - 1);
                }
                else
                {
                    count = value & (2 * threshold - 1);
                    if (count >= threshold)
                    {
                        count -= max;
                    }

                    bits.Skip(nbBits);
                }

                count--;
                remaining -= Math.Abs(count);
                counts.Add((short)count);
                previousZero = count == 0;
                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }

            if (remaining != 1)
            {
                throw new InvalidDataException("zstd FSE distribution does not sum to its table size.");
            }

            if (bits.Overflow)
            {
                throw new InvalidDataException("zstd FSE distribution is truncated.");
            }

            consumed = bits.BytesConsumed;
            return counts.ToArray();
        }
    }

    /// <summary>A Huffman literals decoding table indexed by the next <see cref="MaxBits"/> bits of a reverse stream.</summary>
    private sealed class HuffmanTable
    {
        private readonly byte[] _symbols;

        private readonly byte[] _lengths;

        private HuffmanTable(int maxBits, byte[] symbols, byte[] lengths)
        {
            MaxBits = maxBits;
            _symbols = symbols;
            _lengths = lengths;
        }

        public int MaxBits { get; }

        /// <summary>Port of <c>HUF_readStats</c> plus <c>HUF_readDTableX1</c>: reads the tree description and builds the table.</summary>
        public static HuffmanTable Read(ReadOnlySpan<byte> data, out int consumed)
        {
            if (data.Length < 1)
            {
                throw new InvalidDataException("zstd Huffman tree description is missing.");
            }

            var header = data[0];
            var weights = new List<byte>();
            if (header < 128)
            {
                var compressed = data.Slice(1, Math.Min(header, data.Length - 1));
                if (compressed.Length != header)
                {
                    throw new InvalidDataException("zstd Huffman tree description is truncated.");
                }

                var distribution = FseTable.ReadDistribution(compressed, 6, 255, out var accuracyLog, out var tableBytes);
                var table = FseTable.Build(distribution, accuracyLog);
                var bits = new ReverseBitReader(compressed[tableBytes..]);
                var state1 = (int)bits.Read(accuracyLog);
                var state2 = (int)bits.Read(accuracyLog);
                if (bits.Overflow)
                {
                    throw new InvalidDataException("zstd Huffman weights bitstream is truncated.");
                }

                // FSE_decompress's tail rule: the two states alternate, and once a state update reads past the
                // start of the stream the other state's (already valid) symbol is emitted before stopping
                while (true)
                {
                    weights.Add(table.Symbols[state1]);
                    state1 = table.Baselines[state1] + (int)bits.Read(table.Bits[state1]);
                    if (bits.Overflow)
                    {
                        weights.Add(table.Symbols[state2]);
                        break;
                    }

                    weights.Add(table.Symbols[state2]);
                    state2 = table.Baselines[state2] + (int)bits.Read(table.Bits[state2]);
                    if (bits.Overflow)
                    {
                        weights.Add(table.Symbols[state1]);
                        break;
                    }

                    if (weights.Count > 255)
                    {
                        throw new InvalidDataException("zstd Huffman tree has more weights than symbols.");
                    }
                }

                consumed = 1 + header;
            }
            else
            {
                var count = header - 127;
                var bytes = (count + 1) / 2;
                if (data.Length < 1 + bytes)
                {
                    throw new InvalidDataException("zstd Huffman weights are truncated.");
                }

                for (var i = 0; i < count; i++)
                {
                    var b = data[1 + i / 2];
                    weights.Add((byte)(i % 2 == 0 ? b >> 4 : b & 0x0F));
                }

                consumed = 1 + bytes;
            }

            if (weights.Count is 0 or > 255)
            {
                throw new InvalidDataException("zstd Huffman tree has an invalid number of weights.");
            }

            var total = 0L;
            foreach (var weight in weights)
            {
                if (weight > 12)
                {
                    throw new InvalidDataException("zstd Huffman weight out of range.");
                }

                if (weight > 0)
                {
                    total += 1L << (weight - 1);
                }
            }

            if (total == 0)
            {
                throw new InvalidDataException("zstd Huffman tree has no weighted symbols.");
            }

            var maxBits = 64 - BitOperations.LeadingZeroCount((ulong)total);
            var leftover = (1L << maxBits) - total;
            if (leftover <= 0 || (leftover & (leftover - 1)) != 0)
            {
                throw new InvalidDataException("zstd Huffman weights do not complete a tree.");
            }

            weights.Add((byte)(BitOperations.TrailingZeroCount((ulong)leftover) + 1));
            if (maxBits > 11)
            {
                throw new InvalidDataException("zstd Huffman tree is deeper than allowed.");
            }

            var tableSize = 1 << maxBits;
            var symbols = new byte[tableSize];
            var lengths = new byte[tableSize];
            var rankStart = new int[maxBits + 2];
            var rankCount = new int[maxBits + 2];
            foreach (var weight in weights)
            {
                rankCount[weight]++;
            }

            for (var w = 1; w <= maxBits; w++)
            {
                rankStart[w + 1] = rankStart[w] + rankCount[w] * (1 << (w - 1));
            }

            for (var symbol = 0; symbol < weights.Count; symbol++)
            {
                var weight = weights[symbol];
                if (weight == 0)
                {
                    continue;
                }

                var length = 1 << (weight - 1);
                var start = rankStart[weight];
                for (var i = 0; i < length; i++)
                {
                    symbols[start + i] = (byte)symbol;
                    lengths[start + i] = (byte)(maxBits + 1 - weight);
                }

                rankStart[weight] += length;
            }

            return new HuffmanTable(maxBits, symbols, lengths);
        }

        /// <summary>Decodes exactly <paramref name="output"/>.Length literals from one reverse bitstream, which must be fully consumed.</summary>
        public void Decode(ReadOnlySpan<byte> stream, Span<byte> output)
        {
            if (stream.Length == 0)
            {
                throw new InvalidDataException("zstd Huffman literals stream is empty.");
            }

            var bits = new ReverseBitReader(stream);
            for (var i = 0; i < output.Length; i++)
            {
                var index = (int)bits.Peek(MaxBits);
                output[i] = _symbols[index];
                bits.Skip(_lengths[index]);
            }

            if (bits.Overflow || !bits.AtEnd)
            {
                throw new InvalidDataException("zstd Huffman literals stream was not consumed exactly.");
            }
        }
    }

    /// <summary>
    /// A zstd backward bitstream: bits are consumed from the last byte towards the first, most significant
    /// first, after the padding bit that marks the end. Reads past the beginning yield zero bits and raise
    /// <see cref="Overflow"/>, which is how the FSE weight decoder detects the end of its stream.
    /// </summary>
    private ref struct ReverseBitReader
    {
        private readonly ReadOnlySpan<byte> _data;

        private long _bitPosition;

        public ReverseBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            if (data.Length == 0)
            {
                _bitPosition = 0;
                return;
            }

            var last = data[^1];
            if (last == 0)
            {
                throw new InvalidDataException("zstd bitstream ends with a zero byte (no padding marker).");
            }

            _bitPosition = data.Length * 8L - (BitOperations.LeadingZeroCount((uint)last) - 24) - 1;
        }

        public readonly bool Overflow => _bitPosition < 0;

        public readonly bool AtEnd => _bitPosition == 0;

        public readonly ulong Peek(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var start = _bitPosition - count;
            if (start < 0)
            {
                // partially before the stream: the missing low bits are zero
                var available = (int)_bitPosition;
                return available <= 0 ? 0 : ReadWindow(0, available) << (count - available);
            }

            return ReadWindow(start, count);
        }

        public ulong Read(int count)
        {
            var value = Peek(count);
            _bitPosition -= count;
            return value;
        }

        public void Skip(int count) => _bitPosition -= count;

        private readonly ulong ReadWindow(long start, int count)
        {
            var byteIndex = (int)(start >> 3);
            var shift = (int)(start & 7);
            ulong window = 0;
            var bytes = Math.Min(8, _data.Length - byteIndex);
            for (var i = 0; i < bytes; i++)
            {
                window |= (ulong)_data[byteIndex + i] << (8 * i);
            }

            return (window >> shift) & ((1UL << count) - 1);
        }
    }

    /// <summary>A forward, least-significant-bit-first reader for FSE table descriptions.</summary>
    private ref struct ForwardBitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        private long _bitPosition;

        public readonly bool Overflow => _bitPosition > _data.Length * 8L;

        public readonly int BytesConsumed => (int)((_bitPosition + 7) / 8);

        public readonly ulong Peek(int count)
        {
            var byteIndex = (int)(_bitPosition >> 3);
            var shift = (int)(_bitPosition & 7);
            ulong window = 0;
            var bytes = Math.Min(8, _data.Length - byteIndex);
            for (var i = 0; i < bytes; i++)
            {
                window |= (ulong)_data[byteIndex + i] << (8 * i);
            }

            return (window >> shift) & ((1UL << count) - 1);
        }

        public ulong Read(int count)
        {
            var value = Peek(count);
            _bitPosition += count;
            return value;
        }

        public void Skip(int count) => _bitPosition += count;
    }

    /// <summary>A growable output buffer that supports overlapping match copies.</summary>
    private sealed class OutputBuffer(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(capacity, 16)];

        public int Length { get; private set; }

        public void Append(ReadOnlySpan<byte> data)
        {
            Ensure(data.Length);
            data.CopyTo(_buffer.AsSpan(Length));
            Length += data.Length;
        }

        public void AppendRepeated(byte value, int count)
        {
            Ensure(count);
            _buffer.AsSpan(Length, count).Fill(value);
            Length += count;
        }

        public void CopyMatch(int offset, int length)
        {
            Ensure(length);
            var source = Length - offset;
            if (offset >= length)
            {
                _buffer.AsSpan(source, length).CopyTo(_buffer.AsSpan(Length));
            }
            else
            {
                for (var i = 0; i < length; i++)
                {
                    _buffer[Length + i] = _buffer[source + i];
                }
            }

            Length += length;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

        private void Ensure(int extra)
        {
            if (Length + extra <= _buffer.Length)
            {
                return;
            }

            var size = Math.Max(_buffer.Length * 2, Length + extra);
            Array.Resize(ref _buffer, size);
        }
    }
}
