using System.IO;
using AgentHub.Hosting;

namespace AgentHub.Tests;

public class SessionFrameTests
{
    private static async Task<SessionFrame?> RoundTrip(SessionFrame frame)
    {
        var stream = new MemoryStream();
        await frame.WriteAsync(stream);
        stream.Position = 0;
        return await SessionFrame.ReadAsync(stream);
    }

    [Fact]
    public async Task Hello_round_trips()
    {
        var read = await RoundTrip(SessionFrame.Hello(4242));
        Assert.NotNull(read);
        Assert.Equal(FrameType.Hello, read!.Type);
        var (version, pid) = SessionFrame.ReadHello(read);
        Assert.Equal(SessionFrame.ProtocolVersion, version);
        Assert.Equal(4242, pid);
    }

    [Fact]
    public async Task Welcome_round_trips()
    {
        var read = await RoundTrip(SessionFrame.Welcome(processId: 991, running: true, exitCode: 0, cols: 132, rows: 43));
        var welcome = SessionFrame.ReadWelcome(read!);
        Assert.Equal(SessionFrame.ProtocolVersion, welcome.Version);
        Assert.Equal(991, welcome.ProcessId);
        Assert.True(welcome.Running);
        Assert.Equal(132, welcome.Cols);
        Assert.Equal(43, welcome.Rows);

        var ended = SessionFrame.ReadWelcome((await RoundTrip(SessionFrame.Welcome(1, false, -1073741510, 80, 24)))!);
        Assert.False(ended.Running);
        Assert.Equal(-1073741510, ended.ExitCode);
    }

    [Fact]
    public async Task Resize_input_exit_and_text_round_trip()
    {
        var (cols, rows) = SessionFrame.ReadResize((await RoundTrip(SessionFrame.Resize(200, 50)))!);
        Assert.Equal((200, 50), (cols, rows));

        var input = await RoundTrip(SessionFrame.Input("ls ✓\r"));
        Assert.Equal(FrameType.Input, input!.Type);
        Assert.Equal("ls ✓\r", SessionFrame.ReadText(input));

        Assert.Equal(7, SessionFrame.ReadExited((await RoundTrip(SessionFrame.Exited(7)))!));
        Assert.Equal("too new", SessionFrame.ReadText((await RoundTrip(SessionFrame.Refused("too new")))!));
    }

    [Fact]
    public async Task Empty_frames_and_binary_payloads_survive()
    {
        var detach = await RoundTrip(SessionFrame.Detach());
        Assert.Equal(FrameType.Detach, detach!.Type);
        Assert.Empty(detach.Payload);

        var bytes = new byte[70_000];
        new Random(3).NextBytes(bytes);
        var output = await RoundTrip(SessionFrame.Output(bytes));
        Assert.Equal(bytes, output!.Payload);
    }

    [Fact]
    public async Task Several_frames_read_back_in_order()
    {
        var stream = new MemoryStream();
        await SessionFrame.Hello(1).WriteAsync(stream);
        await SessionFrame.Input("a").WriteAsync(stream);
        await SessionFrame.Kill().WriteAsync(stream);
        stream.Position = 0;

        Assert.Equal(FrameType.Hello, (await SessionFrame.ReadAsync(stream))!.Type);
        Assert.Equal(FrameType.Input, (await SessionFrame.ReadAsync(stream))!.Type);
        Assert.Equal(FrameType.Kill, (await SessionFrame.ReadAsync(stream))!.Type);
        Assert.Null(await SessionFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task A_frame_cut_short_reads_as_end_of_stream()
    {
        var whole = SessionFrame.Output(new byte[100]).ToBytes();
        var cut = new MemoryStream(whole[..50]);
        Assert.Null(await SessionFrame.ReadAsync(cut));

        var headerOnly = new MemoryStream(whole[..3]);
        Assert.Null(await SessionFrame.ReadAsync(headerOnly));
    }

    [Fact]
    public async Task An_absurd_length_is_corruption_not_a_frame()
    {
        var bytes = new byte[] { (byte)FrameType.Output, 0xFF, 0xFF, 0xFF, 0x7F };
        await Assert.ThrowsAsync<InvalidDataException>(() => SessionFrame.ReadAsync(new MemoryStream(bytes)));
    }
}
