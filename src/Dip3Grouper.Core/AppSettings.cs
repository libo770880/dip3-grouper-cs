using System.Security.Cryptography;

namespace Dip3Grouper;

/// <summary>
/// 应用级可配置项：软件名称、主题、后台登录凭据。
///
/// 存放于 <c>data/settings.json</c>，随绿色版目录一起搬走，Web 版与桌面版共用同一份。
/// 所有属性都写成可空类型，是为了让"配置文件里少写一个键"与"写了 null"都能被
/// <see cref="SettingsStore.Normalize"/> 归一化成有意义的默认值，而不是静默变成 false。
/// </summary>
public sealed class AppSettings
{
    /// <summary>软件名称（窗口标题、启动页、后台侧栏都用它）</summary>
    public string AppName { get; set; }

    /// <summary>副名称 / 使用单位（可空，填了会显示在标题栏软件名下方）</summary>
    public string AppSubName { get; set; }

    /// <summary>主题 id（见桌面端 Theming/ThemeCatalog.cs）</summary>
    public string Theme { get; set; }

    /// <summary>后台是否要求登录</summary>
    public bool? LoginEnabled { get; set; }

    /// <summary>后台登录账号</summary>
    public string AdminUser { get; set; }

    /// <summary>后台登录口令（PBKDF2 哈希，格式 pbkdf2$迭代$盐$摘要）</summary>
    public string AdminPass { get; set; }

    /// <summary>立项指南「项目类别」参数表（下拉检索候选，空 = 自动取现有数据去重值）</summary>
    public List<string> LixiangCategories { get; set; }

    /// <summary>立项指南「计价单位」参数表（下拉检索候选，空 = 自动取现有数据去重值）</summary>
    public List<string> LixiangUnits { get; set; }

    /// <summary>
    /// 点窗口关闭按钮时最小化到系统托盘而不是退出（桌面端专有；Web 版忽略该键）。
    /// 可空：缺省按 true 处理（见 SettingsStore.Normalize）。
    /// </summary>
    public bool? CloseToTray { get; set; }

    /// <summary>
    /// 点窗口关闭按钮时是否先弹确认框（桌面端专有；Web 版忽略该键）。
    /// 确认框里勾「记住我的选择」会把它置为 false，之后直接按 <see cref="CloseToTray"/> 执行。
    /// 可空：缺省按 true 处理（见 SettingsStore.Normalize）。
    /// </summary>
    public bool? CloseAsk { get; set; }

    /// <summary>
    /// 物价页「使用说明」面板是否展开（桌面端专有；本机私有键，不参与数据分发）。
    /// 可空：缺省按 false（收起）—— 1366×768 下展开会吃掉约 150px，首屏只看得到 10 行。
    /// </summary>
    public bool? UsageOpen { get; set; }

    /// <summary>
    /// 表格密度：true = 紧凑行高（默认），false = 标准行高。
    /// 桌面端专有；本机私有键，不参与数据分发。
    /// </summary>
    public bool? CompactRows { get; set; }

    /// <summary>
    /// 局域网「数据分发中心」地址（形如 <c>http://10.0.0.8:3210</c>，也接受只写 <c>10.0.0.8:3210</c>）。
    /// 留空 = 本机独立使用，不做任何联网检查。**本机私有键，不参与数据分发。**
    /// </summary>
    public string DataSourceUrl { get; set; }

    /// <summary>
    /// 分发服务的共享口令（可选）。填了则服务器会校验 <c>?token=</c>，
    /// 防止院内其他机器随手拉到数据。留空 = 不校验。
    /// </summary>
    public string DataShareToken { get; set; }

    /// <summary>启动后在后台静默检查一次数据更新（默认开）</summary>
    public bool? AutoCheckUpdate { get; set; }

    /// <summary>最近一次成功更新的时间（ISO）</summary>
    public string LastUpdateAt { get; set; }

    /// <summary>
    /// 跟随局域网数据源的「公共设置」（软件名称 / 副名 / 立项指南参数）。默认开。
    /// 只影响展示类配置；口令、主题、托盘开关、数据源地址、常用收藏永远归本机所有。
    /// </summary>
    public bool? FollowServerSettings { get; set; }

    /// <summary>最近一次成功同步公共设置的时间（ISO）</summary>
    public string LastSettingsAt { get; set; }

    /// <summary>最后修改时间（ISO）</summary>
    public string UpdatedAt { get; set; }
}

/// <summary>settings.json 的读写与账号校验。</summary>
public static class SettingsStore
{
    public const string DefaultAppName = "DIP 3.0 分组器";
    public const string DefaultUser = "admin";
    public const string DefaultPassword = "admin123";
    public const string DefaultTheme = "azure";

    /// <summary>PBKDF2 迭代次数（桌面端单机校验，10 万次约 30~60ms，足够且不拖慢登录）</summary>
    private const int Iterations = 100_000;

    private static AppSettings _cur;

    /// <summary>当前设置（首次访问自动载入并补齐默认值）</summary>
    public static AppSettings Current => _cur ??= LoadAndNormalize();

    /// <summary>后台是否启用登录校验</summary>
    public static bool LoginEnabled => Current.LoginEnabled ?? true;

    /// <summary>点窗口关闭按钮的行为：true = 最小化到托盘（默认，程序留在托盘），false = 直接退出</summary>
    public static bool CloseToTray => Current.CloseToTray ?? true;

    /// <summary>点窗口关闭按钮时是否先弹确认框（默认开；确认框里勾「记住」后关闭）</summary>
    public static bool CloseAsk => Current.CloseAsk ?? true;

    /// <summary>局域网数据源地址（未配置返回空串）</summary>
    public static string DataSourceUrl => (Current.DataSourceUrl ?? "").Trim();

    /// <summary>是否配置了数据源</summary>
    public static bool HasDataSource => DataSourceUrl.Length > 0;

    /// <summary>物价页使用说明面板是否展开（默认收起）</summary>
    public static bool UsageOpen => Current.UsageOpen ?? false;

    /// <summary>改「使用说明默认展开」并立即落盘</summary>
    public static void SetUsageOpen(bool value)
    {
        if (Current.UsageOpen == value) return;
        Current.UsageOpen = value;
        Save();
    }

    /// <summary>表格是否紧凑行高（默认紧凑）</summary>
    public static bool CompactRows => Current.CompactRows ?? true;

    /// <summary>改表格密度并立即落盘</summary>
    public static void SetCompactRows(bool value)
    {
        if (Current.CompactRows == value) return;
        Current.CompactRows = value;
        Save();
    }

    /// <summary>启动后静默检查数据更新（默认开）</summary>
    public static bool AutoCheckUpdate => Current.AutoCheckUpdate ?? true;

    /// <summary>改「关闭时最小化到托盘」开关并立即落盘</summary>
    public static void SetCloseToTray(bool value)
    {
        Current.CloseToTray = value;
        Save();
    }

    /// <summary>改「点关闭时先询问」开关并立即落盘</summary>
    public static void SetCloseAsk(bool value)
    {
        Current.CloseAsk = value;
        Save();
    }

    /// <summary>
    /// 用户明确表态「点关闭该怎么做」：写入行为并停掉后续询问。
    /// 两个入口共用 —— ① 确认框里勾了「记住我的选择」；② 托盘菜单勾选「关闭时最小化到托盘」
    /// （只勾行为却不关询问，用户会觉得"我设过了怎么还问"）。
    /// </summary>
    public static void SetCloseChoice(bool hideToTray)
    {
        Current.CloseToTray = hideToTray;
        Current.CloseAsk = false;
        Save();
    }

    /// <summary>改数据源配置（地址 / 共享口令 / 自动检查）并立即落盘</summary>
    public static void SetDataSource(string url, string token, bool autoCheck)
    {
        var u = (url ?? "").Trim();
        Current.DataSourceUrl = u.Length == 0 ? null : u;
        var t = (token ?? "").Trim();
        Current.DataShareToken = t.Length == 0 ? null : t;
        Current.AutoCheckUpdate = autoCheck;
        Save();
    }

    /// <summary>
    /// 只改「数据分发共享口令」并立即落盘（服务端后台用它设置；留空 = 关闭校验）。
    /// 与 <see cref="SetDataSource"/> 分开，是因为服务端后台不存在「服务器地址」这个概念 ——
    /// 那台机器本身就是服务端。
    /// </summary>
    public static void SetShareToken(string token)
    {
        var t = (token ?? "").Trim();
        Current.DataShareToken = t.Length == 0 ? null : t;
        Save();
    }

    /// <summary>是否跟随服务端的公共设置（软件名称 / 副名 / 立项指南参数）</summary>
    public static bool FollowServerSettings => Current.FollowServerSettings ?? true;

    /// <summary>改「跟随服务端公共设置」开关并立即落盘</summary>
    public static void SetFollowServerSettings(bool value)
    {
        Current.FollowServerSettings = value;
        Save();
    }

    /// <summary>记录最近一次成功更新的时间</summary>
    public static void MarkUpdated()
    {
        Current.LastUpdateAt = Js.Iso();
        Save();
    }

    /// <summary>口令是否仍是出厂默认值（用于在登录页给出提示）</summary>
    public static bool IsDefaultPassword => VerifyPassword(DefaultPassword, Current.AdminPass);

    /// <summary>品牌方块内的短名称：ASCII 开头取 3 字符，中文开头取 2 字</summary>
    public static string LogoText
    {
        get
        {
            var n = (Current.AppName ?? "").Replace(" ", "");
            if (n.Length == 0) return "DIP";
            var take = char.IsAscii(n[0]) ? Math.Min(3, n.Length) : Math.Min(2, n.Length);
            return n[..take];
        }
    }

    /// <summary>重新从磁盘载入</summary>
    public static AppSettings Reload()
    {
        _cur = null;
        return Current;
    }

    /// <summary>落盘（自动刷新 UpdatedAt）</summary>
    public static void Save()
    {
        var s = Current;
        s.UpdatedAt = Js.Iso();
        Js.WriteJsonFile(AppPaths.SettingsFile, s, indent: 2);
    }

    /// <summary>设置或清除副名称</summary>
    public static void SetSubName(string sub)
        => Current.AppSubName = string.IsNullOrWhiteSpace(sub) ? null : sub.Trim();

    /// <summary>改口令（存哈希，不存明文）</summary>
    public static void SetPassword(string password) => Current.AdminPass = HashPassword(password);

    /// <summary>改登录账号</summary>
    public static void SetUser(string user)
        => Current.AdminUser = string.IsNullOrWhiteSpace(user) ? DefaultUser : user.Trim();

    /// <summary>校验后台登录；未启用登录时一律放行</summary>
    public static bool VerifyLogin(string user, string password)
    {
        if (!LoginEnabled) return true;
        var u = (user ?? "").Trim();
        if (!string.Equals(u, Current.AdminUser ?? DefaultUser, StringComparison.OrdinalIgnoreCase)) return false;
        return VerifyPassword(password, Current.AdminPass);
    }

    // ==================================================================
    // 口令哈希
    // ==================================================================

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>常数时间比对，避免计时侧信道</summary>
    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iter) || iter <= 0) return false;

        byte[] salt, expect;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expect = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iter, HashAlgorithmName.SHA256, expect.Length);
        return CryptographicOperations.FixedTimeEquals(key, expect);
    }

    // ==================================================================
    // 内部
    // ==================================================================

    private static AppSettings LoadAndNormalize()
    {
        AppSettings s = null;
        try { s = Js.ReadJsonFile<AppSettings>(AppPaths.SettingsFile); }
        catch { /* settings.json 损坏时退回默认值，不能因此启动失败 */ }
        s ??= new AppSettings();
        Normalize(s);
        return s;
    }

    /// <summary>把缺项 / null / 空白统一补成默认值</summary>
    private static void Normalize(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.AppName)) s.AppName = DefaultAppName;
        if (s.AppSubName != null && string.IsNullOrWhiteSpace(s.AppSubName)) s.AppSubName = null;
        if (string.IsNullOrWhiteSpace(s.Theme)) s.Theme = DefaultTheme;
        s.LoginEnabled ??= true;
        if (string.IsNullOrWhiteSpace(s.AdminUser)) s.AdminUser = DefaultUser;
        s.CloseToTray ??= true;   // 默认：点关闭最小化到托盘，避免误触丢会话
        s.CloseAsk ??= true;      // 默认：点关闭先问一次「托盘还是退出」，用户勾「记住」后不再问
        s.AutoCheckUpdate ??= true;
        s.FollowServerSettings ??= true;   // 默认跟随服务端公共设置（没配数据源时该开关不生效）
        if (s.DataSourceUrl != null && string.IsNullOrWhiteSpace(s.DataSourceUrl)) s.DataSourceUrl = null;
        if (s.DataShareToken != null && string.IsNullOrWhiteSpace(s.DataShareToken)) s.DataShareToken = null;
        // 首次运行（或哈希被清空）时落到出厂口令，并由登录页提示尽快修改
        if (string.IsNullOrWhiteSpace(s.AdminPass) || s.AdminPass.Split('$').Length != 4)
            s.AdminPass = HashPassword(DefaultPassword);

        s.LixiangCategories = CleanList(s.LixiangCategories);
        s.LixiangUnits = CleanList(s.LixiangUnits);
    }

    /// <summary>列表参数归一化：去空白项、去重（保序）；空表置 null 表示「自动提取」</summary>
    public static List<string> CleanList(List<string> list)
    {
        if (list == null) return null;
        List<string> keep = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in list)
        {
            var v = (raw ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            keep ??= new List<string>();
            keep.Add(v);
        }
        return keep;
    }
}
