using System.Windows;
using System.Windows.Controls;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop.Controls;

/// <summary>
/// 通用「一行记录」编辑对话框：按字段列表动态生成输入框。
/// 病种库 / 立项指南 / 映射表 / 综合病组 的新增与编辑共用。
/// </summary>
public partial class RowEditDialog : Window
{
    /// <param name="Key">字段 key（回传字典的键）</param>
    /// <param name="Label">界面显示名（中文）</param>
    /// <param name="Value">当前值</param>
    /// <param name="Multiline">是否多行输入</param>
    /// <param name="ReadOnly">是否只读（主键等不可变字段）</param>
    /// <param name="Options">下拉检索候选（非空时渲染为可编辑下拉框，仍允许自由输入）</param>
    public sealed record Field(string Key, string Label, string Value, bool Multiline, bool ReadOnly,
                               IReadOnlyList<string> Options = null);

    private readonly List<(Field Meta, Control Editor, Func<string> Get)> _editors = new();

    /// <summary>保存后的扁平字段（仅含界面上的字段）。取消时为 null。</summary>
    public Dictionary<string, string> Result { get; private set; }

    /// <summary>internal：SelfTest 需要直接构造对话框做模板断言（不弹窗）。</summary>
    internal RowEditDialog(string title, IEnumerable<Field> fields)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        Build(fields);
    }

    /// <summary>弹出编辑框；返回 null 表示取消。</summary>
    public static Dictionary<string, string> Edit(Window owner, string title, IEnumerable<Field> fields)
    {
        var dlg = new RowEditDialog(title, fields) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void Build(IEnumerable<Field> fields)
    {
        foreach (var f in fields)
        {
            FieldHost.Children.Add(new TextBlock
            {
                Text = f.Label,
                Style = (Style)FindResource("Label"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            Control editor;
            Func<string> get;
            if (f.Options is { Count: > 0 } && !f.ReadOnly && !f.Multiline)
            {
                // 可编辑下拉框：点开下拉选择，也可直接输入 / 键入过滤
                var cb = new ComboBox
                {
                    IsEditable = true,
                    IsTextSearchEnabled = true,
                    ItemsSource = f.Options,
                    Style = (Style)FindResource("Cb"),
                    Margin = new Thickness(0, 0, 0, 12),
                };
                // WPF 坑：模板/ItemsSource 应用前设置 Text 会被清空，必须 Loaded 后回填
                var init = f.Value ?? "";
                if (init.Length > 0)
                    cb.Loaded += (_, _) =>
                    {
                        var idx = cb.Items.IndexOf(init);
                        if (idx >= 0) cb.SelectedIndex = idx;
                        else cb.Text = init;
                    };
                editor = cb;
                get = () => cb.Text;
                _editors.Add((f, editor, get));
                FieldHost.Children.Add(cb);
                continue;
            }

            var box = new TextBox
            {
                Text = f.Value ?? "",
                IsReadOnly = f.ReadOnly,
                Style = (Style)FindResource(f.Multiline ? "Tb.Multi" : "Tb"),
                Margin = new Thickness(0, 0, 0, 12),
            };
            if (f.ReadOnly)
            {
                box.ThBg("B.Faint");
                box.ThFg("B.Muted");
                box.IsTabStop = false;
            }
            editor = box;
            get = () => box.Text;
            _editors.Add((f, editor, get));
            FieldHost.Children.Add(box);
        }

        TxtHint.Text = _editors.Count == 0 ? "该区段没有可编辑字段" : $"{_editors.Count} 个字段";
        Loaded += (_, _) =>
        {
            foreach (var (meta, editor, _) in _editors)
            {
                if (meta.ReadOnly || meta.Multiline) continue;
                editor.Focus();
                if (editor is TextBox tb) tb.SelectAll();
                break;
            }
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = new Dictionary<string, string>();
        foreach (var (meta, _, get) in _editors)
            Result[meta.Key] = (get() ?? "").Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
