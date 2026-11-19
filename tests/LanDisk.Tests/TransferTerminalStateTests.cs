using LanDisk.Core.Model;
using LanDisk.Core.State;

namespace LanDisk.Tests;

/// <summary>
/// TRANSFER13 — a transfer record must always reach a terminal state, exactly once.
///
/// <para><b>The defect these pin.</b> <c>RecordDownload</c> created a record in <c>InProgress</c> and nothing
/// ever moved it: no progress update, no completion path, no abort handler. When the browser cancelled a
/// download the streaming copy observed <c>RequestAborted</c>, threw, and unwound — leaving the record at
/// "0 B / 0 B, 进行中" for ever, which is what the Owner saw after cancelling <c>1111.txt</c>. Two properties
/// make that impossible now: every path writes a terminal state, and a terminal state can never be undone.</para>
/// </summary>
public sealed class TransferTerminalStateTests : IDisposable
{
    private readonly string _path;

    public TransferTerminalStateTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "landisk-transfers-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private TransferLog NewLog() => new(_path);

    private static TransferRecord NewRecord(string id, long totalBytes = 1024) => new()
    {
        Id = id,
        Direction = TransferDirection.Download,
        ShareId = "share-1",
        ShareName = "文件与共享",
        RelativePath = "1111.txt",
        FileName = "1111.txt",
        TotalBytes = totalBytes,
        TransferredBytes = 0,
        State = TransferState.InProgress,
        DeviceId = "device-1",
        DeviceName = "iPhone"
    };

    private static TransferState StateOf(TransferLog log, string id)
        => log.Recent(50).First(t => t.Id == id).State;

    // ---------------------------------------------------------------- 正常完成

    [Fact]
    public void A_completed_download_reaches_committed_and_records_the_bytes()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("t1"));

        Assert.True(log.Terminalize("t1", TransferState.Committed, 1024, null));

        TransferRecord rec = log.Recent(50).First(t => t.Id == "t1");
        Assert.Equal(TransferState.Committed, rec.State);
        Assert.Equal(1024, rec.TransferredBytes);
        Assert.Null(rec.Error);
    }

    /// <summary>R19：0 字节文件不能永远停在 InProgress。</summary>
    [Fact]
    public void A_zero_byte_download_can_reach_a_terminal_state()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("zero", totalBytes: 0));

        Assert.True(log.Terminalize("zero", TransferState.Committed, 0, null));
        Assert.Equal(TransferState.Committed, StateOf(log, "zero"));
    }

    // ---------------------------------------------------------------- 取消与失败

    [Fact]
    public void A_cancelled_download_reaches_cancelled()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("c1"));

        Assert.True(log.Terminalize("c1", TransferState.Cancelled, 0, "客户端取消了下载"));

        TransferRecord rec = log.Recent(50).First(t => t.Id == "c1");
        Assert.Equal(TransferState.Cancelled, rec.State);
        Assert.Contains("取消", rec.Error);
    }

    [Fact]
    public void A_failed_download_reaches_failed_with_the_reason()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("f1"));

        Assert.True(log.Terminalize("f1", TransferState.Failed, 0, "传输中断：connection reset"));

        TransferRecord rec = log.Recent(50).First(t => t.Id == "f1");
        Assert.Equal(TransferState.Failed, rec.State);
        Assert.Contains("connection reset", rec.Error);
    }

    // ---------------------------------------------------------------- 幂等与竞态

    /// <summary>
    /// 终态只能有一个，且只能写一次：完成与取消几乎同时到达时，后到的那个不得改写先到的。
    /// </summary>
    [Fact]
    public void The_first_terminal_state_wins_and_the_second_is_a_no_op()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("race"));

        Assert.True(log.Terminalize("race", TransferState.Committed, 1024, null));
        bool second = log.Terminalize("race", TransferState.Cancelled, 0, "客户端取消了下载");

        Assert.False(second);                                   // 第二次没有生效
        Assert.Equal(TransferState.Committed, StateOf(log, "race"));
    }

    [Fact]
    public void Terminalize_refuses_a_non_terminal_state()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("t2"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => log.Terminalize("t2", TransferState.InProgress, 0, null));
    }

    [Fact]
    public void Terminalize_on_an_unknown_id_is_a_no_op()
    {
        TransferLog log = NewLog();
        Assert.False(log.Terminalize("does-not-exist", TransferState.Committed, 0, null));
    }

    /// <summary>
    /// R21 的核心：终态一旦提交，普通写入不得把它拉回"进行中"（也不得改成另一个终态）。
    /// 这是先前真正会发生的回退路径 —— Upsert 是无条件替换。
    /// </summary>
    [Fact]
    public void A_later_progress_write_cannot_revert_a_terminal_state()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("t3"));
        log.Terminalize("t3", TransferState.Cancelled, 0, "客户端取消了下载");

        // 模拟一次迟到的进度写入：同一 Id、状态仍是 InProgress
        TransferRecord late = NewRecord("t3");
        late.TransferredBytes = 512;
        log.Upsert(late);

        Assert.Equal(TransferState.Cancelled, StateOf(log, "t3"));
    }

    /// <summary>
    /// 非终态之间仍然可以正常更新（进度写入不能被这次修复误伤）。
    /// </summary>
    [Fact]
    public void Progress_between_non_terminal_states_still_works()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("t4"));

        TransferRecord moved = NewRecord("t4");
        moved.State = TransferState.Processing;
        moved.TransferredBytes = 256;
        log.Upsert(moved);

        TransferRecord rec = log.Recent(50).First(t => t.Id == "t4");
        Assert.Equal(TransferState.Processing, rec.State);
        Assert.Equal(256, rec.TransferredBytes);
    }

    /// <summary>终态集合的定义必须与状态枚举一致，避免以后新增状态时漏掉。</summary>
    [Theory]
    [InlineData(TransferState.Committed, true)]
    [InlineData(TransferState.Cancelled, true)]
    [InlineData(TransferState.Failed, true)]
    [InlineData(TransferState.InProgress, false)]
    [InlineData(TransferState.Processing, false)]
    public void Terminal_states_are_exactly_the_three_outcomes(TransferState state, bool expected)
        => Assert.Equal(expected, TransferLog.IsTerminal(state));

    /// <summary>终态必须能跨重新读取存活（R21：刷新传输页后状态不变）。</summary>
    [Fact]
    public void A_terminal_state_survives_a_reload_from_disk()
    {
        TransferLog first = NewLog();
        first.Upsert(NewRecord("persist"));
        first.Terminalize("persist", TransferState.Cancelled, 0, "客户端取消了下载");

        TransferLog reopened = NewLog();
        Assert.Equal(TransferState.Cancelled, StateOf(reopened, "persist"));
    }

    // ---------------------------------------------------------------- 无正文请求不留记录

    /// <summary>
    /// HEAD / 304 / 416 是浏览器自行发起的协议探测，不产生正文。它们**不得**留下用户可见的下载记录：
    /// 否则"传输"页会出现一个用户从未发起、却被标成"已取消"的条目 —— 比原来卡在"进行中"更难解释。
    /// </summary>
    [Fact]
    public void A_request_that_never_produced_a_body_leaves_no_record()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("probe"));

        Assert.True(log.Remove("probe"));
        Assert.DoesNotContain(log.Recent(50), t => t.Id == "probe");
    }

    /// <summary>已成终态的是真实事件，Remove 不得把它抹掉。</summary>
    [Fact]
    public void Remove_never_deletes_an_already_terminal_record()
    {
        TransferLog log = NewLog();
        log.Upsert(NewRecord("real"));
        log.Terminalize("real", TransferState.Cancelled, 0, "客户端取消了下载");

        Assert.False(log.Remove("real"));
        Assert.Contains(log.Recent(50), t => t.Id == "real" && t.State == TransferState.Cancelled);
    }

    [Fact]
    public void Remove_on_an_unknown_id_is_a_no_op()
    {
        TransferLog log = NewLog();
        Assert.False(log.Remove("nothing-here"));
    }
}
