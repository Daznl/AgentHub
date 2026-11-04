using System.Buffers.Binary;
using System.Text;

namespace AgentHub.Hosting;

/// <summary>
/// What AgentHub and a session host say to each other over the session's named pipe. Every message is one frame:
/// one type byte, a little-endian 32-bit payload length, then the payload. The client speaks first with <see cref="Hello"/>;
/// the host answers <see cref="Welcome"/> (or <see cref="Refused"/>), then <see cref="Replay"/> with everything the
/// session has written so far, then <see cref="Output"/> as it comes and <see cref="Exited"/> once.
/// </summary>
public enum FrameType : byte
{
    // Client to host
    Hello = 1,
    Input = 2,
    Resize = 3,
    Kill = 4,
    Detach = 5,

    // Host to client
    Welcome = 16,
    Refused = 17,
    Replay = 18,
    Output = 19,
    Exited = 20
}

public sealed record SessionFrame(FrameType Type, byte[] Payload)
{
    /// <summary>
    /// Bumped when a frame changes in a way an older side cannot read; a host and a client that disagree part politely.
    /// Fields added at the end of <see cref="Hello"/> and <see cref="Welcome"/> are not that: readers treat a short payload
    /// as "not sent", so a host started by an older AgentHub still serves a newer one, and the other way round.
    /// </summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>Largest payload either side accepts; anything bigger is a corrupt stream, not a message.</summary>
    public const int MaxPayload = 16 * 1024 * 1024;

    private const int HeaderLength = 5;

    public static readonly byte[] NoPayload = [];

    public byte[] ToBytes()
    {
        var bytes = new byte[HeaderLength + Payload.Length];
        bytes[0] = (byte)Type;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), Payload.Length);
        Payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    public async Task WriteAsync(Stream stream, CancellationToken cancellation = default)
    {
        await stream.WriteAsync(ToBytes(), cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or null when the stream ends cleanly before a frame starts. A frame cut short is null too.</summary>
    public static async Task<SessionFrame?> ReadAsync(Stream stream, CancellationToken cancellation = default)
    {
        var header = new byte[HeaderLength];
        if (!await FillAsync(stream, header, cancellation).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayload) throw new InvalidDataException($"Session frame payload length {length} is out of range.");

        var payload = length == 0 ? NoPayload : new byte[length];
        if (length > 0 && !await FillAsync(stream, payload, cancellation).ConfigureAwait(false)) return null;
        return new SessionFrame((FrameType)header[0], payload);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ---- Typed builders and readers ----

    /// <summary>
    /// Client: protocol version, the client's process id (so the host log says who attached), and the stream offset up to
    /// which it already shows the session's output (-1 for none), so the host can replay only what came after.
    /// </summary>
    public static SessionFrame Hello(int clientPid, long fromOffset = -1)
    {
        var payload = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), clientPid);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(6), fromOffset);
        return new SessionFrame(FrameType.Hello, payload);
    }

    /// <summary>A hello from the first build of the client has no offset; that reads as -1.</summary>
    public static (ushort Version, int ClientPid, long FromOffset) ReadHello(SessionFrame frame) => (
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload),
        BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(2)),
        frame.Payload.Length >= 14 ? BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(6)) : -1);

    public static SessionFrame Input(string text) => new(FrameType.Input, Encoding.UTF8.GetBytes(text));

    public static SessionFrame Resize(int cols, int rows)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)Math.Clamp(cols, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)Math.Clamp(rows, 1, ushort.MaxValue));
        return new SessionFrame(FrameType.Resize, payload);
    }

    public static (int Cols, int Rows) ReadResize(SessionFrame frame) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload), BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(2)));

    public static SessionFrame Kill() => new(FrameType.Kill, NoPayload);
    public static SessionFrame Detach() => new(FrameType.Detach, NoPayload);

    /// <summary>
    /// Host: protocol version, the CLI's process id, whether it still runs, its exit code if not, the console size, then the
    /// stream offsets the replay that follows ends at and starts at (so the client knows whether it got the delta it asked
    /// for or the whole ring).
    /// </summary>
    public static SessionFrame Welcome(int processId, bool running, int exitCode, int cols, int rows, long totalAppended = -1, long replayStart = -1)
    {
        var payload = new byte[31];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), processId);
        payload[6] = running ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7), exitCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(11), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(13), (ushort)rows);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(15), totalAppended);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(23), replayStart);
        return new SessionFrame(FrameType.Welcome, payload);
    }

    /// <summary>A welcome from the first build of the host has no offsets; both read as -1 (unknown).</summary>
    public static WelcomeInfo ReadWelcome(SessionFrame frame) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload),
        BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(2)),
        frame.Payload[6] == 1,
        BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(7)),
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(11)),
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(13)),
        frame.Payload.Length >= 31 ? BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(15)) : -1,
        frame.Payload.Length >= 31 ? BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(23)) : -1);

    public static SessionFrame Refused(string reason) => new(FrameType.Refused, Encoding.UTF8.GetBytes(reason));
    public static string ReadText(SessionFrame frame) => Encoding.UTF8.GetString(frame.Payload);

    public static SessionFrame Replay(byte[] bytes) => new(FrameType.Replay, bytes);
    public static SessionFrame Output(byte[] bytes) => new(FrameType.Output, bytes);

    public static SessionFrame Exited(int exitCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        return new SessionFrame(FrameType.Exited, payload);
    }

    public static int ReadExited(SessionFrame frame) => BinaryPrimitives.ReadInt32LittleEndian(frame.Payload);
}

/// <summary>
/// What the host says on attach. <paramref name="TotalAppended"/> is the stream offset the replay ends at (everything the
/// session has written so far); <paramref name="ReplayStart"/> is where the replay begins. Both -1 from a host built before
/// they existed.
/// </summary>
public sealed record WelcomeInfo(ushort Version, int ProcessId, bool Running, int ExitCode, int Cols, int Rows, long TotalAppended, long ReplayStart);
