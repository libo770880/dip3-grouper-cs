using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dip3Grouper;
using Microsoft.AspNetCore.Http.Features;

namespace Dip3Grouper.Web;

public static class WebRouter
{
    // 列名映射表、导入解析、模板生成等后台编排逻辑统一由 Core 的
    // GrouperService / AdminService 提供（Web 端与桌面客户端共用唯一实现）。
    private static string Trim(string s) => (s ?? "").Trim();

    public static async Task Handle(HttpContext ctx, AppState S)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var url = req.Path.Value ?? "/";

        // 网页端写操作的审计：以会话账号开一个作用域（AsyncLocal），
        // 这样连它内部 await 的 Core 写操作（导入、版本快照…）也自动带上正确的「谁」。
        var needsAuth = NeedsAdminAuth(req.Method, url);
        IDisposable scope = null;
        if (needsAuth || url == "/api/admin/login")
            scope = AuditLog.Scope(SettingsStore.Current.AdminUser ?? SettingsStore.DefaultUser, "网页端");

        // 通用审计：只覆盖「Core 未自己记账」的后台写接口，避免同一次操作记两条。
        // 导入类接口由 AdminService 记录（它知道解析了多少行、新增/更新各多少，信息更全）。
        var audited = needsAuth && string.Equals(req.Method, "POST", StringComparison.Ordinal)
                      && !CoreAuditedPaths.Contains(url);

        try
        {
            await HandleCore(ctx, S, url);
        }
        finally
        {
            if (audited)
            {
                var ok = res.StatusCode is >= 200 and < 400;
                var meta = DescribeAdminWrite(url);
                AuditLog.Write(meta.Action, meta.Target, meta.Label, ok,
                    ok ? null : $"HTTP {res.StatusCode}");
            }
            scope?.Dispose();
        }
    }

    /// <summary>由 Core（AdminService）自行记账的写接口，路由层不再重复记一遍。</summary>
    private static readonly HashSet<string> CoreAuditedPaths = new(StringComparer.Ordinal)
    {
        "/api/admin/config",
        "/api/admin/diseases-import",
        "/api/admin/lixiang-import",
        "/api/admin/jiedu-import",
        "/api/admin/mapping-import",
        "/api/admin/zonghe-import",
        "/api/admin/zonghe-import-empty",
        "/api/admin/dezhou-import",
    };

    /// <summary>后台写接口 → （动作, 对象, 描述）</summary>
    private static (string Action, string Target, string Label) DescribeAdminWrite(string url) => url switch
    {
        "/api/admin/settings-app" => (AuditAction.Setting, "系统设置", "修改软件名称 / 单位副名"),
        "/api/admin/settings-login" => (AuditAction.Setting, "系统设置", "修改后台账号与登录口令"),
        "/api/admin/settings-lixiang" => (AuditAction.Setting, "系统设置", "修改立项指南参数表"),
        "/api/admin/settings-share" => (AuditAction.Setting, "系统设置", "修改数据分发共享口令"),
        "/api/admin/notice-save" => (AuditAction.Save, "通知公告", "保存通知公告"),
        "/api/admin/notice-toggle" => (AuditAction.Edit, "通知公告", "启用 / 停用通知"),
        "/api/admin/notice-delete" => (AuditAction.Delete, "通知公告", "删除通知"),
        "/api/admin/notice-clear" => (AuditAction.Delete, "通知公告", "清空通知公告"),
        "/api/admin/jiedu-delete" => (AuditAction.Delete, "立项指南", "删除解读辅导条目"),
        "/api/admin/lixiang-guide-usage" => (AuditAction.Edit, "立项指南", "保存类别级使用说明"),
        "/api/admin/lixiang-add" => (AuditAction.Add, "立项指南", "新增立项指南项目"),
        "/api/admin/lixiang-update" => (AuditAction.Edit, "立项指南", "编辑立项指南项目"),
        "/api/admin/lixiang-delete" => (AuditAction.Delete, "立项指南", "删除立项指南项目"),
        "/api/admin/mapping-gray" => (AuditAction.Edit, "映射与灰码", "切换灰码标记"),
        "/api/admin/mapping-add" => (AuditAction.Add, "映射与灰码", "新增医保映射"),
        "/api/admin/mapping-update" => (AuditAction.Edit, "映射与灰码", "编辑医保映射"),
        "/api/admin/mapping-delete" => (AuditAction.Delete, "映射与灰码", "删除医保映射"),
        "/api/admin/zonghe-add" => (AuditAction.Add, "病种库", "新增综合病组规则"),
        "/api/admin/zonghe-update" => (AuditAction.Edit, "病种库", "编辑综合病组规则"),
        "/api/admin/zonghe-delete" => (AuditAction.Delete, "病种库", "删除综合病组规则"),
        "/api/admin/dezhou-add" => (AuditAction.Add, "德州物价", "新增物价项目"),
        "/api/admin/dezhou-update" => (AuditAction.Edit, "德州物价", "编辑物价项目"),
        "/api/admin/dezhou-delete" => (AuditAction.Delete, "德州物价", "删除物价项目"),
        "/api/admin/dezhou-usage" => (AuditAction.Edit, "德州物价", "编辑类别使用说明"),
        "/api/admin/diseases-add" => (AuditAction.Add, "病种库", "新增病种库记录"),
        "/api/admin/diseases-update" => (AuditAction.Edit, "病种库", "编辑病种库记录"),
        "/api/admin/diseases-delete" => (AuditAction.Delete, "病种库", "删除病种库记录"),
        _ => ("操作", "后台", url),
    };

    private static async Task HandleCore(HttpContext ctx, AppState S, string url)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            // ==================== 后台写操作鉴权 ====================
            // 浏览、查询、模板下载、导出、前台分组一律免登录；只有「会改数据」的接口要求已登录
            // （清单见 NeedsAdminAuth）。关闭「后台登录校验」时整体放行，与桌面端语义一致。
            if (NeedsAdminAuth(req.Method, url) && !AdminAllowed(ctx))
            {
                await WriteJson(res, 401, new { error = "请先登录后再修改数据", needLogin = true });
                return;
            }

            // ==================== 静态页 ====================
            if (req.Method == "GET" && (url == "/" || url == "/index.html")) { await SendWebFile(res, "index.html"); return; }
            if (req.Method == "GET" && (url == "/admin" || url == "/admin.html")) { await SendWebFile(res, "admin.html"); return; }

            // ==================== 公开设置 ====================
            // 免鉴权：前台页面靠它渲染标题与品牌；桌面客户端靠它「跟随服务端公共设置」。
            // 只回展示类配置，**不含**后台口令、主题等本机私有项（见 PublicSettingsSnapshot）。
            if (req.Method == "GET" && url == "/api/settings/public")
            {
                await WriteJson(res, 200, new
                {
                    appName = SettingsStore.Current.AppName ?? SettingsStore.DefaultAppName,
                    appSubName = SettingsStore.Current.AppSubName ?? "",
                    logoText = SettingsStore.LogoText,
                    lixiangCategories = SettingsStore.Current.LixiangCategories ?? new List<string>(),
                    lixiangUnits = SettingsStore.Current.LixiangUnits ?? new List<string>(),
                });
                return;
            }

            // ==================== 登录会话 ====================
            // 会话状态：前端据此决定要不要显示「登录」入口
            if (req.Method == "GET" && url == "/api/admin/session")
            {
                await WriteJson(res, 200, new
                {
                    loginEnabled = SettingsStore.LoginEnabled,
                    authenticated = AdminAllowed(ctx),
                    user = SettingsStore.Current.AdminUser ?? SettingsStore.DefaultUser,
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/login")
            {
                await HandleLogin(ctx, res);
                return;
            }

            // 退出登录：未登录时也允许调用（幂等，就当没发生）
            if (req.Method == "POST" && url == "/api/admin/logout")
            {
                WebSession.Drop(AdminToken(req));
                ClearAdminCookie(res);
                await WriteJson(res, 200, new { ok = true });
                return;
            }

            // ==================== 系统设置 ====================
            if (req.Method == "GET" && url == "/api/admin/settings")
            {
                var st = SettingsStore.Current;
                await WriteJson(res, 200, new
                {
                    appName = st.AppName ?? SettingsStore.DefaultAppName,
                    appSubName = st.AppSubName ?? "",
                    loginEnabled = SettingsStore.LoginEnabled,
                    adminUser = st.AdminUser ?? SettingsStore.DefaultUser,
                    defaultUser = SettingsStore.DefaultUser,
                    isDefaultPassword = SettingsStore.IsDefaultPassword,
                    lixiangCategories = st.LixiangCategories ?? new List<string>(),
                    lixiangUnits = st.LixiangUnits ?? new List<string>(),
                    // 参数表留空时前端拿这两个值预填，方便在「现有数据」基础上增删
                    // （与桌面端 AdminSettingsView.LoadParams 的语义完全一致）
                    categoriesInUse = S.Lixiang.DistinctCategories(),
                    unitsInUse = S.Lixiang.DistinctUnits(),
                    // 数据分发共享口令（保护 /api/data/* 与 /api/settings/public）：
                    // 这是「防院内其他机器随手拉数据」的共享口令，不是后台口令，
                    // 所以明文回给已登录的管理员，方便对照客户端里填的是不是同一个。
                    shareToken = st.DataShareToken ?? "",
                    hasShareToken = !string.IsNullOrWhiteSpace(st.DataShareToken),
                    updatedAt = st.UpdatedAt ?? "",
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/settings-app")
            {
                var body = await ReadBodyJson(req);
                var name = Str(body, "appName").Trim();
                SettingsStore.Current.AppName = name.Length == 0 ? SettingsStore.DefaultAppName : name;
                SettingsStore.SetSubName(Str(body, "appSubName"));
                SettingsStore.Save();
                await WriteJson(res, 200, new
                {
                    ok = true,
                    appName = SettingsStore.Current.AppName,
                    appSubName = SettingsStore.Current.AppSubName ?? "",
                    logoText = SettingsStore.LogoText,
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/settings-login")
            {
                var body = await ReadBodyJson(req);
                var user = Str(body, "user").Trim();
                var p1 = Str(body, "password");
                var p2 = Str(body, "password2");
                var on = Bool(body, "loginEnabled");

                if (p1.Length > 0 && p1.Length < 6)
                {
                    await WriteJson(res, 400, new { error = "新口令至少 6 位" });
                    return;
                }
                if (p1.Length > 0 && p2.Length > 0 && !string.Equals(p1, p2, StringComparison.Ordinal))
                {
                    await WriteJson(res, 400, new { error = "两次输入的口令不一致" });
                    return;
                }
                // 开着登录校验时，口令必须真能通过校验，防止把账号改成一个连自己都登不进去的名字
                if (on && !SettingsStore.IsDefaultPassword && p1.Length == 0 && user.Length == 0)
                {
                    await WriteJson(res, 400, new { error = "请填写登录账号" });
                    return;
                }

                var passChanged = p1.Length > 0;
                SettingsStore.Current.LoginEnabled = on;
                SettingsStore.SetUser(user);
                if (passChanged) SettingsStore.SetPassword(p1);
                SettingsStore.Save();

                if (!on) WebSession.Clear();                 // 关掉校验：已签发会话不再有意义
                else if (passChanged) WebSession.KeepOnly(AdminToken(req));  // 改口令：本人留在原地，其他人掉线

                await WriteJson(res, 200, new
                {
                    ok = true,
                    loginEnabled = SettingsStore.LoginEnabled,
                    adminUser = SettingsStore.Current.AdminUser,
                    isDefaultPassword = SettingsStore.IsDefaultPassword,
                    passwordChanged = passChanged,
                    sessions = WebSession.ActiveCount,
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/settings-lixiang")
            {
                var body = await ReadBodyJson(req);
                SettingsStore.Current.LixiangCategories = SettingsStore.CleanList(StrList(body, "categories"));
                SettingsStore.Current.LixiangUnits = SettingsStore.CleanList(StrList(body, "units"));
                SettingsStore.Save();
                await WriteJson(res, 200, new
                {
                    ok = true,
                    categories = SettingsStore.Current.LixiangCategories ?? new List<string>(),
                    units = SettingsStore.Current.LixiangUnits ?? new List<string>(),
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/settings-share")
            {
                var body = await ReadBodyJson(req);
                var token = Str(body, "token").Trim();
                SettingsStore.SetShareToken(token);
                await WriteJson(res, 200, new
                {
                    ok = true,
                    shareToken = SettingsStore.Current.DataShareToken ?? "",
                    hasShareToken = !string.IsNullOrWhiteSpace(SettingsStore.Current.DataShareToken),
                });
                return;
            }

            // ==================== 前台消息推送 ====================
            // 运营闭环：后台（/admin → 消息推送）编辑发布，前台（/）轮询取回。
            // /api/notice 免登录 —— 前台页面本来就不需要登录；只回「已发布且在生效期内」的，
            // 草稿、生效排期与发布者不外泄（见 NoticeStore.ActiveForFront）。
            if (req.Method == "GET" && url == "/api/notice")
            {
                var now = DateTime.Now;
                await WriteJson(res, 200, new
                {
                    rev = S.Notice.Rev,
                    serverTime = now.ToString(NoticeStore.TimeFormat),
                    items = S.Notice.ActiveForFront(now),
                });
                return;
            }

            // 后台：全部消息（含草稿）+ 统计。**需要登录** —— 草稿属于未公开的运营信息。
            if (req.Method == "GET" && url == "/api/admin/notices")
            {
                await WriteJson(res, 200, new
                {
                    rev = S.Notice.Rev,
                    now = DateTime.Now.ToString(NoticeStore.TimeFormat),
                    stats = S.Notice.Stats(),
                    items = S.Notice.All(),
                });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/notice-save")
            {
                var body = await ReadBodyJson(req);
                // enabled 缺省视为「发布」，避免前端漏传字段时把消息悄悄存成草稿
                var enabled = !body.ContainsKey("enabled") || Bool(body, "enabled");
                var (err, item) = S.Notice.Upsert(
                    Str(body, "id"), Str(body, "title"), Str(body, "body"), Str(body, "level"),
                    Bool(body, "pinned"), Str(body, "startAt"), Str(body, "endAt"), enabled,
                    SettingsStore.Current.AdminUser ?? SettingsStore.DefaultUser);
                if (err != null) { await WriteJson(res, 400, new { error = err }); return; }
                await WriteJson(res, 200, new { ok = true, item, stats = S.Notice.Stats() });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/notice-toggle")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Notice.SetEnabled(Str(body, "id"), Bool(body, "enabled"));
                await WriteJson(res, 200, ok ? new { ok = true, stats = S.Notice.Stats() } : (object)new { error = "消息不存在" });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/notice-delete")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Notice.Remove(Str(body, "id"));
                await WriteJson(res, 200, ok ? new { ok = true, stats = S.Notice.Stats() } : (object)new { error = "消息不存在" });
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/notice-clear")
            {
                await WriteJson(res, 200, new { ok = true, removed = S.Notice.ClearDrafts(), stats = S.Notice.Stats() });
                return;
            }

            // ==================== 后台状态 ====================
            if (req.Method == "GET" && url == "/api/admin/status")
            {
                await WriteJson(res, 200, new
                {
                    config = MappingStore.LoadConfigMeta(),
                    mapping = S.Mapping.Stats(),
                    zonghe = S.Zonghe.Stats(),
                    lixiang = LixiangSummary(S),
                    dezhou = DezhouSummary(S),
                });
                return;
            }

            // ==================== 病种库导入 ====================
            if (req.Method == "POST" && url == "/api/admin/config")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var file = mp.Files[0];
                try
                {
                    var meta = AdminService.ImportConfig(S, file.Content, file.FileName);
                    await WriteJson(res, 200, new { ok = true, meta });
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"病种库导入失败: {e.Message}" });
                }
                return;
            }

            // ==================== 解读辅导 ====================
            if (req.Method == "GET" && url == "/api/lixiang/jiedu")
            {
                await WriteJson(res, 200, S.Lixiang.JieduList(Q(req, "issue"), Q(req, "q")));
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/jiedu-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "merge" ? "merge" : "replace";
                var f = mp.Files[0];
                try
                {
                    var (preface, list) = AdminService.ParseJieduDocument(f.Content, f.FileName);
                    if (list.Count == 0) { await WriteJson(res, 400, new { error = "未解析到问答条目，请确认文件为解读辅导文档" }); return; }
                    var result = S.Lixiang.JieduImport(preface, list, mode, f.FileName);
                    await WriteJson(res, 200, Concat(new { ok = true, mode }, result));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"解读导入失败: {e.Message}" });
                }
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/jiedu-delete")
            {
                var body = await ReadBodyJson(req);
                await WriteJson(res, 200, Concat(new { ok = true }, S.Lixiang.JieduRemoveIssue(Str(body, "issue"))));
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/lixiang-guide-usage")
            {
                var body = await ReadBodyJson(req);
                try
                {
                    await WriteJson(res, 200, S.Lixiang.SetGuideUsage(Str(body, "tab"), Str(body, "usage")));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                return;
            }

            // ==================== 映射表 ====================
            if (req.Method == "POST" && url == "/api/admin/mapping-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "append" ? "append" : "replace";
                try
                {
                    var r = AdminService.ImportMapping(S, mp.Files[0].Content, mp.Files[0].FileName, mode);
                    await WriteJson(res, 200, new { ok = true, mode, parsed = r.Parsed, added = r.Added, updated = r.Updated, total = r.Total, stats = r.Stats });
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"映射表导入失败: {e.Message}" });
                }
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/mapping")
            {
                var grayParam = Q(req, "gray");
                bool? gray = grayParam == "1" ? true : grayParam == "0" ? false : null;
                var page = ParseIntJs(Q(req, "page"), 1);
                var pageSize = Math.Min(ParseIntJs(Q(req, "pageSize"), 50), 200);
                var r = S.Mapping.List(Q(req, "q"), gray, page, pageSize);
                await WriteJson(res, 200, new { total = r.Total, page = r.Page, pageSize = r.PageSize, rows = r.Rows });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/mapping-gray")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Mapping.SetGray(ParseIntJs(Str(body, "id"), 0), Bool(body, "gray"));
                await WriteJson(res, 200, ok ? new { ok = true, stats = S.Mapping.Stats() } : (object)new { error = "记录不存在" });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/mapping-add")
            {
                var body = await ReadBodyJson(req);
                var (err, id) = S.Mapping.Add(Root(body));
                if (err != null)
                {
                    await WriteJson(res, 200, id > 0 ? new { error = err, id } : (object)new { error = err });
                    return;
                }
                await WriteJson(res, 200, new { ok = true, id, stats = S.Mapping.Stats() });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/mapping-update")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Mapping.Update(ParseIntJs(Str(body, "id"), 0), Root(body));
                await WriteJson(res, 200, ok ? new { ok = true } : (object)new { error = "记录不存在" });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/mapping-delete")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Mapping.Remove(ParseIntJs(Str(body, "id"), 0));
                await WriteJson(res, 200, ok ? new { ok = true, stats = S.Mapping.Stats() } : (object)new { error = "记录不存在" });
                return;
            }

            // ==================== 联想 ====================
            if (req.Method == "GET" && url == "/api/suggest-diag")
            {
                var limit = Math.Min(ParseIntJs(Q(req, "limit"), 10), 100);
                var q = Q(req, "q");
                var items = GrouperService.SuggestDiag(S, q, limit)
                    .Select(r => new
                    {
                        code = r.Code,
                        name = r.Name,
                        insCode = r.InsCode,
                        insName = r.InsName,
                        gray = r.Gray,
                        dip = r.Dip,
                    }).ToList();
                var qTrim = (q ?? "").Trim();
                if (qTrim.Any(ch => ch > 0x7F))
                    await WriteJson(res, 200, new { items, total = GrouperService.SuggestDiagTotal(S, q) });
                else
                    await WriteJson(res, 200, new { items });
                return;
            }
            if (req.Method == "GET" && url == "/api/suggest-proc")
            {
                var limit = Math.Min(ParseIntJs(Q(req, "limit"), 10), 100);
                var items = GrouperService.SuggestProc(S, Trim(Q(req, "q")), limit)
                    .Select(r => new { code = r.Code, name = r.Name }).ToList();
                await WriteJson(res, 200, new { items });
                return;
            }
            if (req.Method == "GET" && url == "/api/suggest-linked")
            {
                var linked = GrouperService.SuggestLinked(S, Trim(Q(req, "mainDiag")));
                await WriteJson(res, 200, new
                {
                    mainDiag = linked.MainDiag,
                    otherDiags = linked.OtherDiags.Select(x => new
                    {
                        code = x.Code,
                        name = x.Name,
                        insCode = x.InsCode,
                        insName = x.InsName,
                        gray = x.Gray,
                        linked = x.Linked,
                    }).ToList(),
                    procs = linked.Procs.Select(x => new { code = x.Code, name = x.Name, linked = x.Linked }).ToList(),
                });
                return;
            }
            if (req.Method == "GET" && url == "/api/translate")
            {
                await WriteJson(res, 200, MappingStore.TranslateToDict(S.Mapping.Translate(Q(req, "code"))));
                return;
            }

            // ==================== 综合病组 ====================
            if (req.Method == "GET" && url == "/api/admin/zonghe")
            {
                await WriteJson(res, 200, S.Zonghe.List(Q(req, "q"),
                    ParseIntJs(Q(req, "page"), 1), Math.Min(ParseIntJs(Q(req, "pageSize"), 50), 200)));
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/zonghe-add")
            {
                var body = await ReadBodyJson(req);
                var (err, id) = S.Zonghe.Add(Root(body));
                if (err != null) { await WriteJson(res, 200, new { error = err }); return; }
                S.Grouper.SetZonghe(S.Zonghe.Rows);
                await WriteJson(res, 200, new { ok = true, id, stats = S.Zonghe.Stats() });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/zonghe-update")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Zonghe.Update(ParseIntJs(Str(body, "id"), 0), Root(body));
                if (ok) S.Grouper.SetZonghe(S.Zonghe.Rows);
                await WriteJson(res, 200, ok ? new { ok = true } : (object)new { error = "更新失败（记录不存在或范围非法）" });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/zonghe-delete")
            {
                var body = await ReadBodyJson(req);
                var ok = S.Zonghe.Remove(ParseIntJs(Str(body, "id"), 0));
                if (ok) S.Grouper.SetZonghe(S.Zonghe.Rows);
                await WriteJson(res, 200, ok ? new { ok = true, stats = S.Zonghe.Stats() } : (object)new { error = "记录不存在" });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/zonghe-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "append" ? "append" : "replace";
                try
                {
                    var r = AdminService.ImportZonghe(S, mp.Files[0].Content, mp.Files[0].FileName, mode);
                    await WriteJson(res, 200, Concat(new { ok = true, mode }, r, new { stats = S.Zonghe.Stats() }));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"综合病组导入失败: {e.Message}" });
                }
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/zonghe-import-empty")
            {
                S.Zonghe.Clear();
                S.Grouper.SetZonghe(S.Zonghe.Rows);
                await WriteJson(res, 200, new { ok = true, stats = S.Zonghe.Stats() });
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/zonghe-template")
            {
                await SendBytes(res, AdminService.ZongheTemplate(), "zonghe-template.xlsx");
                return;
            }

            // ==================== 德州市物价（后台管理） ====================
            // 与桌面端 AdminDezhouView 共用 Core 的 DezhouPriceStore / AdminService，规则只有一份。
            if (req.Method == "GET" && url == "/api/admin/dezhou")
            {
                var r = S.Dezhou.ListRows(Q(req, "doc"), Q(req, "category"), Q(req, "kind"), Q(req, "q"),
                    ParseIntJs(Q(req, "page"), 1), ParseIntJs(Q(req, "pageSize"), 50), Q(req, "insurance"),
                    null, Q(req, "level"));
                await WriteJson(res, 200, new
                {
                    label = r.Label,
                    columns = DezhouPriceStore.Columns,
                    keyField = "id",
                    total = r.Total,
                    page = r.Page,
                    pageSize = r.PageSize,
                    rows = r.Items,
                });
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/dezhou-batches")
            {
                var batches = S.Dezhou.Batches().Select(b => new
                {
                    doc = b.Doc,
                    exec = b.Exec,
                    prov = b.Prov,
                    scope = b.Scope,
                    expect = b.Expect,
                    rows = b.Rows,
                    mains = b.Mains,
                    adds = b.Adds,
                    exts = b.Exts,
                    categories = (b.Categories ?? new List<DezhouCategory>()).Select(c => new
                    {
                        name = c.Name,
                        items = c.Items,
                        main = c.Main,
                        add = c.Add,
                        ext = c.Ext,
                        usage = c.Usage ?? new List<string>(),
                    }).ToList(),
                }).ToList();
                await WriteJson(res, 200, new { batches, docs = S.Dezhou.DistinctDocs() });
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/dezhou-template")
            {
                var (bytes, fname) = AdminService.DezhouTemplate();
                await SendBytes(res, bytes, fname);
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/dezhou-export")
            {
                var (bytes, fname) = AdminService.ExportDezhou(S, Q(req, "doc"), Q(req, "category"),
                    Q(req, "kind"), Q(req, "q"));
                await SendBytes(res, bytes, fname);
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/dezhou-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                // replace + 指定文号 = 覆盖该批；replace + 空文号 = 清空整库后导入；append = 按 (文号, 编码) 合并
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "append" ? "append" : "replace";
                var docField = mp.Fields.TryGetValue("doc", out var df) ? df : null;
                try
                {
                    var r = AdminService.ImportDezhou(S, mp.Files[0].Content, mp.Files[0].FileName, docField, mode);
                    await WriteJson(res, 200, new
                    {
                        ok = true,
                        mode,
                        parsed = r.Parsed,
                        added = r.Added,
                        updated = r.Updated,
                        skipped = r.Skipped,
                        total = r.Total,
                        stats = DezhouSummary(S),
                    });
                }
                catch (AdminInputException e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                catch (Exception e)
                {
                    // NPOI 对非 Excel 文件抛的是英文底层异常，这里换成能看懂的提示
                    var m = e.Message.Contains("OLE2") || e.Message.Contains("OOXML")
                        ? "文件不是有效的 Excel（请上传 .xlsx / .xls）"
                        : e.Message;
                    await WriteJson(res, 400, new { error = $"德州物价导入失败: {m}" });
                }
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/dezhou-add")
            {
                var body = await ReadBodyJson(req);
                var r = S.Dezhou.AddRow(FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Dezhou.SaveWithRecompute();
                await WriteJson(res, 200, new { ok = true, key = r.Row, stats = DezhouSummary(S) });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/dezhou-update")
            {
                var body = await ReadBodyJson(req);
                var r = S.Dezhou.UpdateRow(Str(body, "id"), FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Dezhou.SaveWithRecompute();
                await WriteJson(res, 200, new { ok = true, stats = DezhouSummary(S) });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/dezhou-delete")
            {
                var body = await ReadBodyJson(req);
                var r = S.Dezhou.RemoveRow(Str(body, "id"));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Dezhou.SaveWithRecompute();
                await WriteJson(res, 200, new { ok = true, stats = DezhouSummary(S) });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/dezhou-usage")
            {
                var body = await ReadBodyJson(req);
                var lines = (Str(body, "usage") ?? "").Replace("\r\n", "\n").Split('\n');
                var n = S.Dezhou.SetUsage(Str(body, "doc"), Str(body, "category"), lines);
                await WriteJson(res, 200, new { ok = true, length = n, stats = DezhouSummary(S) });
                return;
            }

            // ==================== 病种库列表 ====================
            if (req.Method == "GET" && url == "/api/admin/diseases")
            {
                try
                {
                    await WriteJson(res, 200, Diseases.ListRows(S.Config, Q(req, "tab") is { Length: > 0 } t ? t : "bingxiang",
                        Q(req, "q"), ParseIntJs(Q(req, "page"), 1), ParseIntJs(Q(req, "pageSize"), 50)));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/diseases-counts")
            {
                await WriteJson(res, 200, new { tabs = Diseases.SectionCounts(S.Config) });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/diseases-add")
            {
                var body = await ReadBodyJson(req);
                var r = Diseases.AddRow(S.Config, Str(body, "tab"), FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                Diseases.SaveConfig(S.Config);
                var tab = Str(body, "tab");
                if (tab is "excludedDiags" or "excludedProcs") S.Grouper.RefreshExclusions();
                if (tab is "outlib") S.Grouper.RefreshOutLib();   // 清单增删改后预警索引要重建
                await WriteJson(res, 200, new { ok = true, key = r.Row, counts = Diseases.SectionCounts(S.Config) });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/diseases-update")
            {
                var body = await ReadBodyJson(req);
                var tab = Str(body, "tab");
                var r = Diseases.UpdateRow(S.Config, tab, Str(body, "key"), FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                Diseases.SaveConfig(S.Config);
                if (tab is "excludedDiags" or "excludedProcs") S.Grouper.RefreshExclusions();
                if (tab is "outlib") S.Grouper.RefreshOutLib();   // 清单增删改后预警索引要重建
                await WriteJson(res, 200, new { ok = true });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/diseases-delete")
            {
                var body = await ReadBodyJson(req);
                var tab = Str(body, "tab");
                var r = Diseases.RemoveRow(S.Config, tab, Str(body, "key"));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                Diseases.SaveConfig(S.Config);
                if (tab is "excludedDiags" or "excludedProcs") S.Grouper.RefreshExclusions();
                if (tab is "outlib") S.Grouper.RefreshOutLib();   // 清单增删改后预警索引要重建
                await WriteJson(res, 200, new { ok = true, counts = Diseases.SectionCounts(S.Config) });
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/diseases-template")
            {
                var (bytes, fname) = AdminService.DiseasesTemplate(Q(req, "tab"));
                await SendBytes(res, bytes, fname);
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/diseases-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "upsert" ? "upsert" : "replace";
                var tabField = mp.Fields.TryGetValue("tab", out var tf) ? tf : null;
                try
                {
                    var r = AdminService.ImportDiseases(S, mp.Files[0].Content, mp.Files[0].FileName, tabField, mode);
                    await WriteJson(res, 200, new { ok = true, mode, report = r.Report, counts = r.Counts });
                }
                catch (AdminInputException e)
                {
                    // 输入校验类错误：文案直接回给前端（与 Node 版逐字一致，不加前缀）
                    await WriteJson(res, 400, new { error = e.Message });
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"病种库结构导入失败: {e.Message}" });
                }
                return;
            }

            // ==================== 立项指南 ====================
            if (req.Method == "GET" && url == "/api/lixiang/sections")
            {
                // categories 供前台「类别」下拉（与桌面端同一个提取口径：留空的按区段名呈现）
                await WriteJson(res, 200, new
                {
                    tabs = S.Lixiang.SectionCounts(),
                    categories = S.Lixiang.DistinctCategories(),
                });
                return;
            }
            if (req.Method == "GET" && url == "/api/lixiang/items")
            {
                try
                {
                    await WriteJson(res, 200, S.Lixiang.ListRows(Q(req, "tab"), Q(req, "q"),
                        ParseIntJs(Q(req, "page"), 1), ParseIntJs(Q(req, "pageSize"), 50),
                        Q(req, "category"), Q(req, "flag")));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                return;
            }
            // ==================== 前台只读：病种库 / 德州物价 / 技术规范 ====================
            // 网页端这三页原来直接打 /api/admin/*：后台一开登录，前台就 401；而且等于让浏览器
            // 拿「管理接口」当查询接口用。这里补一组只读接口，口径与后台 / 桌面端共用同一份
            // Core 方法（不存在两套过滤规则），可见性沿用「数据分发共享口令」，与 /api/data/* 一致：
            // 后台设了口令就要带 ?token= 或 X-DIP3-Token，没设则局域网内可读。
            if (IsPublicDataPath(url) && !DataAccessAllowed(req))
            {
                await Unauthorized(res);
                return;
            }

            if (req.Method == "GET" && url == "/api/diseases-counts")
            {
                // start = 该区段首行在「累计序号」里的位置：桌面端在表头上写「本区段从 N 起」，
                // 网页端要同一句话，就必须知道前面各区段各多少行。
                var cum = 0;
                var tabs = Diseases.Sections.Select(s =>
                {
                    var total = s.GetRows(S.Config).Count;
                    var start = cum + 1;
                    cum += total;
                    return new { tab = s.Tab, label = s.Label, total, start };
                }).ToList();
                await WriteJson(res, 200, new { tabs });
                return;
            }
            if (req.Method == "GET" && url == "/api/diseases")
            {
                try
                {
                    await WriteJson(res, 200, Diseases.ListRows(S.Config,
                        Q(req, "tab") is { Length: > 0 } t ? t : "bingxiang",
                        Q(req, "q"), ParseIntJs(Q(req, "page"), 1), ParseIntJs(Q(req, "pageSize"), 50)));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                return;
            }

            if (req.Method == "GET" && url == "/api/dezhou-meta")
            {
                var st = S.Dezhou.Stat();
                // 四个视图各有一套统计口径（桌面端 UpdateStat 按视图切换文案）。网页端如果只给「现行项目」
                // 那一份，切到作废对照时统计条还写着「共 3,880 条」，跟表里的 5,107 行当场打架。
                // 比对口径会影响降 / 涨 / 平，所以带 level 参数，网页端切口径时重取一次。
                var lv = Q(req, "level"); if (lv.Length == 0) lv = "3";
                var cs = S.Dezhou.CancelStat();
                var ls = S.Dezhou.LegacyStat();
                var mp = S.Dezhou.CompareStat(lv);
                await WriteJson(res, 200, new
                {
                    docs = S.Dezhou.DistinctDocs(),
                    categories = S.Dezhou.Categories(""),
                    legacyCategories = S.Dezhou.LegacyCategories(),
                    cancelCategories = S.Dezhou.CancelCategories(""),
                    stat = new
                    {
                        total = st.Total, main = st.Main, add = st.Add, ext = st.Ext,
                        insured = st.Insured, batchCount = st.BatchCount, categoryCount = st.CategoryCount,
                    },
                    cancelStat = new
                    {
                        total = cs.Total, mapped = cs.Mapped, dead = cs.Dead,
                        batches = cs.Batches, categories = cs.Categories, kept = cs.Kept,
                    },
                    legacyStat = new
                    {
                        total = ls.Total, dead = ls.Dead, live = ls.Live, categories = ls.Categories,
                    },
                    cmpStat = new
                    {
                        total = mp.Total, priced = mp.Priced, cancelOnly = mp.CancelOnly, brandNew = mp.BrandNew,
                        down = mp.Down, up = mp.Up, flat = mp.Flat, level = lv,
                    },
                });
                return;
            }

            // 类别级「使用说明」：桌面端详情卡会摘录前两条并提示「本类共 N 条」，
            // 网页端原来没有这条数据，卡片里只有一行「计价说明」，两边读起来不是一回事。
            // 数据量小（一个类别几条），按 (文号, 类别) 单取，不进 dezhou-meta 那个大对象。
            if (req.Method == "GET" && url == "/api/dezhou-usage")
            {
                var lines = S.Dezhou.UsageOf(Q(req, "doc"), Q(req, "category"));
                await WriteJson(res, 200, new { lines });
                return;
            }

            // 跨库关联（立项指南 ↔ 德州物价 ↔ 技术规范）：算法在 Core 的 CrossLinker，
            // 与桌面端详情卡末尾那段「关联·立项指南」完全同源（同名直连优先，其次 Dice≥0.62 的 top3）。
            if (req.Method == "GET" && url == "/api/cross")
            {
                var lib = Q(req, "lib");
                var name = Q(req, "name").Trim();
                var groups = name.Length == 0
                    ? new List<CrossLinkGroup>()
                    : CrossLinker.For(S, lib, name);
                await WriteJson(res, 200, new { groups });
                return;
            }

            // 病理开具指引（桌面端右侧抽屉的同一份数据、同一套报价；价格档 2 = 二级 / 3 = 三级）
            if (req.Method == "GET" && url == "/api/zhijian")
            {
                var z = S.Zhijian;
                z.EnsureLoaded();

                var kind = Q(req, "kind").Trim();
                var sysName = Q(req, "system").Trim();
                var rows = z.Search(Q(req, "q"), sysName, kind);

                var levelId = Q(req, "level").Trim();
                var level = z.Level(levelId);

                var count = Math.Clamp(ParseIntJs(Q(req, "wax"), 1), 0, 60);
                var mode = Q(req, "mode").Trim();
                if (mode.Length == 0) mode = Q(req, "complex") is "1" or "true" ? ZhijianStore.ModeComplex : ZhijianStore.ModeNormal;
                var quote = z.Quote(count, mode, level.Id);

                var bags = Math.Clamp(ParseIntJs(Q(req, "bags"), 0), 0, 60);
                var pieces = Math.Clamp(ParseIntJs(Q(req, "pieces"), 1), 1, 60);
                var size = Math.Clamp(ParseIntJs(Q(req, "size"), 2), 0, 60);

                await WriteJson(res, 200, new
                {
                    meta = z.Meta,
                    basePrice = z.Base,
                    levels = z.Levels.Select(l => new
                    {
                        id = l.Id, name = l.Name, diag = l.DiagNet, diagRaw = l.Diag,
                        proc = l.Proc, procComplex = l.ProcComplex,
                        stain = l.Stain, stainCut = l.StainCutUnit,
                        cellProc = l.CellProc, ihc = l.Ihc, note = l.Note,
                    }),
                    level = new
                    {
                        level.Id, level.Name, diag = level.DiagNet, diagStep = level.DiagStep,
                        proc = level.Proc, procComplex = level.ProcComplex,
                        stain = level.Stain, stainCut = level.StainCutUnit,
                        cellProc = level.CellProc, cellBlock = level.CellBlock,
                        ihc = level.Ihc, ihcCut = level.IhcCutUnit,
                        consult = level.Consult, consultExtra = level.ConsultExtra, note = level.Note,
                    },
                    rules = z.Rules,
                    // 专项口径按档次取：前端只认 special[].lines，不再自己拼金额
                    special = z.Special.Select(x => new
                    {
                        title = x.Title, lines = x.LinesFor(level.Id), note = x.Note,
                    }),
                    systems = z.Systems.Select(x => new { name = x.Name, count = x.Items?.Count ?? 0 }),
                    rows = rows.Select(e => new
                    {
                        system = e.System, kind = e.Kind, wax = e.Wax,
                        sample = e.Sample, tier = e.Tier,
                    }),
                    total = z.Entries.Count,
                    quote = new
                    {
                        wax = quote.Count, count = quote.Count, mode = quote.Mode,
                        complex = quote.Complex, level = quote.LevelId, levelName = quote.LevelName,
                        proc = quote.Proc, diag = quote.Diag, stain = quote.Stain, total = quote.Total,
                        lines = quote.Lines.Select(l => new
                        {
                            name = l.Name, note = l.Note, qty = l.Qty, unit = l.Unit, amount = l.Amount,
                        }),
                    },
                    quick = new { bags, pieces, size, wax = ZhijianStore.WaxCount(bags, pieces, size) },
                });
                return;
            }

            // 基本物耗指引（桌面端同一份 data/wuhao.json）：清单 / 边界 / 条款一次给全，
            // 前端只渲染不重算；?q= 是判定词，命中清单即「不可另收」
            if (req.Method == "GET" && url == "/api/wuhao")
            {
                var w = S.Wuhao;
                w.EnsureLoaded();
                var q = Q(req, "q").Trim();
                var v = w.Judge(q);
                await WriteJson(res, 200, new
                {
                    meta = w.Meta,
                    rules = w.Rules,
                    groups = w.Groups.Select(g => new
                    {
                        id = g.Id, name = g.Name, desc = g.Desc,
                        count = w.Items.Count(i => i.Group == g.Id),
                    }),
                    items = w.Items.Select(i => new
                    {
                        id = i.Id, name = i.Name, group = i.Group, groupName = w.GroupName(i.Group),
                        alias = i.Alias, cats = i.Cats, count = i.Count, note = i.Note,
                        catText = i.CatText,
                    }),
                    edges = w.Edges,
                    sources = w.Sources.Select(x => new { cat = x.Cat, key = x.Key, clause = x.Clause }),
                    total = w.Items.Count,
                    verdict = new
                    {
                        tone = v.Tone, head = v.Head, sub = v.Sub, basis = v.Basis, note = v.Note,
                        item = v.Item == null ? null : new { name = v.Item.Name, cats = v.Item.Cats, note = v.Item.Note },
                        edge = v.Edge == null ? null : new { name = v.Edge.Name, verdict = v.Edge.Verdict },
                        near = v.Near.Select(n => new { name = n.Name, catText = n.CatText }),
                    },
                });
                return;
            }

            if (req.Method == "GET" && url == "/api/dezhou")
            {
                var view = Q(req, "view"); if (view.Length == 0) view = "live";
                var doc = Q(req, "doc"); var cat = Q(req, "category"); var kw = Q(req, "q").Trim();
                var page = Math.Max(1, ParseIntJs(Q(req, "page"), 1));
                // 上限 200 与 Core 的 ListRows 一致：这里若允许 500，接口只回 200 行而页码按 500 算，
                // 第 2 页会从第 501 条起跳，中间 300 条永远翻不到。
                var size = Math.Clamp(ParseIntJs(Q(req, "pageSize"), 50), 5, 200);
                var skip = (page - 1) * size;

                string label;
                int total;
                object rows;

                if (view == "cancel")
                {
                    IEnumerable<DezhouCancelItem> src = S.Dezhou.Cancels;
                    if (doc.Length > 0) src = src.Where(x => x.Doc == doc);
                    if (cat.Length > 0) src = src.Where(x => x.Category == cat);
                    if (kw.Length > 0)
                        src = src.Where(x => Has(x.OldCode, kw) || Has(x.OldName, kw) || Has(x.NewCode, kw) ||
                                             Has(x.NewName, kw) || Has(x.Note, kw));
                    var all = src.ToList();
                    label = "作废对照"; total = all.Count;
                    // keep = 通知废止表里划横线的保留项目（不作废），照旧列出来但带「保留」状态，
                    // 免得物价员按这张表把还在执行的项目误停用。
                    rows = all.Skip(skip).Take(size).Select(x => new
                    {
                        doc = x.Doc, exec = x.Exec, category = x.Category,
                        oldCode = x.OldCode, oldName = x.OldName,
                        newCode = x.NewCode, newName = x.NewName, note = x.Note,
                        pending = x.Pending, keep = x.Keep,
                        state = x.Keep ? "保留" : "作废",
                    }).ToList();
                }
                else if (view == "legacy")
                {
                    IEnumerable<DezhouLegacyItem> src = S.Dezhou.Legacy;
                    if (cat.Length > 0) src = src.Where(x => x.Category == cat);
                    if (kw.Length > 0)
                        src = src.Where(x => Has(x.Code, kw) || Has(x.Name, kw) || Has(x.Category, kw) ||
                                             Has(x.NatName, kw) || Has(x.NatCode, kw));
                    var all = src.ToList();
                    label = "旧版物价"; total = all.Count;
                    // 「状态 / 现行标准」不是存下来的，是拿作废对照反查出来的（桌面端也在 VM 里现算），
                    // 这里先在服务端算好再下发，网页端只负责显示，避免两边各写一套判定。
                    rows = all.Skip(skip).Take(size).Select(x =>
                    {
                        var dead = S.Dezhou.LegacyCanceled(x);
                        return new
                        {
                            code = x.Code, name = x.Name, category = x.Category, unit = x.Unit,
                            price3 = x.Price3, price2 = x.Price2, price1 = x.Price1,
                            insurance = x.Insurance, selfPay = x.SelfPay,
                            status = dead ? "已作废" : "现行", isDead = dead,
                            currentStd = dead ? S.Dezhou.LegacyCurrentStd(x) : "",
                            newMark = x.IsNew == "是" ? "新" : "",
                        };
                    }).ToList();
                }
                else if (view == "cmp")
                {
                    var all = S.Dezhou.ListCompare(cat, kw, Q(req, "scope"));
                    label = "新旧比对"; total = all.Count;
                    rows = all.Skip(skip).Take(size).ToList();
                }
                else
                {
                    // 「仅看收藏」：收藏存在浏览器的 localStorage 里，服务端不知道，所以网页端把编码列表带上来。
                    // 走 ListRows 的 filter 谓词（而不是先取一页再在前端筛）——否则分页总数与页码都会错。
                    HashSet<string> only = null;   // 本项目未开 nullable，写 ? 会招 CS8632 警告
                    var codesCsv = Q(req, "codes");
                    if (codesCsv.Length > 0)
                        only = new HashSet<string>(
                            codesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                            StringComparer.OrdinalIgnoreCase);
                    var r = S.Dezhou.ListRows(doc, cat, Q(req, "kind"), kw, page, size,
                        Q(req, "insurance"),
                        only == null ? null : it => only.Contains((it.Code ?? "").Trim()),
                        Q(req, "level"));
                    label = r.Label; total = r.Total; rows = r.Items;
                }

                await WriteJson(res, 200, new { label, total, page, pageSize = size, rows });
                return;
            }

            if (req.Method == "GET" && url == "/api/dezhou-export")
            {
                var view = Q(req, "view"); if (view.Length == 0) view = "live";
                var doc = Q(req, "doc"); var cat = Q(req, "category"); var kw = Q(req, "q").Trim();
                // 导出范围与桌面端 ExpScope 同三档：all 全部结果 / page 仅当前页 / fav 仅收藏
                var range = Q(req, "range"); if (range.Length == 0) range = "all";
                var pg = Math.Max(1, ParseIntJs(Q(req, "page"), 1));
                var pgSize = Math.Clamp(ParseIntJs(Q(req, "pageSize"), 50), 5, 200);
                // 收藏在浏览器里，编码列表由网页端带上（同 /api/dezhou 的 codes 参数）
                HashSet<string> only = null;   // 本项目未开 nullable，写 ? 会招 CS8632 警告
                var codesCsv = Q(req, "codes");
                if (codesCsv.Length > 0)
                    only = new HashSet<string>(
                        codesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);

                if (view == "cmp")
                {
                    var all = S.Dezhou.ListCompare(cat, kw, Q(req, "scope"));
                    if (all.Count == 0) { await WriteJson(res, 400, new { error = "当前条件下没有记录可导出" }); return; }
                    IEnumerable<DezhouDraftCompareRow> pick = all;
                    var cmpLabel = "全部比对结果";
                    if (range == "page")
                    {
                        pick = all.Skip((pg - 1) * pgSize).Take(pgSize);
                        cmpLabel = $"第{pg}页";
                    }
                    var (bytes, fname) = DezhouExport.Cmp(S, pick.ToList(), cmpLabel);
                    await SendBytes(res, bytes, fname);
                    return;
                }

                IEnumerable<DezhouPriceItem> seq = S.Dezhou.Items
                    .Where(i => DezhouPriceStore.Matches(i, doc, cat, Q(req, "kind"), kw,
                                                        Q(req, "insurance"), Q(req, "level")));
                if (only != null) seq = seq.Where(i => only.Contains((i.Code ?? "").Trim()));
                if (range == "page") seq = seq.Skip((pg - 1) * pgSize).Take(pgSize);
                var items = seq.ToList();
                var label = range == "page" ? $"第{pg}页" : range == "fav" ? "仅收藏" : "全部结果";
                if (items.Count == 0) { await WriteJson(res, 400, new { error = "当前条件下没有记录可导出" }); return; }
                var (b2, f2) = DezhouExport.Front(S, items, label);
                await SendBytes(res, b2, f2);
                return;
            }

            if (req.Method == "GET" && url == "/api/guifan-meta")
            {
                S.Guifan.EnsureLoaded();
                var st = S.Guifan.Stat();
                await WriteJson(res, 200, new
                {
                    categories = S.Guifan.Categories(),
                    chapters = S.Guifan.Chapters().Select(c => new { code = c.Code, name = c.Name, category = c.Category, total = c.Items }),
                    units = S.Guifan.UnitCounts().Select(kv => new { key = kv.Key, total = kv.Value }),
                    stat = new
                    {
                        total = st.Total, chapterCount = st.ChapterCount, categoryCount = st.CategoryCount,
                        withMaterial = st.WithMaterial, withScore = st.WithScore,
                    },
                });
                return;
            }
            if (req.Method == "GET" && url == "/api/guifan")
            {
                S.Guifan.EnsureLoaded();
                try
                {
                    await WriteJson(res, 200, S.Guifan.ListRows(Q(req, "category"), Q(req, "chapter"), Q(req, "q"),
                        ParseIntJs(Q(req, "page"), 1), ParseIntJs(Q(req, "pageSize"), 50),
                        Q(req, "unit"), Q(req, "mat")));
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = e.Message });
                }
                return;
            }

            if (req.Method == "POST" && url == "/api/admin/lixiang-import")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var mode = mp.Fields.TryGetValue("mode", out var mv) && mv == "upsert" ? "upsert" : "replace";
                var tabField = mp.Fields.TryGetValue("tab", out var tf) ? tf : null;
                try
                {
                    var r = AdminService.ImportLixiang(S, mp.Files[0].Content, mp.Files[0].FileName, tabField, mode);
                    await WriteJson(res, 200, new { ok = true, mode, report = r.Report, counts = r.Counts, total = r.Total });
                }
                catch (AdminInputException e)
                {
                    // 输入校验类错误：文案直接回给前端（与 Node 版逐字一致，不加前缀）
                    await WriteJson(res, 400, new { error = e.Message });
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = $"立项指南导入失败: {e.Message}" });
                }
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/lixiang-add")
            {
                var body = await ReadBodyJson(req);
                var r = S.Lixiang.AddRow(Str(body, "tab"), FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Lixiang.Save();
                await WriteJson(res, 200, new { ok = true, key = r.Row, counts = S.Lixiang.SectionCounts() });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/lixiang-update")
            {
                var body = await ReadBodyJson(req);
                var r = S.Lixiang.UpdateRow(Str(body, "tab"), Str(body, "key"), FlatRow(body));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Lixiang.Save();
                await WriteJson(res, 200, new { ok = true });
                return;
            }
            if (req.Method == "POST" && url == "/api/admin/lixiang-delete")
            {
                var body = await ReadBodyJson(req);
                var r = S.Lixiang.RemoveRow(Str(body, "tab"), Str(body, "key"));
                if (!r.Ok) { await WriteJson(res, 400, new { error = r.Error }); return; }
                S.Lixiang.Save();
                await WriteJson(res, 200, new { ok = true, counts = S.Lixiang.SectionCounts() });
                return;
            }
            if (req.Method == "GET" && url == "/api/admin/lixiang-template")
            {
                var onlyTab = Q(req, "tab");
                if (onlyTab is { Length: 0 }) onlyTab = null;
                var (bytes, fname) = AdminService.LixiangTemplate(S, onlyTab);
                res.StatusCode = 200;
                res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                res.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(fname)}\"";
                res.ContentLength = bytes.Length;
                await res.Body.WriteAsync(bytes);
                return;
            }

            // ==================== 单条 / 批量分组 ====================
            if (req.Method == "POST" && url == "/api/group")
            {
                var body = await ReadBodyJson(req);
                var caseObj = body.TryGetValue("case", out var ce) && ce.ValueKind == JsonValueKind.Object
                    ? ToDict(ce) : body;
                var kase = Dip3Grouper.ReadCase(caseObj);
                await WriteJson(res, 200, GrouperService.Group(S, kase, Bool(body, "useMapping")));
                return;
            }
            // 单条分组溯源：逐层判定过程（独立接口 —— /api/group 的返回体保持与 Node 版逐字节一致）
            if (req.Method == "POST" && url == "/api/group-trace")
            {
                var body = await ReadBodyJson(req);
                var caseObj = body.TryGetValue("case", out var ce) && ce.ValueKind == JsonValueKind.Object
                    ? ToDict(ce) : body;
                var kase = Dip3Grouper.ReadCase(caseObj);
                var r = GrouperService.Group(S, kase, Bool(body, "useMapping"), withTrace: true);
                await WriteJson(res, 200, new { result = r, trace = r.Trace });
                return;
            }
            if (req.Method == "GET" && url == "/api/batch-template")
            {
                await SendBytes(res, GrouperService.BuildBatchTemplate(), "dip3-batch-template.xlsx");
                return;
            }
            if (req.Method == "POST" && url == "/api/batch")
            {
                var body = await ReadBodyJson(req);
                var useMapping = Bool(body, "useMapping") && S.Mapping.Rows.Count > 0;
                var rawRows = body.TryGetValue("rows", out var re) && re.ValueKind == JsonValueKind.Array
                    ? re.EnumerateArray().Select(ToDict).ToList()
                    : new List<Dictionary<string, JsonElement>>();
                var br = GrouperService.Batch(S, rawRows, useMapping);
                var token = Token();
                S.Downloads[token] = br.Xlsx;
                await WriteJson(res, 200, new { rows = br.Rows, summary = br.Summary, token });
                return;
            }
            if (req.Method == "POST" && url == "/api/batch-file")
            {
                var mp = await ReadMultipart(req);
                if (mp == null) { await WriteJson(res, 400, new { error = "缺少 multipart boundary" }); return; }
                if (mp.Files.Count == 0) { await WriteJson(res, 400, new { error = "未收到文件" }); return; }
                var useMapping = mp.Fields.TryGetValue("useMapping", out var um) && (um == "1" || um == "true") && S.Mapping.Rows.Count > 0;
                List<Dictionary<string, JsonElement>> caseRows;
                try
                {
                    caseRows = GrouperService.ReadSheetRows(mp.Files[0].Content);
                }
                catch (Exception e)
                {
                    await WriteJson(res, 400, new { error = "文件解析失败: " + e.Message });
                    return;
                }
                var br = GrouperService.Batch(S, caseRows, useMapping);
                var token = Token();
                S.Downloads[token] = br.Xlsx;
                await WriteJson(res, 200, new { rows = br.Rows, summary = br.Summary, token, filename = mp.Files[0].FileName });
                return;
            }
            if (req.Method == "GET" && url.StartsWith("/api/download/", StringComparison.Ordinal))
            {
                var token = url["/api/download/".Length..];
                if (!S.Downloads.TryGetValue(token, out var buf))
                {
                    res.StatusCode = 404;
                    await res.WriteAsync("下载链接已失效，请重新分组");
                    return;
                }
                await SendBytes(res, buf, "dip3-result.xlsx");
                return;
            }

            // ==================== 局域网数据分发（只读） ====================
            // 供桌面客户端「数据更新」页拉取业务数据；只认 DataCatalog 白名单里的名字，
            // 本机私有文件（settings.json / dezhou-favorites.json）不在名单内，因此不可能被拉走。
            if (req.Method == "GET" && url == "/api/data/manifest")
            {
                if (!DataAccessAllowed(req)) { await Unauthorized(res); return; }
                await WriteJson(res, 200, DataCatalog.BuildManifest());
                return;
            }

            if (req.Method == "GET" && url.StartsWith("/api/data/file/", StringComparison.Ordinal))
            {
                if (!DataAccessAllowed(req)) { await Unauthorized(res); return; }
                var name = Uri.UnescapeDataString(url["/api/data/file/".Length..]);
                var path = DataCatalog.LocalPath(name);   // 名字不命中白名单即返回 null，天然无路径穿越
                if (path == null || !File.Exists(path))
                {
                    res.StatusCode = 404;
                    await res.WriteAsync("Not Found");
                    return;
                }
                res.StatusCode = 200;
                res.ContentType = "application/octet-stream";
                res.Headers["X-DIP3-Name"] = name;
                res.Headers["X-DIP3-Sha256"] = DataCatalog.Sha256(path) ?? "";
                await res.SendFileAsync(path);
                return;
            }

            // ==================== 客户端自动更新（只读） ====================
            // 桌面客户端「检查更新」拉这两个接口：清单给版本号/大小/哈希/说明，包给安装 zip。
            // 与数据分发同一套共享口令控制；未发布新版时 version 接口回一个空版本号而不是 404，
            // 客户端据此显示「已是最新」，不必把 404 翻译成异常。
            if (req.Method == "GET" && url == "/api/app/version")
            {
                if (!DataAccessAllowed(req)) { await Unauthorized(res); return; }
                var appInfo = AppUpdateClient.ServerInfo();
                await WriteJson(res, 200, appInfo ?? new AppVersionInfo { Version = "" });
                return;
            }

            if (req.Method == "GET" && url == "/api/app/package")
            {
                if (!DataAccessAllowed(req)) { await Unauthorized(res); return; }
                var pkg = AppUpdateClient.ServerPackage;
                if (!File.Exists(pkg))
                {
                    res.StatusCode = 404;
                    await res.WriteAsync("No Package");
                    return;
                }
                res.StatusCode = 200;
                res.ContentType = "application/octet-stream";
                res.Headers["X-DIP3-Sha256"] = DataCatalog.Sha256(pkg) ?? "";
                await res.SendFileAsync(pkg);
                return;
            }

            res.StatusCode = 404;
            res.ContentType = "text/plain; charset=utf-8";
            await res.WriteAsync("Not Found");
        }
        catch (Exception e)
        {
            if (!res.HasStarted)
            {
                res.StatusCode = 500;
                await WriteJson(res, 500, new { error = e.Message });
            }
        }
    }


    private static string Q(HttpRequest req, string key) => req.Query.TryGetValue(key, out var v) ? v.ToString() : "";

    /// <summary>前台只读数据接口（受共享口令保护，与 /api/data/* 同一套判定）。</summary>
    private static bool IsPublicDataPath(string url)
        => url is "/api/diseases" or "/api/diseases-counts" or "/api/dezhou" or "/api/dezhou-meta"
               or "/api/dezhou-usage" or "/api/dezhou-export" or "/api/cross"
               or "/api/guifan" or "/api/guifan-meta"
               or "/api/zhijian" or "/api/wuhao";

    /// <summary>SQL 里的 LIKE '%x%'：不区分大小写、任一侧为空即不命中。</summary>
    private static bool Has(string field, string needle)
        => !string.IsNullOrEmpty(field) && field.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 数据分发接口的共享口令校验：由 settings.json 的 <c>dataShareToken</c> 控制，
    /// 留空 = 不校验（院内随便谁都能拉）；填了就要求 <c>?token=</c> 或 <c>X-DIP3-Token</c> 头一致。
    /// </summary>
    private static bool DataAccessAllowed(HttpRequest req)
    {
        var need = (SettingsStore.Current.DataShareToken ?? "").Trim();
        if (need.Length == 0) return true;
        var got = Q(req, "token");
        if (got.Length == 0 && req.Headers.TryGetValue("X-DIP3-Token", out var v)) got = v.ToString();
        return string.Equals((got ?? "").Trim(), need, StringComparison.Ordinal);
    }

    private static async Task Unauthorized(HttpResponse res)
    {
        res.StatusCode = 401;
        res.ContentType = "text/plain; charset=utf-8";
        await res.WriteAsync("共享口令不正确");
    }

    // ==================================================================
    // 后台登录鉴权
    // ==================================================================

    /// <summary>
    /// 哪些后台接口需要登录。原则：**只守会改数据的**。
    /// <list type="bullet">
    ///   <item>POST /api/admin/*（导入、增删改、灰码标注…）→ 需要</item>
    ///   <item>GET /api/admin/settings（含「口令是否默认值」等状态）→ 需要，避免匿名探测</item>
    ///   <item>GET /api/admin/notices（含未发布的草稿）→ 需要，草稿不是公开信息</item>
    ///   <item>login / logout → 不需要（否则谁也登不进来；logout 幂等）</item>
    ///   <item>其余 GET（列表、统计、模板、导出）与全部前台接口 → 不需要</item>
    /// </list>
    /// </summary>
    private static bool NeedsAdminAuth(string method, string url)
    {
        if (!url.StartsWith("/api/admin/", StringComparison.Ordinal)) return false;
        if (url == "/api/admin/login" || url == "/api/admin/logout") return false;
        if (url == "/api/admin/settings") return true;
        if (url == "/api/admin/notices") return true;
        return string.Equals(method, "POST", StringComparison.Ordinal);
    }

    /// <summary>当前请求是否已被授权：关掉登录校验则一律放行，否则看会话令牌。</summary>
    private static bool AdminAllowed(HttpContext ctx)
        => !SettingsStore.LoginEnabled || WebSession.Valid(AdminToken(ctx.Request));

    /// <summary>会话令牌：请求头优先（方便 curl 与脚本），否则读 Cookie。</summary>
    private static string AdminToken(HttpRequest req)
    {
        if (req.Headers.TryGetValue(WebSession.HeaderName, out var v) && (v.ToString() ?? "").Trim().Length > 0)
            return v.ToString().Trim();
        return req.Cookies.TryGetValue(WebSession.CookieName, out var c) ? (c ?? "") : "";
    }

    private static void SetAdminCookie(HttpResponse res, string token)
        => res.Headers.Append("Set-Cookie",
            $"{WebSession.CookieName}={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={(int)WebSession.Ttl.TotalSeconds}");

    private static void ClearAdminCookie(HttpResponse res)
        => res.Headers.Append("Set-Cookie",
            $"{WebSession.CookieName}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0");

    /// <summary>登录：校验账号口令、失败限速、成功签发会话。</summary>
    private static async Task HandleLogin(HttpContext ctx, HttpResponse res)
    {
        var req = ctx.Request;
        var ip = ClientIp(ctx);

        var wait = WebSession.LockRemaining(ip);
        if (wait > 0)
        {
            await WriteJson(res, 429, new { error = $"尝试过于频繁，请 {wait} 秒后再试", retryAfter = wait });
            return;
        }

        // 已关闭登录校验：无需会话，直接告知前端「已放行」
        if (!SettingsStore.LoginEnabled)
        {
            await WriteJson(res, 200, new { ok = true, loginEnabled = false, user = "" });
            return;
        }

        var body = await ReadBodyJson(req);
        var user = Str(body, "user");
        var pass = Str(body, "password");

        if (!SettingsStore.VerifyLogin(user, pass))
        {
            WebSession.NoteFail(ip);
            await Task.Delay(400);   // 拖慢爆破；PBKDF2 本身已 30~60ms
            var left = WebSession.LockRemaining(ip);
            AuditLog.Write(AuditAction.Login, "后台登录",
                $"网页端登录失败：账号 {user}", false, "IP " + ip, user, "网页端");
            await WriteJson(res, 401, new
            {
                error = left > 0 ? $"账号或口令不正确，已锁定 {left} 秒" : "账号或口令不正确",
                retryAfter = left,
            });
            return;
        }

        WebSession.NoteOk(ip);
        SetAdminCookie(res, WebSession.Create());
        AuditLog.Write(AuditAction.Login, "后台登录", $"网页端登录成功（IP {ip}）", true,
            null, SettingsStore.Current.AdminUser ?? SettingsStore.DefaultUser, "网页端");
        await WriteJson(res, 200, new
        {
            ok = true,
            loginEnabled = true,
            user = SettingsStore.Current.AdminUser ?? SettingsStore.DefaultUser,
            isDefaultPassword = SettingsStore.IsDefaultPassword,
        });
    }

    private static string ClientIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    /// <summary>body 里的字符串数组 → List&lt;string&gt;（用于立项指南参数表）。</summary>
    private static List<string> StrList(Dictionary<string, JsonElement> body, string key)
    {
        var list = new List<string>();
        if (body == null || !body.TryGetValue(key, out var e)) return list;
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in e.EnumerateArray())
        {
            var v = it.ValueKind == JsonValueKind.String ? it.GetString() : it.ToString();
            if (!string.IsNullOrWhiteSpace(v)) list.Add(v.Trim());
        }
        return list;
    }

    /// <summary>概览用：立项指南摘要（区段数 / 项目总数 / 解读期数与条数 / 导入信息）。</summary>
    private static object LixiangSummary(AppState S)
    {
        var counts = S.Lixiang.SectionCounts();
        var meta = S.Lixiang.State.Meta ?? new LixiangMeta();
        var list = S.Lixiang.State.Jiedu?.List ?? new List<JieduItem>();
        return new
        {
            sections = counts.Count,
            total = counts.Values.Sum(x => x.Total),
            issues = list.Select(x => x.Issue).Distinct().Count(),
            jieduTotal = list.Count,
            importedAt = meta.ImportedAt ?? "",
            sourceName = meta.SourceName ?? "",
        };
    }

    /// <summary>概览用：德州物价摘要（规模 / 主项加收扩展构成 / 医保支付 / 使用说明覆盖 / 导入信息）。</summary>
    private static object DezhouSummary(AppState S)
    {
        var st = S.Dezhou.Stat();
        var (covered, cats, entries) = S.Dezhou.UsageStat();
        return new
        {
            total = st.Total,
            main = st.Main,
            add = st.Add,
            ext = st.Ext,
            insured = st.Insured,
            batchCount = st.BatchCount,
            categoryCount = st.CategoryCount,
            usageCovered = covered,
            usageCategories = cats,
            usageEntries = entries,
            importedAt = S.Dezhou.Meta?.ImportedAt ?? "",
            source = S.Dezhou.Meta?.Source ?? "",
        };
    }

    /// <summary>对齐 JS parseInt(v,10) || def（0 与 NaN 均回落到默认值）。</summary>
    private static int ParseIntJs(string s, int def)
        => int.TryParse((s ?? "").Trim(), out var n) ? (n == 0 ? def : n) : def;

    /// <summary>合并多个（匿名）对象为响应字典，键序保持传入顺序。</summary>
    private static Dictionary<string, object> Concat(params object[] objs)
    {
        var d = new Dictionary<string, object>();
        foreach (var o in objs)
        {
            if (o == null) continue;
            if (o is Dictionary<string, object> dd)
            {
                foreach (var kv in dd) d[kv.Key] = kv.Value;
                continue;
            }
            foreach (var p in o.GetType().GetProperties())
                if (p.CanRead && p.GetIndexParameters().Length == 0)
                    // 统一按 camelCase 落键：既兼容原匿名类型（属性名本就是小写），
                    // 也让改造后的具名类型（PascalCase）序列化结果与原先逐字段一致。
                    d[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = p.GetValue(o);
        }
        return d;
    }

    private static string Str(Dictionary<string, JsonElement> body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.GetRawText(),
        };
    }

    private static bool Bool(Dictionary<string, JsonElement> body, string key)
        => body != null && body.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>body.row（前端提交的扁平字符串字段）→ 字符串字典。</summary>
    private static Dictionary<string, string> FlatRow(Dictionary<string, JsonElement> body)
    {
        var d = new Dictionary<string, string>();
        if (body == null || !body.TryGetValue("row", out var r) || r.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in r.EnumerateObject())
        {
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetRawText(),
            };
        }
        return d;
    }

    private static JsonElement Root(Dictionary<string, JsonElement> body)
        => body != null && body.TryGetValue("__root", out var r) ? r : default;

    private static Dictionary<string, JsonElement> ToDict(JsonElement e)
    {
        var d = new Dictionary<string, JsonElement>();
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return d;
    }

    private static string Token()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<Dictionary<string, JsonElement>> ReadBodyJson(HttpRequest req)
    {
        using var sr = new StreamReader(req.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, JsonElement>();
        using var doc = JsonDocument.Parse(text);
        var d = ToDict(doc.RootElement);
        d["__root"] = doc.RootElement.Clone();
        return d;
    }

    private sealed class MultipartData
    {
        public List<(string FileName, byte[] Content)> Files { get; } = new();
        public Dictionary<string, string> Fields { get; } = new();
    }

    private static async Task<MultipartData> ReadMultipart(HttpRequest req)
    {
        if (!req.HasFormContentType) return null;
        var form = await req.ReadFormAsync();
        var data = new MultipartData();
        foreach (var f in form.Files)
        {
            using var s = f.OpenReadStream();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            data.Files.Add((f.FileName, ms.ToArray()));
        }
        foreach (var kv in form) data.Fields[kv.Key] = kv.Value.ToString();
        return data;
    }

    private static async Task WriteJson(HttpResponse res, int code, object obj)
    {
        res.StatusCode = code;
        res.ContentType = "application/json; charset=utf-8";
        await res.WriteAsync(JsonSerializer.Serialize(obj, Js.Http));
    }

    private static async Task SendBytes(HttpResponse res, byte[] bytes, string fileName)
    {
        res.StatusCode = 200;
        res.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        res.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"";
        res.ContentLength = bytes.Length;
        await res.Body.WriteAsync(bytes);
    }

    private static async Task SendWebFile(HttpResponse res, string name)
    {
        var path = Path.Combine(AppPaths.WebDir, name);
        if (!File.Exists(path))
        {
            res.StatusCode = 404;
            await res.WriteAsync("Not Found");
            return;
        }
        res.StatusCode = 200;
        res.ContentType = "text/html; charset=utf-8";
        await res.SendFileAsync(path);
    }
}
