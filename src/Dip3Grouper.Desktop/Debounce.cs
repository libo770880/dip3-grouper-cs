using System.Windows.Threading;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 输入防抖：连续键入时只在停顿 <c>delay</c> 后触发一次动作（UI 线程上跑，无需加锁）。
/// 用于检索框——每敲一个字都全表扫描会让大数据集（技术规范 1.15 万条）明显发涩。
/// </summary>
internal sealed class Debounce
{
    private readonly DispatcherTimer _timer;
    private readonly Action _action;

    public Debounce(int delayMs, Action action)
    {
        _action = action;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _action();
        };
    }

    /// <summary>重新计时（键入时调用）</summary>
    public void Poke()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>取消尚未触发的动作（如「清空」按钮立即刷新时避免再补一次）</summary>
    public void Cancel() => _timer.Stop();
}
