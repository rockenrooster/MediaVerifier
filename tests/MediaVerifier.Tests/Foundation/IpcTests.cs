using System.Diagnostics;
using System.Text;
using MediaVerifier.Core.Ipc;

namespace MediaVerifier.Tests.Foundation;

public sealed class IpcTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static string UniquePipe() => "MediaVerifier.Tests.Ipc." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task MutexIsExclusiveAndReleasableFromAnyThread()
    {
        string name = @"Local\MediaVerifier.Tests." + Guid.NewGuid().ToString("N");
        NamedMutexLock? first = NamedMutexLock.TryAcquire(name, TimeSpan.Zero);
        Assert.NotNull(first);
        Assert.Null(NamedMutexLock.TryAcquire(name, TimeSpan.Zero));
        Assert.True(NamedMutexLock.IsHeldElsewhere(name));

        // Release from a pool thread: the lock object owns its own thread.
        await Task.Run(first!.Dispose);

        using NamedMutexLock? second = NamedMutexLock.TryAcquire(name, TimeSpan.Zero);
        Assert.NotNull(second);
        Assert.False(second!.WasAbandoned);
    }

    [Fact]
    public void AbandonedMutexCountsAsAcquired()
    {
        string name = @"Local\MediaVerifier.Tests." + Guid.NewGuid().ToString("N");
        Thread owner = new(() =>
        {
            Mutex mutex = new(initiallyOwned: false, name);
            mutex.WaitOne();
            // The thread ends without releasing: the mutex is abandoned, as after a crash.
        });
        owner.Start();
        owner.Join();

        using NamedMutexLock? taken = NamedMutexLock.TryAcquire(name, TimeSpan.FromSeconds(2));

        Assert.NotNull(taken);
        Assert.True(taken!.WasAbandoned);
    }

    [Theory]
    [InlineData("""{"cmd":"activate"}""", true)]
    [InlineData("""{"cmd":"run-plan","planId":3}""", true)]
    [InlineData("""{"cmd":"open","uri":"mediaverifier://finding/123"}""", true)]
    [InlineData("""{"cmd":"run-plan","planId":0}""", false)]
    [InlineData("""{"cmd":"run-plan"}""", false)]
    [InlineData("""{"cmd":"open","uri":"https://example.com"}""", false)]
    [InlineData("""{"cmd":"activate","planId":4}""", false)]
    [InlineData("""{"cmd":"shutdown"}""", false)]
    [InlineData("""{"cmd":"activate","extra":1}""", false)]
    [InlineData("""not json""", false)]
    [InlineData("", false)]
    public void ParsesOnlyValidCommands(string json, bool valid)
    {
        IpcMessage? message = IpcMessage.TryParse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(valid, message is not null);
    }

    [Fact]
    public void MessagesSerialiseAsSingleLines()
    {
        Assert.Equal("{\"cmd\":\"activate\"}\n", IpcMessage.ForActivate().ToJsonLine());
        Assert.Equal("{\"cmd\":\"run-plan\",\"planId\":3}\n", IpcMessage.ForRunPlan(3).ToJsonLine());
        Assert.Equal("{\"cmd\":\"open\",\"uri\":\"mediaverifier://finding/123\"}\n", IpcMessage.ForOpen("mediaverifier://finding/123").ToJsonLine());
    }

    [Fact]
    public async Task ServerReceivesActivate()
    {
        string pipe = UniquePipe();
        using IpcServer server = new(pipe);
        TaskCompletionSource<IpcMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) => received.TrySetResult(message);
        server.Start();

        Assert.True(IpcClient.TrySend(pipe, IpcMessage.ForActivate(), Wait));

        IpcMessage got = await received.Task.WaitAsync(Wait);
        Assert.Equal(IpcMessage.Activate, got.Command);
    }

    [Fact]
    public async Task OversizedAndMalformedMessagesAreRejectedWithoutStoppingTheServer()
    {
        string pipe = UniquePipe();
        using IpcServer server = new(pipe);
        List<IpcMessage> messages = [];
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) =>
        {
            lock (messages)
            {
                messages.Add(message);
            }

            if (message.Command == IpcMessage.RunPlan)
            {
                done.TrySetResult();
            }
        };
        server.Start();

        byte[] oversized = Encoding.UTF8.GetBytes("{\"cmd\":\"open\",\"uri\":\"mediaverifier://" + new string('a', 9000) + "\"}\n");
        _ = IpcClient.TrySendRaw(pipe, oversized, Wait);
        WaitForRejections(server, 1);
        Assert.True(IpcClient.TrySendRaw(pipe, "{not json}\n"u8, Wait));
        WaitForRejections(server, 2);
        Assert.True(IpcClient.TrySendRaw(pipe, "{\"cmd\":\"format-disk\"}\n"u8, Wait));
        WaitForRejections(server, 3);
        Assert.True(IpcClient.TrySend(pipe, IpcMessage.ForRunPlan(7), Wait));

        await done.Task.WaitAsync(Wait);
        lock (messages)
        {
            IpcMessage only = Assert.Single(messages);
            Assert.Equal(7, only.PlanId);
        }

        Assert.Equal(3, server.RejectedCount);
    }

    [Fact]
    public void ClientReportsFailureWhenNobodyListens()
    {
        Assert.False(IpcClient.TrySend(UniquePipe(), IpcMessage.ForActivate(), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    [Trait("Category", "System")]
    public async Task SecondProcessSendsActivateAndTheFirstReceivesIt()
    {
        string pipe = UniquePipe();
        using IpcServer server = new(pipe);
        TaskCompletionSource<IpcMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, message) => received.TrySetResult(message);
        server.Start();

        string script =
            "$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + pipe + "', [System.IO.Pipes.PipeDirection]::Out);" +
            "$c.Connect(10000);" +
            "$b = [System.Text.Encoding]::UTF8.GetBytes('{\"cmd\":\"activate\"}' + [char]10);" +
            "$c.Write($b, 0, $b.Length); $c.Flush(); $c.WaitForPipeDrain(); $c.Dispose()";
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using Process child = Process.Start(start)!;
        await child.WaitForExitAsync(new CancellationTokenSource(30_000).Token);
        Assert.Equal(0, child.ExitCode);

        IpcMessage got = await received.Task.WaitAsync(Wait);
        Assert.Equal(IpcMessage.Activate, got.Command);
    }

    private static void WaitForRejections(IpcServer server, int count)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (server.RejectedCount < count && watch.Elapsed < Wait)
        {
            Thread.Sleep(10);
        }

        Assert.Equal(count, server.RejectedCount);
    }
}
