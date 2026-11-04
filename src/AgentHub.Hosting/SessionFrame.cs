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
    /// <summary>Bumped when a frame's shape changes; a host and a client that disagree part politely.</summary>
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

    /// <summary>Client: protocol version and the client's process id (so the host log says who attached).</summary>
    public static SessionFrame Hello(int clientPid)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), clientPid);
        return new SessionFrame(FrameType.Hello, payload);
    }

    public static (ushort Version, int ClientPid) ReadHello(SessionFrame frame) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload), BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(2)));

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

    /// <summary>Host: protocol version, the CLI's process id, whether it still runs, its exit code if not, and the console size.</summary>
    public static SessionFrame Welcome(int processId, bool running, int exitCode, int cols, int rows)
    {
        var payload = new byte[15];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), processId);
        payload[6] = running ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7), exitCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(11), (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(13), (ushort)rows);
        return new SessionFrame(FrameType.Welcome, payload);
    }

    public static WelcomeInfo ReadWelcome(SessionFrame frame) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload),
        BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(2)),
        frame.Payload[6] == 1,
        BinaryPrimitives.ReadInt32LittleEndian(frame.Payload.AsSpan(7)),
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(11)),
        BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(13)));

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

public sealed record WelcomeInfo(ushort Version, int ProcessId, bool Running, int ExitCode, int Cols, int Rows);
