using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>One central-directory entry of a <c>.eval</c> zip.</summary>
internal sealed record ZipMemberInfo(string Name, ushort Method, uint Crc, long CompressedSize, long UncompressedSize, long LocalHeaderOffset, ushort DosTime, ushort DosDate);

/// <summary>
/// Port of the zip reading that <c>_util/async_zip.py</c> <c>AsyncZipReader</c> and Python's <c>zipfile</c> do for
/// <c>.eval</c> logs: the central directory (zip64 aware) is the index, members are read by name with the last
/// entry winning for a repeated name (a re-logged sample supersedes its prior record, as
/// <c>ZipFile.NameToInfo</c> resolves), and stored (0), deflate (8) and zstandard (93, what Python writes) members
/// are decoded and CRC-checked. <see cref="System.IO.Compression.ZipArchive"/> cannot read method 93.
/// </summary>
internal sealed class ZipLogReader : IDisposable
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;

    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;

    private const uint Zip64LocatorSignature = 0x07064B50;

    private const uint CentralDirectorySignature = 0x02014B50;

    private const uint LocalHeaderSignature = 0x04034B50;

    private const ushort MethodStored = 0;

    private const ushort MethodDeflate = 8;

    private const ushort MethodZstandard = 93;

    private readonly Stream _stream;

    private readonly bool _ownsStream;

    private readonly Dictionary<string, ZipMemberInfo> _byName = new(StringComparer.Ordinal);

    private ZipLogReader(Stream stream, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Entries = ReadCentralDirectory(out var directoryOffset);
        CentralDirectoryOffset = directoryOffset;
        foreach (var entry in Entries)
        {
            _byName[entry.Name] = entry;
        }
    }

    /// <summary>The members in central-directory order (a repeated name appears once per record).</summary>
    public IReadOnlyList<ZipMemberInfo> Entries { get; }

    /// <summary>Where the central directory starts: the position an append-mode writer resumes at.</summary>
    public long CentralDirectoryOffset { get; }

    /// <summary>The distinct member names, in first-seen order.</summary>
    public IEnumerable<string> Names => Entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal);

    /// <summary>Opens a local <c>.eval</c> file for reading, tolerating a writer replacing it concurrently.</summary>
    public static ZipLogReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return new ZipLogReader(stream, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads from <paramref name="stream"/> (copied into memory when it cannot seek); the caller keeps ownership.</summary>
    public static ZipLogReader FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            return new ZipLogReader(stream, ownsStream: false);
        }

        var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        return new ZipLogReader(memory, ownsStream: true);
    }

    /// <summary>Whether <paramref name="firstBytes"/> starts with a ZIP local file header (<c>PK\x03\x04</c>), as <c>EvalRecorder.handles_bytes</c> checks.</summary>
    public static bool IsZip(ReadOnlySpan<byte> firstBytes) =>
        firstBytes.Length >= 4 && firstBytes[0] == 0x50 && firstBytes[1] == 0x4B && firstBytes[2] == 0x03 && firstBytes[3] == 0x04;

    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The last record for <paramref name="name"/>, or null.</summary>
    public ZipMemberInfo? Find(string name) => _byName.GetValueOrDefault(name);

    public byte[] Read(string name) =>
        Read(Find(name) ?? throw new FileNotFoundException($"There is no member '{name}' in the log zip.", name));

    public string ReadText(string name) => Encoding.UTF8.GetString(Read(name));

    public string ReadText(ZipMemberInfo entry) => Encoding.UTF8.GetString(Read(entry));

    public byte[] Read(ZipMemberInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _stream.Position = entry.LocalHeaderOffset;
        Span<byte> header = stackalloc byte[30];
        _stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
        {
            throw new InvalidDataException($"Bad local file header for member '{entry.Name}'.");
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        _stream.Position += nameLength + extraLength;
        if (entry.CompressedSize > int.MaxValue || entry.UncompressedSize > int.MaxValue)
        {
            throw new NotSupportedException($"Member '{entry.Name}' is larger than 2 GB.");
        }

        var compressed = new byte[entry.CompressedSize];
        _stream.ReadExactly(compressed);
        var data = entry.Method switch
        {
            MethodStored => compressed,
            MethodDeflate => Inflate(compressed, (int)entry.UncompressedSize),
            MethodZstandard => ZstdDecoder.Decompress(compressed, entry.UncompressedSize),
            _ => throw new NotSupportedException($"Member '{entry.Name}' uses unsupported compression method {entry.Method}."),
        };
        if (data.Length != entry.UncompressedSize)
        {
            throw new InvalidDataException($"Member '{entry.Name}' decompressed to {data.Length} bytes, expected {entry.UncompressedSize}.");
        }

        if (Crc32.Compute(data) != entry.Crc)
        {
            throw new InvalidDataException($"Bad CRC-32 for member '{entry.Name}'.");
        }

        return data;
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private static byte[] Inflate(byte[] compressed, int expectedSize)
    {
        using var source = new MemoryStream(compressed);
        using var inflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedSize);
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private List<ZipMemberInfo> ReadCentralDirectory(out long directoryOffset)
    {
        var length = _stream.Length;
        if (length < 22)
        {
            throw new InvalidDataException("The file is too small to be a zip archive.");
        }

        var tailLength = (int)Math.Min(length, 22 + 65535);
        var tail = new byte[tailLength];
        _stream.Position = length - tailLength;
        _stream.ReadExactly(tail);
        var eocd = -1;
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectorySignature)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException("The zip archive has no end-of-central-directory record.");
        }

        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        var eocdPosition = length - tailLength + eocd;
        if (entryCount == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
        {
            if (eocdPosition >= 20)
            {
                Span<byte> locator = stackalloc byte[20];
                _stream.Position = eocdPosition - 20;
                _stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Zip64LocatorSignature)
                {
                    var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(locator[8..]);
                    Span<byte> record = stackalloc byte[56];
                    _stream.Position = zip64Offset;
                    _stream.ReadExactly(record);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
                    {
                        throw new InvalidDataException("Bad zip64 end-of-central-directory record.");
                    }

                    entryCount = BinaryPrimitives.ReadInt64LittleEndian(record[32..]);
                    directorySize = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
                    directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(record[48..]);
                }
            }
        }

        if (directoryOffset + directorySize > length)
        {
            throw new InvalidDataException("The zip central directory lies beyond the end of the file.");
        }

        var directory = new byte[directorySize];
        _stream.Position = directoryOffset;
        _stream.ReadExactly(directory);
        var entries = new List<ZipMemberInfo>((int)Math.Min(entryCount, 1 << 20));
        var position = 0;
        for (long i = 0; i < entryCount; i++)
        {
            if (position + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position)) != CentralDirectorySignature)
            {
                throw new InvalidDataException("Bad central directory record.");
            }

            var span = directory.AsSpan(position);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            var dosTime = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
            var dosDate = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            long uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[42..]);
            var name = Encoding.UTF8.GetString(span.Slice(46, nameLength));
            var extra = span.Slice(46 + nameLength, extraLength);
            var extraPosition = 0;
            while (extraPosition + 4 <= extra.Length)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[extraPosition..]);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(extraPosition + 2)..]);
                var field = extra.Slice(extraPosition + 4, Math.Min(size, extra.Length - extraPosition - 4));
                if (id == 0x0001)
                {
                    var fieldPosition = 0;
                    if (uncompressedSize == 0xFFFFFFFF && fieldPosition + 8 <= field.Length)
                    {
                        uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(field[fieldPosition..]);
                        fieldPosition += 8;
                    }

                    if (compressedSize == 0xFFFFFFFF && fieldPosition + 8 <= field.Length)
                    {
                        compressedSize = BinaryPrimitives.ReadInt64LittleEndian(field[fieldPosition..]);
                        fieldPosition += 8;
                    }

                    if (localOffset == 0xFFFFFFFF && fieldPosition + 8 <= field.Length)
                    {
                        localOffset = BinaryPrimitives.ReadInt64LittleEndian(field[fieldPosition..]);
                    }
                }

                extraPosition += 4 + size;
            }

            entries.Add(new ZipMemberInfo(name, method, crc, compressedSize, uncompressedSize, localOffset, dosTime, dosDate));
            position += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }
}
