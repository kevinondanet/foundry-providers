namespace InspectAzureAI.Eval.Sandbox.Local;

/// <summary>
/// Port of <c>util/_subprocess.py</c> <c>CircularByteBuffer</c>: keeps the last <c>capacity</c> bytes of a
/// stream so unbounded command output cannot exhaust memory. Guarded by a lock because a pipe pump that
/// was abandoned after the drain grace period may still be writing when the runner snapshots the tail.
/// </summary>
internal sealed class TailByteBuffer(long capacity)
{
    private readonly object _sync = new();

    private readonly List<byte[]> _chunks = [];

    private long _buffered;

    private long _total;

    public long TotalBytes
    {
        get
        {
            lock (_sync)
            {
                return _total;
            }
        }
    }

    public bool Truncated => TotalBytes > capacity;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            _total += bytes.Length;
            if (capacity <= 0)
            {
                return;
            }

            if (bytes.Length >= capacity)
            {
                _chunks.Clear();
                _buffered = 0;
                bytes = bytes[^(int)capacity..];
            }

            _chunks.Add(bytes.ToArray());
            _buffered += bytes.Length;
            // Drop whole leading chunks while the remainder still covers the tail we have to keep.
            while (_chunks.Count > 1 && _buffered - _chunks[0].Length >= capacity)
            {
                _buffered -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    public byte[] ToArray() => Snapshot().Bytes;

    /// <summary>The retained tail and the total byte count, read together so they describe the same moment.</summary>
    public (byte[] Bytes, long Total) Snapshot()
    {
        lock (_sync)
        {
            var all = new byte[_buffered];
            var offset = 0;
            foreach (var chunk in _chunks)
            {
                chunk.CopyTo(all, offset);
                offset += chunk.Length;
            }

            if (capacity > 0 && all.Length > capacity)
            {
                all = all[^(int)capacity..];
            }

            return (all, _total);
        }
    }
}
