using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 网页后台登录会话与失败限速 —— 纯内存逻辑，可直接单测。
///
/// 这些用例盯的是两条容易写错的分支：
/// <list type="number">
///   <item>口令改动后必须只留本人会话、其余立刻失效（<see cref="WebSession.KeepOnly"/>）；</item>
///   <item>锁定到期后失败计数**必须归零**，否则每一次尝试都会立刻触发新锁定，
///         等于输错 5 次就永久锁死（这正是自检抓出来的那个 bug）。</item>
/// </list>
/// 静态状态是全局的，所以每个用例用互不相同的来源标识，避免互相干扰。
/// </summary>
public class WebSessionTests
{
    private static string Src(string tag) => "test-" + tag + "-" + System.Guid.NewGuid().ToString("N");

    [Fact]
    public void 会话签发与校验()
    {
        var a = WebSession.Create();
        var b = WebSession.Create();

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);          // 32 字节 → 十六进制
        Assert.True(WebSession.Valid(a));
        Assert.True(WebSession.Valid(b));
        Assert.True(WebSession.Valid("  " + a + "  "));   // 前后空白容忍（Cookie 头里容易带）
    }

    [Fact]
    public void 无效令牌一律拒绝()
    {
        Assert.False(WebSession.Valid(null));
        Assert.False(WebSession.Valid(""));
        Assert.False(WebSession.Valid("   "));
        Assert.False(WebSession.Valid("deadbeef"));
        Assert.False(WebSession.Valid(new string('f', 64)));
    }

    [Fact]
    public void 退出后立即失效()
    {
        var t = WebSession.Create();
        Assert.True(WebSession.Valid(t));

        WebSession.Drop(t);
        Assert.False(WebSession.Valid(t));

        WebSession.Drop(t);                  // 幂等：重复注销不抛
        WebSession.Drop(null);
    }

    [Fact]
    public void 改口令时只保留本人会话()
    {
        var mine = WebSession.Create();
        var other1 = WebSession.Create();
        var other2 = WebSession.Create();

        WebSession.KeepOnly(mine);

        Assert.True(WebSession.Valid(mine));         // 刚改口令的人不用重登
        Assert.False(WebSession.Valid(other1));      // 别人拿着旧口令开的会话立刻掉线
        Assert.False(WebSession.Valid(other2));
    }

    [Fact]
    public void 关闭登录校验时清空全部会话()
    {
        var t = WebSession.Create();
        WebSession.Clear();
        Assert.False(WebSession.Valid(t));
        Assert.Equal(0, WebSession.ActiveCount);
    }

    [Fact]
    public void 未达阈值不锁定登录成功后计数清零()
    {
        var s = Src("counter");
        for (var i = 1; i <= WebSession.MaxFails - 1; i++) WebSession.NoteFail(s);

        Assert.Equal(WebSession.MaxFails - 1, WebSession.FailCount(s));
        Assert.Equal(0, WebSession.LockRemaining(s));       // 还没到阈值，不该锁

        WebSession.NoteOk(s);
        Assert.Equal(0, WebSession.FailCount(s));
        Assert.Equal(0, WebSession.LockRemaining(s));
    }

    [Fact]
    public void 达到阈值即锁定()
    {
        var s = Src("lock");
        for (var i = 1; i <= WebSession.MaxFails; i++) WebSession.NoteFail(s);

        Assert.True(WebSession.LockRemaining(s) > 0);
    }

    /// <summary>
    /// 回归用例：锁定到期后计数必须归零，下一次失败重新从 1 开始。
    /// 若不归零（旧实现），第 5 次之后的每一次尝试都会立刻再锁 60 秒 —— 永久锁死。
    /// </summary>
    [Fact]
    public void 锁定到期后重新计算而非永久锁死()
    {
        var s = Src("expire");
        var savedLock = WebSession.LockTime;
        var savedWindow = WebSession.FailWindow;
        try
        {
            WebSession.LockTime = System.TimeSpan.FromMilliseconds(60);
            WebSession.FailWindow = System.TimeSpan.FromMinutes(10);

            for (var i = 1; i <= WebSession.MaxFails; i++) WebSession.NoteFail(s);
            Assert.True(WebSession.LockRemaining(s) > 0);

            System.Threading.Thread.Sleep(140);              // 等锁定过期

            Assert.Equal(0, WebSession.LockRemaining(s));    // 锁已自动解除

            WebSession.NoteFail(s);                          // 到期后第一次失败
            Assert.Equal(1, WebSession.FailCount(s));        // 计数从 1 重新开始
            Assert.Equal(0, WebSession.LockRemaining(s));    // 且没有被立刻再锁
        }
        finally
        {
            WebSession.LockTime = savedLock;
            WebSession.FailWindow = savedWindow;
            WebSession.NoteOk(s);
        }
    }

    [Fact]
    public void 超时未失败则计数重来()
    {
        var s = Src("window");
        var saved = WebSession.FailWindow;
        try
        {
            WebSession.FailWindow = System.TimeSpan.FromMilliseconds(50);

            for (var i = 1; i <= WebSession.MaxFails - 1; i++) WebSession.NoteFail(s);
            Assert.Equal(WebSession.MaxFails - 1, WebSession.FailCount(s));

            System.Threading.Thread.Sleep(120);              // 超过冷静期

            WebSession.NoteFail(s);
            Assert.Equal(1, WebSession.FailCount(s));        // 不是累加到阈值
            Assert.Equal(0, WebSession.LockRemaining(s));    // 也就不会因为历史失误被锁
        }
        finally
        {
            WebSession.FailWindow = saved;
            WebSession.NoteOk(s);
        }
    }

    [Fact]
    public void 策略参数可复位()
    {
        var saved = WebSession.MaxFails;
        WebSession.MaxFails = 99;
        WebSession.ResetPolicy();
        Assert.Equal(5, WebSession.MaxFails);
        WebSession.MaxFails = saved;
    }
}
