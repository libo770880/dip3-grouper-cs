using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dip3GrouperApp.Desktop.Views;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 关联详情弹窗：展示某个三库条目的完整详情（字段与列表展开的详情卡一致）。
/// 弹窗内的关联链接为「原地推进」——再点一条会压栈并切换内容，底部「返回上一级」可逐层回退。
/// </summary>
public partial class RelatedWindow : Window
{
    private readonly List<(string Lib, int Id)> _history = new();

    public RelatedWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnKey;
    }

    /// <summary>供跨库关联链接调用；若宿主本身就是关联弹窗则原地推进，否则新开弹窗。</summary>
    public static void Open(Window owner, string lib, int id)
    {
        if (owner is RelatedWindow rw)
        {
            rw.Push(lib, id);
            return;
        }
        var w = new RelatedWindow();
        if (owner is { IsLoaded: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Push(lib, id);
        w.ShowDialog();
    }

    /// <summary>切换/推进到目标条目（供自检调用时不需要开窗）。</summary>
    public bool Push(string lib, int id)
    {
        if (!Related.TryLoad(lib, id, out var doc)) return false;
        _history.Add((lib, id));
        Render(doc);
        return true;
    }

    private void Render(RelatedDoc doc)
    {
        TxtLib.Text = doc.LibLabel;
        TxtCrumb.Text = _history.Count > 1
            ? string.Join("  ›  ", _history.Select(h => Related.Label(h.Lib)))
            : "点击关联条目可继续查看其详情";
        DataContext = doc;
        BtnBack.Visibility = _history.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_history.Count <= 1) return;
        _history.RemoveAt(_history.Count - 1);     // 丢弃当前层
        var (lib, id) = _history[^1];
        _history.RemoveAt(_history.Count - 1);     // 由 Push 重新压入
        if (!Push(lib, id)) _history.Clear();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Back && _history.Count > 1)
        {
            e.Handled = true;
            OnBack(sender, e);
        }
    }

    private void OnHeadDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// 离屏渲染取样（诊断用，命令行 --shot-related &lt;库:Id&gt; &lt;输出.png&gt;）：
    /// 不显示窗口，直接把内容按窗口尺寸布局后渲染成 PNG，用于无需人工点击地核对弹窗版式。
    /// 返回 null 表示成功，否则返回错误说明。
    /// </summary>
    public static string Shot(string lib, int id, string outPath)
    {
        var w = new RelatedWindow { Width = 820, Height = 640 };
        if (!w.Push(lib, id)) return $"未找到目标 {lib}#{id}";
        if (w.Content is not FrameworkElement root) return "窗口内容为空";

        var size = new Size(w.Width, w.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        return null;
    }
}
