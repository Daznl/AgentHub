namespace AgentHub.Hosting;

/// <summary>
/// The last <see cref="Capacity"/> bytes a session wrote, so a client that attaches later can redraw what the terminal
/// showed. A fixed ring: once full, the oldest bytes go. Thread-safe.
/// </summary>
public sealed class ReplayBuffer
{
    public const int DefaultCapacity = 2 * 1024 * 1024;

    private readonly byte[] _ring;
    private int _start;   // index of the oldest byte
    private int _count;
    private readonly object _lock = new();

    public ReplayBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new byte[capacity];
    }

    public int Capacity => _ring.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Total bytes ever appended, including those the ring has since dropped.</summary>
    public long TotalAppended { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            TotalAppended += bytes.Length;
            if (bytes.Length >= _ring.Length)
            {
                // Only the tail fits; everything before it is gone anyway.
                bytes[^_ring.Length..].CopyTo(_ring);
                _start = 0;
                _count = _ring.Length;
                return;
            }

            var end = (_start + _count) % _ring.Length;
            var firstPart = Math.Min(bytes.Length, _ring.Length - end);
            bytes[..firstPart].CopyTo(_ring.AsSpan(end));
            bytes[firstPart..].CopyTo(_ring.AsSpan(0));

            var overflow = _count + bytes.Length - _ring.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _ring.Length;
                _count = _ring.Length;
            }
            else
            {
                _count += bytes.Length;
            }
        }
    }

    /// <summary>
    /// The buffered bytes, oldest first. When the ring has wrapped, the copy starts at the first byte that begins a UTF-8
    /// character, so a client's decoder never sees the tail of a character the ring cut in half.
    /// </summary>
    public byte[] Snapshot()
    {
        lock (_lock)
        {
            var bytes = new byte[_count];
            var firstPart = Math.Min(_count, _ring.Length - _start);
            Array.Copy(_ring, _start, bytes, 0, firstPart);
            Array.Copy(_ring, 0, bytes, firstPart, _count - firstPart);
            if (_count < _ring.Length) return bytes;

            var skip = 0;
            while (skip < bytes.Length && skip < 4 && (bytes[skip] & 0xC0) == 0x80) skip++;
            return skip == 0 ? bytes : bytes[skip..];
        }
    }
}
