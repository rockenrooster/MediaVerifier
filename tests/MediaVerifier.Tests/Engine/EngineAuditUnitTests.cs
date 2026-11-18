using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Engine;

/// <summary>Unit-level checks for the November 2026 audit fixes: gates, the work queue, estimates and scratch folders.</summary>
public sealed class EngineAuditUnitTests
{
    [Fact]
    public async Task The_cpu_gate_takes_a_new_capacity_while_leases_are_held()
    {
        CpuGate gate = new(capacity: 4);
        CpuGate.Lease held = await gate.AcquireAsync(4, CancellationToken.None);

        // Speed lowered mid-scan: a waiting heavy job is clamped to the new capacity and runs once the lease is back.
        Task<CpuGate.Lease> waiting = gate.AcquireAsync(3, CancellationToken.None);
        gate.SetCapacity(2);
        Assert.False(waiting.IsCompleted);
        held.Dispose();
        CpuGate.Lease granted = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, granted.Weight);
        Assert.Equal(2, gate.InUse);
        using (CancellationTokenSource probe = new())
        {
            Task<CpuGate.Lease> more = gate.AcquireAsync(1, probe.Token);
            Assert.False(more.IsCompleted, "a lower capacity must hold");
            probe.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => more);
        }

        granted.Dispose();

        gate.SetCapacity(8);
        using CpuGate.Lease big = await gate.AcquireAsync(8, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, big.Weight);
    }

    [Fact]
    public async Task The_io_gates_apply_a_changed_readers_setting()
    {
        int readers = 1;
        using IoGates gates = new(_ => readers);
        VolumeInfo hdd = new("hdd", StorageClass.Hdd);
        using CpuGate.Lease first = await gates.For(hdd).AcquireAsync(1, CancellationToken.None);
        Assert.False(gates.For(hdd).AcquireAsync(1, CancellationToken.None).IsCompleted);

        readers = 3;
        gates.Refresh();

        Assert.Equal(3, gates.LimitFor(hdd));
        using CpuGate.Lease third = await gates.For(hdd).AcquireAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_requeued_item_never_waits_and_the_capacity_holds()
    {
        WorkQueue queue = new(capacity: 2);
        await queue.AddAsync(Item(1), CancellationToken.None);
        await queue.AddAsync(Item(2), CancellationToken.None);
        WorkItem taken = (await queue.TakeAsync(CancellationToken.None))!;
        await queue.AddAsync(Item(3), CancellationToken.None);

        // Full again: a paused worker puts its file back without waiting for space.
        await Task.Run(() => queue.Requeue(taken)).WaitAsync(TimeSpan.FromSeconds(5));

        List<long> drained = [];
        for (int i = 0; i < 3; i++)
        {
            drained.Add((await queue.TakeAsync(CancellationToken.None))!.FileId);
        }

        Assert.Equal([1L, 2L, 3L], drained.Order());

        // Empty: exactly the capacity fits again, not one more.
        await queue.AddAsync(Item(4), CancellationToken.None);
        await queue.AddAsync(Item(5), CancellationToken.None);
        Task sixth = queue.AddAsync(Item(6), CancellationToken.None);
        Assert.NotSame(sixth, await Task.WhenAny(sixth, Task.Delay(300)));
    }

    [Fact]
    public void The_estimate_changes_at_most_once_a_second_and_by_at_most_a_fifth()
    {
        Estimator estimator = new(new EstimateAverages { BytesPerSecond = 1000 });
        Dictionary<MediaKind, long> none = [];
        TimeSpan start = TimeSpan.FromMinutes(1);

        TimeSpan first = estimator.Remaining(100_000, none, ScanDepth.Quick, 1, start, 100)!.Value;
        TimeSpan sameSecond = estimator.Remaining(10_000, none, ScanDepth.Quick, 1, start + TimeSpan.FromMilliseconds(400), 100)!.Value;
        TimeSpan nextSecond = estimator.Remaining(10_000, none, ScanDepth.Quick, 1, start + TimeSpan.FromSeconds(1), 100)!.Value;

        Assert.Equal(TimeSpan.FromSeconds(100), first);
        Assert.Equal(first, sameSecond);
        Assert.Equal(TimeSpan.FromSeconds(80), nextSecond);
    }

    [Fact]
    public void A_folder_classifies_only_the_kinds_it_contains()
    {
        ScanSettings settings = new();
        MediaClassifier photos = MediaClassifier.For(settings, MediaKindMask.Photo);
        Assert.Equal(MediaKind.Photo, photos.Classify("a.jpg"));
        Assert.Equal(MediaKind.Unknown, photos.Classify("a.mp4"));
        Assert.Equal(MediaKind.Unknown, photos.Classify("memo.amr"));

        // No kind chosen (an old or edited database): everything is media, as before Contains existed.
        Assert.Equal(MediaKind.Audio, MediaClassifier.For(settings, MediaKindMask.None).Classify("memo.amr"));
    }

    [Fact]
    public void Cleaning_run_scratch_folders_keeps_the_current_run_and_other_folders()
    {
        using TempDirectory temp = new("run-temp");
        AppPaths paths = temp.CreateAppPaths();
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.JobTempDirectory(3, 1));
        Directory.CreateDirectory(paths.JobTempDirectory(4, 1));
        string other = Path.Combine(paths.TempDirectory, "selfcheck-12");
        Directory.CreateDirectory(other);

        Assert.Equal(1, paths.CleanRunTemp(keepRunId: 4));
        Assert.False(Directory.Exists(paths.RunTempDirectory(3)));
        Assert.True(Directory.Exists(paths.RunTempDirectory(4)));
        Assert.True(Directory.Exists(other));
    }

    private static WorkItem Item(long id) => new()
    {
        FileId = id,
        Root = new RootRecord { Id = 1, Path = @"D:\L" },
        Volume = new VolumeInfo("v", StorageClass.Ssd),
        RelDir = "",
        Name = id + ".jpg",
        Kind = MediaKind.Photo,
        Stat = new FileStat(1, 1, FileAttributes.Normal, null),
    };
}
