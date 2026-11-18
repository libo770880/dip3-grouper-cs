using System.Windows;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Views.Admin;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 后台管理窗口（概览 / 病种库管理 / 立项指南 / 德州物价 / 映射与灰码 / 系统设置）。
/// 打开前是否要求登录由 data/settings.json 的 loginEnabled 决定（见 MainWindow.OnOpenAdmin）。
/// </summary>
public partial class AdminWindow : Window
{
    public AdminWindow()
    {
        InitializeComponent();
        TxtDataDir.Text = "数据目录\n" + AppPaths.Root;

        ApplyBranding();
        App.SettingsChanged += ApplyBranding;
        Closed += (_, _) => App.SettingsChanged -= ApplyBranding;
    }

    /// <summary>小屏适配：后台窗 1220×820 比低配机屏幕大，居中会把标题栏推出屏幕外。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WinFit.ClampToWorkArea(this);
    }

    /// <summary>侧栏品牌与窗口标题跟随「软件名称」设置</summary>
    private void ApplyBranding()
    {
        var s = SettingsStore.Current;
        Title = s.AppName + " · 后台管理";
        TxtSideLogo.Text = SettingsStore.LogoText;
        TxtSideAppName.Text = s.AppName;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ShowPage();
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage();
    }

    /// <summary>切换页面：显示对应视图并触发其首次数据加载</summary>
    private void ShowPage()
    {
        var overview = NavDash.IsChecked == true;
        var diseases = NavDiseases.IsChecked == true;
        var lixiang = NavLixiang.IsChecked == true;
        var dezhou = NavDezhou.IsChecked == true;
        var version = NavVersion.IsChecked == true;
        var audit = NavAudit.IsChecked == true;
        var mapping = NavMapping.IsChecked == true;
        var data = NavData.IsChecked == true;
        var settings = NavSettings.IsChecked == true;

        PageOverview.Visibility = Vis(overview);
        PageDiseases.Visibility = Vis(diseases);
        PageLixiang.Visibility = Vis(lixiang);
        PageDezhou.Visibility = Vis(dezhou);
        PageVersion.Visibility = Vis(version);
        PageAudit.Visibility = Vis(audit);
        PageMapping.Visibility = Vis(mapping);
        PageData.Visibility = Vis(data);
        PageSettings.Visibility = Vis(settings);

        TxtPageTitle.Text = overview ? "概览"
            : diseases ? "病种库管理"
            : lixiang ? "立项指南"
            : dezhou ? "德州物价"
            : version ? "版本管理"
            : audit ? "审计日志"
            : mapping ? "映射与灰码"
            : data ? "数据更新"
            : "系统设置";

        if (overview) PageOverview.EnsureLoaded();
        else if (diseases) PageDiseases.EnsureLoaded();
        else if (lixiang) PageLixiang.EnsureLoaded();
        else if (dezhou) PageDezhou.EnsureLoaded();
        else if (version) PageVersion.EnsureLoaded();
        else if (audit) PageAudit.EnsureLoaded();
        else if (mapping) PageMapping.EnsureLoaded();
        else if (data) PageData.EnsureLoaded();
        else PageSettings.EnsureLoaded();
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
