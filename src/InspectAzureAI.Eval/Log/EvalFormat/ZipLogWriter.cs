using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// The append-mode zip writing behind <see cref="ZipLogFile"/>: Python opens its temp file with
/// <c>ZipFile(mode="a")</c>, appends members after the last local entry, and on <c>close()</c> writes the central
/// directory, which the next re-open overwrites with further members. <see cref="AddMember"/> and
/// <see cref="WriteCentralDirectory"/> reproduce that on one seekable stream so the archive can be snapshotted
/// after every flush and extended afterwards; <see cref="OpenAppend"/> resumes an existing archive the same way
/// (the in-place <c>header.json</c> replacement of <c>write_eval_log(header_only=True)</c>). A repeated member name
/// is appended as a second record, as Python's <c>writestr</c> does. New members are deflate-compressed
/// (method 8), which Python's <c>zipfile</c> reads natively; Python itself writes zstandard, which the BCL
/// cannot produce, and members carried over by <see cref="OpenAppend"/> keep whatever method they had.
/// </summary>
internal sealed class ZipLogWriter : IDisposable
{
    private const uint LocalHeaderSignature = 0x04034B50;

    private const uint CentralDirectorySignature = 0x02014B50;

    private const uint EndOfCentralDirectorySignature = 0x06054B50;

    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;

    private const uint Zip64LocatorSignature = 0x07064B50;

    private const ushort VersionDeflate = 20;

    private const ushort VersionZip64 = 45;

    private const ushort VersionZstandard = 63;

    private const ushort MethodDeflate = 8;

    private const ushort MethodZstandard = 93;

    /// <summary>Python's <c>ZipInfo</c> for <c>writestr</c>: created on Unix (3) with mode 0o600.</summary>
    private const ushort VersionMadeBy = (3 << 8) | VersionDeflate;

    private const uint ExternalAttributes = 0x180u << 16; // 0o600

    private readonly Stream _stream;

    private readonly List<Entry> _entries = [];

    private long _dataEnd;

    public ZipLogWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new ArgumentException("The zip stream must be seekable and writable.", nameof(stream));
        }

        _stream = stream;
        _dataEnd = 0;
    }

    /// <summary>The underlying stream (positioned wherever the last operation left it).</summary>
    public Stream Stream => _stream;

    /// <summary>The names written so far, in order, repeats included (Python's <c>namelist()</c>).</summary>
    public IReadOnlyList<string> Names => _entries.Select(entry => entry.Name).ToList();

    /// <summary>
    /// Port of <c>ZipFile(path, "a")</c> on an existing archive: its central directory becomes the entry list and
    /// the next member is written over it, exactly where Python resumes.
    /// </summary>
    public static ZipLogWriter OpenAppend(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = ZipLogReader.FromStream(stream);
        var writer = new ZipLogWriter(stream);
        foreach (var entry in reader.Entries)
        {
            writer._entries.Add(new Entry(entry.Name, Encoding.UTF8.GetBytes(entry.Name), entry.Method, entry.Crc, entry.CompressedSize, entry.UncompressedSize, entry.LocalHeaderOffset, entry.DosTime, entry.DosDate));
        }

        writer._dataEnd = reader.CentralDirectoryOffset;
        return writer;
    }

    public bool Contains(string name) => _entries.Any(entry => entry.Name == name);

    /// <summary>
    /// Port of <c>zf.filelist = [i for i in zf.filelist if i.filename != name]</c>: drops every record named
    /// <paramref name="name"/> from the directory. The member bytes stay in the file, unreferenced, as in Python.
    /// </summary>
    public int RemoveMember(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _entries.RemoveAll(entry => entry.Name == name);
    }

    /// <summary>Port of <c>ZipFile.writestr</c>: appends <paramref name="data"/> as a deflated member named <paramref name="name"/>.</summary>
    public void AddMember(string name, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var compressed = Deflate(data);
        var crc = Crc32.Compute(data);
        var (dosTime, dosDate) = DosDateTime(DateTime.Now);
        var offset = _dataEnd;
        // member sizes are bounded by int; only the offset can exceed the 32-bit fields
        var zip64 = offset >= 0xFFFFFFFFL;

        _stream.Position = offset;
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], zip64 ? VersionZip64 : VersionDeflate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x0800); // UTF-8 names
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], MethodDeflate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], zip64 ? 0xFFFFFFFF : (uint)compressed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], zip64 ? 0xFFFFFFFF : (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)(zip64 ? 20 : 0));
        _stream.Write(header);
        _stream.Write(nameBytes);
        if (zip64)
        {
            Span<byte> extra = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], data.Length);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], compressed.Length);
            _stream.Write(extra);
        }

        _stream.Write(compressed);
        _dataEnd = _stream.Position;
        _entries.Add(new Entry(name, nameBytes, MethodDeflate, crc, compressed.Length, data.Length, offset, dosTime, dosDate));
    }

    /// <summary>
    /// Port of <c>ZipFile.close()</c> followed by the re-open in append mode: writes the central directory after the
    /// last member so the stream holds a complete archive, while the next <see cref="AddMember"/> overwrites it.
    /// </summary>
    public void WriteCentralDirectory()
    {
        _stream.Position = _dataEnd;
        var directoryStart = _dataEnd;
        Span<byte> record = stackalloc byte[46];
        Span<byte> extra = stackalloc byte[28];
        foreach (var entry in _entries)
        {
            var zip64 = entry.UncompressedSize >= 0xFFFFFFFFL || entry.CompressedSize >= 0xFFFFFFFFL || entry.Offset >= 0xFFFFFFFFL;
            var version = zip64 ? VersionZip64 : entry.Method == MethodZstandard ? VersionZstandard : VersionDeflate;
            BinaryPrimitives.WriteUInt32LittleEndian(record, CentralDirectorySignature);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], version);
            BinaryPrimitives.WriteUInt16LittleEndian(record[8..], 0x0800);
            BinaryPrimitives.WriteUInt16LittleEndian(record[10..], entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], entry.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(record[14..], entry.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], zip64 ? 0xFFFFFFFF : (uint)entry.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[24..], zip64 ? 0xFFFFFFFF : (uint)entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(record[28..], (ushort)entry.NameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[30..], (ushort)(zip64 ? 28 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(record[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record[38..], ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(record[42..], zip64 ? 0xFFFFFFFF : (uint)entry.Offset);
            _stream.Write(record);
            _stream.Write(entry.NameBytes);
            if (zip64)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 24);
                BinaryPrimitives.WriteInt64LittleEndian(extra[4..], entry.UncompressedSize);
                BinaryPrimitives.WriteInt64LittleEndian(extra[12..], entry.CompressedSize);
                BinaryPrimitives.WriteInt64LittleEndian(extra[20..], entry.Offset);
                _stream.Write(extra);
            }
        }

        var directoryEnd = _stream.Position;
        var directorySize = directoryEnd - directoryStart;
        var needZip64 = _entries.Count >= 0xFFFF || directorySize >= 0xFFFFFFFFL || directoryStart >= 0xFFFFFFFFL;
        if (needZip64)
        {
            Span<byte> zip64Record = stackalloc byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(zip64Record, Zip64EndOfCentralDirectorySignature);
            BinaryPrimitives.WriteInt64LittleEndian(zip64Record[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64Record[12..], VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(zip64Record[14..], VersionZip64);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64Record[16..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(zip64Record[20..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(zip64Record[24..], _entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(zip64Record[32..], _entries.Count);
            BinaryPrimitives.WriteInt64LittleEndian(zip64Record[40..], directorySize);
            BinaryPrimitives.WriteInt64LittleEndian(zip64Record[48..], directoryStart);
            _stream.Write(zip64Record);
            Span<byte> locator = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(locator[8..], directoryEnd);
            BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
            _stream.Write(locator);
        }

        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, EndOfCentralDirectorySignature);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], (ushort)Math.Min(_entries.Count, 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], (ushort)Math.Min(_entries.Count, 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)Math.Min(directorySize, 0xFFFFFFFFL));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], (uint)Math.Min(directoryStart, 0xFFFFFFFFL));
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[20..], 0);
        _stream.Write(eocd);
        _stream.SetLength(_stream.Position);
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(Math.Max(data.Length / 3, 64));
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static (ushort Time, ushort Date) DosDateTime(DateTime value)
    {
        if (value.Year < 1980)
        {
            value = new DateTime(1980, 1, 1);
        }

        var time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        var date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
        return (time, date);
    }

    private sealed record Entry(string Name, byte[] NameBytes, ushort Method, uint Crc, long CompressedSize, long UncompressedSize, long Offset, ushort DosTime, ushort DosDate);
}
