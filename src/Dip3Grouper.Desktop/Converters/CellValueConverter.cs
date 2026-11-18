using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;

namespace Dip3GrouperApp.Desktop.Converters;

/// <summary>
/// 表格单元格取值：数组用 " | " 连接、布尔转 是/否、JsonElement 解包。
/// 对齐网页版渲染逻辑 `Array.isArray(v) ? v.join(' | ') : String(v)`。
/// </summary>
public class CellValueConverter : IValueConverter
{
    public static readonly CellValueConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return "";
            case bool b:
                return b ? "是" : "否";
            case string s:
                return s;
            case JsonElement je:
                return je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString() ?? "",
                    JsonValueKind.True => "是",
                    JsonValueKind.False => "否",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => je.ToString(),
                };
            case IEnumerable en:
                return string.Join(" | ", en.Cast<object>().Select(x => Convert(x, targetType, parameter, culture)));
            default:
                return value.ToString() ?? "";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
