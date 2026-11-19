using LanDisk.Core.Configuration;

namespace LanDisk.Tests;

/// <summary>
/// CORE13 — <see cref="JsonStore.Write"/> 必须扛得住 Windows 上的瞬时文件占用，并且不共享临时文件名。
///
/// <para><b>这是从一次真实偶发失败追出来的产品缺陷。</b> 全量单元回归第一次出现
/// <c>Remove_never_deletes_an_already_terminal_record</c> 失败，异常是
/// <c>UnauthorizedAccessException: Access to the path is denied</c>。有界重复验证复现了它
/// （单项连续 20 次中失败 1 次，约 5%），根因是 <c>Write</c> 用固定名 <c>path + ".tmp"</c> 且
/// 对 <c>File.Move(tmp, path, true)</c> **不重试** —— 目标文件被索引器/杀毒/其它进程瞬时持有时，
/// Windows 会直接拒绝替换。影响的不只是测试：任何一次状态写入（设置、传输记录、设备表）都可能这么失败。</para>
/// </summary>
public sealed class JsonStoreAtomicWriteTests : IDisposable
{
    private readonly string _dir;

    public JsonStoreAtomicWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "landisk-jsonstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record Payload(string Name, int Value);

    /// <summary>并发写同一路径不得抛异常，且最终文件必须是一份完整可解析的 JSON。</summary>
    [Fact]
    public void Concurrent_writes_to_the_same_path_do_not_collide()
    {
        string path = Path.Combine(_dir, "state.json");
        const int writers = 16;

        Exception? failure = null;
        Parallel.For(0, writers, i =>
        {
            try { JsonStore.Write(path, new Payload("w" + i, i)); }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        });

        Assert.Null(failure);

        Payload? read = JsonStore.Read<Payload>(path, () => null!);
        Assert.NotNull(read);
        Assert.StartsWith("w", read!.Name, StringComparison.Ordinal);
    }

    /// <summary>写入结束后不得残留任何临时文件（固定 .tmp 名正是先前的问题之一）。</summary>
    [Fact]
    public void A_successful_write_leaves_no_temporary_file_behind()
    {
        string path = Path.Combine(_dir, "leftover.json");

        for (int i = 0; i < 20; i++) JsonStore.Write(path, new Payload("n" + i, i));

        string[] leftovers = Directory.GetFiles(_dir, "*.tmp*");
        Assert.Empty(leftovers);
        Assert.True(File.Exists(path));
    }

    /// <summary>目标文件被独占打开时，写入应当通过重试最终成功，而不是立刻抛给调用方。</summary>
    [Fact]
    public void A_briefly_locked_destination_is_retried_rather_than_failing()
    {
        string path = Path.Combine(_dir, "locked.json");
        JsonStore.Write(path, new Payload("first", 1));

        // 短暂独占目标文件，模拟索引器/杀毒的一瞬间占用。
        var holder = new Thread(() =>
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
                Thread.Sleep(60);
            }
            catch { /* the retry loop may win the race; that is fine */ }
        });
        holder.Start();
        Thread.Sleep(10);

        Exception? failure = null;
        try { JsonStore.Write(path, new Payload("second", 2)); }
        catch (Exception ex) { failure = ex; }
        holder.Join();

        Assert.Null(failure);
        Payload? read = JsonStore.Read<Payload>(path, () => null!);
        Assert.NotNull(read);
    }

    /// <summary>
    /// 永久失败必须**有界**地抛出，不得无限重试、更不得假装成功；并且不留下任何 .tmp-* 垃圾。
    /// 这里把目标做成一目录，替换永远不可能成功 —— 一个确定性的"永久失败"。
    /// </summary>
    [Fact]
    public void A_permanently_impossible_target_fails_after_bounded_attempts_without_leaving_temp_files()
    {
        string path = Path.Combine(_dir, "target-is-a-directory");
        Directory.CreateDirectory(path);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => JsonStore.Write(path, new Payload("never", 1)));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 5000,
            "重试必须有界，实际耗时 " + stopwatch.ElapsedMilliseconds + "ms");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    /// <summary>文件内容永远是完整合法的 JSON，不会出现写了一半的内容。</summary>
    [Fact]
    public void The_file_always_holds_one_complete_document()
    {
        string path = Path.Combine(_dir, "complete.json");

        for (int i = 0; i < 30; i++)
        {
            JsonStore.Write(path, new Payload("value-" + i, i));

            // 每次写入之后都立刻完整读回：半截内容会在这里直接解析失败。
            Payload? read = JsonStore.Read<Payload>(path, () => null!);
            Assert.NotNull(read);
            Assert.Equal("value-" + i, read!.Name);
            Assert.Equal(i, read.Value);
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }
}
