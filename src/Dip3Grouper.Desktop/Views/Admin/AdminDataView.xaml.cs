using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 数据更新：从局域网数据源（另一台机器上的 <c>Dip3Grouper.Web</c>）
/// 把业务数据同步到本机。
///
/// <para>五块内容：数据源配置（含「跟随服务端公共设置」）→ 差异清单 → 备份回滚 → 离线更新包。
/// 只同步 <see cref="DataCatalog"/> 白名单里的业务数据；
/// <c>data/settings.json</c>（软件名/主题/后台口令）与 <c>data/dezhou-favorites.json</c>
/// （常用收藏）是本机私有的，永远不参与，避免多台机器互相覆盖 ——
/// 唯一例外是**管理员主动开启**的公共设置跟随（软件名称 / 副名 / 立项指南参数），
/// 走的是另一个只读接口 <c>/api/settings/public</c>，口令与主题不在其列。</para>
/// </summary>
public partial class AdminDataView : UserControl
{
    private bool _loaded;
    private bool _busy;

    /// <summary>最近一次检查结果（「立即更新」据此知道该下哪些文件）</summary>
    private DataCheckResult _last;

    /// <summary>进度条满宽（与 XAML 里 ProgHost.Width 一致）</summary>
    private const double ProgressWidth = 320;

    public AdminDataView()
    {
        InitializeComponent();
    }

    /// <summary>首次进入时载入（与其它后台页一致的懒加载约定）</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        LoadForm();
        RefreshBackups();
        LoadAppUpdate();
    }

    // ==================================================================
    // 数据源
    // ==================================================================

    private void LoadForm()
    {
        TxtServer.Text = SettingsStore.DataSourceUrl;
        PwbToken.Password = SettingsStore.Current.DataShareToken ?? "";
        ChkAuto.IsChecked = SettingsStore.AutoCheckUpdate;
        ChkFollow.IsChecked = SettingsStore.FollowServerSettings;

        var last = SettingsStore.Current.LastUpdateAt;
        TxtSourceStatus.Text = !SettingsStore.HasDataSource
            ? "尚未配置数据源，本机独立使用。"
            : !string.IsNullOrEmpty(last) && DateTimeOffset.TryParse(last, out var t)
                ? "上次更新：" + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "已配置数据源，点「检查更新」开始。";
        TxtSourceStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");

        RefreshSetStatus();
    }

    private void OnSaveSource(object sender, RoutedEventArgs e)
    {
        SettingsStore.SetDataSource(TxtServer.Text, PwbToken.Password, ChkAuto.IsChecked == true);
        AuditLog.Write(AuditAction.Setting, "系统设置",
            SettingsStore.HasDataSource ? $"数据源地址设为 {SettingsStore.DataSourceUrl}" : "清空数据源（本机独立使用）");
        SetStatus(TxtSourceStatus, SettingsStore.HasDataSource
            ? "已保存：" + SettingsStore.DataSourceUrl
            : "已清空数据源，本机独立使用。", true);
        RefreshSetStatus();
    }

    // ==================================================================
    // 公共设置（服务端 → 本机）
    // ==================================================================

    /// <summary>把当前开关与上次同步时间反映到状态行</summary>
    private void RefreshSetStatus()
    {
        if (!SettingsStore.HasDataSource)
        {
            SetStatus(TxtSetStatus, "未配置数据源，本机设置自管。", true);
            return;
        }
        if (!SettingsStore.FollowServerSettings)
        {
            SetStatus(TxtSetStatus, "已关闭跟随，本机设置不受服务端影响。", true);
            return;
        }
        var at = PublicSettingsClient.LastSyncText();
        SetStatus(TxtSetStatus, at.Length > 0 ? "上次同步：" + at : "已开启跟随，启动时会自动同步一次。", true);
    }

    private void OnToggleFollow(object sender, RoutedEventArgs e)
    {
        SettingsStore.SetFollowServerSettings(ChkFollow.IsChecked == true);
        RefreshSetStatus();
    }

    private async void OnSyncSettings(object sender, RoutedEventArgs e) => await SyncSettingsAsync();

    /// <summary>拉取并应用服务端公共设置（<paramref name="silent"/> = 自动流程，失败不刷红字）</summary>
    internal async Task<PublicSettingsResult> SyncSettingsAsync(bool silent = false)
    {
        var url = DataUpdateClient.Normalize(TxtServer.Text);
        if (url.Length == 0)
        {
            if (!silent) SetStatus(TxtSetStatus, "请先填写服务器地址。", false);
            return null;
        }

        SetStatus(TxtSetStatus, "正在同步设置…", true);
        var res = await PublicSettingsClient.SyncAsync(url);
        if (res == null) return null;

        if (res.Changed)
        {
            // 标题、品牌区、立项指南下拉立刻跟随；后台设置页下次进入时重新载入即可
            App.NotifySettingsChanged();
        }
        SetStatus(TxtSetStatus, res.Ok
            ? "同步时间 " + DateTime.Now.ToString("HH:mm:ss") + "：" + res.Message
            : res.Message, res.Ok || silent);
        return res;
    }

    // ==================================================================
    // 检查更新
    // ==================================================================

    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();

    /// <summary>检查更新（启动静默检查走 <c>silent: true</c>，失败时不打扰用户）</summary>
    internal async Task<DataCheckResult> CheckAsync(bool silent = false)
    {
        if (_busy) return _last;

        var url = DataUpdateClient.Normalize(TxtServer.Text);
        if (url.Length == 0)
        {
            if (!silent) SetStatus(TxtSourceStatus, "请先填写服务器地址。", false);
            return null;
        }

        SetBusy(true);
        SetStatus(TxtSourceStatus, "正在检查…", true);
        try
        {
            // 顺手把当前输入落盘，避免「改了地址没点保存就去检查」的困惑
            SettingsStore.SetDataSource(TxtServer.Text, PwbToken.Password, ChkAuto.IsChecked == true);

            _last = await DataUpdateClient.CheckAsync(url);
            GridDiff.ItemsSource = _last.Items;
            UpdateDiffEmpty();
            TxtDiffTip.Text = _last.Ok
                ? _last.Message + (_last.ManifestAt is { Length: > 0 } at && DateTimeOffset.TryParse(at, out var mt)
                    ? $"\n数据源清单生成时间：{mt.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : "")
                : _last.Message;

            if (_last.Ok)
                SetStatus(TxtSourceStatus, _last.PendingCount == 0 ? "已是最新。" : _last.PendingText, true);
            else if (!silent)
                SetStatus(TxtSourceStatus, _last.Message, false);
            return _last;
        }
        catch (Exception ex)
        {
            if (!silent) SetStatus(TxtSourceStatus, "检查失败：" + ex.Message, false);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ==================================================================
    // 立即更新
    // ==================================================================

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // 还没检查过就先检查一次，省得用户先去点「检查更新」
        if (_last == null || !_last.Ok)
        {
            var r = await CheckAsync();
            if (r == null || !r.Ok) return;
        }
        if (_last.PendingCount == 0)
        {
            SetStatus(TxtApplyStatus, "本地数据已是最新，无需更新。", true);
            return;
        }

        SetBusy(true);
        ProgHost.Visibility = Visibility.Visible;
        Progress(0);
        SetStatus(TxtApplyStatus, "正在更新…", true);
        try
        {
            var res = await DataUpdateClient.ApplyAsync(_last.Server, _last.Pending,
                (pct, msg) =>
                {
                    OnUi(() =>
                    {
                        Progress(pct);
                        if (!string.IsNullOrEmpty(msg)) TxtProg.Text = msg;
                    });
                });

            Progress(100);
            if (res.Applied.Count > 0)
            {
                SetStatus(TxtApplyStatus, res.Message + " 正在热重载…", true);
                App.State.ReloadAll();
                SettingsStore.MarkUpdated();
            }
            SetStatus(TxtApplyStatus, res.Message, res.Ok);

            // 更新后重新比对一次，表格立刻回到「一致」
            await CheckAsync();
            RefreshBackups();

            // 数据跟着服务端走的同时，展示类设置也一并跟上（开关关着就跳过）
            if (res.Applied.Count > 0 && SettingsStore.FollowServerSettings) await SyncSettingsAsync(silent: true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtApplyStatus, "更新失败：" + ex.Message, false);
        }
        finally
        {
            SetBusy(false);
            ProgHost.Visibility = Visibility.Collapsed;
            TxtProg.Text = "";
        }
    }

    private void Progress(int pct)
    {
        var p = Math.Max(0, Math.Min(100, pct));
        ProgFill.Width = ProgressWidth * p / 100.0;
    }

    // ==================================================================
    // 备份与回滚
    // ==================================================================

    private void OnRefreshBackups(object sender, RoutedEventArgs e)
    {
        RefreshBackups();
        SetStatus(TxtRestoreStatus, CmbBackup.Items.Count == 0 ? "暂无备份点。" : null, true);
    }

    private void RefreshBackups()
    {
        var names = DataUpdateClient.BackupDirs().Select(Path.GetFileName).ToList();
        CmbBackup.ItemsSource = names;
        if (names.Count > 0) CmbBackup.SelectedIndex = 0;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (CmbBackup.SelectedItem is not string name)
        {
            SetStatus(TxtRestoreStatus, "请先选择一个备份点。", false);
            return;
        }

        var dir = Path.Combine(DataUpdateClient.BackupRoot, name);
        var res = DataUpdateClient.Restore(dir);
        if (res.Applied.Count > 0)
        {
            App.State.ReloadAll();
            SettingsStore.MarkUpdated();
        }
        SetStatus(TxtRestoreStatus, res.Message, res.Ok);
        RefreshBackups();
        // 还原后本机内容又和服务器不同了，重新比对一次让表格说真话
        _ = CheckAsync(silent: true);
    }

    private void OnOpenBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataUpdateClient.BackupRoot);
            Process.Start("explorer.exe", $"\"{DataUpdateClient.BackupRoot}\"");
        }
        catch (Exception ex)
        {
            SetStatus(TxtRestoreStatus, "打开目录失败：" + ex.Message, false);
        }
    }

    // ==================================================================
    // 离线更新包
    // ==================================================================

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出数据更新包",
            FileName = $"DIP3-数据更新包-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "数据更新包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            SetBusy(true);
            var path = DataUpdateClient.ExportPackage(dlg.FileName,
                (pct, msg) => OnUi(() => SetStatus(TxtPackStatus, msg, true)));
            AuditLog.Write(AuditAction.Export, "数据", $"导出离线更新包 {Path.GetFileName(path)}");
            SetStatus(TxtPackStatus, "已导出：" + path, true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtPackStatus, "导出失败：" + ex.Message, false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入数据更新包",
            Filter = "数据更新包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            SetBusy(true);
            var res = DataUpdateClient.ImportPackage(dlg.FileName,
                (pct, msg) => OnUi(() =>
                {
                    if (!string.IsNullOrEmpty(msg)) SetStatus(TxtPackStatus, msg, true);
                }));

            if (res.Applied.Count > 0)
            {
                App.State.ReloadAll();
                SettingsStore.MarkUpdated();
                RefreshBackups();
            }
            SetStatus(TxtPackStatus, res.Message, res.Ok);
            if (res.Applied.Count > 0) _ = CheckAsync(silent: true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtPackStatus, "导入失败：" + ex.Message, false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ==================================================================
    // 客户端更新
    // ==================================================================

    /// <summary>最近一次客户端更新检查结果（「下载并更新」据此知道该下哪个包）</summary>
    private AppUpdateCheck _lastApp;

    private void LoadAppUpdate()
    {
        TxtAppVersion.Text = AppVersion.Current;
        SetStatus(TxtAppStatus, SettingsStore.HasDataSource
            ? "更新源：" + SettingsStore.DataSourceUrl + "（与数据源共用一个地址）"
            : "尚未配置更新源，可在上方「数据源」里填写服务器地址。", true);
        BtnAppApply.IsEnabled = false;
    }

    private async void OnAppCheck(object sender, RoutedEventArgs e) => await CheckAppAsync();

    /// <summary>检查客户端更新（启动静默检查走 <c>silent: true</c>）</summary>
    internal async Task<AppUpdateCheck> CheckAppAsync(bool silent = false)
    {
        var url = DataUpdateClient.Normalize(TxtServer.Text);
        if (url.Length == 0)
        {
            if (!silent) SetStatus(TxtAppStatus, "请先填写服务器地址。", false);
            return null;
        }

        SetStatus(TxtAppStatus, "正在检查客户端版本…", true);
        try
        {
            _lastApp = await AppUpdateClient.CheckAsync(url);
            BtnAppApply.IsEnabled = _lastApp.Ok && _lastApp.HasUpdate;
            TxtAppNotes.Text = _lastApp.Info == null
                ? ""
                : $"最新版本 {_lastApp.Info.Version} · {_lastApp.Info.SizeText} · 发布 {_lastApp.Info.PublishedText}"
                  + (string.IsNullOrWhiteSpace(_lastApp.Info.Notes) ? "" : "\n" + _lastApp.Info.Notes);
            SetStatus(TxtAppStatus, _lastApp.Message, _lastApp.Ok || silent);
            return _lastApp;
        }
        catch (Exception ex)
        {
            if (!silent) SetStatus(TxtAppStatus, "检查失败：" + ex.Message, false);
            return null;
        }
    }

    private async void OnAppApply(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_lastApp == null || !_lastApp.Ok) _lastApp = await CheckAppAsync();
        if (_lastApp == null || !_lastApp.Ok || !_lastApp.HasUpdate)
        {
            SetStatus(TxtAppStatus, "没有可用的新版本。", true);
            return;
        }

        var info = _lastApp.Info;
        if (MessageBox.Show(
                $"将更新到 {info.Version}（当前 {AppVersion.Current}），安装包 {info.SizeText}。\n\n"
                + "下载完成后程序会自动退出，替换程序文件并重新启动。\n\n确认继续？",
                "客户端更新确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetBusy(true);
        ProgAppHost.Visibility = Visibility.Visible;
        AppProgress(0);
        try
        {
            var dl = await AppUpdateClient.DownloadAsync(_lastApp.Server, info, (pct, msg) => OnUi(() =>
            {
                AppProgress(pct);
                if (!string.IsNullOrEmpty(msg)) TxtAppProg.Text = msg;
            }));

            if (!dl.Ok)
            {
                SetStatus(TxtAppStatus, dl.Message, false);
                AuditLog.Write(AuditAction.Update, "客户端更新",
                    $"下载客户端 {info.Version} 失败", false, dl.Message);
                return;
            }

            var plan = AppUpdateClient.PrepareApply(dl.FilePath, info.Version);
            AuditLog.Write(AuditAction.Update, "客户端更新",
                $"下载并准备更新到 {info.Version}（{plan.FileCount} 个文件）");
            SetStatus(TxtAppStatus, "准备就绪，" + plan.Summary + "　正在退出并替换…", true);
            LaunchUpdate(plan);
        }
        catch (Exception ex)
        {
            AuditLog.Write(AuditAction.Update, "客户端更新",
                $"更新到 {info.Version} 失败", false, ex.Message);
            SetStatus(TxtAppStatus, "更新失败：" + ex.Message, false);
        }
        finally
        {
            SetBusy(false);
            ProgAppHost.Visibility = Visibility.Collapsed;
            TxtAppProg.Text = "";
        }
    }

    private void OnAppLocal(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择客户端安装包",
            Filter = "安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var plan = AppUpdateClient.ApplyLocalPackage(dlg.FileName);
            if (MessageBox.Show(
                    $"将用所选安装包更新本程序（{plan.FileCount} 个文件）。\n\n"
                    + "程序会自动退出、替换并重新启动。\n\n确认继续？",
                    "本地更新包安装确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            AuditLog.Write(AuditAction.Update, "客户端更新",
                $"从本地更新包安装 {Path.GetFileName(dlg.FileName)}（{plan.FileCount} 个文件）");
            SetStatus(TxtAppStatus, "准备就绪，" + plan.Summary + "　正在退出并替换…", true);
            LaunchUpdate(plan);
        }
        catch (Exception ex)
        {
            SetStatus(TxtAppStatus, "安装失败：" + ex.Message, false);
        }
    }

    /// <summary>启动替换脚本并退出本程序（脚本会等本进程结束后覆盖文件再重启）。</summary>
    private static void LaunchUpdate(AppUpdatePlan plan)
    {
        AppUpdateClient.Launch(plan, Environment.ProcessId);
        Application.Current.Shutdown();
    }

    private void OnAppOpenDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppUpdateClient.UpdateDir);
            Process.Start("explorer.exe", $"\"{AppUpdateClient.UpdateDir}\"");
        }
        catch (Exception ex)
        {
            SetStatus(TxtAppStatus, "打开目录失败：" + ex.Message, false);
        }
    }

    private void AppProgress(int pct)
    {
        var p = Math.Max(0, Math.Min(100, pct));
        ProgAppFill.Width = ProgressWidth * p / 100.0;
    }

    // ==================================================================
    // 内部
    // ==================================================================

    /// <summary>进度回调可能来自后台线程，落到 UI 线程再动控件</summary>
    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    /// <summary>差异表为空时显示「暂无差异」提示（只有表头的白框会被当成加载失败）</summary>
    private void UpdateDiffEmpty()
        => TxtDiffEmpty.Visibility = GridDiff.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnCheck.IsEnabled = !busy;
        BtnApply.IsEnabled = !busy;
        BtnSave.IsEnabled = !busy;
        BtnRestore.IsEnabled = !busy;
        BtnImport.IsEnabled = !busy;
        BtnExport.IsEnabled = !busy;
        BtnSyncSet.IsEnabled = !busy;
        BtnAppCheck.IsEnabled = !busy;
        BtnAppLocal.IsEnabled = !busy;
        if (busy) BtnAppApply.IsEnabled = false;
        else BtnAppApply.IsEnabled = _lastApp is { Ok: true, HasUpdate: true };
        Cursor = busy ? Cursors.Wait : null;
    }

    private void SetStatus(TextBlock target, string text, bool ok)
    {
        target.Text = text ?? "";
        target.SetResourceReference(TextBlock.ForegroundProperty, ok ? "B.Muted" : "B.Danger");
    }

    /// <summary>供自检直读：当前表格里的差异行数</summary>
    internal int DiagDiffRows => GridDiff.Items.Count;

    /// <summary>供自检直读：数据源输入框文本</summary>
    internal string DiagServerText => TxtServer.Text;

    /// <summary>供自检直读：最近一次检查结果</summary>
    internal DataCheckResult DiagLast => _last;

    /// <summary>供自检直读：当前版本号文本</summary>
    internal string DiagVersionText => TxtAppVersion.Text;

    /// <summary>供自检直读：最近一次客户端更新检查结果</summary>
    internal AppUpdateCheck DiagLastApp => _lastApp;

    /// <summary>供自检直读：「下载并更新」是否可用</summary>
    internal bool DiagAppApplyEnabled => BtnAppApply.IsEnabled;
}
