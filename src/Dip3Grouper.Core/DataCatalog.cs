using System.Security.Cryptography;

namespace Dip3Grouper;

/// <summary>
/// 一个「可分发数据文件」的定义。
///
/// <para><see cref="Name"/> 是稳定标识：既用作分发接口的 URL 段
/// （<c>/api/data/file/{name}</c>），也用作客户端与服务器清单的比对键。
/// 之所以不复用文件路径做标识 —— 路径带目录层级，塞进 URL 既难读，
/// 也容易被拼出越界路径；而名字只做「等值查表」，天然没有穿越空间。</para>
/// </summary>
public sealed class DataFileSpec
{
    /// <summary>稳定标识（URL 安全，只用小写字母与连字符）</summary>
    public string Name { get; init; }

    /// <summary>中文名（界面显示）</summary>
    public string Label { get; init; }

    /// <summary>分组（界面归拢用）</summary>
    public string Group { get; init; }

    /// <summary>相对工程根的路径，一律以 <c>/</c> 书写，落地时再换分隔符</summary>
    public string RelPath { get; init; }
}

/// <summary>清单里的一条文件记录（服务器与客户端共用同一结构）。</summary>
public sealed class DataFileInfo
{
    public string Name { get; set; }
    public string Label { get; set; }
    public string Group { get; set; }
    public string RelPath { get; set; }
    public bool Exists { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }
    public string UpdatedAt { get; set; }
}

/// <summary>数据分发清单：一次「检查更新」的全部依据。</summary>
public sealed class DataManifest
{
    /// <summary>发布方标识，用于客户端识别对面确实是本程序的分发服务</summary>
    public string Generator { get; set; } = "Dip3Grouper";

    /// <summary>清单结构版本，将来加减字段时据此兼容</summary>
    public int Version { get; set; } = 1;

    /// <summary>清单生成时间（ISO，UTC）</summary>
    public string GeneratedAt { get; set; }

    public List<DataFileInfo> Files { get; set; } = new();
}

/// <summary>
/// 可分发数据文件的**白名单**与清单生成。
///
/// <para>这里是整个「局域网数据更新」的安全边界，两条硬规矩：</para>
/// <list type="number">
///   <item>业务数据可以下发，<b>本机私有数据绝不下发</b> —— <c>data/settings.json</c>
///         （软件名 / 主题 / 后台口令 / 托盘开关）与 <c>data/dezhou-favorites.json</c>
///         （每台电脑各不相同的「常用收藏」）都必须留在白名单之外。否则一次同步
///         就会把全院各机的主题、口令、收藏互相覆盖。</item>
///   <item>路径只能由白名单里的 <see cref="DataFileSpec.RelPath"/> 拼出，
///         任何来自网络的名字都必须先命中白名单才会落到磁盘路径上。</item>
/// </list>
/// </summary>
public static class DataCatalog
{
    /// <summary>可下发的数据文件（顺序即界面显示顺序）</summary>
    public static readonly IReadOnlyList<DataFileSpec> Specs = new[]
    {
        new DataFileSpec
        {
            Name = "dip3-config", Label = "病种库配置", Group = "病种库",
            RelPath = "config/dip3-config.json",
        },
        new DataFileSpec
        {
            Name = "config-meta", Label = "病种库导入信息", Group = "病种库",
            RelPath = "data/config-meta.json",
        },
        new DataFileSpec
        {
            Name = "zonghe", Label = "综合病组规则", Group = "病种库",
            RelPath = "data/zonghe.json",
        },
        new DataFileSpec
        {
            Name = "mappings", Label = "ICD-10 医保映射", Group = "映射",
            RelPath = "data/mappings.json",
        },
        new DataFileSpec
        {
            Name = "lixiang", Label = "立项指南与解读", Group = "资料",
            RelPath = "data/lixiang.json",
        },
        new DataFileSpec
        {
            Name = "dezhou-price", Label = "德州市物价", Group = "资料",
            RelPath = "data/dezhou-price.json",
        },
        new DataFileSpec
        {
            // 征求意见稿批次：和主库一样属于**业务数据**，要能跟随服务器下发
            // （各院一线看到的是同一份征求意见数据）；正式发布转正后由下次导入覆盖。
            Name = "dezhou-draft", Label = "德州新项目征求意见稿", Group = "资料",
            RelPath = "data/dezhou-draft.json",
        },
        new DataFileSpec
        {
            Name = "guifan", Label = "国家技术规范", Group = "资料",
            RelPath = "data/guifan.json",
        },
        new DataFileSpec
        {
            // 送检指引是「全科共用一份」的参考资料（疾病系统 → 标本 → 蜡块档位），
            // 海报一改就要全院同步，因此进白名单；不含任何本机偏好。
            Name = "zhijian", Label = "组织病理送检指引", Group = "资料",
            RelPath = "data/zhijian.json",
        },
        new DataFileSpec
        {
            // 基本物耗指引同样「全院一份」：36 类指南条款 + 边界清单，政策一改要全院同步。
            Name = "wuhao", Label = "基本物耗指引", Group = "资料",
            RelPath = "data/wuhao.json",
        },
    };

    /// <summary>本机私有文件（**永不参与同步**，仅供界面与自检做反向断言）</summary>
    public static readonly IReadOnlyList<string> PrivateRelPaths = new[]
    {
        "data/settings.json",
        "data/dezhou-favorites.json",
        // 基本物耗的「本机手工维护」差异集：先把本院/本科室的叫法补进清单再谈收费，
        // 属于**每台机器各自**的口径微调。它必须留在分发白名单之外 ——
        // 一旦下发，某台机器上随手补的条目就会盖掉全院的清单。
        "data/wuhao.user.json",
        // 「物价解读 AI」的模型地址 / API Key / 地区：每人自己申请的密钥只能留在自己机器上。
        // 一旦下发，别人的密钥会被覆盖、本院内网模型地址也会串到别的机器，且等于把凭据扩散出去。
        "data/price-ai.json",
        // 操作审计日志是**每台机器各自**的操作记录，绝不能随数据分发被别人覆盖。
        "data/audit-log.json",
        // 通知公告与「我读过哪些消息」都走各自的接口（服务端是唯一源头），
        // 不通过文件分发；已读记录更是每台机器各不相同的本机状态。
        "data/notice.json",
        "data/notice-read.json",
    };

    /// <summary>按标识查白名单；未命中返回 null</summary>
    public static DataFileSpec Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var s in Specs)
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        return null;
    }

    public static bool IsDistributable(string name) => Find(name) != null;

    /// <summary>该文件在本机的绝对路径；名字不在白名单里返回 null</summary>
    public static string LocalPath(string name)
    {
        var spec = Find(name);
        return spec == null ? null : Path.Combine(AppPaths.Root, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string LocalPath(DataFileSpec spec)
        => spec == null ? null : Path.Combine(AppPaths.Root, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>就地计算 SHA-256（小写十六进制）；文件不存在返回 null</summary>
    public static string Sha256(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>本机清单：逐文件算大小、时间与 SHA-256</summary>
    public static DataFileInfo Describe(DataFileSpec spec)
    {
        var path = LocalPath(spec);
        var fi = File.Exists(path) ? new FileInfo(path) : null;
        return new DataFileInfo
        {
            Name = spec.Name,
            Label = spec.Label,
            Group = spec.Group,
            RelPath = spec.RelPath,
            Exists = fi != null,
            Size = fi?.Length ?? 0,
            UpdatedAt = fi == null ? null : fi.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            Sha256 = fi == null ? null : Sha256(path),
        };
    }

    /// <summary>生成本机清单（服务器端分发接口 / 更新包导出都用它）</summary>
    public static DataManifest BuildManifest()
    {
        var m = new DataManifest { GeneratedAt = Js.Iso() };
        foreach (var s in Specs) m.Files.Add(Describe(s));
        return m;
    }

    /// <summary>白名单文件的总体积（字节）</summary>
    public static long TotalBytes()
    {
        long n = 0;
        foreach (var s in Specs)
        {
            var p = LocalPath(s);
            if (File.Exists(p)) n += new FileInfo(p).Length;
        }
        return n;
    }

    /// <summary>字节数 → 紧凑可读文本（界面显示用）</summary>
    public static string SizeText(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}
