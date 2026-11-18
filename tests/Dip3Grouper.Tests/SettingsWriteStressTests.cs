using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// settings.json 写读压力测试。
///
/// <para>背景：桌面端自检的「设置往返」用例曾在发布冒烟阶段**偶发**失败。排查确认写路径
/// （临时文件 + File.Replace + 重试）本身可靠，失败来自「运行中的另一个客户端实例用
/// 旧内存覆盖了刚写入的文件」。这里对写读路径本身做高频压测：同一进程内高频
/// 「序列化落盘 → 立刻读磁盘原文 → 反序列化回读」，任何一次不一致都说明写路径有竞态。</para>
///
/// <para>注意：**不得触碰 AppPaths / SettingsStore 等全局静态**（xunit 不同测试类默认并行，
/// 改全局根目录会让读数据文件的其它测试读到空目录）。</para>
/// </summary>
public class SettingsWriteStressTests
{
    [Fact]
    public void WriteReadHighFrequency_AlwaysConsistent()
    {
        var path = Path.Combine(Path.GetTempPath(), "dip3-stress-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var url = $"10.0.{i / 256}.{i % 256}:3210";
                var token = "tok-" + i;
                var auto = i % 2 == 0;

                var s = new AppSettings
                {
                    AppName = "压力测试",
                    AppSubName = "第" + i + "轮",
                    DataSourceUrl = url,
                    DataShareToken = token,
                    AutoCheckUpdate = auto,
                    CloseToTray = !auto,
                    LixiangCategories = new List<string> { "综合类", "临床诊疗类" },
                };
                Js.WriteJsonFile(path, s, indent: 2);

                // 写完立刻读磁盘原文：必须已包含刚写入的值（不允许读到旧值）
                var raw = File.ReadAllText(path);
                Assert.Contains(url, raw);
                Assert.Contains(token, raw);
                Assert.Contains(auto ? "\"autoCheckUpdate\": true" : "\"autoCheckUpdate\": false", raw);

                // 反序列化回读：与写入值一致
                var back = Js.ReadJsonFile<AppSettings>(path);
                Assert.Equal(url, back.DataSourceUrl);
                Assert.Equal(token, back.DataShareToken);
                Assert.Equal(auto, back.AutoCheckUpdate);
                Assert.Equal(!auto, back.CloseToTray);
                Assert.Equal(new List<string> { "综合类", "临床诊疗类" }, back.LixiangCategories);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件尽力清理 */ }
        }
    }
}
