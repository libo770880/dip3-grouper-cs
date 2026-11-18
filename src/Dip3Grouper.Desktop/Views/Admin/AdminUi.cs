using System;
using System.Windows;
using System.Windows.Controls;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 后台视图共用的「执行一段操作 → 结果写状态条 → 异常弹窗」小工具。
/// 原先这段逻辑在 AdminDezhouView / AdminDiseasesView / AdminLixiangView 各有一份完全相同的拷贝，
/// 现在统一到这里，三个视图只保留一行包装（调用点签名不变）。
/// </summary>
internal static class AdminUi
{
    public static void Run(Func<string> action, TextBlock status)
    {
        try
        {
            status.Text = action();
        }
        catch (Exception ex)
        {
            status.Text = "操作失败：" + ex.Message;
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
