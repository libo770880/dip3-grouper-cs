# DIP 3.0 分组器 · C# 版

国家 DIP 3.0 医保分组器的 **C#/.NET 8 全量重写版**，功能与界面与原 Node.js 版
（`../dip3-grouper`）**完全对等**，且**脱离 Node.js 运行时**独立运行。

- 分组优先级链路：先期分组(XQ) → 诊断辅助细分(FZ：烧伤/肿瘤/结核) → 并项规则核心病种(BX)
  → 基础规则核心病种(JC) → 基层病种(JICENG，可选) → 综合病组兜底(ZH)
- **两种交付形态，同一套业务核心**：
  - **Web 版**（`Dip3Grouper.Web`）：Kestrel 自研路由，浏览器访问，前端页面与 Node 版逐字节相同
  - **桌面客户端**（`Dip3Grouper.Desktop`）：WPF 原生界面，免安装绿色版，起窗口即用、不占端口
- 相同的数据文件（`data/*.json`、`config/dip3-config.json`）—— 与 Node 版**逐字节相同**
- 业务编排（`GrouperService` / `AdminService` / `AppState`）只在 `Dip3Grouper.Core` 实现一次，
  Web 与桌面共用，因此两端分组结果、导入语义、错误文案完全一致

---

## 1. 目录结构

```
dip3-grouper-cs/
├─ run-web.bat                    启动 Web 版（纯 ASCII，见 §8.1）
├─ publish-web.bat                打包 Web 绿色版 → dist\DIP3-Web\（先发命令行版再叠软件壳，见 §9.30）
├─ publish-desktop.bat            打包桌面绿色版 → dist\DIP3-Desktop\（纯 ASCII；带 x86 参数出 32 位，见 §9.4）
├─ Dip3Grouper.sln
├─ src/
│  ├─ Dip3Grouper.Core/           业务核心（分组、数据存储、Excel、文本解析）
│  │  ├─ Grouper.cs               分组主流程（六层优先级链路）
│  │  ├─ Matcher.cs               诊断/手术编码匹配（含区间码、通配）
│  │  ├─ GrouperService.cs        业务编排（分组/批量/联想/区段），Web 与桌面共用
│  │  ├─ AdminService.cs          后台编排（导入解析/模板生成/导入报告），Web 与桌面共用
│  │  ├─ AppState.cs              运行时状态与热重载，Web 与桌面共用
│  │  ├─ BuildConfig.cs           Excel 病种库 → dip3-config.json
│  │  ├─ Diseases.cs              病种库增删改查
│  │  ├─ MappingStore.cs          医保编码映射表 + 灰码
│  │  ├─ Lixiang.cs               立项指南（区段/项目/使用说明/解读辅导）
│  │  ├─ ZongheStore.cs           综合病组
│  │  ├─ DezhouStore.cs           德州市物价（立项指南规范整合项目 + 三级价格）
│  │  ├─ ExcelUtil.cs             xlsx 读写（基于 NPOI）
│  │  ├─ XlsxGray.cs              xlsx 背景色 → 灰码识别
│  │  ├─ DocToText.cs             doc/docx/txt → 纯文本
│  │  ├─ PinyinHelper.cs          拼音检索
│  │  ├─ Infrastructure.cs        Js / JsonPretty / JsStringEncoder / AppPaths / FileExt
│  │  └─ Models.cs                数据模型（字段顺序即落盘字节顺序）
│  ├─ Dip3Grouper.Web/            Web 服务（Kestrel + 自研路由）
│  │  ├─ Program.cs               命令行入口：参数解析、启动横幅
│  │  ├─ WebServer.cs             Kestrel 宿主构建（命令行版与软件壳共用）+ 端口存活探测
│  │  └─ WebRouter.cs             全部 42 个 HTTP 接口（薄路由层，逻辑全在 Core）
│  ├─ Dip3Grouper.WebShell/       Web 绿色版用户入口（WinForms，无黑框 + 托盘 + 面板，见 §9.30）
│  │  ├─ Program.cs               单实例、端口复用检测、局域网地址打分（网卡优先级）、异常落盘
│  │  └─ ServerForm.cs            面板：状态 / 本机 / 局域网 / 后台 / 数据目录，各带打开与复制
│  ├─ Dip3Grouper.Desktop/        桌面客户端（WPF 原生界面）
│  │  ├─ App.xaml(.cs)            入口：资源字典合并、--home/--jiceng/--selftest
│  │  ├─ SelfTest.cs              命令行自检（91 项：数据链路 + 三库映射/关联链接/多条件检索/防抖/懒构建/预热 + 全页面构造 + 模板/列宽断言 + 维护往返 + 登录窗回车通道 + 图标托盘与关闭询问）
│  │  ├─ MainWindow.xaml(.cs)     前台主窗口（无边框 + 自绘标题栏 + 六页签）
│  │  ├─ AdminWindow.xaml(.cs)    后台窗口（深色侧边栏 + 四页）
│  │  ├─ Themes/                  Colors / Controls / Data 三份资源字典
│  │  ├─ Controls/                SuggestBox（联想）、RowEditDialog（行编辑）、FlatGrid（动态列）
│  │  └─ Views/                   前台六页 + Views/Admin/ 后台六页
│  └─ Dip3Grouper.Cli/            命令行（group / batch / build-config / patch-config / doc2txt）
├─ packaging/desktop/             绿色版内附带文件（启动器 .bat + 使用说明.txt）
├─ tests/Dip3Grouper.Tests/       xunit 测试
├─ tools/                         对拍与调试脚本
│  ├─ iso-lab.mjs                 隔离实验台（安全跑写接口对拍）
│  ├─ compare-api.mjs             只读接口 + 分组接口对拍
│  ├─ compare-write-api.mjs       写接口对拍（含落盘字节级比较）
│  ├─ compare-templates.py        模板导出（xlsx）语义对拍
│  ├─ xlsx-diff.py                xlsx 单元格级语义比较
│  ├─ make_zip.py                 绿色版打成 zip（UTF-8 中文文件名）
│  ├─ capture_window3.py          置前后屏幕拷贝截图（WPF 窗口验证用）
│  ├─ click_and_shot.py           点窗口内相对坐标后截图（页面遍历验证用）
│  └─ fixtures/                   测试素材（映射表 / 病案 / 分组结果）
├─ web/                           前端页面（index.html / admin.html）
├─ config/dip3-config.json        病种库配置
└─ data/
   ├─ mappings.json               医保编码映射表（约 5.6 MB）
   ├─ lixiang.json                立项指南（区段 + 解读辅导）
   ├─ dezhou-price.json           德州市物价（立项指南规范整合项目，2,793 条 + 作废对照 3,950 行 + 旧版物价 6,589 行，含类别使用说明）
   ├─ dezhou-favorites.json      德州物价「常用收藏」（用户数据，随用随建）
   ├─ guifan.json                 全国医疗服务项目技术规范 2023 年版（11 章 11,516 个项目）
   ├─ zonghe.json                 综合病组
   └─ config-meta.json            病种库导入元信息
```

---

## 2. 构建

需要 .NET 8 SDK。

```bash
dotnet build -c Release src/Dip3Grouper.Web/Dip3Grouper.Web.csproj
dotnet build Dip3Grouper.sln              # 全部（含 CLI 与测试）
```

> Git Bash 下若提示找不到 `dotnet`：
> `export PATH="/c/Program Files/dotnet:$PATH"`

---

## 3. 运行

### 3.1 Web（推荐）

```bat
run-web.bat                          :: 默认 3210 端口
run-web.bat 3220                     :: 指定端口
run-web.bat 3210 --jiceng            :: 开启基层病种兜底（与 Node 版正式实例一致）
```

启动后：

| 地址 | 说明 |
|---|---|
| `http://localhost:3210` | 前台（单条分组 / 批量分组 / 病种库查询 / 立项指南） |
| `http://localhost:3210/admin` | 后台管理（概览、病种库、立项指南、映射与灰码、德州物价、消息推送、系统设置） |
| `http://localhost:3210/admin?p=dezhou` | 直达后台某一页（`p` = `dash`/`config`/`lixiang`/`mapping`/`dezhou`/`notice`/`settings`） |

> **绿色发行包的两个入口**（`dist\DIP3-Web`）：
> 双击 `Dip3Grouper.WebShell.exe` = 软件版，无黑框、托盘常驻。面板直接列出四条，不用自己查 IP：
> **本机访问 / 局域网访问 / 后台管理 / 数据目录**，每条都能「打开」和「复制」；
> 局域网地址是按网卡打分自动挑的（有默认网关优先、排除 VMware/Hyper-V/VPN 等虚拟卡），
> 插拔网线或切 Wi-Fi 后点「刷新地址」（见 §9.31）。
> `Dip3Grouper.Web.exe` = 命令行版，供 Windows 服务 / 计划任务 / 需要看控制台日志时使用。
> 源码目录调试继续用上面的 `run-web.bat`。
>
> **运行环境**：不用装 .NET / 数据库 / VC++ 运行库，运行时随包发；要求 Windows 10 1809+ 或 Windows 11。
> 科室里其他电脑只需浏览器，打开面板上那条局域网地址即可。

网页后台与桌面端后台功能对齐：概览为四张数据卡（病种库 / 映射表 / 立项指南 / 德州物价）加各区段条数胶囊；
「德州物价」页支持按文号导入、模板/导出、明细增删改、类别使用说明维护；「消息推送」页向前台推送通知/公告（见 3.1.1）；
「系统设置」页可维护 **软件名称与副名 / 后台登录账号口令 / 立项指南参数（项目类别、计价单位）/
数据分发共享口令**。

**数据分发共享口令**（系统设置最后一节）：保护 `/api/data/*`（客户端「数据更新」拉数据）的口令，
留空＝不校验。填了之后，桌面客户端必须在「后台管理 → 数据更新 → 共享口令」里填同一个值。
它与后台登录口令是两回事，互不影响；`/api/notice` 与 `/api/settings/public` 不受它影响
（前台页面与通知本来就要让全院免登录可见）。

**网页后台的登录**：只拦「会改数据」的接口（`POST /api/admin/*` 与 `GET /api/admin/settings`），
浏览、查询、模板下载、导出一律免登录。默认账号 `admin` / `admin123`，会话走 8 小时 Cookie
（`dip3_admin`，HttpOnly），也可用 `X-DIP3-Admin` 请求头。失败 5 次锁定该来源 60 秒，到期自动解锁。
在「系统设置 → 后台登录」里可改账号口令或整体关闭校验（口令与桌面客户端共用 `data/settings.json`）。
（桌面端另有「数据更新」页，属客户端专有功能——浏览器用户直接读主库数据，不需要拉更新。）

也可直接调用 exe：

```bat
src\Dip3Grouper.Web\bin\Release\net8.0\Dip3Grouper.Web.exe --port 3210 --jiceng
```

> ⚠ `run-web.bat` 优先使用 **Release** 产物。改过 Web 端代码后如果只编了 Debug，脚本会跑到旧
> exe，新接口（如 `/api/data/manifest`）会 404。发布前请重编 Release，或直接用下面的
> `publish-web.bat`（它内部就是 Release 发布）。

#### 绿色版（局域网主库机器用这个，免装 .NET）

```bat
publish-web.bat            :: 默认 win-x64 → dist\DIP3-Web\
publish-web.bat x86        :: 32 位      → dist\DIP3-Web-x86\
```

#### 低配机临时方案（2026-10-21）

低配机（如 1024×768 老机）跑 WPF 客户端吃力时，改用浏览器瘦客户端：

- **姿势 A（推荐）**：Web 服务端跑在科室任意一台正常机器（或主库机），
  低配机只开浏览器访问 `http://<服务机IP>:3210`——零安装、内存占用趋近于零。
  服务机需放行 3210 入站。
- **姿势 B**：低配机本机跑 `publish-web.bat x86` 产物（32 位自包含 .NET，
  常驻内存约 100 MB 量级，比 WPF 客户端轻得多），本机浏览器开 `http://localhost:3210`。
- Web 版功能面：单条分组 / 批量分组 / 病种库查询 / 立项指南查询（德州物价与
  征求意见目前仅桌面端有）。

产出 `dist\DIP3-Web\`（自包含绿色版，含 `config\` `data\` `web\` 与中文启动脚本），打成 zip 后拷到
主库机器：

1. 整个文件夹解压（如 `D:\DIP3服务端`）；
2. 双击「**启动 DIP3 数据服务端.bat**」—— 窗口会列出本机可用地址（含局域网 IP），**此窗口须常开**；
3. 其他电脑在「后台 → 数据更新」填 `http://<主库IP>:3210`。

换端口：`启动 DIP3 数据服务端.bat 3220`。局域网访问前需在防火墙放行 3210 入站
（控制面板 → Windows Defender 防火墙 → 高级设置 → 入站规则 → 新建规则 → 端口 → TCP 3210 → 允许）。

`publish-web.bat` 第 7 步会自动起一次服务做冒烟测试（请求 `/api/data/manifest`，通过才继续）。

> **重新发布不会冲掉已部署的设置。** 脚本会先把 `dist\DIP3-Web\data\` 下的运行期文件
> （`settings.json`、`notice.json`、`dezhou-favorites.json`、`_backup\`）暂存，发布完成后再放回，
> 所以服务端的软件名称/副名/后台口令/数据源配置、已发布的通知、更新备份点都能跨发布保留。
> Desktop 版的 `publish-desktop.bat` 同理（见 §8.14）。

#### 3.1.2 公共设置跟随（服务端 → 桌面客户端）

服务端后台改的**展示类设置**可以统一下发到各台桌面客户端，不用逐台手改：

| 字段 | 是否下发 | 说明 |
|---|---|---|
| 软件名称 / 单位副名 | ✅ | 客户端窗口标题、启动页品牌区跟着变 |
| 立项指南参数（项目类别 / 计价单位） | ✅ | 影响客户端编辑立项项目时的下拉候选 |
| 后台登录账号口令 | ❌ | 本机私有。下发口令哈希等于把全院后台交出去 |
| 界面主题 / 关闭行为（先询问·收托盘） / 数据源地址 | ❌ | 本机私有，各人各机自定 |
| 德州物价「常用收藏」 | ❌ | 每台机器各不相同 |

- 通道：`GET /api/settings/public`（只读、免鉴权，只回展示类字段）；
- 开关：桌面端「**后台管理 → 数据更新 → 跟随服务端公共设置**」（默认开，需先填服务器地址）；
- 时机：客户端启动时静默同步一次；「立即更新」数据成功后也会顺带同步；也可点「立即同步设置」手动触发；
- 语义：**服务端有值才下发**。服务端某字段为空 = 管理员没在那台服务上配，客户端保留自己的值，
  不会被清空（避免漏填一项就把全院客户端的配置抹掉）。

> ⚠ 反过来说：服务端副名从「A」改成「B」，开启跟随的客户端下次启动就会变成「B」；
> 想在某台机器上单独用别的名字，就在「系统设置」里关掉跟随（顶部提示会写明本机正在跟随服务端）。

#### 3.1.1 前台消息中心（后台推送通知）

后台「**消息推送**」页发布 → 前台（`/`）右上角「🔔 消息」收到，**无需刷新页面**（每 60 秒自动检查一次，
切回标签页时若距上次检查超过 30 秒会补一次）。

| 概念 | 说明 |
|---|---|
| 级别 | 一般（蓝）/ 提醒（橙）/ 重要（红）。只影响配色与图标 |
| 钉住 | 钉住的消息在前台**导航栏下方常驻横幅**显示（单行，点「查看」看全文）；不钉的只进抽屉 |
| 生效期 | 留空＝立即生效 / 长期有效；填了只在时间段内出现，到点自动出现、过期自动消失（排班调整、系统维护） |
| 发布开关 | 关掉即**草稿**：只有登录后的后台能看见，前台完全不可见 |

前台在本次浏览器会话首次打开页面时，若存在未读消息会**自动展开一次**抽屉（这就是「推送」的到达感）。
逐条状态存在各浏览器自己的 localStorage：

- **已读** —— 只影响未读角标，打开抽屉/点「全部已读」即清零；
- **本机关闭**（条目右上 ✕）—— 只让**这台机器**不再显示该条，不删后台数据，也不影响别人。

同理，后台列表用状态胶囊标注每条：**生效中** / **待生效** / **草稿** / **已过期**；
状态判定用服务器时间，避免管理员电脑时钟不准导致显示错乱。

数据落在 `data/notice.json`（`rev` 版本号单调递增，前台据此判断是否需要重绘）。
它是**运营数据**，因此**不进** `DataCatalog` 的下发白名单，也不参与 `ReloadAll`。
接口清单见 §10。

#### 3.1.3 桌面客户端的消息提示（WPF）

桌面客户端同样收得到服务端推送的通知 —— 走的是**同一个接口** `GET /api/notice`（`Core/NoticeClient.cs`）：

- **入口**：主窗口顶栏「后台管理」左侧的 **🔔 铃铛**。有未读时显示红色数字角标（99+ 封顶）；
- **钉住的消息**在导航栏下方**常驻横幅**显示（级别色标 + 单行文字 +「查看」+ ✕）；
  点 ✕ 只让**本机**不再显示这条，不动服务端数据；
- 点铃铛弹出**消息面板**：级别胶囊、标题、时间、正文（默认 3 行，点条目展开全文），
  顶部「刷新 / 全部已读」，底部显示最后刷新时间或失败原因；
- **发现新消息时弹一次系统托盘气泡**（同一条只打扰一次），之后靠角标提醒；
- 每 60 秒自动轮询一次；打开面板即整批标记已读（本批在面板里保持高亮，方便看出哪几条是新的）；
- 未配置数据源地址时铃铛置灰，点击会提示到「后台管理 → 数据更新」里填服务器地址；
- 「我读过/关掉过哪些」落在本机 `data/notice-read.json`（已读 id + 横幅隐藏 id），
  服务端删掉的消息会自动清理对应记录；该文件**本机私有**，不进下发白名单。

### 3.2 桌面客户端（WPF 原生界面）

开发期直接跑：

```bash
dotnet run --project src/Dip3Grouper.Desktop/Dip3Grouper.Desktop.csproj
```

命令行参数：

```bash
# 指定数据目录（数据放在别处 / 共享盘）
Dip3Grouper.Desktop.exe --home D:\DIP3数据

# 开启基层病种兜底（与院内正式服务口径一致）
Dip3Grouper.Desktop.exe --jiceng

# 自检：不打开窗口，跑数据链路 + 全部页面构造，结果写 %TEMP%\dip3-desktop-selftest.log
# 退出码 0 = 全部通过
Dip3Grouper.Desktop.exe --selftest

# 只开后台登录窗（不载入业务数据），并把键盘通道收到的原始按键写进
# %TEMP%\dip3-login-trace.log —— 排查"回车/输入法"这类只在实机出现的问题用
Dip3Grouper.Desktop.exe --login
```

界面结构：

| 位置 | 页面 |
|---|---|
| 启动画面 | 品牌渐变 + 进度条 + 阶段文案（数据载入在后台线程，进度实时回传） |
| 主窗口（前台） | 单条分组 · 批量分组 · 病种库查询 · 立项指南查询 · 德州物价查询 · 项目技术规范 |
| 后台窗口 | 概览 · 病种库管理 · 立项指南 · **德州物价** · 映射与灰码 · 系统设置 |

**单条分组 · 分组溯源**：结果卡在明细下方给出「分组溯源」区块，按优先级逐层展示
判定过程（排除表判定 → 先期分组 → 诊断辅助细分 → 并项规则 → 基础规则 → 基层病种
→ 综合病组，只列出实际参与判定的层），命中 / 未命中 / 跳过 / 影响四态着色；
每层给出结论与编码级依据——命中哪个码、组合缺哪个码、主诊断命中但主手术不在
范围内等「卡点」，未入组时直接指出根因（如「其他诊断缺少恶性肿瘤编码」）。
结核区间行在诊断辅助细分命中但归入综合病组时，溯源会点明「归入组别」。
Web 前台同步提供（`/api/group-trace`，见 §10）。

**德州物价 · 作废对照**：物价查询页顶部分段切换「现行项目 / 作废对照」。作废对照来自
四批规范整合通知附件的「映射关系表」（德医保发〔2025〕33号/37号、〔2026〕3号/11号，
存于 `data/dezhou-price.json` 的 `cancels` 键，随数据分发同步到各客户端），
一行 = 一条「作废的原物价项目 → 对应新立项指南项目」；无替代的显示「直接废止，无替代项目」
（如临床量表 C/D 类整体并入分档评估）。「对应新编码」为蓝色链接，点击直接跳到
「现行项目」视图并按该编码检索，筛选条出现「← 返回」按钮可回到原视图与检索词
（手动切分段 / 清空即取消返回态）。共用批次 / 类别 / 关键词筛选与分页，
关键词支持拼音，可按旧编码、新编码、项目名互查。

**德州物价 · 旧版物价**：分段第三项，来自《德州市医疗服务项目价格对码表——发县市区版（241230）》
（德医保发〔2024〕45号，规范整合前全市执行的旧价格项目，存于 `dezhou-price.json` 的 `legacy` 键，
6,589 行 / 11 个归集口径，含内涵、除外内容、限定支付范围与地纬执行编码）。
每行联查作废对照自动标出**状态**：「已作废」（红，出现在任一批次映射关系表的作废侧）/
「现行」（绿）；已作废行同时给出**现行标准**（对应新项目「编码 名称」，一对多逐条换行），
每条为蓝色链接，点击跳到「现行项目」视图并按该编码检索。
「状态」下拉可只看已作废或现行；关键词命中旧编码、国家代码、名称、内涵等，支持拼音。

**应用图标与托盘**：exe / 任务栏 / Alt-Tab / 托盘共用一份多尺寸图标 `Assets/app.ico`
（16~256 共 9 帧，与标题栏品牌方块同一套渐变 + "DIP" 字标；再生成脚本 `.tmp/icon/make_icon.py`）。
点关闭按钮**先弹确认框**：「最小化到托盘」（默认按钮，程序留在后台继续接收通知公告）/
「退出程序」/「取消」，可勾「记住我的选择」不再询问；托盘菜单：打开主界面 / 最小化到托盘 /
打开后台管理 / 关闭时最小化到托盘（勾选开关，勾选即视为不再询问）/ 退出程序；双击托盘图标唤回主界面。
系统注销 / 关机时自动跳过确认框直接退出（确认框不能挡住关机流程）。
行为持久化在 `data/settings.json`：`closeAsk`（点关闭先问一次，默认开）+ `closeToTray`
（记住后的行为，默认收托盘）；后台「系统设置 → 窗口与托盘」可分别调整两项。
| 登录窗 | 打开后台前校验账号口令（可在系统设置中关闭） |

桌面端**不起 HTTP 服务、不占端口、不依赖 Node**，直接调用 `Dip3Grouper.Core`。

#### 应用设置（`data/settings.json`）

后台「系统设置」页维护，保存后立即生效：

| 项 | 说明 | 默认 |
|---|---|---|
| 软件名称 | 窗口标题、启动页、后台侧栏、品牌方块（ASCII 取 3 字符 / 中文取 2 字） | DIP 3.0 分组器 |
| 单位 / 副名 | 显示在软件名称下方，可空 | 空 |
| 界面主题 | 经典蓝 / 青碧 / 靛紫 / 朱砂 / 石墨（深色），点击色卡即时换肤并保存 | 经典蓝 |
| 登录校验 | 打开后台管理前是否要求登录 | 开 |
| 登录账号 | 不区分大小写 | admin |
| 登录口令 | PBKDF2-SHA256 哈希存储，程序内不留明文 | admin123 |

忘记口令：删除 `data/settings.json` 后重启即恢复出厂账号（该文件损坏也会自动回落默认值，不会导致启动失败）。

### 3.3 命令行

```bash
Dip3Grouper.Cli group '{"主要诊断编码":"Z51.100x002","其他诊断编码":"C34.900","主要手术操作编码":"99.2503"}'
Dip3Grouper.Cli group --file case.json [--jiceng]
Dip3Grouper.Cli batch --input cases.xlsx [--output results.xlsx] [--jiceng]
Dip3Grouper.Cli build-config <xlsx路径> [输出json路径]
Dip3Grouper.Cli patch-config <xlsx路径>
Dip3Grouper.Cli doc2txt <doc|docx|txt路径> [输出txt路径]
```

---

## 4. 参数与环境变量

### 启动参数

| 参数 | 适用 | 说明 |
|---|---|---|
| `--port <n>` | Web | 监听端口，默认 `3210` |
| `--jiceng` | Web / CLI | 开启基层病种兜底层；不传则关闭 |
| `--home <目录>` | 全局 | 指定工程根（含 `data/`、`config/`、`web/`） |
| `--config` / `-c` | CLI | 指定病种库配置路径，默认 `config/dip3-config.json` |
| `--file` / `-f` / `--input` / `-i` | CLI | 输入文件 |
| `--output` / `-o` | CLI | 输出文件 |

### 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `DIP3_HOME` | 自动探测 | 工程根目录。未设置时从 exe 所在目录向上查找同时含 `config/` 与 `web/index.html` 的目录 |
| `DIP3_LAB_NODE_PORT` | `3211` | 隔离实验台 Node 实例端口 |
| `DIP3_LAB_CS_PORT` | `3231` | 隔离实验台 C# 实例端口 |
| `DIP3_NODE_ROOT` | `../dip3-grouper` | 对拍脚本用：Node 工程根 |
| `DIP3_CS_ROOT` | 本工程根 | 对拍脚本用：C# 工程根 |
| `DIP3_BACKUP_KEEP` | `5` | 对拍脚本保留的备份份数 |

---

## 5. 数据文件与缩进约定

落盘格式必须与 Node 版 `JSON.stringify(value, null, n)` **逐字节一致**，
由 `JsonPretty` + `JsStringEncoder` 保证（见 §6）。

| 文件 | 缩进 | 写入时机 |
|---|---|---|
| `data/mappings.json` | 1 空格 | 映射表增删改 / 导入 |
| `data/lixiang.json` | 1 空格 | 立项指南增删改 / 导入 |
| `data/dezhou-price.json` | 1 空格 | 德州物价导入（`--import-dezhou`）+ 后台增删改 / Excel 导入导出 |
| `data/zonghe.json` | 1 空格 | 综合病组增删改 / 导入 |
| `data/config-meta.json` | 2 空格 | 病种库导入 |
| `data/notice.json` | 1 空格 | 后台「消息推送」页发布 / 停用 / 删除（运营数据，不下发） |
| `config/dip3-config.json` | 紧凑（无缩进） | 病种库增删改 / `build-config` |

> **综合病组分两层**：① 病种库内置 —— 结核表区间行（A15-A16，26 条，带手术/耐药条件），
> 分组第 2 层直接命中，**不在** `zonghe.json`；② 追加兜底层 —— `data/zonghe.json`
> （核心病种未入组时按主诊断三位类目兜底，无手术条件）。
> 两层语义不同，**不要**把内置 26 条灌进 zonghe.json —— 会使「A15 保守行外手术不误入」
> 等对拍用例翻车。概览卡两层数字分开显示。后台已无「综合病组」菜单，
> 兜底层通过数据文件维护（`ZongheStore` 增删改 / `ImportZonghe` 仍可用）。

字段顺序也是字节的一部分：`Models.cs` 中属性的声明顺序决定序列化顺序，
**不得随意调整**（例如 `XianqiRow.BirthWeightNote` 必须排在 `Note` 之后）。

---

## 6. 与 Node 版的一致性保证

### 6.1 为什么能做到逐字节一致

- **`JsonPretty`** —— .NET 8 的 `Utf8JsonWriter` 只能输出 2 空格缩进
  （`IndentSize` 需 .NET 9），故自研：用 `Utf8JsonReader` 逐 token 重排，
  缩进宽度可配（1 / 2 / 0）。
- **`JsStringEncoder`** —— 自定义 `JavaScriptEncoder`，精确复刻 JavaScript
  `JSON.stringify` 的转义策略：**只**转义 `"` `\` 与 C0 控制字符；
  补充平面字符（如 U+2C317「𬌗」）按原始 UTF-8 原样输出。
  > `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 会把补充平面字符转义成
  > `\uD870\uDF17`；`JavaScriptEncoder.Create(UnicodeRanges.All)` 会把 `"`
  > 写成 `\u0022` —— 两者都不匹配 JS 行为，故必须自研。

### 6.2 对拍脚本

```bash
# 只读接口 + /api/group + /api/batch（默认 3210 ↔ 3220）
node tools/compare-api.mjs [nodeBase] [csBase]

# 模板导出（xlsx 单元格语义）
python tools/compare-templates.py [nodeBase] [csBase]

# 写接口对拍 —— 会真实改动 data/ 与 config/，务必走隔离实验台
node tools/iso-lab.mjs run
```

### 6.3 隔离实验台（`tools/iso-lab.mjs`）

写接口对拍会真实触发落盘。若直接对着 3210/3220 跑，服务内存态会在还原后
再次落盘，导致数据错乱（本项目踩过此坑）。因此：

```
node tools/iso-lab.mjs up       # 重置隔离数据 + 拉起两个隔离实例
node tools/iso-lab.mjs status   # 查看端口与隔离目录状态
node tools/iso-lab.mjs down     # 按端口精确 kill
node tools/iso-lab.mjs run      # 一键 up → compare-write-api → down
```

隔离实例：Node 版 `http://localhost:3211`（工程副本），C# 版 `http://localhost:3231`
（同一 exe + `DIP3_HOME` 指向独立数据目录）。

`compare-write-api.mjs` 的安全约定：

- 默认**拒绝**对 3210 / 3220 运行（除非显式 `--force`）
- 运行前备份两端数据 → 运行 → 还原 + sha256 校验 + 文件清单校验
- 还原采用**逐文件就地覆盖**（不做整目录删除）；残留的导入临时文件自动忽略
- 还原失败时以退出码 `3` 结束，并把备份留在 `.tmp/backups/<时间戳>/` 供人工恢复
- 落盘比较会先抹平 `importedAt` / `uploadedAt` / `updatedAt` 时间戳

---

## 7. 测试

```bash
# C#
dotnet test Dip3Grouper.sln

# Node（在 ../dip3-grouper 下）
node test/run.js
```

覆盖范围：

| 测试 | 内容 |
|---|---|
| `GroupingTests` | 分组主流程（先期/辅助细分/并项/基础/基层/综合病组）、区间码、通配 |
| `LixiangTests` | 立项指南模板往返导入、`doc2txt` 清洗、解读辅导文本切分 |
| `JsonPrettyTests` | 缩进与转义细节、`JsStringEncoder` 行为 |
| `DataFileFormatTests` | 真实数据文件「读入 → 序列化 → 逐字节一致」 |
| `XlsxGrayTests` | 灰码背景色解析（含 rels 属性顺序回归） |

---

## 8. 注意事项与已知差异

### 8.1 `run-web.bat` 必须保持纯 ASCII

`.bat` 由 `cmd.exe` 按**控制台 OEM 代码页**（中文 Windows 为 936）读取。
若文件含 UTF-8 中文注释，注释行会变成乱码并被当作命令执行，脚本直接崩。
**如需修改，请勿写入任何非 ASCII 字符。**

### 8.2 无控制台句柄时不要直接设 `Console.OutputEncoding`

服务以重定向方式托管（如隔离实验台把 stdout 写入日志文件）时没有控制台句柄，
直接赋值会抛 `System.IO.IOException: 句柄无效`。统一使用
`ConsoleSetup.UseUtf8()`（内部 try/catch）。

### 8.3 `data/zonghe.json` 的历史格式

磁盘上的 `zonghe.json` 是**紧凑无缩进**的老文件（历史遗留，两端当时一致），
而写入器输出 1 空格缩进 —— 两端行为一致，故未改动。下次通过界面修改综合病组后
会自动转为 1 空格。因此 `DataFileFormatTests` 对该文件只断言「写入器输出对齐 Node」，
不做「磁盘文件逐字节一致」断言。

### 8.4 灰码背景色识别（已修复）

`workbook.xml.rels` 中 `Target` / `Id` 的**属性顺序不可假定**：

- Excel 写 `Id → Type → Target`
- SheetJS 等第三方库写 `Target → Id`

早期实现使用 `Id="x"[^>]*Target="..."` 这一"前向"正则，遇到后者必然失配，
使灰码背景色识别**静默失效**（调用方 try/catch 退化为「医保编码为空」判定）。
现已改为逐标签取属性（`XlsxGray.AttrOf`），对属性顺序不敏感，两端同步修复。

### 8.5 文档解析（`doc2txt`）依赖本机能力

Node 版 `jiedu-import` 通过 `tools/doc2txt.py` 调 Python 解析 `.doc`；
C# 版用 `DocToText` 原生实现，**不依赖 Python**。
两者对同一文本的解析结果一致（已验证 0 差异）。在受限沙箱中 Node 侧
可能因无法派生 Python 子进程而失败，此时对拍脚本会将该组用例标记为
环境限制并跳过。

### 8.6 `.bat` 必须同时满足「纯 ASCII」与「CRLF 换行」

两个都是硬要求，缺一个脚本就会崩，且报错信息很有迷惑性：

| 违反项 | 现象 |
|---|---|
| 含 UTF-8 中文 | 注释行乱码并被当成命令执行 |
| LF 换行 | 每一行都被错位解析，报一堆 `'ho' 不是内部或外部命令`、`'rsion' 不是…` |

本项目 2026-09-30 踩过 LF 这一坑（`publish-desktop.bat` 首次运行整个脚本被
拆碎）。**用编辑器/AI 工具新建或改写 `.bat` 后，务必确认是 CRLF。**

顺带：脚本里若用 `dotnet`，建议先 `where dotnet`，失败再回退到
`%ProgramFiles%\dotnet\dotnet.exe` —— 部分环境的 PATH 里没有 dotnet。

### 8.7 根命名空间不能与 `Dip3Grouper` 类同名

`Dip3Grouper.Core/Models.cs` 里有一个 `public class Dip3Grouper`，而它的
命名空间也叫 `Dip3Grouper`。这在 Core 里没问题（用 `using GrouperEngine = global::Dip3Grouper.Dip3Grouper;`
别名绕开），但**在带 XAML 的工程里会直接编译失败**：

XAML 代码生成器会写出全限定类型名，例如

```csharp
namespace Dip3Grouper.Desktop {          // ← 生成的代码
    internal Dip3Grouper.Desktop.Views.SingleGroupView ViewSingle;
    //       ^^^^^^^^^^^^ 最左段被解析成「类」而不是命名空间
}
```

于是报 `CS0426: 类型"Dip3Grouper"中不存在类型名"Desktop"`，
且错误行号指向 XAML 里对应的那几行（如 `<views:SingleGroupView .../>`）。

**因此桌面工程的 `RootNamespace` 刻意用 `Dip3GrouperApp.Desktop`**
（程序集名仍是 `Dip3Grouper.Desktop`，不受影响）。新增 WPF 工程时请注意。

### 8.8 XAML / csproj 注释里不能出现连续两个短横线

`<!-- ... -->` 内部出现 `--` 会让 XML 解析器提前结束注释，报
`MC3000`（XAML）或 `MSB4025`（csproj）。踩过两次：

- `Themes/Colors.xaml` 注释里的 CSS 变量名 `--shadow`
- `Dip3Grouper.Desktop.csproj` 注释里的命令行 `--self-contained`

写注释时把 `--` 改写成「两个短横线」或用单横线。

### 8.9 `RuntimeIdentifier` / `SelfContained` 不要写死在 csproj

写进 csproj 会让**日常 Debug 构建也走 RID 专用还原**，编译时间从约 30 秒
膨胀到 10 分钟。正确做法是只在发布时用命令行指定：

```bat
dotnet publish ... -r win-x64 /p:SelfContained=true
dotnet publish ... -r win-x86 /p:SelfContained=true   :: 32 位，脚本里由 RID 变量切换
```

### 8.10 `Color` 资源不能直接当画刷用

`Themes/Colors.xaml` 里 `Color.*` 是 `Color`，`B.*` 是 `SolidColorBrush`。
把 `Color.*` 赋给 `Foreground` / `Background` / `BorderBrush` 会在**运行期**
抛 `XamlParseException: "#FF1D6FE0" 不是属性"Foreground"的有效值`
（编译期不报错）。画刷属性一律用 `B.*`。

> 这类「编译通过、一跑就崩」的界面问题正是 `--selftest` 存在的理由：
> 它会逐个构造页面并强制布局，把资源解析错误暴露在命令行里。

### 8.11 DataGrid 选中行文字变白（系统选中态）

现象：表格里**点中某一行后，该行文字变成白色**，在浅蓝选中底色上看不清；
未选中 / 悬停时一切正常。`--selftest` 抓不到（不是资源解析错误，是运行期的系统选中态）。

根因：WPF `DataGridCell` 的选中态前景色取自
`SystemColors.HighlightTextBrush`（深色主题下为白），而 `DgRow` 上设置的
`Foreground` **不一定能继承到自动生成的单元格文本**——`DataGridTextColumn`
生成 `TextBlock` 后，继承链上只要有一环把前景色显式定成系统值，行级设置就失效。

修复：在**单元格层**（`DgCell`，离文本最近、优先级高于行级继承）显式锁定前景：

```xml
<Setter Property="Foreground" Value="{DynamicResource B.Text}" />
<Style.Triggers>
  <Trigger Property="IsSelected" Value="True">
    <Setter Property="Foreground" Value="{DynamicResource B.Text}" />
  </Trigger>
</Style.Triggers>
```

因为 `DgCell` 被所有 `DataGrid`（含 `FlatGrid` 动态列）共用，一处修复全站生效。

### 8.12 换肤后颜色不更新（StaticResource 固化）

现象：点了新主题，大部分界面变了，**个别地方还是旧颜色**。

根因：`{StaticResource B.Xxx}` 在 XAML 载入时就解析成**具体画刷对象**，
换肤只是替换了资源字典里的条目，已固化的引用不会跟着变。
代码里 `FindResource("B.Xxx")` 同理 —— 拿到的是快照，不是引用。

修复：用色处一律 `DynamicResource`；代码侧用 `SetResourceReference`
（已封装成 `Theming/ThemeExt.cs` 的 `ThFg/ThBg/ThBorder` 链式扩展）。
这也是为什么主题切换能**不重建窗口**就全站生效 —— 见 §9.7。
`DiseaseLib` / `Lixiang` / 后台各页的表格选中行均恢复深色可读。

> 纯 UI 类的运行期问题（颜色、遮挡、间距）只能靠**截图对比**发现，
> 自检覆盖不到。`tools/click_and_shot.py`（点窗口内相对坐标后截图）
> 就是为此准备的：改样式 → 编译 → 点开对应页面截图 → 肉眼确认。

### 8.13 联想输入框水印与输入内容重叠

现象：诊断/手术联想框（`SuggestBox`）里打了字，**灰色的示例占位文字还叠在上面**。

根因：水印是覆盖在 `TextBox` 上的 `IsHitTestVisible=False` 的 `TextBlock`，
原先没有任何显隐逻辑，恒显示。

修复：水印加 `Style.Triggers` —— 绑定 `Input.Text`，**为空才 `Visible`**，
有内容即 `Collapsed`（XAML 声明式，无需 code-behind）。
批量分组页粘贴框的水印 `PasteHint` 一直有该逻辑（code-behind 切换），不受影响。
验证姿势：空态截图 → `click_type.py` 输入 `A00.1` 截图（水印消失、下拉正常）→
点「清空」截图（水印恢复）。

### 8.14 发布脚本会覆盖运行期数据（已修复）

现象：在**已部署**的服务端后台改了软件名称/副名/账号口令/数据源，或者发布了通知、
积累了更新备份，**下一次 `publish-web.bat` 之后全没了**；桌面端则是本机设置与
「常用收藏」消失。更隐蔽的是——它看起来像是"设置没生效"或"客户端不跟随服务端"。

根因：两个发布脚本都是 `rmdir /S /Q` 整个输出目录 → 再用仓库的 `data\` 覆盖，
而 `data\` 里混着两类东西：**可再生的业务数据**（`mappings.json` 等，必须来自仓库）
和**运行期产生的本机数据**（`settings.json` / `notice.json` / `dezhou-favorites.json` / `_backup\`，只存在于部署目录）。

修复：发布前把后四类暂存到 `%TEMP%\dip3_web_keep` / `%TEMP%\dip3_desktop_keep`，
发布完成后放回；失败路径也会尽力归还，并保留暂存目录供手工兜底。
上一步失败留下的未归还暂存，不会被下一次运行误删。

> 相关陷阱：发布时若程序仍在运行（`Dip3Grouper.Desktop.exe` / `Dip3Grouper.Web.exe`），
> `dotnet publish` 会因 dll 被占用而 `MSB3027/MSB3021` 失败。发布前先退出程序；
> 脚本现在会把暂存数据还回去，不会留下半截部署。

---

## 9. 桌面客户端架构要点

### 9.1 一套业务核心，两种外壳

| 层 | 位置 | Web 版 | 桌面版 |
|---|---|---|---|
| 分组引擎 | `Core/Grouper.cs` | ✔ | ✔ |
| 业务编排 | `Core/GrouperService.cs` | ✔ | ✔ |
| 后台编排 | `Core/AdminService.cs` | ✔ | ✔ |
| 运行时状态 | `Core/AppState.cs` | ✔ | ✔ |
| 传输层 | `Web/WebRouter.cs` | HTTP 薄路由 | 不经过 |
| 界面层 | `Web/web/*.html` / `Desktop/Views/*.xaml` | 浏览器 | WPF 原生 |

`WebRouter.cs` 只做「解析请求 → 调 Core → 写响应」，不含业务逻辑；
`AdminService` / `GrouperService` 是唯一实现，两端行为因此不会分叉。

为了让桌面端能强类型使用，原本散在各处的**匿名类型返回值全部改为具名类型**
（`SectionListResult`、`MappingStats`、`ZongheListResult`、`JieduListResult`、
`LixiangSectionReport` 等），属性名与顺序保持不变，依赖 `Js.Http` 的
camelCase 命名策略保证 **JSON 输出逐字段一致**（已由 149 项只读对拍覆盖）。

### 9.2 桌面端自检（`--selftest`）

67 项检查，分五组：

| 组 | 内容 |
|---|---|
| 数据链路 | 病种库 / 手术库 / 映射表 / 综合病组 / 立项指南 / **德州市物价** / **项目技术规范** 载入 |
| 分组正确性 | 5 条与 xunit、Node 对拍同口径的断言 + 联想 + 批量 + 模板 |
| 后台只读链路 | 各区段分页、模板导出、灰码筛选、解读检索 |
| 界面构造 | 6 个前台页 + 6 个后台页 + 4 个窗口，逐个构造并强制布局（含 **关联详情弹窗** 与详情卡关联行模板的展开断言）；编辑对话框 `Cb` 模板必须含 `PART_EditableTextBox` 且候选选中 / 自由输入的 Text 均可回显（否则 IsEditable 下拉永远显示空白） |
| 设置与换肤 | 设置默认值补全 / 口令哈希往返 / **后台登录窗回车通道** / 5 套主题 × 36 语义键齐全 / **13 组前景背景对比度** / 逐主题切换后页面重建 |

后台登录窗回车专项断言：`BtnLogin.IsDefault`、密码框 `InputMethod.IsInputMethodEnabled == false`
（输入法开着时回车会被 IME 拿去上屏，WPF 就收不到 `Key.Enter`）、回车能走到校验（用
错误口令验证，不依赖实机真实口令）、错误口令回显 + 清空密码框、同一按键多条通道重复触发已去重。

德州物价专项断言：编码格式（15 位数字，允许字母后缀）、批次行数合计 = 总条数、
各批次主项数与官方公布数偏差 ≤ 5%（后台允许增删改）、三档价格单调（一级 ≤ 二级 ≤ 三级，容忍 ≤ 3 条源文件特例）、
编码前缀 / 名称 / 加收项 / 批次 / 类别 / 性质检索全部命中；
**作废对照**：cancels 行数 > 0 时校验无缺编码/名称行、按批次筛选有效、关键词命中、
统计口径一致（有替代 + 直接废止 = 总行数）；旧数据包（无 cancels 键）退化为跳过不阻塞。
**维护往返**：模板导出 → `ReadDezhouRows` 解析逐字段一致（表头归一化匹配），
非法编码 / 空名称 / 非法性质被拦截，新增 → 编辑 → 统计重算（`Recompute`）→ 删除复原，
全程不落盘。

三库映射专项断言：德州主项 → 立项指南同名直连（实测 99.8% 精确）、指南 → 规范存在同名对、
「心脏移植术」给出原位/异位心脏移植术相似候选（字符二元组 Dice ≥ 0.62）、三方向各抽样 200 行稳定无异常。

#### 9.2.1 德州市物价数据（`data/dezhou-price.json`）

来源：德州市医疗保障局四批「规范整合」通知附件项目表（PDF 解析）：

| 文号 | 类别 | 主项目 | 执行 |
|---|---|---|---|
| 德医保发〔2025〕33号 | 器官移植 · 量表 · 综合诊查 · 护理 · 放射检查 | 97 | 2025-08-01 |
| 德医保发〔2025〕37号 | 中医类（灸法拔罐推拿）· 外治 · 针法 · 骨伤 · 特殊疗法 | 61 | 2025-10-15 |
| 德医保发〔2026〕3号 | 骨骼肌肉系统 · 产科 · 超声检查 | 152 | 2026-03-01 |
| 德医保发〔2026〕11号 | 放射治疗等 15 类 | 1096 | 2026-05-01 |

共 2,793 行（含加收项 1,198 / 扩展项 187），主项目 1,408 与官方口径一致。
每条含：15 位项目编码（`kind` 由编码第 12–15 位判定主项/加收/扩展）、项目名称、服务产出、
价格构成、计价单位、**三级/二级/一级价格**、是否医保支付、自付比例、限定支付范围、计价说明。

**类别「使用说明」**（2026-10-03 新增）：每份通知的每个类别项目表末尾都有一段「使用说明：」
编号条款（该类项目计价与收费的通用规则，如加收项/扩展项定义、儿童加收 20%、物耗范围等），
逐类抽取后存入 `meta.batches[].categories[].usage`（字符串数组），**28 个类别 / 327 条全覆盖**。
前台「德州物价查询」页：选中具体类别 → 上方面板显示该类说明（可收起、限高滚动）；
只选文号 → 按类别分组显示该文号全部说明；详情卡另附本类前 2 条摘录。
再生成方式：`.tmp/dezhou/parse_usage.py`（按类别页区间从后往前找「使用说明：」切块，
中文相邻空格清理）→ `--import-dezhou-usage usage.json`（按「文号+类别」贴回 meta）。
`Recompute()` 与 `--import-dezhou` 重导项目表都会按 (文号, 类别) **原样保留**说明，不会丢。

**常用物价收藏**（2026-10-03 新增）：独立文件 `data/dezhou-favorites.json`（与物价主数据分离，
重导物价不丢收藏），以 15 位编码为键、记录收藏时的文号与名称仅作展示，最近收藏排最前。
前台表格行首 ★ 一键收藏/取消（即时落盘）；筛选区「仅看收藏 (N)」开关只看收藏项；
`DezhouFavoritesStore` 挂在 `AppState.DezhouFav`，`ListRows` 以可选 `filter` 注入收藏过滤。

**前台导出**（2026-10-07 新增）：筛选区「导出：全部结果 / 仅当前页 / 仅收藏」+「导 出」按钮，
SaveFileDialog 存 xlsx（NPOI，`Core/DezhouExport.cs`）。列与后台导入模板完全一致（17 列，
`DezhouPriceStore.Columns` + `AdminService.DezhouField`），**导出件可直接再导回后台**；
覆盖到类别使用说明时自动附第二张表「类别使用说明」（文号 / 类别 / 序号 / 说明），
无说明则不加空表。文件名 `德州物价_{范围}_{yyyyMMdd-HHmm}.xlsx`，完成后状态栏计数 + 轻提示。

再生成方式：`.tmp/dezhou/parse.py`（pdfplumber 解析四份 PDF）→ `postprocess.py`（统一口径）→
`Dip3Grouper.Desktop.exe --import-dezhou <raw.json>`（规范化落盘，字节格式由写盘器保证）。

**旧版物价**（2026-10-10 新增）：《德州市医疗服务项目价格对码表——发县市区版（241230）》
（德医保发〔2024〕45号，规范整合前的旧价格项目），存 `dezhou-price.json` 的 `legacy` 键，
6,589 行 / 11 个归集口径；含 45 号码、地纬执行编码、国家项目代码/名称、内涵、除外内容、
三级/二级/一级价格、医保支付、自付比例、限定支付范围、「本次新公布项目」标记。
运行时与 `cancels` 联查（懒构建索引）自动判定每行 **已作废 / 现行**，已作废行给出
**现行标准**（对应新项目，一对多逐条链接，点击跳「现行项目」按编码检索）——即「旧版 → 现行」的全链对照。
前台物价查询页第三分段「旧版物价」，状态列红/绿着色 +「状态不限/仅已作废/仅现行」筛选。
注意：`legacy` 行的 JSON 键是解析脚本输出的短键（`p3/p2/p1/ins/self…`），
`DezhouLegacyItem` 已逐一挂 `[JsonPropertyName]` 对齐——改动解析脚本键名须同步模型。
再生成方式：`.tmp/dezhou/parse_legacy.py`（openpyxl 解析 xlsx → 三份 dezhou-price.json）。

结果写到 `%TEMP%\dip3-desktop-selftest.log`，退出码 0 表示全绿。
`publish-desktop.bat` 在打包末尾会自动跑一次，**自检不过就不算发布成功**。

#### 9.2.2 项目技术规范数据（`data/guifan.json`）

来源：《全国医疗服务项目技术规范（2023 年版）》（国家卫生健康委 / 国家中医药管理局 / 国家疾控局）
正文 PDF（2162 页）解析。11 章 11,516 个项目（含不计入总数的辅助操作章 29 项），
**逐章项目数与官方《使用说明》表 1 完全一致**（167 / 64 / 1754 / 612 / 855 / 6012 / 1210 / 98 / 314 / 401 / 29）。

每条含规范正文的 17 个要素：项目编码（8 位字母数字）、中英文名称、项目内涵、必需 / 可选耗材、
低值耗材分档、基本人力消耗及耗时、技术难度、风险程度、人力资源消耗相对值、计量单位、说明、
特殊情况资源消耗调整系数、收费票据分类、会计科目分类、病案首页费用分类；外加所属
类别（综合 / 诊断 / 治疗 / 康复 / 中医 / 辅助操作）、章 / 节 / 组层级与 PDF 页码。

前台「项目技术规范」页按 类别 / 章 筛选 + 关键词检索（编码前缀 / 名称 / 内涵 / 耗材 / 拼音），
选中行展开详情卡展示全部要素。数据约 10 MB，`AppState.Guifan` **首次使用时才载入**，不拖慢启动。

再生成方式：`.tmp/guifan/parse_guifan.py`（pdfplumber 按表头映射解析，直接产出 `data/guifan.json`）→
`Dip3Grouper.Desktop.exe --import-guifan <raw.json>`（规范化落盘）。解析要点：
表格**列数会变**（辅助操作章仅 5 列），必须按表头名映射而非固定列位；节行有两种形态
——`HK | (九)循环系统` 与区间形式 `HK-HM | (九)循环系统`；换行接法按两侧字符决定
（中文↔中文直接拼接、后接英文/数字补空格）。

#### 9.2.3 三库互相映射（`CrossMap.cs`）

德州物价（本地落地价格）、立项指南（国家规范整合口径）、项目技术规范（2023 年版 17 要素）
三个详情卡互相追加「关联」块，命中行带 **同名 / 相似** 徽章：

| 方向 | 依据 | 效果 |
|---|---|---|
| 德州主项 → 立项指南 | 名称同名直连 | 实测精确 99.8%（德州落地本就是指南整合项目） |
| 立项指南 ↔ 技术规范 | 同名直连 + 字符二元组 Dice ≥ 0.62 的 top3 | 国家整合时普遍改名（心脏移植术 → 原位/异位…），精确仅 1.2%，相似候选覆盖约 35%，**标注「相似」供人工核对，非官方映射** |

关联行摘要：立项指南 = 类别 · 计价单位 · 区段；德州物价 = 15 位编码 · 三级价格 · 文号 · 主项/加收/扩展；
技术规范 = 8 位编码 · 章 · 难度/风险/资源消耗。索引为首次使用时构建（约 1.5 万名称的二元组倒排），不影响启动。

**打开速度与检索体验**（`Bench.cs` / `Debounce.cs` / `--bench` / `--shot-page`）：

- 先测后改：`--bench` 把页面打开与检索链路各阶段耗时打到控制台。实测（Release）：技术规范
  首开 ≈ 0.5s（JSON 解析 240ms + 跨库索引 140ms + 首屏 50 行元素树/布局 ≈ 500ms）、
  首次拼音检索 730ms（1.15 万条名称现转拼音）。
- ⚠ **懒加载库的 Loaded 标志必须在解析完成后置位**：GuifanStore.Load 曾把 `Loaded = true` 放在
  解析之前——后台预热线程先进入时，UI 线程的 `EnsureLoaded` 立即返回并读到空 Items，
  且前台 `_loaded` 已一次性置位不再重试 → 技术规范页打开空白、点查询才恢复。
  已改为解析完成后置位，并加 8 线程并发载入一致性自检（67 项）。

- 启动空闲预热：主窗口显示后按 ApplicationIdle 串行预热数据页（载入 + 强制布局），
  后台线程预热规范 JSON / 拼音缓存 / 跨库索引——点开页签近似瞬时（`MainWindow.PrewarmDataPages` / `App.WarmupAsync`）。
- **预热队列按数据量排序（2026-10-25）**：`ViewLib（病种库，最重）→ ViewDz（德州物价）→
  ViewLx（立项指南）→ ViewGf（技术规范）`；预热中输入优先级高于 idle，用户点击仍即时响应。
- **首次渲染成本只能靠预热吸收**：bench 二分证实各页「首页 50 行首次渲染」0.2~1.7s
  （模板实例化 + JIT），换页 0.2ms——首次慢不是数据慢，是 WPF 渲染管线慢。
  ⚠ `ScrollViewer.CanContentScroll=False` 会**禁用行虚拟化**（像素滚动），
  曾让德州物价首开从 ~0.7s 涨到 2.0s；已改回 True（像素级平滑滚动与虚拟化二选一，取虚拟化）。
  修复后 bench：技术规范 527→256ms、立项指南 457→211ms、病种库页 207ms、
  德州物价首开 2,023→1,664ms（余量为渲染管线本身，预热吸收后点击无感）。
- ⚠ **对 `Visibility=Collapsed` 的元素预热等于没做（2026-10-27）**：`Measure()` 对折叠元素
  直接返回 0×0，布局树根本不下传，DataGrid 一行都不会生成——此前「只调 Measure/Arrange」的
  预热全部白做，用户首开德州物价照样 1.6s。预热必须临时置 `Visible + Opacity=0 +
  IsHitTestVisible=false`，走完 `Measure → Arrange → UpdateLayout` 再还原（`MainWindow.WarmPage`）。
  改后实测预热 病种库 422 / 德州物价 586 / 立项指南 441 / 技术规范 463ms，首开 26~31ms。
- **德州物价首开的真凶是「类别使用说明」面板，不是 DataGrid**：`ScrollViewer + ItemsControl`
  一次性建出 100+ 条 `Usage.Item`（每条含一个可选复制 `TextBox`），单这一块 711ms。
  改 `ListBox` + `VirtualizingPanel.VirtualizationMode=Recycling` + `CanContentScroll=True`
  后降到 20ms（整页预热 3,514→489ms）。自检口径随之调整：面板里被实例化的 `TextBox` 数应
  `>0` 且 `<条目总数`（总数 >12 时），等于总数说明虚拟化没生效、首开必然卡。
- **页签载入进度条要「看得见」需要三件套**：① 首次判定不能用 `DataReady`（预热把数据取好后
  恒 false，条永不出现），改按 `_visited` 集合；② 最短显示 480ms，否则 30ms 的载入一闪就没；
  ③ 收起前先让滑块跑到右端（180ms）再淡出（140ms）——半路消失看着像卡断，用户会说「没显示」。
  `--probe-nav <out.png>` 会抓「首开瞬间」「收尾」两帧并打印 `slideX / trackOpacity`。
- ⚠ **`_showSeq` 只能在确认「首次进入」之后自增**：一次导航可能触发两遍 `ShowView`
  （RadioButton 换组时 Unchecked / Checked 各来一次），序号若在方法开头就 ++，第二遍会把第一遍
  排队中的载入任务判成过期 → **页面切过去了、数据永远不载，就是一整页空白**。
  同理，载入任务不因轮次过期而跳过（`EnsureLoaded` 幂等，折叠页的 `UpdateLayout` 是 0 成本），
  只有进度条归新一轮管。回归探针 `--probe-rapid`：预热未完成时毫秒级连点四页，要求每页都出行
  （修前 dz/lx 为 0 行，修后各 50 行）。
- 详情块懒构建：行 VM 的 Blocks 只在选中行（详情展开）时构建，首屏不再为 50 行白做跨库相似度查询。
- 检索防抖：关键词 280ms 防抖自动检索（回车 / 查询按钮仍立即执行，清空会取消未触发的检索）。
- 多条件检索：规范 = 类别/章/计量单位/耗材；物价 = 批次/类别/性质/医保支付；指南 = 区段/类别/加收扩展。
  关键词全库支持**全拼与首字母**（xzyz → 心脏移植、ptmzzc → 普通门诊诊察），拼音缓存后台预热。
- ⚠ WPF DataGrid 的 star 列分配不可靠（全宽下也会被压到最小宽 20，名称列整个消失——离屏
  取样 `--shot-page` 当场抓出）。三库前台 + 后台德州的名称列改为 SizeChanged 显式把
  「表格宽 − 固定列合计」分给名称列，勿回退为 star。

**关联链接与详情弹窗**（`RelatedLink.cs` / `Related.cs` / `RelatedWindow.xaml`）：每条关联行都是可点击链接
（蓝字 + 悬停下划线 + 手型），点击弹出「关联详情」对话框展示目标条目的完整详情（字段块与列表展开的
详情卡同源：`Related.TryLoad` 按 (库, Id) 定位条目后复用各库前台 `BuildBlocks`，规范条目另含量表条）。
弹窗内可继续点击关联条目**原地推进**（历史栈 + 「← 返回上一级」/退格键回退），Esc 关闭。
共享模板 `Block.Tpl` / `Line.Tpl` 与链接样式 `Link.Line`、量表样式 `Meter.Bar` 集中在 `Themes/Data.xaml`
（⚠ 必须放在字典末尾：`StaticResource` 只能引用上方已定义的键，此前置顺序错误曾由自检第 46 项当场抓出）。
离屏取样：`--shot-related <库:Id> <输出.png>` 可不开窗渲染弹窗 PNG，用于自动化核对版式。

### 9.2b 选中复制与右键菜单（Tx.Sel / RowMenu）

- **可选中文本**：详情卡字段值 / 编码徽章 / 项目名称 / 价格行 / 类别使用说明正文统一改用
  `Tx.Sel`（`Themes/Data.xaml`，只读 TextBox：无边框透明底、自动换行、外观与 TextBlock 一致），
  支持拖选 + Ctrl+C；`ContextMenu` 显式置空，右键统一交给所在行的业务菜单。
- **右键菜单**：`RowMenu.cs` 附加属性挂到四个表格（物价 / 立项指南 / 解读辅导 / 技术规范）、
  使用说明条目与关联弹窗；右键即选中该行并按行数据动态出菜单——物价：加入 / 移出常用收藏、
  复制（选中文字 / 编码 / 名称 / 本行 TSV / 详情全文）、打开关联（最多 5 条）；其余页为复制 + 关联。
  复制 / 收藏后有鼠标位置轻提示（1.1s 自动消失）。菜单样式 `Ctx.Menu / Ctx.Item / Ctx.Sep`
  在 `Themes/Controls.xaml`。离屏核对：`--shot-menu dz|lx|gf <输出.png>`、`--shot-page dzsel`（展开首行详情）。

### 9.3 绿色版布局

```
dist/DIP3-Desktop/
├─ Dip3Grouper.Desktop.exe    + 一堆 .dll（自包含 .NET 8 运行时，免安装）
├─ config/dip3-config.json
├─ data/*.json
├─ 启动 DIP3 桌面版.bat
├─ 启动 DIP3 桌面版 - 基层兜底.bat
└─ 使用说明.txt
```

数据根目录解析优先级（`App.ResolveDataRoot`）：

1. `--home <目录>`
2. 环境变量 `DIP3_HOME`
3. exe 同目录（绿色版布局：同目录存在 `config/` 或 `data/`）
4. `Core/AppPaths` 的默认逻辑（自 `BaseDirectory` 向上找含 `web/index.html` + `config/` 的目录）

> ⚠ 绿色版**必须整体解压后使用**：只把 exe 单独拷走会因为找不到
> `config/` 与 `data/` 而启动失败。

#### 小屏适配（2026-10-21）

低配机常是 1024×768（个别还开 125% 缩放）。窗口默认 1300×870 居中时，
顶部自绘标题栏会被推出屏幕外——实机表现就是「头部菜单导航不见了」。
两层修复（`WinFit.ClampToWorkArea` + `MainWindow.ApplyCompactNav`）：

1. **尺寸钳制**（`SourceInitialized` 时）：`MinWidth/MinHeight` 先降进工作区，
   `Width/Height` 超出再缩，位置重居中。主窗与后台窗都挂了；多屏正常摆放不受影响。
2. **紧凑导航**（`SizeChanged` 时按宽度分档，拉大自动还原）：
   - `< 1180` DIP：副标题收起、页签边距 18→6、「后台管理」→「后台」；
   - `< 900` DIP（≈1024×768@125%）：品牌只剩 logo、页签字号 14→11.5。
   - `Nav.Top` 模板的水平边距由硬编码 `Margin="18,0"` 改为 `{TemplateBinding Padding}`，
     代码才能收缩（样式默认值不变）。
   - 2026-10-25：**「新项目征求意见」页签已从导航移除**（数据转正进主库后该页不再需要
     一线入口），导航现为 6 项；`DraftPriceView` 类与离屏诊断入口保留，需要时可随时恢复。

诊断入口：`--probe-main gf out.png@1024x728` / `@819x582`（@后缀模拟小屏，
离屏会手动对齐 clamp 后的 MinWidth，否则 1060 下限把小尺寸顶回去）。

### 9.4 分发打包与实机验证

**打包**（产物 288 个文件，原 187 MB → zip 70.9 MB）：

```bat
python tools/make_zip.py dist/DIP3-Desktop-x86 dist/DIP3-Desktop-win-x86.zip
```

zip 内顶层目录为 `DIP3-Desktop-x86/`，收件人解压一层即是可运行文件夹。
`make_zip.py` 用 UTF-8 标志位写中文文件名（`使用说明.txt` / 两个启动 bat），Windows 自带解压可直接识别。

#### 32 位（win-x86）分支

同一个发布脚本带参数即可出 32 位包，源码零改动：

```bat
publish-desktop.bat            :: 默认 win-x64 → dist\DIP3-Desktop\
publish-desktop.bat x86        :: 32 位      → dist\DIP3-Desktop-x86\
```

- 自包含 .NET 8 运行时按 RID 分别还原（`Microsoft.WindowsDesktop.App.Runtime.win-x86`），
  目标机同样**免装 .NET**。
- 依赖全为纯托管（NPOI / ToolGood.Words / CodePages），**无原生 DLL**，
  故 x86 下不存在位数不匹配问题；已核对 `dist\DIP3-Desktop-x86\` 内 288 个
  PE 文件全部为 `i386`，无 amd64 残留。
- 两个分支的目录、数据、启动脚本、自检完全一致，只有 exe 的架构不同
  （apphost：x64 = `amd64`，x86 = `i386`；`Dip3Grouper.Core.dll` 是 AnyCPU，
  PE 头恒为 `i386`，属正常现象，不能据此判断位数）。
- 32 位进程地址空间上限约 2 GB，本产品数据量（物价 6,589 条 / 征求意见 1,089 条）
  远未触顶；仅导出超大 xlsx 时内存曲线比 x64 陡，属可接受范围。

**发布前实机验证清单**（2026-09-30 已全部通过）：

| 验证项 | 做法 | 结果 |
|---|---|---|
| 发布产物自检 | 在 `dist/DIP3-Desktop/` 内跑 `--selftest` | 32/32 通过 |
| 陌生路径可运行 | 整体拷到含**中文 + 空格**的临时路径再跑 `--selftest` | 32/32 通过 |
| 数据确实读本地 | 在拷贝里把 `config/` 临时改名再跑自检 | 数据链路 7 项 FAIL（证明读的是自己目录，无开发路径残留）；改回后恢复 32/32 |
| 主窗口真实渲染 | 启动后截图 | 单条分组页正常 |
| 后台真实渲染 | 模拟点击「后台管理」「病种库管理」后截图 | 概览四卡 + 病种库管理表格正常 |

截图脚本：`tools/capture_window3.py`（置前后屏幕拷贝）、`tools/click_and_shot.py`（点窗口内相对坐标后截图）。

> 坑：对 WPF（D3D 硬件渲染）窗口直接 `PrintWindow` 即使带
> `PW_RENDERFULLCONTENT` 也会截出**全黑图**；必须先把窗口真正置前
> （`AttachThreadInput` + ALT 键微触发绕过前台锁），再用
> `CopyFromScreen` 截屏区域。

### 9.5 界面节奏（留白规范，改布局时请遵守）

页面/卡片统一一套间距，避免"这块紧那块松"：

| 位置 | 值 |
|---|---|
| 页面外边距（左右 / 上下） | 20 / 16 |
| 页面标题 → 内容 | 14（表格类页 12） |
| 卡片内边距（`Card` / `ResultCard`） | 16 |
| 卡片之间 | 16 |
| 表单标签列宽 / 行间距 | 92 / 9 |
| 区块之间（工具条、胶囊、搜索行 …） | 10～12 |

**内容占满窗口**：前台四页与后台四页都不再限制 `MaxWidth` 居中，
直接用 `Margin="20,16,20,16"` 撑满可用区。竖向自适应：

- 单条分组：标题 `Auto` + 内容 `*`；**表单卡 `VerticalAlignment=Top`（自然高度，过高时卡内滚动）**，
  **结果卡撑满剩余高度**（内容过高时卡内滚动），空态提示按 `ViewportHeight` 绑定居中。
- 列表类页（批量 / 病种库 / 立项 / 后台各页）：`DataGrid` 放在 `*` 行自动吃满高度。

### 9.6 清空语义（一致行为）

| 位置 | 行为 |
|---|---|
| 单条分组 `清 空` | 清空四个编码框 + 年龄 + 出生体重，年龄单位复位"岁"，取消勾选映射，结果区回到空态，焦点回主诊断 |
| 病种库查询 `清 空` | 清空关键词并**保持当前区段**重载第 1 页，焦点回搜索框 |
| 立项指南 · 价格项目 `清 空` | 清空关键词并**保持当前区段**重载第 1 页 |
| 立项指南 · 解读辅导 `清 空` | 清空关键词并**保持当前期次**重载列表 |

统一原则：清空只清"输入"，不动用户已经选好的区段 / 期次 / 页签上下文；
清空后立即重载并显示默认列表，不做二次确认。

### 9.7 主题系统（运行时换肤）

五套主题（经典蓝 / 青碧 / 靛紫 / 朱砂 / 石墨深色）定义在 `Desktop/Theming/ThemeManager.cs`，
每套是一张「语义键 → 十六进制色」的完整色板（36 个键），与 `Themes/Colors.xaml` 的默认值一一对应。

换肤原理：`ThemeManager.Apply(id)` 把 `App.Resources` **顶层**的 `B.*` / `Color.*` 条目
整体替换成新画刷 —— 顶层字典优先于合并字典，且用色处全部写的是 `DynamicResource`，
所以替换即刻生效，无需重建窗口。

两条硬规矩（违反必出事故，自检第 38/39 项会拦）：

1. **用色处一律 `DynamicResource`**。`StaticResource` 在 XAML 载入时就固化成旧对象，
   换肤不会跟着变。代码里同理：不要 `FindResource("B.Xxx")` 取画刷，
   要用 `Theming/ThemeExt.cs` 的 `ThFg/ThBg/ThBorder`（内部走 `SetResourceReference`）。
2. **新增颜色先登记语义键**。在 `Colors.xaml` 声明 + 五套主题各配一值，
   不允许页面里写死十六进制 —— 深色主题下会白底白字。
   自检会做 WCAG 对比度校验（正文 ≥ 7:1、次要文字 ≥ 4.5:1、强调色上白字 ≥ 3:1）。

### 9.8 病种库版本管理 · 操作审计 · 客户端自动更新（2026-10 新增）

三块新能力都实现在 Core（`VersionStore.cs` / `AuditLog.cs` / `AppUpdate.cs`），
桌面端后台各有一页，Web 端复用同一套 Core（写操作同样进审计）。

**版本管理**（后台 →「⟲ 版本管理」）：把病种库那一组文件（`config/dip3-config.json`、
`data/config-meta.json`、`data/zonghe.json` —— 直接复用 `DataCatalog` 的「病种库」分组白名单，
两处不会走偏）整份快照到 `data/_versions/<时间戳>/`，支持历史列表、两版差异比对
（逐区段统计增/删/改 + 主键样例，报告可导出 txt）与一键回滚（当前内容先备份到
`data/_backup/` 再原子上位替换，与数据更新的安装逻辑同构）。

- 快照时机：**每次改动之前**自动生成（整库导入 / 区段导入 / 综合病组导入 / 后台逐行增删改）；
  指纹 = `config/dip3-config.json` 的 SHA-256，内容与上一版一致时**不重复建版**
  （自动快照也不进审计，避免一次导入记两条）。
- 手动「新建版本快照」用于打里程碑（如「2026 版定稿」），手动建版 / 回滚 / 删除均写审计。
- 桌面端保存病种库行后调 `App.State.ReloadAll()` 即可，无需重启。

**操作审计**（后台 →「☰ 审计日志」）：`data/audit-log.json` 记录「谁、何时、对什么做了什么、
成功与否」，环形封顶 3000 条（超出丢最旧），支持按动作 / 对象 / 关键词筛选、只看失败、
导出 CSV（UTF-8 BOM + RFC4180 转义）与清空（清空本身也留痕）。

- 「谁」分两层：桌面端登录后 `AuditLog.SetDefaultActor(user, "桌面端")` 设一次全局身份；
  网页端在 `WebRouter.Handle` 里对每个需要鉴权的请求 `AuditLog.Scope(user, "网页端")`
  （`AsyncLocal`，自动流进其内被 await 的 Core 调用，请求结束自动还原）。
- **避免重复记账**：8 个导入接口由 `AdminService` 自己记录（知道解析 / 新增 / 更新行数，
  信息更全），路由层用 `CoreAuditedPaths` 排除；其余 POST 写接口由路由层统一记。
- 该文件是**本机私有数据**（已加入 `DataCatalog.PrivateRelPaths`）：
  它是每台机器各自的操作记录，永不参与数据分发，避免一次同步把别院的日志覆盖过来。
- 写失败静默（审计永远不阻断业务）。

**客户端自动更新**（后台 →「数据更新 · 客户端更新」卡）：从局域网更新源（与数据源共用地址）
检查程序版本 → 下载安装包 → SHA-256 校验 → 解包 → 生成批处理 → 退出进程后替换并重启。

- 服务端发布方法：把绿色版打包成 `data/app-update/package.zip`，再放一份
  `data/app-update/manifest.json`（用 `AppUpdateClient.BuildServerManifest(version, notes)`
  生成 —— 大小与哈希由程序自己算，**不要手填**）。未发布时检查接口回「已是最新」，不报错。
- 客户端版本号取 csproj `<Version>`（当前 1.5.0；Core 读不到时回落 `AppVersion.Fallback`），
  语义化比较（`1.5` ≡ `1.5.0`）。
- 替换脚本 `apply-update.cmd` **必须纯 ASCII**（cmd 按 OEM 代码页读文件，中文会变乱码命令）；
  含中文的路径一律经命令行参数（CreateProcessW 宽字符通道）传入，脚本里只用 `%STAGE%`
  `%APP%` `%EXE%` 占位。脚本先等父进程 PID 退出（exe/dll 被占用时无法原地替换），
  再 `xcopy` 覆盖、`start` 重启、自删。
- 也支持「从本地更新包安装…」（离线机器：导出更新包 → U 盘 → 本地安装，走同一替换链路）。
- 启动时静默检查一次（可关），有新版只弹托盘气泡提示，不打断使用。

自检第 80–86 项守门（版本快照 / 去重 / 差异 / 回滚 / 审计检索与 CSV / 身份作用域 /
版本号比较 / 更新全链路）；全链路用例跑在临时根 + `AppDirOverride` 重定向的程序目录，
绝不触碰真实安装目录。

### 9.9 德州物价「新项目征求意见」稿（2026-10 新增，前台独立页）

批次还在**征求意见阶段、尚未正式发布**时的过渡方案：数据与主库完全隔离，
前台可见但全程标黄，绝不误导一线。

- **存储隔离**：写 `data/dezhou-draft.json`，与主库 `data/dezhou-price.json` 互不掺和。
  价格未生效不进现行标准；「拟作废」对照不点亮主库已作废红标（`CancelIndex()` /
  `CancelOf()` 里 `Pending=true` 的行被跳过）；正式发布换文号也不必全库改字符串。
- **导入**（命令行，解析通知 xlsx 的附件 1–18：新增/废止/映射三类 sheet）：
  `Dip3Grouper.Desktop.exe --import-dezhou-draft <xlsx> [--label 名称] [--doc 文号] [--draft-note 说明]`
  导入器强制 `Status="draft"` / `Pending=true`；sheet 按名后缀归类，类别名裁尾部
  「类」字后归一（附件 8 药学**类** ↔ 附件 17 药学废止）；映射表两段式表头按
  「第 2 个同名列」定位，新码纵向合并向下继承；废止表缺新项目侧时用映射表按
  oldCode 回填（未命中 = 直接废止）。
- **前台**：主窗口曾新增「新项目征求意见」页（NavDraft），四视图分段切换——
  新增项目（草稿黄底 `DgRow.Draft` + 详情卡「未生效」角标）/ 废止对照（有替代 /
  直接废止）/ 映射关系（两段式原始表）/ **新旧价格比对**（新价 ↔ 旧价，含差额与涨幅）。
  顶部黄条（DraftBg 语义键，五套主题各配）；筛选、检索（280ms 防抖）、分页、Excel 导出与德州物价页同款。
  2026-10-25：**该页签已从主窗口导航移除**（转正后不再需要一线入口）；
  `DraftPriceView` 类、数据文件与离屏诊断入口（见节末）保留，档案可查。
- **新旧价格比对**（2026-10-13 追加）：每条拟新增项目先经映射表（其次作废对照）找到旧项目编码，
  再查**旧版物价（45 号对码表）**取旧三级价；比对基准取数顺序 legacy → cancel → none 三态：
  `legacy` 有旧价可比（513 条，算差额与涨幅）/ `cancel` 仅作废对照（1 条，无旧价）/ `none`
  纯新增（575 条，无旧项目）。差额列涨红跌绿（`B.Danger` / `B.Ok` 语义键 + DataTrigger，
  中国行情惯例）；旧价是文本（如「自主定价」）的行差额留「—」，状态栏单列「旧价非数字」。
  比对视图可按「有旧价可比 / 纯新增项目」过滤，导出含汇总行。取数在
  `DezhouDraftStore.Compare()`（懒构建 + `InvalidateCompare()` 缓存失效），
  旧版物价数据源由 `AppState.Init` 注入（`LegacySource`，数据层互不持引用）。
- **表格优化与排序**（2026-10-13 追加）：四个视图表头点击排序（`Sorting="OnSorting"`
  接管 + 每视图记忆方向，切回箭头还在）；排序**整表进行后再分页**（分页下只排当前页会错乱，
  新增视图因此改用 `AllItems` + 本地分页）；价格列按数值排（公式与解析不出排最后），
  差额/涨幅按原始 double 排（字符串 "+" 会排错）。公式价（`ROUND(G9*0.2,0)`）表格里缩显
  「公式」灰字，完整式子在单元格提示；占位文本（本批纯新增 / 无旧价）与「仅作废对照」
  状态分别灰显 / 警示色，全部走 `B.Muted` / `B.Warn` 语义键触发器。
- **主库兼容**：`DezhouPriceItem.Status`（默认 ""）与 `DezhouCancelItem.Pending`
  （默认 false）为新字段，历史数据无键 → 反序列化得默认值 → 行为零变化。
- **转正（2026-10-25 已执行，2026-10-27 被正式版取代）**：`Dip3Grouper.Cli merge-draft` 把本稿并入主库，
  当时用暂定文号 **`德医保发〔2026〕24号（暂定）`**；2026-10-27 官方发文版（**德医保发〔2026〕25号**）
  落地后该批次已整批撤下并按正式版重建，详见 §9.11。
  动作**幂等**：items 同码不覆盖、作废按 (旧码,新码) 去重、批次按 doc 去重；执行前自动备份
  `data/_backup/merge-draft-<ts>/`（主库 + 草稿两份）。
  合并结果：items 2793 → **3,880**（+1,089，2026-10-27 清理 2 条同码空价行后由 3,882 修正）、cancels 3950 → **5,107**（+1,157，3 条与主库重复已去重）、
  批次 4 → **5**（新批次携带 9 个类别 + 107 条类别级使用说明）、草稿 `status=draft → active`
  （本页保留作档案视图，黄条显示转正说明）。
  配套改动：`MergeInto` 现在会用 `Meta.Doc/Exec` 兜底填**行内 doc/exec**——`items` 与
  `cancels` **两者都要兜底**（主库批次/类别筛选完全靠行内 `doc`；首版只兜了 items，
  导致 1,157 条作废行在「作废对照」批次下拉里整个消失，重跑幂等合并时补齐并顺带修复历史账），
  并聚合草稿 9 个子批为一条主库批次（含 usage）。自检新增断言：作废行 doc 不得为空、
  doc 必须存在于 `meta.batches`；items 同理（防同类回归）。
  诊断入口：`--shot-page dzcdoc:24号`（切作废对照 + 按批次下拉筛选，核对批次维度可见性）、
  `--shot-page dzlegacy:编码`（旧版物价按编码检索，核对作废状态着色）。
- **作废判定与「现行标准」索引必须分开**（2026-10-25 二次修复）：旧版物价的「已作废/现行」
  原先复用 `CancelIndex()`，而该索引只收「有替代（NewCode 非空）」的行 →
  **462 条直接废止（无替代）的旧项目全被误标「现行」**。现拆成两个懒构建缓存：
  `CanceledSet()`（`Pending=false` 且旧码非空即算作废，供 `LegacyCanceled` / 筛选 / 统计）
  与 `CancelIndex()`（只收有替代的行，供「现行标准」列与链接）。
  修复后旧版物价：已作废 3,476 → **3,807**、现行 **2,782**；`330804062a` 等同族加收项
  正确显示「已作废」+「（直接废止·无替代项目）」。三处缓存失效点（Load / Import / ImportCancels）
  都要同时清 `_canceledSet`。
  ⚠ 用 `--dry` 预演时注意：命令行解析器必须显式注册 `--dry` 这个 flag，否则它会落进位置参数、
  预演失效直接真执行（本次踩过，数据正确且已备份）。
  ⚠ 新批次医保支付/自付比例为空（原征求意见 Excel 无这两列），自检第 12 项已把「暂定批次」单列
  不计入异常口径。→ **2026-10-27 正式版已补齐这三列，暂定批次撤下，该豁免按文号动态匹配（命中 0）。**
- 离屏截图入口：`--shot-page draft|draftsel|draftcancel|draftmap|draftcmp|draftcmpn|draftq:词|draft@宽x高`。

### 9.10 德州物价四视图、新旧比对与加收项公式价（2026-10-27 新增）

主库页 `DezhouPriceView` 现在是**四段切换**（`SegLive / SegCancel / SegLegacy / SegCmp`，
筛选条共用、各视图按需显隐）：

| 视图 | 内容 | 关键点 |
|---|---|---|
| 现行项目 | 3,880 条规范整合项目 | 三级价格 / **扣减基本物耗** / 医保支付 / 收藏 / 导出 |
| 作废对照 | 5,107 条「旧项目 → 新项目」 | 新编码列可点跳现行项目，批次下拉按 `doc` 过滤 |
| 旧版物价 | 45 号对码表 6,589 条 | 状态列联查 `CanceledSet()`，红「已作废」/绿「现行」 |
| **新旧比对** | 6,768 行（现行 ↔ 旧版物价） | 差额 / 涨幅按三级价，涨红跌绿 |

**新旧比对的取数路径**（`DezhouPriceStore.CompareRows()`，懒构建 + `InvalidateCompare()` 失效）：
现行项目编码 → 作废对照反查旧码（`Pending=true` 的拟作废行不参与）→ 旧版物价取旧三级价。
三态 `OldSource`：`legacy` 有旧价可比（4,519）/ `cancel` 仅作废对照（75）/ `none` 纯新增（2,174）。
差额 / 涨幅复用 `DezhouDraftStore.FillDelta`（草稿比对与主库比对**同一套算法**，改一处两边一致）。
范围下拉：全部 / 有旧价可比 / 仅作废对照 / 纯新增。

**加收项的 Excel 公式价（本轮修复）**：征求意见稿里「儿童（加收）」等加收项的价格列是公式
`ROUND(G42*0.2,0)`（主项同档价 × 加收比例，四舍五入取整），合并进主库后**原样保留在数据里**
（375 条、1,125 个价格格）。数据保留、**展示换算**：

- 主项定位：加收项编码 = 主项编码前 12 位 + `000`（`BaseItemOf`，走后加的 `_codeMap` 懒索引）。
- 换算规则 `ResolvePrice / ResolvePrices`：数字原样、`ROUND(<列><行>*k,0)` 按主项同档价 × k
  用 `MidpointRounding.AwayFromZero` 取整、文本（自主定价等）原样；形态不认识或主项缺失时原样返回。
- **四条出口统一走 `PriceOf`**：前台表格（`DzRowVm.Price3/2/1`）、详情卡「价格换算（加收项）」
  块、新旧比对 `NewPrice3`、Excel 导出（`DezhouExport.Cell`）。主项缺失时显示「—」，
  悬停说明「原表公式 … 未能匹配主项」——绝不把 `ROUND(...)` 甩给物价人员。
- 悬停说明 `PriceNote`：「加收项：主项 013310000010000 的三级价 1000 × 20% → 200
  （原表公式 ROUND(G42*0.2,0)，已自动换算）」，普通项目不加气泡。
- 后台维护表（`AdminDezhouView` / Web `/api/admin/dezhou`）**仍显示公式原文**——那里是编辑源，
  改价要保留公式语义，不能被展示层转换污染。
- 校验：自检「加收项价格口径」逐格对账（旧数据形态：展示值必须等于主项 × 比例）；
  单元测试 `DezhouPriceFormulaTests`（7 例，含半值取整、主项缺失、主项自身也是公式的兜底）。
  2026-10-27 正式版落地后数据形态变化，断言已改为双分支，见 §9.11。

### 9.11 德医保发〔2026〕25号 正式版转正（2026-10-27 新增）

**背景**：2026-10-24 官方下发《关于规范整合消化系统等 9 类医疗服务价格项目的通知》发文版本
（文号 **德医保发〔2026〕25号**），此前并入主库的 `德医保发〔2026〕24号（暂定）` 批次作废。

**清空 + 重导**：`.tmp/dezhou/import_official_25.py`（预演 / `--apply` 双模式）
= 备份 `data/_backup/dezhou-price.<ts>.json` → 删 `doc` 含「暂定」的 items + cancels →
按正式版重写 25 号批次（`id` 从 `max_id+1` 续）→ 替换 `meta.batches` → 承接类别使用说明 →
刷新 `meta.total/importedAt/source`；`legacy`（45 号对码表）**不动**。

| 口径 | 数量 |
|---|---|
| 现行项目 items | 3,880（33号 216 / 37号 150 / 3号 328 / 11号 2,097 / **25号 1,089**） |
| 其中 25 号：主项 / 加收 / 扩展 | 528 / 530 / 31 |
| 作废对照 cancels | 5,107（换文号不换行数） |
| 旧版物价 legacy | 6,589 |
| 类别使用说明 | 9 类 107 条（与暂定批次逐条一致，直接承接） |

**正式版 vs 暂定批次的三处实质差异**（编码集合完全相同，1089→1089，0 增 0 减）：

1. **379 格公式价被官方替换成固定金额**：326 格数值与 `ROUND(主项*0.2,0)` 换算逐一吻合、
   49 格留空（加收减收政策未落地）。→ 主库已无任何公式价。
2. **326 条加收项的计价说明「加收比例20%。」变空**：正式版原件该列即为空，属官方定稿精简
   （主项目 0 条丢失）。25 号批次剩 49 条仍带「加收比例20%」且三格价格全空。
3. **正式版补齐医保三列**（暂定批次 1089 行全为空）：
   医保支付 是 969 / 否 120；自付比例 0、0.05、1、职工5%·居民20%；限定支付范围 16 条。

**新增字段「扣减基本物耗价格」（`Deduct`，仅「一般治疗」类项目表有此列）**：
指项目价中含可另行收费的基本物耗金额，收费时须按此扣减。共 11 条：
注射费（静脉输液）1.5、清创缝合 2.5/3.3/4.6/9.2、换药 2.4/3.6/7.2/16.8、导尿 10、膀胱冲洗 4.9。

- 模型：`DezhouPriceItem.Deduct`（`JsonIgnore(WhenWritingDefault)` —— 空串不落盘，
  不污染其他四批历史数据格式）。
- 链路全通：`DezhouDraftParser.ParseNew` 读列 → `DezhouPriceStore.Columns` / `RowKeys` / 解析 case
  → `AdminService.DezhouFieldMap`（「扣减基本物耗价格/扣减基本物耗/扣减物耗」）+ `DezhouField`
  + `DezhouImportRow` + 导入 flat + 模板示例值 → `DezhouDraftStore.FlatOf`（转正不漏字段）
  → 详情卡 `BuildBlocks`（「扣减基本物耗价格 1.5 元（本项目价格中含可另行收费的基本物耗，收费时按此金额扣减）」）。
- 模板 / 导出 / 网页列元数据都从 `Columns.Label` 生成，加一列自动三处生效。

**自检口径同步**（数据形态变了，断言不能让守门条永远红）：

- 第 63 项改名「加收项价格口径（公式换算 / 正式版固定金额）」，双分支：
  A 仍有公式价 → 照旧逐格换算对账（兼容历史数据包 / 未来再出现公式）；
  B 已无公式价 → 验「有值即合法金额、无值允许官方留空」+ 比对行无公式残留 + 无公式悬停说明 +
  金额格占比下限。**不做全局 20% 断言**：加收/减收项本身独立定价（如「单气囊小肠镜（加收）」
  1800 与主项 333 无关），历史四批比例 0.04~1.0 并存，硬卡只会误报。
  价格列本就允许文字口径（「自主定价」294 格、「暂不定价」「基价30元/车次…」），按文字价放行。
  实测：1,728 条加收项 · 4,740 格金额合法 · 294 格文字口径 · 150 格官方留空（49 条三格皆空）。
- 第 12 项「暂定批次」豁免改为按文号动态匹配（有暂定批次就排、没有为 0），避免再次转正时口径错位。
  正式版医保列已全填，「非是/否」取值 0 条。
- 后台维护项（第 65 项）模板往返补断 `Deduct == "2.5"`。
- 单元测试 `真实数据_公式价若存在必须全部可换算…` 去掉 `NotEmpty(formula)` 硬断言，
  改为「真实数据里出现公式即必须换算干净、`PriceOf` 不得外泄公式文本」。

**CLI**：`merge-draft --doc` 默认值从 `德医保发〔2026〕24号（暂定）` 改为 `德医保发〔2026〕25号`；
`data/dezhou-draft.json` 里 3,382 行空文号回填为 25 号，`meta.note` 写明「已定稿为正式版，
本页留作征求意见稿档案视图」。原件备份 `data/_backup/dezhou-draft.<ts>.json`。

⚠ **执行日期**：~~通知正文未写具体执行日期，25 号批次 `exec` 暂留空~~
→ **2026-11-01 执行**（2026-10-27 落定）：`meta.batches[].exec` 与 1,089 行内 `exec` 已一并补，
无需重导。批次下拉、行内执行日期列、新旧比对口径同步生效。

⚠ **33 号批 2 条同码空价行清理**（2026-10-27）：`011102010010000 临床量表评估(自评)` /
`011102010020000 临床量表评估(他评)` 在 33 号批各有一条**同名同码但三档价格全空**的重复行
（带价的同码行保留），删除后 33 号 218→**216** 行、主项 99→**97**，`meta.total` 3,882→**3,880**，
`meta.batches[].rows/mains` 同步重算（自检第 62 项「批次行数合计 = 总条数」守门）。

### 9.12 物价表格表头 / 文字价被裁（2026-10-27 新增）

**症状**：现行项目表头显示成「医保支」「自付比」，旧版物价价格列显示成「自主定…」。

**根因**：`DgHead` 的 `ContentPresenter` 既不换行也不省略，列宽不足时 WPF 直接把右侧硬裁；
`Cols` 里 `Insurance = 56` / `SelfPay = 62` 连 4 个汉字都放不下（12.5px 半粗中文 ≈ 17.4px/字，
加左右内边距 20 需 ≈ 90）。

**修法（两层）**：

1. `Themes/Data.xaml` 的 `DgHead` 模板：给 `ContentPresenter.Resources` 加隐式 `TextBlock` 样式
   （`TextWrapping=Wrap` + `TextTrimming=CharacterEllipsis`）。
   ⚠ **不能**写 `TextElement.TextWrapping` ——ContentPresenter 没有这个附加属性，XAML 直接编译失败（MC3072）。
2. 列宽按「中文 17.4px/字 + 20px 内边距」重排，并把**单元格内容**也纳入：价格列会放
   「自主定价」「暂自主定价」（旧版物价 6 行 18 格）这类文字价，故三档价统一 108。

| 视图 | 调整 |
|---|---|
| 现行项目 | 三级/二级/一级价格 68→**108→106**（第七轮回落，见 §9.13）；医保支付 56→**92**；自付比例 62→**92**；**新增「扣减基本物耗」128**（插在一级价格之后，见下） |
| 作废对照 | 对应新编码 118→**126**（「对应新编码 ▸」7 字） |
| 旧版物价 | 三级/二级/一级 78/64/64→**108**；单位 52→**58**；医保 50→**58**；自付 56→**58** |
| 新旧比对 | 新三级价 70→**92**；旧三级价 70→**92**；旧项目编码 104→**110**；基准来源 88→**92** |

**现行项目表新增「扣减基本物耗」列**（2026-10-27）：此前该字段只进了模板 / 导出 / 网页维护表 / 详情卡，
**前台表格没列**，物价人员查价时看不到。本次补上：

- `Cols` 插 `("Deduct", "扣减基本物耗", 128, true, true)`，位置在一级价格之后、医保支付之前
  （与后台维护表 `DezhouStore.Columns` 的字段秩序一致（`price1 → deduct → insurance`），
  所以导出表、网页表、桌面表三处列序统一）。
- `DzRowVm.Deduct`（⚠ 别加错类：`DzLegacyRowVm` 同样有 `Price1/Insurance` 字段序列，Edit 极易命中它）
  + `ToRow` 里 `Deduct = it.Deduct`。
- 只有「一般治疗」类 11 条主项有值（注射费 1.5、清创缝合 2.5/3.3/4.6/9.2、换药 2.4/3.6/7.2/16.8、
  导尿 10、膀胱冲洗 4.9），加收/扩展项与其余批次**恒为空**（数据里就是 `null`，不是缺列）。
  截图核对：检索「静脉输液」3 行里主项显示 `1.5`，两条加收项为空 ✔。

**守门（自检第 64 项新增两段）**：
① 遍历四个视图的列定义，按 `HeadNeed(表头) = Σ(中文 17.4 / 半角 8) + 20` 断言 `need ≤ width`；
② 从库里实采「非数字的短文字价（≤6 字）」逐个断言价格列放得下。
长句文字价（「基价30元/车次（10公里以内），里程费3元/公里（往返）」）**不纳入** ——
为 3 行把价格列撑到 400+px 会让整张表没法看，这类行走悬停提示看全文。
星号列（名称列 `Width = 0`，吃掉剩余宽度）自动跳过。

⚠ 旧版物价的价格字段是短键 `p3/p2/p1`（items 才是 `price3/price2/price1`），
按长键统计文字价会漏掉整个 legacy 库（本次踩过：`textPrices` 只采到 3 个值，漏了「暂自主定价」）。

**页签进入过渡（体验）**：`MainWindow.ShowView` 给目标视图做 160ms 淡入
（`UIElement.OpacityProperty` 动画，渲染线程合成，UI 线程载数据时也在动）；首次进入尚未预热的
数据页时先亮顶部 3px 渐变滑块条（`LoadTrack` + `LoadSlide` 的 `TranslateTransform` 循环动画），
载完收起。`MainWindow.FadeMs` 静态开关：离屏探针 / 自检置 0，保证截图确定性。
诊断入口：`--shot-page dzfind:词[:sel1]`（现行项目按词检索，可顺带展开第 N 行详情）、
`--shot-page dzcmp:词`（新旧比对）、`--shot-page dzlevel:3|2|1|eq[:词]`（按医院级别筛后截图）。


---

### 9.13 价格列「看着错位」的真相 + 医院级别筛选（2026-10-27 新增）

**症状**：用户截图圈出三级/二级/一级三列，说「价格列错位」——数字右侧被顶到列末，左边空一大片。

**两次误判**（记录下来，避免以后重走）：

1. 先以为是 `TextBlock.HorizontalAlignment="Right"` 造成的。改成 `TextAlignment="Right"` +
   `HorizontalAlignment="Stretch"` 后重新截图，**逐像素比对完全一致**（数字都在 x=540–636）——
   证明对齐根本不是原因，`TextAlignment` 在 `DockPanel` 定宽子项里效果等价。
2. 再做像素测量：三列数字右边缘 x=133 / 269 / 403，列中心间隔 134–136，**列本身完全没错位**。

**真正原因：列宽虚胖**。108 这个值是按「暂自主定价」5 字（≈107px）定的，而这个值
**只存在于旧版物价**（6 行 18 格）；现行项目里根本没有它，最长的是「自主定价」4 字（≈70px，
771 格），另有「暂不定价」3 格、`\` 3 格、长句 29 字 3 格。拿旧的宽口径套现行项目，
5 位数字右侧就白留 40+px，观感即「错位」。

**修法**：现行项目三档价格列 108→**106**，依据取两者较大值：

- 表头下限：`● 三级价格` = 「●」按半角 8 + 4 汉字 ×17.4 + 内边距 20 ≈ **106**
  （选中级别时列头加 `● ` 前缀标注「本级」，见下）；
- 单元格下限：「自主定价」87px（长句行走悬停提示，不撑列，同 §9.12）。

旧版物价**保持 108** —— 那里「暂自主定价」真实存在。

**医院级别筛选**：三级/二级/一级是**价格档位**，不是三条独立记录（同一项目按医院等级定价；
某档为空 = 该级别不开展/不设价）。实测 items 3,880：三档齐全 3,829、三档全空 51、三档同价 294。

链路（Core → 桌面 → Web → 导出 → 守门）：

| 层 | 改动 |
|---|---|
| Core | `DezhouPriceStore.HasLevel(it, "3"/"2"/"1"/"eq")`；`Matches(..., level)` 追加守门；`ListRows(..., filter, level)` 尾参（`pageSize` 仍被夹在 5~200）；`PriceAtLevel` / `LevelName` |
| 桌面 | `LevelSel` 下拉（级别不限 / 三级 / 二级 / 一级 / 各级同价）插在医保筛选之前；选中级别时对应列头加 `● ` 前缀、`eq` 时把二级一级列折叠只剩三级列；`_level` **仅会话有效不落盘** |
| Web | `/api/admin/dezhou?level=` + `admin.html` 的 `dzLevelFilter` |
| 导出 | `ExportScope` 的 `Matches(..., _level)`，导出跟当前筛选口径走 |

**坑 1：`SelectionChanged` 延迟派发**。`LevelSel` 的项是运行期 `Items.Add` 出来的，
`SelectedIndex = 0` 触发的 `SelectionChanged` 可能在 `_building = false` **之后**才派发，
于是「构建时的默认值」被当成「用户手选」，把级别写进了 `settings.json`。
解法：额外 `_levelSyncing` 标志 + `Dispatcher.BeginInvoke(..., DispatcherPriority.Input)` 放行。
本轮最终**去掉了级别持久化**（`_level` 只在会话内有效），从根上避免这类误写。

**坑 2：`settings.json` 的键名是 camelCase**。`Js` 序列化用 camelCase，`DzLevel` 属性在文件里是
`"dzLevel"`。排查时按 PascalCase 查会一直得到 `None`，误判「根本没写入」。
（该键已删除，现在文件里不应再有 `dzLevel`。）

⚠ 守门教训：自检第 64 项第一版把**旧版物价**的文字价也拿来验**现行项目**，报「Price3 放『暂自主定价』
需 107px，实际 92px」。文字价必须**分库收集**（`textPrices` / `legacyTextPrices`），否则会逼着现行项目
按旧库定宽。

**守门**：
- 自检第 12 项：各级别不混入该档空价、`eq` 三档必须相等、`eq` 条数 ≤ 全量、三档皆空不能被 `3` 放行、
  级别 + 关键词组合有结果（`LevelAll(lv)` 逐页累加取全量，因为 `ListRows` 的 pageSize 有 200 上限）；
- 自检第 64 项新增 `● ` 前缀表头宽度断言；
- 单测 `医院级别筛选_只保留该档有价的项目`（5 组 Theory）、`医院级别取价_按档取且回落到三级`、
  `真实数据_级别筛选与三档价自洽`。

**实测**：`--shot-page dzlevel:2:静脉输液` 条数 3→2（儿童加收无二级价被挡掉），
底部「全部批次 · 二级医院 · 1/1 页 · 共 2 条」，列头「● 二级价格」。

**坑 3：离屏探针里 UI 事件不派发**。`DiagLevel` 首版靠 `LevelSel.SelectedIndex = i` 触发
`OnLevelChanged`，但截图里条数仍是 3、列头没 `● `——离屏路径在 `Render` 之前没人泵消息，
`SelectionChanged` 一直排在队列里没跑到。修法：诊断入口**直接落值**（`_level = level;
ApplyLevelToColumns();`），不依赖事件时序（与 `DiagSelect` 同思路）。
⚠ 凡 `--shot-page` 要模拟下拉 / 勾选，一律直写字段。


---

### 9.14 数值列居中 + 表头同步（2026-10-27 新增）

**症状**（用户截图圈出三级/二级/一级/扣减基本物耗）：数字贴着列左边，右边空一大截，"看着还是错位"。

**原因**：价格列当时是**右对齐**（`TextAlignment.Right` + `HorizontalAlignment.Stretch`）。
列宽 106 里数字只占约 40，右侧留 33、左侧留 33 —— 但右对齐的观感是「数字被推到最右端」，
在窄列里反而更刺眼；且表头是左对齐，**头尾不在一条线上**。

**修法（两层，缺一不可）**：

1. **单元格居中**：`MakeTextCol` 的第四参数语义从 `right`（布尔右对齐）改成 `align`（是否数值列），
   由 `DezhouPriceView.AlignmentOf(align, wrap)` 统一裁决：
   - 数值列 → `TextAlignment.Center` **且必须** `HorizontalAlignment.Stretch`
     （TextBlock 默认按内容宽自适应，只设 TextAlignment 完全不动 —— 这是最容易漏的一步）；
   - 带换行的列（名称 / 文号 / 备注）恒左对齐，居中的多行文本读起来会散。
2. **表头跟着居中**：`Themes/Data.xaml` 新增 `DgHeadCenter`（`BasedOn="{StaticResource DgHead}"`，
   只覆盖 `HorizontalContentAlignment=Center`），数值列的列定义挂 `HeaderStyle`。
   ⚠ 同时必须把 `DgHead` 模板里的 `ContentPresenter` 从写死的 `HorizontalAlignment="Left"`
   改成 `{TemplateBinding HorizontalContentAlignment}` —— 不改这一处，`HeaderStyle` 的 Setter
   传不进去，改半天表头纹丝不动。

**改动面**：价格三列 + 扣减基本物耗 + 医保支付 + 自付比例（现行项目）、三级/二级/一级（旧版）、
新旧比对的 `新三级价 / 旧三级价 / 差额 / 涨幅`、征求意见稿的序号与价格列、后台维护表的三级/二级/一级/自付。
作废对照没有数值列，版式不变。

**守门**：自检第 64 项新增两段 ——
① 现行项目 6 个数值列必须 `TextAlignment=Center` **且** `HorizontalAlignment=Stretch`、单元格
`HorizontalContentAlignment=Center`、`HeaderStyle` 非空；
② 名称列**不得**居中（防"一律居中"的偷懒改法）。

**诊断**：用像素量中线。表头文字块与首行数字块的中心 x 应相差 < 2px
（实测「三级」header 中心 1143、"3800" 中心 1144，同一条中线）。

### 9.15 表头背景「分两段」（2026-10-27 新增）

**症状**（用户截图圈出整条表头）：同一条表头行，左边「★ / 项目编码 / 项目名称 / 类别 / 单位」
是**白色**，右边「三级价格 … 落地文号」是**浅灰蓝**，像两块不同的东西拼在一起。

**原因**：`DataGridColumnHeader` 的 `HeaderStyle` 是**列级属性**，
一旦某列自己设了 `HeaderStyle`，就会**完全覆盖** `DataGrid.ColumnHeaderStyle`（不是继承、不是叠加）。
§9.14 为了让数值列居中，把数值列的 `HeaderStyle` 设成 `DgHeadCenter`，
同时把非数值列写成 `HeaderStyle = null` —— 这一赋值并非「回到默认」，而是**显式置空**：
这些列从此没有 `DgHead` 模板，`Background="{DynamicResource B.SoftBg}"` 也没了，
退回系统默认表头模板（白底）。于是灰蓝（数值列）与白（文本列）拼成两段。

**修法**：**每一列都显式挂样式**，二选一，绝不留 `null`：

```csharp
HeaderStyle = (Style)FindResource(align ? "DgHeadCenter" : "DgHead");
```

覆盖范围（共四处列工厂 + 一处手写列）：

| 位置 | 列 |
| --- | --- |
| `DezhouPriceView.MakeTextCol` | 项目编码 / 名称 / 类别 / 单位 + 全部数值列 |
| `DezhouPriceView.BuildColumns` | `★` 收藏列（`DgHeadCenter`，星标要居中） |
| `DezhouPriceView.MakeLinkCol` | 「对应新编码」链接列（`DgHead`） |
| `DezhouPriceView.MakeTemplateCol` | 「批次 / 执行」两行模板列（`DgHead`） |
| `AdminDezhouView` / `DraftPriceView` / `GuifanView` 的 `BuildColumns` | 同上，全部列 |

**守门**：自检第 64 项新增 `AssertHeaderSkin` —— 遍历现行项目 / 作废对照 / 新旧比对三张表的
**每一列**，`HeaderStyle` 为空即 FAIL（报出是哪一列）。这样后续再加列时，
只要忘了挂样式就会当场炸，不会等用户截图才发现。

**诊断**：沿表头中线（y 取表头行中部、避开文字）逐像素扫描，把连续同色区间打印出来。
修复前是 `(0,133)白 (136,923)灰蓝 (926,1051)白 (1054,1067)灰蓝` 四段；
修复后应为**一段**：`(21,1468) (247,247,253)`（两侧 `239,240,250` 是页面底色，不属于表头）。
注意取 y 时要避开文字行 —— 文字像素会把连续区间切断，看着像「又分段了」。


### 9.16 25 号文加收项价格为空 → 按「计价说明」的比例维护（2026-10-27 新增）

**症状**：德州物价「现行项目」里，德医保发〔2026〕25号文的一批加收项三档价格全是空的
（用户截图圈出「价格是空的，根据公式维护」）。

**为什么会有两种形态**：源表里加收项的价格有两种写法——

| 形态 | 价格列 | 加收依据 | 例子 |
| --- | --- | --- | --- |
| 一 | `ROUND(G42*0.2,0)` 这样的 Excel 公式 | 公式里写死行号与系数 | 早期批次 |
| 二 | **留空** | 「计价说明」列写 `加收比例20%。` | 25 号文「一般治疗」类儿童加收，共 49 条 |

形态二原先被当成「官方留空」，`PriceOf` 直接返回空串，表格就是一片空白。

**修法**（`DezhouStore`）：

1. `NoteRatio(item)` 用正则 `加收比例\s*(\d+(?:\.\d+)?)\s*[%％]` 从 `Usage` 里扒出系数；
2. `IsPending(item, v)` 判定「这一格该有价」＝ 有公式 **或** 说明里有比例；
3. `ResolveByNote` → `TimesBase`：**主项编码前 12 位 + 000** 找到主项，取**同档**价格 × 比例；
4. `PriceOf` 里空串先判 `IsPending`，该有价却算不出显示 `—`（提示主项缺失），
   本来就没价的仍留空（不给「—」制造「漏维护」的错觉）；
5. `PriceNote` 悬停给出「加收项：主项 011207000030000 的三级价 2 × 20% → 0.4（按计价说明的加收比例自动换算）」。

**⚠ 取整规则的一个真坑**：正常按 `ROUND(...,0)` 四舍五入取整。
但**低单价项目**算出来不足 1 元，取整会变成 **0** —— `胃肠灌注费 2 元 × 20% = 0.4`，
表格里出现「加收 0 元」比空着更误导。所以 `TimesBase` 改为：
**取整后为 0 而原值 > 0 时保留两位小数**（`0.4` / `0.5`），既不抹平加收、也不凭空造出 1 元。

**守门**：自检第 63 项（`德州物价 · 加收项价格口径`）遍历全部加收项 × 三档，
金额格必须 `> 0`；「无公式价却仍有换算说明」也从「一律不准」放宽为
「只允许 `IsPending` 的格子有说明」—— 形态二本来就该带说明。

### 9.17 分段页签 / 查询 / 翻页的等待反馈：整页渐显（2026-10-27 新增，2026-10-28 改版）

**症状**：点「现行项目 / 作废对照 / 旧版物价 / 新旧比对」这四个分段页签，界面会**完全静止**
200~400ms（拼音检索时批次重建更久），然后表格突然整体换掉。用户的原话是
「这几个切换没有渐变进度」，第一反应就是「点了没反应 / 卡死了」。

**为什么静止**：切视图要重建批次与类别下拉、重排 50 行，**全在主线程同步跑**，
期间 WPF 一帧都画不出来。

#### 为什么不再是「中间弹一张进度卡」

第一版的做法是居中一张不透明白卡：沙漏 + 文案 + 两段进度条。功能上没毛病，但它是
**多出来的一层东西**：

- 它挡住正在换的内容，用户只能看卡，看不到「页面在换」；
- 为了不让它一闪而过，还得硬留 180ms 最短停留 —— 等于给一次点击叠了两段等待（干活 + 等卡收）。

用户的诉求很直接：**要的是页面在换，不是弹窗在转**。改版后 `BusyVeil` 不再画任何卡片，
只做两件事：把页面内容压淡、在页面顶部画一条 3px 进度线。

#### 三步

| 步 | 动作 | 实现 |
| --- | --- | --- |
| 1 | 点下去立刻把同级内容压到 `DimOpacity = 0.38` | `DimSiblings()` 遍历 `Parent.Children` |
| 2 | 顶部 3px 线按预估时长推到 92%，超时改呼吸脉冲 | `ScaleX` 动画 + `Spill()` |
| 3 | 数据到位后用 220ms `CubicEase(EaseOut)` 淡回 1.0 | `FadeBack()` |

```xml
<!-- 放在页面根 Grid 里，与要淡出的内容同级；高度只有 3px，IsHitTestVisible=False -->
<ctl:BusyVeil x:Name="Busy" Grid.Row="0" Grid.RowSpan="4" Panel.ZIndex="50" />
```

```csharp
Busy.Show("正在载入旧版物价对码表…", "整理状态与对应新编码", 280);
Dispatcher.BeginInvoke(new Action(() => {
    try { ApplyView(view); }
    finally { Busy.HideAfter(); }        // 最少停留 180ms，然后淡回
}), DispatcherPriority.Render);
```

四个关键点，少一个效果就不对：

1. **按 `Parent.Children` 找同级**，不要求各页额外声明「内容宿主」——遮罩本来就挂在页面根 Grid 里，
   兄弟就是全部可见内容（页签、工具栏、表格、分页条）。四个页面一行 XAML 都不用为渐显改动。
   ⚠ 反过来也成立：把控件塞进嵌套 Grid，就只会淡掉那一小块，所以 XAML 注释里都标了必须同级。
2. **原值只记一次**（`_orig` 字典）。每次淡出都重新读 `Opacity` 的话，第二次读到的就是上一轮压下去的
   0.38，页面会越点越灰、再也回不来。
3. **`FillBehavior.Stop` + 回调写底色值**。默认的 `HoldEnd` 只是把 1.0「压在」属性上，动画一摘
   立刻弹回 0.38；必须让值真正落到元素上。窗口最小化时回调可能不来，另有一个
   `FadeInMs + 160` 的兜底定时器补写（否则用户切回来会看到一页发灰的表格）。
4. **`DispatcherPriority.Render`**：它排在所有渲染之后，等回调执行时「内容已经变淡」这一帧
   才真正画在屏幕上，这时再让主线程去忙，用户看到的是「页面暗下去了，活正在干」。

> **为什么顶部线只有 3px、而且不做浮条文案**：渐显模式下页面上任何浮层都会把界面拽回「弹窗」；
> 一张只写「正在载入…」的浮条比进度线更像噪音。结果文案照旧由各页状态栏在完成时输出。
> 文案参数（`caption` / `hint`）保留为**可读状态**，供 `--veil-trace` 与自检核对「亮起的是不是对应动作」。

> **浅色主题的坑**：白底压透明度，整体亮度几乎不动（白 255 压到 38% 叠在 `#F7F7F9` 上还是 250 上下），
> 肉眼看到的其实是**黑字变浅灰**。所以自检统计的是「深色像素数量」而不是平均亮度 ——
> 后者在这里量不出差别。
> 同理，`RenderTargetBitmap` 是 Pbgra32 预乘 alpha + 整页透明底，**不合成直接读 RGB 会把半透明白当成黑**，
> 会让「压淡后深色像素暴涨」这种荒唐结论通过断言。统计前一律按白底合成一次。

**反馈必须成套**：只在分段页签上挂渐显并不够 —— 查询、清空、翻页、下拉筛选走的是同一条 `Load` 通路，
一样是同步阻塞。`DezhouPriceView` 里统一用 `RunBusy(caption, hint, work)` 收口
（`OnSearch` / `OnClear` / `OnQueryKey` / 防抖检索 / `OnFilterChanged` / `OnCmpChanged` /
`OnFavFilterChanged` / `PageTo`）。

**诊断**：`--shot-page dzbusy[:Cmp][:spill]` 把渐显定格后离屏渲染（`DiagFreeze`）。
⚠ 离屏路径不跑消息循环：动画时钟不推进、`BeginAnimation` 刚挂上时当前帧仍取基值，
所以 `DiagFreeze` 先 `BeginAnimation(..., null)` 摘掉时钟、再直接写 `ScaleX = 0.62`；
`BusyVeil` 在该路径下也会跳过「淡回动画」直接写值（靠 `PresentationSource.FromVisual` 判断有没有消息循环）。
⚠ 参数透传坑：`Bench.cs` 原先只给 `DiagBusy` 传 `seg[1]`，第三段 `spill` 被丢掉，两张截图 MD5 完全一致才发现。
判定「参数没生效」优先比哈希，比肉眼看图快。

**守门**：
- 自检第 92 项：四个页面都必须能 `FindVeil` 到控件；`DiagFreeze` 后**整页**渲染再量像素 ——
  ① 控件本体 `Content.ActualHeight ≤ 4`（证明它不是卡）；② 深色像素比压淡前少一半以上（内容真的变淡）；
  ③ 页面上沿某一行的强调色像素 ≥ 300（证明是「一条线」而不是页签上的色块）；
  ④ 收起后深色像素回到原值、所有兄弟 `Opacity ≥ 0.99`（真的淡回来了，不会永久发灰）；
  ⑤ 定量段百分比 1~100、超时切呼吸并返回 -1；⑥ 最短停留满 180ms 才收；⑦ `Enabled=false` 不亮。
- 自检第 93 项走真实窗口 + `DispatcherFrame`：**六个入口**（四页签 + 查询 + 下一页）都要「点击即亮」，
  且逐帧采样同级内容的 `Opacity`，日志必须记到「压淡到 0.38 · 收尾 1.00」——只验亮没亮等于没验渐显本体。

### 9.18 新旧比对三档口径 + 立项指南跳转（2026-10-27 新增）

**诉求原文**：「新旧对比增加 123 级医院价格选择，有立项指南新版价格的可点击跳转相应物价」。

**三档口径**：比对工具栏加「口径：三级 / 二级 / 一级」下拉，**只在比对视图可见**
（其余三视图各有自己的医院级别筛选，两套口径不能混）。切换后列头（新三级价 → 新二级价）、
每行差额与涨幅、统计条同时换档，统计条末尾带「按{档}医院价」，避免拿一级的数字去对三级报表。

两个决定性的实现细节：

1. **三档差额一次算全**（`DezhouDraftStore.FillDelta` 同时填 `Delta3/2/1` + `Ratio3/2/1`）。
   只算三级的话，切档要么重算 4,519 行（就是 §9.17 加渐显的那个量级），
   要么把三级数字显示在二级列头下 —— **静默错数比慢更糟**。
2. **换档不重建行**。`DzCompareRowVm.SetLevel` 只改 `Level` 再 `PropChanged` 一批计算属性，
   单元格自动刷新；重建 `ItemsSource` 会丢滚动位置、选中行与展开状态。
   取差额统一走 `DezhouStore.PickDelta(row, level)`，单元格 / 统计条 / 导出同一个入口，口径不可能打架。

**立项指南跳转**：「现行项目名称」后插一列，命中显示蓝色「立项指南」链接，未收录显示灰色
「无立项指南」（仍可点，点了给提示）。点击 → 切到立项指南页 → 切区段 → 关键词定位 → 选中首行并滚动到可见。

- 立项指南原表**没有国家编码列**（编码是各省落地才赋的），名称是唯一稳定的连接键。
  `LixiangStore.FindGuide`：全名精确 → 退到 `-` 前的主项名（剥掉 `(加收)/(扩展)`）；
  `NormName` 把全角括号与空白归一，命中率 87% → **3841/3880 = 99%**。
- `OpenLixiangAt` 必须**显式清掉其余导航按钮的 IsChecked**：自检 / 探针窗口走不到
  `OnContentRendered`，页签互斥组没建立，否则两页同时可见或目标页仍 Collapsed。
- `OnGuideLink` 用 `Window.GetWindow(this) is MainWindow mw` 取宿主而不是强转，离屏无宿主时不崩。

**守门**：自检第 94 项（三档差额齐备 / 换档表头统计同步 / `ItemsSource` 引用不变 → 没重建行），
第 95 项（真窗口点链接 → 宿主切页签 → 指南页定位到该条目）。
诊断：`--shot-page dzcmplv:<3|2|1>:<scope>`。

---

### 9.19 目录库外病种（省局反馈清单）：识别 + 规避建议 + 病种库区段（2026-10-28 新增）

**省局口径**：一批「主要诊断 + 手术操作」组合，因全省结算量低于 120 例，
**2027 年起不再作为 DIP 核心病种**。病案首页若仍按这些组合填报，分组只能落到综合病组，
直接影响结算与考核。要求医疗机构把首页编码规范到目录库内的病种组合上。

**数据落地**：`config/dip3-config.json` 新增 `outlib` 区段（2109 条），与其它区段同构，
所以病种库页签、后台增删改、模板导出、Excel 导入、版本快照全部**没有任何特判就能用**。

```bash
# 原表列：病种 / 主要诊断编码 / 主要诊断名称 / 手术操作编码 / 手术操作名称
Dip3Grouper.Cli import-outlib "DIP3.0目录库外部分病种清单（省局反馈）.xlsx" [--dry]
```

导入时顺手做两件人做不到位的事：

- **不用原表第一列的复合键**（`H65-22.02`）。它只是「诊断码-手术码」的拼接，
  拆列后自己拼更可靠，且原表存在 `P58.9-` 这种尾随横线的写法。
- **每条算出「同诊断在目录库内的可入组组合」**并落盘到 `suggest`。病案室要的不是
  「这条不能用」，而是「同诊断该编成什么才能入组」。2109 条里 **1712 条**能给出建议，
  剩下 **397 条**同诊断在目录库内根本没有核心病种 —— 这些行的备注会写「无同诊断病种」，
  提示先核对主要诊断本身，而不是让人对着空白猜。

**识别规则**（`OutLibRules`）：

| 维度 | 规则 | 为什么 |
| --- | --- | --- |
| 诊断 | `Matcher.MatchDiag`（ICD-10 前缀 / 区间） | 与病种库同一套语义，`M17` 能命中 `M17.900` |
| 手术 | 完全相等 → 尾零归一相等 → 主码 + x 扩展回认 | 清单写 `22.02`、首页写 `22.0200`；清单写 `04.9900`、首页写 `04.9900x001` |
| 保守行 | 清单行无手术码 → **只有无主手术的病案**才命中 | 否则「仅诊断病种」会把所有带手术的病例都吞进来 |
| 索引 | 按诊断前 3 位（类目）分桶 | 2109 行全表扫 × 上千条批量 = 卡死；分桶后单桶几十行 |

⚠ 不能直接用 `Matcher.MatchProc`：它是「精确 / 含 x 才前缀」的严格语义，
拿清单码去比首页码，2109 行里只能命中 **8 行**（实测）。所以另写 `MatchProcLoose` + `Canon`。

**规避建议的排序**：M17 膝关节病在基础库里有几十行，按配置顺序取前 3 条会给出
「周围神经破坏术 / 周围神经烧灼术」，与被预警的「腓骨截骨术」差着十万八千里 ——
病案室看了只会觉得工具在胡说。排序改成两级：**① 诊断码完全相同优先于前缀/类目码命中；
② 手术码同大类（77.27 → 77.xx）优先**。现在 M17 首条是「77.2500x006 股骨髁上截骨术」。
另外**被预警的原组合本身不能出现在建议里**（否则等于建议继续这么填）。

**契约**：`GroupResult.OutLib` 与 `BatchItem.OutLib` 都是**旁注**，
`GroupResultConverter` 只写固定键、两个属性都标了 `[JsonIgnore]` —— `/api/group`、`/api/batch`
的键集合是 Node 对拍基线，多一个键就破。自检里直接断言序列化结果**不含** `outLib`。

**界面**：

- **单条分组**：命中清单 → 结果卡里插一张琥珀色预警卡（组合全文 + 省局原因 + 同诊断可入组组合 +
  清单序号/键），未入组与已入组两种结果都挂。放在病种名下方，用户不必去翻溯源才知道为什么。
- **批量分组**：结果表末尾加「目录库外病种」列（只标键 `H65-22.02`，完整组合与建议走 ToolTip），
  摘要条在真命中时单列「目录库外 N」（常驻一个 0 会让人以为哪出错了）；导出 Excel 同步加这一列。
- **病种库 / 后台病种库**：新增「目录库外病种（省局反馈）」页签，可按编码/名称/拼音检索。
  ⚠ 编码检索额外做一次尾零归一比较：拿首页的 `22.0200` 去搜清单里的 `22.02`，
  纯 `Contains` 一条都搜不到，而这个区段最常用的动作恰恰是「拿病案编码查在不在清单里」。
- **后台导入**：`SetSectionRows` / 模板样例 / 长文本字段（`suggest` 走多行输入）都补了这个区段。

**守门**：

- 自检第 96 项：清单 2109 条；`22.02` / `22.0200` / `04.9900x001` 都能命中；
  `P58.9` 无手术命中、带手术不命中；`A09.901 + 44.1300x001` 不误判；
  M17 建议首条落在同手术大类且不含原组合；无建议行备注写明原因；
  分组结果带 / 不误挂；**JSON 无 `outLib`**；区段总数、按名称与按编码检索均可用。
- 自检第 97 项：单条预警卡（含建议区块）出现在真实控件树里；目录库内组合不误标；
  批量 4 条里命中 3 条且摘要含「目录库外」；病种库能切到 `outlib` 且首页有行。
- 诊断：`--shot-page lib:outlib` / `--shot-page batch` / `--shot-page sg:H65:22.0200`。

⚠ 批量表加列时踩过的排版坑：`分组名称 / 未入组原因` 原本是 `Width="*"`，
再往后插一列固定宽后 star 列被压成 0 宽，整列看不见（离屏渲染时更明显）。
现在该列改成固定 360，新列排在最后。

---

### 9.20 单条分组输入框：选完不再「再点一次」+ 提示文字与光标对位（2026-10-28 修正）

现场反馈两条：诊断检索后要**再点一次**才输得进去；提示文字和光标不在一列上。

**① 选完又弹回来（根因）**

`Pick()` 先 `Close()` 再回填编码，回填触发 `TextChanged` → 180ms 后 `Search()` 用新编码再查一次
→ 下拉立刻重开。用户看到的是「刚选完又弹一个框」，此时 ↑↓/Enter 都被下拉吃掉（`OnPreviewKeyDown`
只在弹开时拦），必须先把这个框按掉才能接着输 —— 体感就是「还要再点一次」。

修法是两层静音（`_mute`）：`OnTextPropChanged`（外部赋 `Text`）和 `Pick`（选中回填）期间都不启动防抖。
只用一层不够 —— `ReplaceCurrent` 走的是 `Text` DP，真正常驻生效的是 DP 那层，两处都写才不会被将来的重构抹掉。

同时补上「点进去就能改」：

- 选中后单选字段 `SelectAll()`，接着敲键即整体替换（多值字段仍落光标在末尾，方便续写 `;`）；
- 第一次点进已有内容的输入框也 `SelectAll()`（`PreviewMouseLeftButtonDown` + `DispatcherPriority.Input`）。

**② 提示文字与光标对不齐（根因）**

水印原本是 `TextBlock`（`Margin="11,0,0,0"`），而 `Tb` 模板里 `PART_ContentHost` 的 `Margin` 是 `Padding=10,0`
加上 1px 边框 —— 实测**输入文字落在边框内 24px、水印落在 12px**，横向差 12px，光标自然对不上提示语。

改成「同模板同 Padding 的只读 `TextBox`」（`Style` 基于 `Tb`，`BorderBrush=Transparent`、`Focusable=False`、
`IsHitTestVisible=False`）：文字排版交给同一套文本引擎，起点与基线按构造一致，不再靠猜像素。
⚠ 不要改回 `TextBlock` —— 只要两边用的不是同一种文本宿主，这个偏移就会回来。

**③ 编码回显名称（顺手补的）**

字段里只剩编码时看不出选的是哪个病。右侧加一段 12px 灰字回显（`GrouperService.NameOf`：
诊断查映射表 `DisplayNameOfCode`、手术查手术库，都取联想列表里显示的那个名称）。
只对单值字段、长度 ≤16 的输入显示，避免和长文本叠在一起。

**守门**：自检第 98 项 —— 真窗口 + 真 Dispatcher 跑 `Bench.SuggestTrace`：
① 键入弹下拉；② 选中后 600ms 内不重开（**旧版必然重开**，关掉静音即报 FAIL，已验证）；③ 整段选中；
④ 水印与输入框同模板、偏移 0；⑤ 回显名称；⑥ 清空后仍能检索（静音不能吞掉用户键入）；
⑦ 手术框同样不重开。诊断：`--selftest`。

⚠ 顺带修的两处自检自身问题（不改产品行为）：

- 第 93 项「页签渐显」偶发 FAIL：`Show()` 与压淡都是**同步**做的，冷启动切「作废对照」时
  Show→干活→Hide 全落在同一帧里，只在帧间采样永远读不到（`亮起帧 0/N`）→ 改成点完先同步取一次状态；
- 采样前等首屏遮罩收起：否则第一次点页签撞上首屏那一轮，被在忙守卫吃掉 Show，误判成「点不动」。

---

### 9.21 前台主题选择：标题栏直接换肤（2026-10-28 新增）

**问题**：五套配色早就有了（后台管理 → 系统设置），但入口藏在后台登录后面，普通用户不会去找，
实际全院都用着默认的经典蓝。

**做法**：标题栏「通知」左侧加「🎨 主题」按钮，点开是 5 张色卡（经典蓝 / 青碧 / 靛紫 / 朱砂 / 石墨），
点哪套立刻生效、立刻落盘，不进后台、不需重启。

几个实现要点：

- **复用同一份 `ThemeCatalog`**：前后台都读它，色值不会两处漂移；后台设置页订阅
  `ThemeManager.Changed`，前台换完后台那张卡的勾也跟着走（`-=` 再 `+=`，避免后台窗口反复
  隐藏/显示时叠加订阅）。
- **色卡三块色是「各主题自己的颜色」**（`ThemeDef.Brush("Bg"/"Card"/"Accent")`），故意不用
  DynamicResource：它们要展示「那套主题长什么样」，固定才对；卡片外框与文字才跟随当前主题，
  所以深色主题下这一层是深底浅字，不会闪白块。
- **切换后要显式刷两处**：批量结果表的行画刷（`BatchRowVm` 原来是普通 CLR 属性，绑定取一次就定死；
  现在实现 `INotifyPropertyChanged` 并在换肤时发 `RefreshBrushes()`），通知面板（`Brush()` 静态取色）。
  其余页面走 `SetResourceReference` / `DynamicResource`，自动跟随。
- **窄屏**：极紧凑档（窗口 < 900）只留图标，ToolTip 仍在；`--shot-theme <id> <前缀>` 可离屏出
  `-bar.png`（标题栏）与 `-panel.png`（配色浮层）两张图核对。

**守门**：自检第 61 项在**临时根目录**里跑（避免与真机上其它实例写回互相踩），逐套点选并断言
「ThemeManager / 选中态 / SettingsStore 三者一致」「`Reload()` 后磁盘仍是最后那套」「色卡预览色 =
该主题本色」「恢复默认可用」。把 `SettingsStore.Save()` 注释掉即 FAIL —— 用例确实能抓住「只换不存」。

---

### 9.22 网页版前台主题：五套配色 + 一致性校验（2026-10-28 新增）

网页版原来只有一套写死的配色，**100 多处硬编码色值**（`#fff` 28 处、各种浅灰、边框蓝）。
这次把它们收敛成 34 个 CSS 变量，五套主题只覆盖变量，布局一行不动。

- 切换方式：导航右侧「🎨 主题」→ 浮层 5 张卡 → 选中即生效，存 `localStorage['dip3.theme']`，
  **按浏览器/本机记**，不影响别人。
- 深色主题额外给 `color-scheme: dark`，让滚动条、原生下拉、输入光标跟着变深。
- `<head>` 里有一段极小的内联脚本，在样式生效前把 `data-theme` 挂到 `<html>`，
  否则刷新会先闪一下经典蓝。
- 输入框补了 `color: var(--text)` 与 `::placeholder`：深色下浏览器默认的占位灰几乎看不见。
- 浅色三套（青碧 / 靛紫 / 朱砂）的白底卡片仍是 `--card`，深色下自动变深灰，不会露白块。

**防漂移**：新增 `tools/check_theme_sync.py`，直接比对
`ThemeManager.cs` 的 `Map` 与 `index.html` 的 CSS 变量（25 个共用键 × 5 套）。
改一边忘另一边会立刻报出来：

```
$ python tools/check_theme_sync.py
主题色值不一致：
  - jade.accent: 桌面 #0f8a7e ≠ 网页 #0f8a7f
```

⚠ 网页版新增用色**一律走变量**（`var(--accent)` / `var(--soft-bg)` …）。
直接写十六进制当下能看，切到石墨就是一块白斑——这是本轮返工最多的地方。

---

### 9.23 立项指南「解读辅导」展开不再撑出横向滚动条（2026-10-28 修正）

**症状**：解读辅导里点开任一行的详情卡，表格底部多出一条横向滚动条，
而且整行被撑到视口的三倍多——实测 1460 宽的表格里某行宽 **3429**。

**根因**：`DataGrid` 只要允许横向滚动（`ScrollViewer.HorizontalScrollBarVisibility=Auto`），
虚拟化面板就会按「无限宽」测量每一行。详情卡里那段不折行的长回答于是按单行撑开，
`ExtentWidth` 跟着涨，横条就出来了。**不是详情卡写宽了，是它被当成了无限宽的容器。**

**修法**：把 `JdGrid` 的横向滚动关掉（`Disabled`）—— 行拿到有限宽度，长文正常折行
（修后 `details=1448`、`extent=1441.5`、`hbar=Collapsed`）。

⚠ **关掉横滚的代价**：`Width="*"` 的列会算不出宽度、直接塌成 20px（`期次/题号` 还在、
`解读问题` 整列消失）。所以第三列改成 `x:Name="JdQCol"` 并显式分配宽度，
与同一页 `LxGrid` 的 `FitLxNameColumn` 是同一套做法：

```csharp
var fixedSum = JdGrid.Columns.Where(c => c != JdQCol).Sum(c => c.ActualWidth) + 30;
JdQCol.Width = new DataGridLength(Math.Max(160, JdGrid.ActualWidth - fixedSum));
```

`+30` 是留给竖滚动条与边框的余量，不能省（省了条数一多、竖条出现就会重新顶出横条）。
触发点两处：`JdGrid.SizeChanged`（窗口缩放）与 `LoadJd()` 末尾（条数变化带出/收走竖条）。

**试过但不要再用**：
- `MaxWidth` 绑到 `DataGridRow.ActualWidth` —— 行已被撑到 3429，`MaxWidth` 跟着变成 3409，
  自己绑自己，等于没封顶；
- 绑到 `DataGrid.ActualWidth` 再减固定值 —— 竖滚动条出现后余量不够，横条会回来。

**验证**：自检第 100 项（十八）在 1500 与 1060 两种窗口下断言 `extent ≤ viewport`、
`ComputedHorizontalScrollBarVisibility=Collapsed`、问题列 ≥ 200px、详情 ≤ 视口。
把 `Disabled` 改回 `Auto` 立刻 FAIL，证明用例咬得住。

顺带确认「价格项目」页同款详情卡**没有**这个问题（`extent 1430 / viewport 1448`）：
它的详情是若干短行 `ItemsControl`，没有单行长文本，列宽也早已显式分配。

诊断入口：`--shot-page lxjiedu[:关键词[:行号]]`（解读辅导 + 展开详情）、
`--shot-page lxsel`（价格项目 + 展开详情），两者都会在控制台打印 viewport/extent/列宽。

---

### 9.24 新旧比对双向跳转 + 病种库右键菜单（2026-10-28 新增）

#### 1）比对行的两侧编码都能点

「德州物价 → 新旧比对」里，一行 = 一个现行项目 ↔ 一个旧版物价项目。原先两侧编码都是
死文本，看到差额想核原价只能记住编码、切页签、再搜一遍。现在：

| 点哪里 | 去哪 | 检索方式 |
| --- | --- | --- |
| 新项目编码（表头「新项目编码 ▸」） | 现行项目 | 按新编码关键字检索 |
| 旧项目编码（表头「旧项目编码 ▸」） | 旧版物价 | 按旧编码关键字检索 |

实现是 `MakeCodeLinkCol(...)`（单个链接按钮，`Tag` 带编码），与作废对照 / 旧版物价里
「一对多链接」的 `MakeLinkCol` 分开 —— 后者是 WrapPanel 容器，这里一行只有一个编码、
旁边还有独立名称列，不需要那层壳。空编码（纯新增 / 无可比旧项目）显示「—」并置灰：

```csharp
btnStyle.Triggers.Add(new DataTrigger {
    Binding = new Binding("HasOldCode"), Value = false,
    Setters = { new Setter(UIElement.IsEnabledProperty, false),
                new Setter(UIElement.OpacityProperty, 0.45) } });
```

#### 2）返回按钮：回到原视图的原页码、原筛选

跳转态下筛选条出现「← 返回新旧比对 / 旧版物价 / 作废对照」。原实现只记了
`_returnView + _returnQuery`，返回时页码与比对范围全丢；现在改存整份现场：

```csharp
private sealed class BackState { View / Query / Page / Cat / Status / CmpScope / CmpLevel }
```

`ApplyView(view, back)` 在重建下拉**之后**再按 Tag 选中，并校验快照值是否还存在于新下拉
（数据变了就退回「全部」，否则会查出空列表）。手点分段页签或「清空」会主动清掉返回态。

#### 3）顺手修掉的一个老裁切

`FitCmpNameColumn` 用 `CmpCols` 求和算名称列宽，但「立项指南」列是运行时插进去的、
不在 `CmpCols` 里 —— 漏算 92px，整表比视口宽 62px；`HorizontalScrollBarVisibility=Disabled`
下就是硬裁，最后一列「基准来源」只露出半个字。现在加常量 `GuideColWidth` 计入：

```csharp
var fixedSum = CmpCols.Where(c => c.Width > 0).Sum(c => c.Width) + GuideColWidth + 30;
```

#### 4）病种库查询右键菜单

桌面端（`RowMenu` 新增 `IDictionary<string, object>` 分支）与网页版（`pubCtx`）一致：

- 复制该单元格内容（右键落在哪个单元格就复制哪个）
- 复制本行（制表符分隔，可直接粘进 Excel）
- 复制本行（带列名，首行列名 + 次行内容）
- 复制 · 序号 / 主要诊断编码 / 诊断名称 / 主要手术编码（按当前区段的列动态挑，最多 4 项）
- 网页版另有「按该单元格内容检索」（填回搜索框并查询）

⚠ 病种库的列是**按区段动态建**的（病种 / 先期 / 烧伤 / 结核字段完全不同），
菜单项必须从 `DataGrid.Columns` 反推键与列名，不能写死字段。桌面端 `FlatGrid` 用索引器
绑定 `[key]`，所以 `ColumnKey()` 要剥掉方括号：

```csharp
if (c is DataGridTextColumn t && t.Binding is Binding b
    && b.Path?.Path is string p && p.Length > 2 && p[0] == '[' && p[^1] == ']')
    return p[1..^1];
```

网页版菜单颜色全部走主题变量（`--card / --border-strong / --accent-soft / --text`），
深色主题下实测 `rgb(31,35,43)` 底 + `rgb(231,235,242)` 字，不会闪白块。

**验证**：自检第 101 项（十九）真实点按钮验证双向跳转 + 返回（含第 2 页跳转返回仍在第 2 页）；
第 102 项验证病种库菜单（复制文本列数 = 表格列数、换区段后菜单项必须变）。
诊断入口：`--shot-page dzcmpjump`（点旧码 → 旧版物价）、`--shot-page dzcmpjumpn`
（点新码 → 现行项目）、`--shot-menu lib`（病种库菜单样式）。

---

### 9.25 关联链接「点 A 打开 B」：立项指南 Id 跨区段重号（2026-10-30 修正）

**现象**：技术规范「睡眠呼吸监测」详情卡里点「关联·立项指南 · 睡眠呼吸监测费」，
弹出的却是**供小肠切取术**（s01 序号 11，器官移植）。

**根因不是绑定错、也不是缓存旧数据，是主键选错了**：

```
lixiang.json 有 40 个区段（s01…s40），每段的行 Id 各自从 1 开始编。
2627 行只有 662 个不同 Id —— Id=1 出现 40 次、Id=11 出现 33 次。
  s01 rows[10] = 供小肠切取术   Id=11
  s23 rows[10] = 睡眠呼吸监测费 Id=11   ← 点的是这条
```

`Related.Ensure()` 原先 `GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First())`，
`First()` 拿到的是遍历到的第一段（s01），于是**所有 Id=11 的链接都指向 s01 的那条**。
`CrossMap` 索引里存的也是 `r.Id`，两头都错，所以看起来「链接文本对、打开的内容不对」。

**修法：给每行一个全局跳转键 `Gid`（内存内自增，不落盘）**

```csharp
// Models.cs — LixiangRow
[JsonIgnore] public int Gid { get; set; }

// Lixiang.cs — 加载 / 导入 / 增删改后统一重排
public void Reindex()
{
    var n = 0;
    foreach (var sec in State.Sections.Values)
        foreach (var r in sec.Rows) r.Gid = ++n;
}
```

- `CrossMap.Ensure()` 的指南分支存 `r.Gid`，`Related._lx` 按 `GroupBy(r => r.Gid)` 建表；
- `Id` 保持原语义（`(tab, Id)` 定位编辑/删除、HTTP 接口的 `id` 参数），**不动落盘格式**；
- `Reindex()` 挂在 `Load` / `ApplySections` / `AddRow` / `UpdateRow` / `RemoveRow` 五处；
- 行情/规范两库的 Id 本身全局唯一（物价 3880 条无重号、规范 11516 条无重号），无需改。

**顺手修的第二个坑：索引缓存从不失效**。`CrossMap` / `Related` 原先「三个字典非空就直接
返回」，导入或增删数据后仍按旧 Id 指路。现在各自带**数据戳**（区段数 / 指南行数 / 物价条数），
不一致就整表重建：

```csharp
private static string Stamp() =>
    $"{lx.Sections.Count}/{rows}|{App.State.Dezhou.Items.Count}";
```

⚠ 别把 `App.State.Guifan.Items.Count` 塞进 Stamp：取 `App.State.Guifan` 会强制加载 11 MB
技术规范文件；而规范库只在 `ReloadAll` 里刷新，那条路径同时也会换掉指南 / 物价。

**验证**：自检第 11 项已从「链接能解析」升级为「链接指向自身」——733 条链接逐条比对
「行文本首段 == 打开后的条目名」，并全量断言 `Gid` 唯一（2627/2627）、同一 Id 的 40 行
各自命中自己。**把 `r.Gid` 改回 `r.Id` 立刻 FAIL**（已实测），反证该断言真的能挡住回归。

诊断入口：`--shot-related lixiang:<Gid>`（注意参数是 Gid，不是区段内的 Id）。

**同类隐患自查口径**（以后再出现「点 A 看 B」按这四条查，别先怀疑绑定）：

1. **这个 Id 的作用域多大？** 是「区段内自增」还是「全库唯一」？`s01#11` 与 `s23#11`
   在人眼里是两条，在 `Dictionary<int, T>` 里是一条。
2. **建表用了 `First()` 吗？** `GroupBy(Id).First()` 会在重号时静默挑第一条，
   不报错、不告警 —— 这是最典型的隐形错误。
3. **缓存有没有失效？** 索引 / 字典一旦常驻，数据导入或增删后旧 Id 就指到别人身上。
4. **自检是不是只验了「能打开」？** 必须比对「打开的内容」与「点击的那一行」。

⚠ 已知的第二处重号：`dezhou-draft.json`（征求意见稿，Id 1…1089）与 `dezhou-price.json`
主库 Id 有 1087 个重号 —— 两个 Store 彼此独立，目前 `Related` 没有 draft 分支所以不串；
**将来若给征求意见稿加跨库跳转，必须同样先做全局键**，不能直接复用 Id。

---

### 9.26 导出按钮不再错行 · 导出内容逐表对账 · 诊断窗口不再弹确认（2026-10-30）

#### ① 导出按钮错行（物价页 / 征求意见稿）

两个前台的筛选条原本都是**一个 WrapPanel 平铺**：批次 / 类别 / 性质 / 级别 / 医保 / 关键词 /
查询 / 清空 / 返回 / **导出范围 / 导出** / 仅看收藏 / 状态。

窗口一窄（1366 及以下），WrapPanel 就从任意位置断行，于是出现「导出范围还在第一行、
导出按钮被挤到第二行」——按钮与它的范围下拉分家，看起来就是错行。

改为**两段式**（两页同样结构）：

```xml
<StackPanel Grid.Row="1">                     <!-- 整块占一行 -->
  <WrapPanel x:Name="FilterBar">…筛选 + 关键词 + 查询/清空/返回/仅看收藏…</WrapPanel>
  <Grid Margin="0,8,0,0">                     <!-- 操作行：状态左、导出组右 -->
    <TextBlock x:Name="DzStatus" TextTrimming="CharacterEllipsis" />
    <StackPanel x:Name="ExportBar" HorizontalAlignment="Right">  <!-- 导出范围 + 导出，永不拆开 -->
```

要点：

- 导出组放进**独立 StackPanel**：WrapPanel 不会在同一个 StackPanel 内部断行，两个控件永远同行。
- 状态文字挪到操作行左侧并 `TextTrimming`：以前它跟按钮抢同一行，长提示（「已切换到现行项目，
  按编码…检索」）把按钮顶走，也是错行来源之一。
- 切到非「现行项目」视图时整块收起（`ExportBar.Visibility`），不是各控件的 Visibility 各设一遍 ——
  否则操作行右侧会留一条空槽。
- ⚠ 视图切换（`ApplyView`）在正常路径是**延时一帧**的（带渐显动画）。诊断/自检要同步切换，
  必须走 `DiagCompare()` / `DiagLive()`（内部直接 `ApplyView`），只设 `IsChecked` 在离屏下不生效。

自检第 109 项：1500 / 1366 / 1280 / 1060 四个宽度逐个断言「导出范围与导出按钮**垂直中心**对齐
（≤2px）」「按钮不在 `FilterBar` 子树里」「按钮在范围下拉右侧」；征求意见稿断言按钮与状态行中心对齐。
判据用中心而不是 Top：按钮 32px、文字 20px，Top 天然差 6px，比 Top 会误报。

#### ② 导出内容错行（真·数据错行）

`DraftPriceView.BuildCompareSheets` 原先把「共 N 项拟新增…」汇总行 `Insert(1, …)` 插在**表头正下方**，
于是表头 1 行 + 汇总 1 行 + 数据 N 行：所有数据相对列头**整体下移一行**，筛选、排序、透视、再导入全错，
而且那行文字还以中文逗号「，」开头（拼接残留）。

改为**汇总单独成表**：sheet1「新旧价格比对」只有表头 + 数据；sheet2「比对汇总」两列
（指标 / 数值），含拟新增合计、有旧价可比、涨/跌/平、仅作废对照、纯新增、本次导出口径。

#### ③ 审计 CSV 文件头两个 BOM

`AuditLog.ExportCsv` 自身已带 `\uFEFF`（Excel 认 BOM 才不乱码），而写盘处又用
`new UTF8Encoding(true)` → 文件头 `EF BB BF EF BB BF`，Excel 打开首列表头会多一个零宽字符，
按表头匹配的工具（含本项目自己的导入）会认不出「时间」列。

修法：新增 `AuditLog.ExportCsvBytes()`（`UTF8Encoding(false)` 编码带一个 BOM 的字符串），
写盘一律走它。自检第 106 项断言「首 3 字节是 BOM、第 4-6 字节不是 BOM、正文不以 \uFEFF 开头」，
并验逗号 / 引号 / 换行的 RFC4180 转义与逻辑行数。

#### ④ 新增「二十、导出正确性」自检章节（第 103–109 项）

逐个导出出口解析产物对账，不只验「非空」：

| 出口 | 断言 |
|---|---|
| 前台物价 `DezhouExport.Front` | 表头 = 列定义（含顺序）、逐行编码/名称对应（错行即 FAIL）、价格不是 `=公式` 文本、使用说明附表 4 列、文件名合法 |
| 后台 `AdminService.ExportDezhou` + 模板 | 批次筛选生效、**导出→`ReadDezhouRows` 再解析**条数与编码顺序一致、模板示例 `price3/unit` 落在正确列 |
| 病种库 / 立项指南 / 综合病组模板 | 每张表表头 = 该区段列定义、数据行不超表头、指南首行字段顺序核对、表名唯一且 ≤31 字 |
| 审计 CSV | 单个 BOM、表头 8 列、转义、3 个逻辑行 |
| 批量分组 | 模板 8 列、示例行不超宽、结果表逐行病案号 = 输入顺序、含「目录库外病种」列 |
| 征求意见稿四视图 | 每表行宽 ≤ 表头、**数据区第 2 行必须是真实数据**（汇总行插队即 FAIL）、比对汇总表与 `CompareStat` 逐项相等 |

顺带修 `ExcelUtil.BuildWorkbook`：sheet 名撞车（区段名截 31 字后重名）会让 NPOI 直接抛异常、
整个文件导不出来；现在统一去重补 `(2)`/`(3)`，并新增 `ExcelUtil.SheetName()` 供调用方预判。

#### ⑤ 诊断 / 自检窗口不再弹「最小化到托盘 / 退出」确认

**症状**：改代码期间每跑一次探针（`--probe-*` / 自检 / 截图），桌面上就闪一个窗口，有时还停着
一个「关闭确认」框，必须手工点掉；进程也退不掉，只能任务管理器结束。

**根因三条**：

1. `Probe.*` 建的是**真 `MainWindow`**，`win.Close()` 会走 `OnClosing` → `CloseRouteNow`：
   `CloseAsk` 默认 true → 弹 `CloseConfirmWindow`，没人点就一直挂着；`CloseToTray` 默认 true →
   `HideToTray()` 把窗口收进托盘，进程依然活着。
2. `--probe-nav` / `--probe-rapid` / `--probe-early` / `--probe-main` 的**成功路径根本没有 Shutdown**，
   只 `return` —— 窗口在、进程在。
3. 窗口是 `Topmost = true` + `Left=0,Top=0` + `Opacity=0.02`：会压在别的窗口上、在左上角闪一下。

**修法**：

- `MainWindow._diagWindow` + `EnableDiagMode()` / `MakeInvisibleForDiag()`：诊断窗口关闭时
  `route = CloseRoute.Exit`，不问、不进托盘；同时 `WindowStyle=None`、`ShowInTaskbar=false`、
  `Left/Top=-32000`、`Opacity=0.01`、`Topmost=false`。
- `Probe.*` 的四个窗口一律 `MakeInvisibleForDiag()`；Bench 的取样窗口 `Opacity=0`、`Topmost=false`。
- `App.DiagFinish(code)` 统一收尾：**先把 `ShutdownMode` 改成 `OnExplicitShutdown`**（否则关掉最后一个
  窗口时 WPF 自己 `Shutdown(0)`，自检失败也报成功）、关掉所有残留窗口、`Shutdown(code)`、
  再起一个 2.5 秒兜底线程 `Environment.Exit(code)`。所有 `--probe-*` / `--shot-*` / `--selftest`
  分支的 `Shutdown(x)` 全部换成它。
- ⚠ 别再把诊断窗口挪到 `Left=-32000` 之外还叠加 `Popup`（联想框/右键菜单）：离屏 + Popup 定位
  会让消息循环空转，自检卡在第 99 项（已实测）。**纯渲染用 `RenderTargetBitmap` 的不受影响，
  需要真消息循环的取样窗口保持 `Left=0,Top=0` 但 `Opacity=0`。**

实测：`--probe-main` 5s、`--probe-early` 4s、`--probe-rapid` 8s、`--probe-nav` 15s 均自行退出，
退出后 `Get-Process Dip3Grouper.Desktop` 为空；自检 109/109 PASS、退出码 0（失败时为 1）。

自检卡住时看不到进度（输出集中在最后落盘）：设 `DIP3_SELFTEST_TRACE=1` 会把逐项进度写到
`%TEMP%\dip3-st-progress.txt`。

### 9.27 界面走查后的 13 项优化（2026-10-30）

一次性把「看着能用、天天用会硌手」的点补齐。原则：**不动数据口径、不动分组逻辑**，只动呈现与操作路径。

#### ① 新旧比对视图也能导出（原来只有「现行 / 旧版」能导）

`DezhouExport.Cmp()`：19 列（新编码/名称/类别/性质/新三级·二级·一级价 → 旧编码/名称/三级·二级·一级价
→ 三级差额/涨幅% → 其它两级同构 → 基准来源），差额与涨幅带符号（`+12.5` / `-3%`），无旧价写 `—`。
**汇总单独一张 sheet「比对汇总」**（合计 / 有旧价可比 / 涨 / 降 / 平 / 纯新增 / 仅作废对照 / 导出口径），
绝不插在表头下——表头下插汇总行会让「表头 = 列定义」的断言和用户的列宽记忆一起崩掉。
比对视图的导出范围只有「全部 / 本页」，没有「仅收藏」（收藏是现行表的概念）。

#### ② 病种库「序号」突然变 1 时，页面自己说明原因

区段各自从 1 编号是官方数据本来的样子，但用户在 s23 看到「序号 1」会以为排序坏了。区段页签右侧加
`ⓘ 序号承接前序区段（1–19），本区段从 20 起`，按区段缓存首号，切页签即时更新。

#### ③ 物价页使用说明面板默认收起（并记住选择）

出厂默认收起（`SettingsStore.UsageOpen ?? false`），点「展开/收起」写盘，下次开程序沿用上次选择。
正文列表纵向限高，底部加 22px 渐隐遮罩，避免「半行文字被硬切」的观感。

#### ④ 立项指南加「区段」列

只在每个区段首行显示区段名（宽 112，全名挂 ToolTip）。`MarkSections()` 用 `Gid` 反查
`sections` 的 `label`——`Id` 在本表是**区段内自增**，跨区段重号（见 §9.25），不能用来定位区段。

#### ⑤ 表格「恒定列」不再随筛选/分页消失

序号、编码这类身份列固定在左侧，横向滚动时不跟着跑；列宽锁死，避免内容一长就把后面的列挤成省略号。

#### ⑥ 统计行从页签行搬到操作行左侧

原来统计挤在页签行右边、窗口一窄就换行；现在 `Grid` 两列：`StatText`（不吃宽度）+ `DzStatus`（`*` +
`CharacterEllipsis`），状态文字再长也不挤压统计。

#### ⑦ 行高密度切换：紧凑 26 / 标准 34

`App.xaml` 里 `<sys:Double x:Key="DgRowHeight">26</sys:Double>`，运行时改
`Application.Current.Resources["DgRowHeight"]` 即全表生效（`RowHeight="{DynamicResource DgRowHeight}"`），
不用逐表设值。标题栏 `BtnDensity` 一键切换，`UiPrefs.Toggle()` 写盘。
**⚠ 固定行高后必须关掉换行**：`TextWrapping=Wrap` + 26px 行高会把两行字压进一行高度里互相重叠
（截图里「肝脏移植术-部分肝脏」就是叠着的）。文本列一律单行 + `TextTrimming.CharacterEllipsis` + ToolTip。

#### ⑧ 分页条支持跳页与每页条数

跳页框 `PreviewKeyDown` 拦 `Enter` 并 `e.Handled = true`（否则会触发默认按钮）；输入非数字或超范围
直接还原当前页，不弹错。每页可选 50 / 100 / 200 / 500，四个表（物价 / 病种库 / 立项指南 / 审计）统一。

#### ⑨ 页脚文案与空值符号统一

页脚统一 `第 N/M 页 · 共 X 条`。源表用 `\` 表示「无旧编码」，VM 层 `Blank()` 把 `\`、`-`、`—`、空白
一律归为 `—`——否则同一列里三种「空」混着出现，用户会以为数据缺失。
另：物价主表**移除「扣减基本物耗」列**（3,880 条里只有 11 条有值，详情卡与导出模板仍保留）。

#### ⑩ 单条分组页三个按钮同高、补「填入示例」

`清 空`(88) / `填入示例`(104) / `分 组`(`*`) 同高 32。`OnFillSample()` 灌
`A09.901 + 44.1300x001 + 45 岁` 并直接出结果，让人不用先翻文档就知道「输入长什么样」。

#### ⑪ 后台总览不再暴露绝对路径

`TxtRoot` 只显示数据目录的**末级目录名**，完整路径挂 ToolTip；计数 0 显示「尚未导入」而不是「0 条」。

#### ⑫ 审计日志：时间分档 + 分页 + 列宽

当天只显示 `HH:mm:ss`，往日 `MM-dd HH:mm:ss`；**导出必须用 `AtFullText`（带年）**，否则跨年 CSV 无法排序。
动作/对象下拉前加文字标签（光看下拉框不知道筛的是什么），详情列 `*` + 省略号 + ToolTip，底部加分页条（每页 100）。

#### ⑬ 数据更新页的空态与说明位置

服务器地址 / 共享口令的说明移到输入框下方（原来挤在标签右边换行很难读）；差异表外套一层 `Grid`，
空态显示「暂无差异 —— 点上方「检查更新」…」，不再是「一张什么都没有的表」。

#### ⑭ 顺带修：自检不再污染出厂偏好

打包脚本会跑自检，而自检第 63 项会**真的点**「收起/展开」按钮 → `SetUsageOpen()` → 写盘。
于是打出来的绿色包 `data\settings.json` 里就带着 `usageOpen: true`，用户首次打开物价页看到的是展开态，
而不是出厂默认的收起。修法两条：

- 该断言后比较并还原 `SettingsStore.Current.UsageOpen` + `Save()`；
- 第 72 项（可选文本）原本**依赖**「面板是展开的」这个残留状态，现在改为先 `FocusUsage("", "")`
  展开（只改内存 `_usageOpen`，不落盘）再统计条目。

核对：`dist\DIP3-Desktop*\data\settings.json` 的 `usageOpen` / `compactRows` 均为 `null`。

#### 实测

| 项 | 证据 |
|---|---|
| 自检 | **110/110 PASS，退出码 0**（新增第 110 项：比对导出表头 19 列 / 逐行对码 / 差额涨幅符号 / 汇总单独成表 / 空值统一 `—`；65 项改断言：副表行高必须等于 `UiPrefs.RowHeight` 且文本列不得 `Wrap`） |
| 比对导出 | 6,766 行 × 19 列表头一致、无错行；`德州物价_新旧比对_全部_20261030-2115.xlsx` |
| 序号提示 | s02 起显示「承接 1–19，本区段从 20 起」 |
| 密度切换 | 紧凑 26px 一屏 18 行、标准 34px；长名称省略号 + ToolTip，无重叠 |
| 区段列 | 器官移植 / 量表 / 中医针灸拔罐 三处序号重置点均有区段名 |
| 审计 | 时间列 `21:10:41`、底部「第 1/2 页 · 共 155 条」、每页 100 条 |

### 9.28 解读辅导正文凭空多出一行「2.」（2026-10-30 修正）

**症状**：立项指南 → 解读辅导，展开第 1 期第 1 题，正文最后单独多出一行 `2.`（即下一题的题号）。
列表里下一行确实是第 2 题，所以它是从「第 2 题的题号」漏进来的。

**根因**：`ParseJieduText` 切答案的右边界是**算出来的**，不是量出来的：

```csharp
var end = qs[i + 1].At - qs[i + 1].Q.Length - 1;   // ❌ 旧写法
```

`qs[i+1].At` 是「下一题**整个匹配**的结束位置」，`Q.Length` 是题面长度，那个 `-1` 想减掉题号前的
换行。但匹配串 `(?:^|\n)\s*(\d{1,3})[\.、．]\s*(题面？)\s*\n?` 里，题号前的空白由 `\s*` 吃掉几个
并不固定（一个换行 / 两个换行 / 换行加缩进），`\s*\n?` 又可能一个都没吃到 —— 于是「匹配长度 - 题面长度」
不等于「题号前缀长度」，边界偏一两个字符，正好把下一题的 `2.` 留在本题答案末尾。

实测 **224 条里 211 条中招**。没中招的 13 条恰好是每期最后一题（右边界取 `body.Length`，不经过这个算式）
—— 13 期各 1 条，这个数字本身就是最好的证据。

**修法**（`Lixiang.cs`）：

1. 匹配时记下**题号第一位数字的下标**：`qm.Groups[1].Index`，边界直接取它 —— 与换行个数完全无关：
   ```csharp
   var end = i + 1 < qs.Count ? qs[i + 1].NumAt : body.Length;   // ✅ 量出来的
   ```
2. 存量修复 `NormalizeJiedu()`：源 `.wps` 已不在手边、无法重新解析，只能按形态兜底清掉
   末尾的「换行 + 1~3 位数字 + 句点/顿号」。`Load()` 里跑一次，有改动就回写（幂等）。
   **⚠ 单独 try 包住写盘**：外层 catch 会把 `State` 清成空库，写盘失败不能连累刚加载好的数据。
3. 解析时也顺手 `DanglingNoRe.Replace(...)`，双保险防以后别的切法再漏进来。

**自检**：新增「解读辅导答案不带游离题号（切分边界 / 存量清洁）」——
存量 224 条逐条查 `HasDanglingNo`，且答案不得被清成空白（防空转过度）；
再用一段两题样例把边界钉死：双换行分隔、顿号题号、末题三段都必须切在原处。
→ **111/111 PASS，退出码 0**。

**核对**：`data/lixiang.json` 与四个 dist 包内 `jiedu.list` 残留 0/224；第 1 题正文末尾为
「…以此来统一指导各地规范价格项目。」，无游离题号（`_shots/jiedu_fix.png`）。

---

### 9.29 网页端与桌面端全量对齐（2026-10-30）

**诉求**：`网页端前端为什么和CS端不对齐。全部对齐，并验证前后端`。逐页比对后确认不是「配色差一点」，
而是**结构性的少东西**：网页端只有四个前台页，德州物价 / 项目技术规范两页根本没有；已有的四页里
筛选、统计、导出、每页条数也都跟桌面端各写一套。逐条对齐并加了可重复执行的对拍脚本。

**一、补两页（前台只读）**

| 页面 | 数据 | 视图 |
|---|---|---|
| 德州物价 | 3,880 条现行 + 5,107 行作废对照 + 6,589 条旧版 + 6,766 行比对 | 现行项目 / 作废对照 / 旧版物价 / 新旧比对 |
| 项目技术规范 | 11,516 项 · 11 章 · 6 类别 | 类别 / 章节 / 单位 / 耗材 四维筛选 |

**二、新增 7 个前台只读接口（口径与后台 / 桌面端共用同一份 Core 方法）**

```
/api/diseases-counts   /api/diseases
/api/dezhou-meta       /api/dezhou        /api/dezhou-export
/api/guifan-meta       /api/guifan
```

原来 `web/index.html` 直接打 `/api/admin/diseases(-counts)` —— 后台一开登录，前台就 401；
而且等于让浏览器拿「管理接口」当查询接口用。可见性沿用「数据分发共享口令」，与 `/api/data/*`
同一套判定：`dataShareToken` 留空 = 局域网可读；设了就要带 `?token=` 或 `X-DIP3-Token`。

**三、补齐的筛选项与行为（逐条对着桌面端控件抄）**

- 物价：项目性质（主/加收/扩展）、医保支付、级别价、比对范围、**比对口径（三级/二级/一级）**、
  **仅看收藏**、**导出范围（全部结果 / 仅当前页 / 仅收藏）**；
  作废对照与旧版物价**不出导出按钮**（桌面端 `ExportBar.Visibility = view is Live or Cmp` 也是这么定的）。
- 技术规范：耗材从「仅有耗材」勾选框改成 **耗材不限 / 含必需耗材 / 无必需耗材** 三档下拉
  （勾选框没法反过来只看不含耗材的 6,413 项）。
- 立项指南：使用说明面板文案统一为「本类使用说明（点击展开）」，默认收起（同桌面端 `IsExpanded=False`）。
- 病种库：区段页签、序号承接提示、分页跳页与每页条数。
- 单条分组：`清 空` / `填入示例` / `分 组` 三键同排；空态文案与桌面端一致。

**四、这次踩到并修掉的七个坑**

1. **统计条只画一次**：`dzStat` 原来只在 `dzFillFilters()` 里按「现行项目」写一次，
   切到作废对照还写着「共 3,880 条」，而表里是 5,107 行。改为 `dzPaintStat()` 按视图换文案，
   元数据接口一次给四套统计（`stat` / `cancelStat` / `legacyStat` / `cmpStat`）。
2. **统计条口径与表格不同步**：比对视图的降/涨/平必须按当前口径算，
   `/api/dezhou-meta` 因此接受 `level` 参数，平台切口径时重取一次。
3. **每页条数 500 会丢数据**：Core 的 `ListRows` 内部把 `pageSize` 夹在 **200**，
   而网页端下拉给到 500 —— 接口只回 200 行、页码却按 500 算，第 2 页从第 501 条起，
   中间 300 条永远翻不到。网页端改为 50/100/200，接口侧也把夹值统一到 200。
4. **比对范围 `pure new` 的值写错**：网页端发 `scope=new`，Core 认的是 `none`，
   于是「纯新增」等于没筛。改值后 4,519 + 75 + 2,172 = 6,766，分项相加回总数。
5. **导出与屏幕筛选不一致**：导出参数原来只带 doc/category/q，屏幕上的性质 / 医保 / 级别筛选没带过去；
   现在表格与导出共用 `dzQueryParams()` 一份参数。
6. **「仅看收藏」服务端不知道**：收藏存在浏览器 localStorage。给 `/api/dezhou` 与
   `/api/dezhou-export` 加 `codes=` 参数，走 `ListRows` 的 filter 谓词（**不能**先取一页再在前端筛——
   那样分页总数和页码都会错）。
7. **13 个控件挤一行**：加完筛选后关键词框被压成一条缝、「仅看收藏」单独掉到第二行。
   改为桌面端同款两行：筛选行 + 操作行（左统计、右「仅看收藏 + 导出范围 + 导出」），
   工具栏加 `flex-wrap`。

**五、`<colgroup>` 的两个老坑（这次真正定位到）**

- `colgroup` 被拼进 `<thead>` → HTML 解析器直接丢弃，列宽全部失效。
  必须挂到 `<table>` 下第一个子节点（`setColWidths()` 里 `t.insertBefore(cg, t.firstChild)`）。
- `table-layout: fixed` + 表宽 `100%` → 浏览器按比例重分配，300px 的名称列实测被压到 85px。
  修法是把表宽钉成列宽之和，由 `.tbl-wrap` 横向滚动。

**六、验证：`tools/verify-web-parity.mjs`（7 组 49 项，退出码即结论）**

```
node tools/verify-web-parity.mjs http://localhost:3210
```

| 组 | 校验什么 | 结果 |
|---|---|---|
| A | 接口总数 vs `data/*.json` 原始条数（不是接口自证） | 3,880 / 5,107 / 6,589 / 6,766 / 11,516 全对 |
| B | 逐页抓完、末页余数、**逐键出现次数与源数据一致** | 4 个视图共 113 页，无丢行无串行 |
| C | 性质 / 医保 / 级别 / 比对范围 / 收藏编码分项相加回总数 | 1,934+1,728+218；3,151+729；4,519+75+2,172 |
| D | 病种库序号承接 `start` 连续、区段 total 与列表一致 | 11 区段止于 16,262 |
| E | 立项指南区段列、解读辅导 224 条、答案末尾无游离题号 | 40 区段 2,627 条 |
| F | 网页表头逐字等于桌面端 `Cols` / `CancelCols` / `LegacyCols` / `CmpCols` / Guifan `Cols` | 10/7/12/10 + 8 列全一致 |
| G | 名称列宽 ≥ 280px、每页条数 ≤ 200、前台不再调 `/api/admin/*` 查询接口 | 通过 |

→ **通过 49 / 失败 0**（对发布版 `dist/DIP3-Web` 实例跑的，不是源码目录）。
桌面端回归 `--selftest` → **111/111 PASS，EXIT=0**。

**七、产物**（2026-10-30 22:10 重打，SHA-256）

```
DIP3-Desktop-win-x64.zip  83,277,844 B  a65dfeb6a3611527bf2f9fe6195a7f1008123268742055ae7fd5552e101b801b
DIP3-Desktop-win-x86.zip  77,643,462 B  7a2e8e2b13269f077ba001b81db22d8fe7a0f46b3cb197dcb1adcd194515b825
DIP3-Web-win-x64.zip      94,515,098 B  c06508cc2189ea2d8b60ccf75397e79ca54c5bc83dc2cc3d0a40c0042a45490e
DIP3-Web-win-x86.zip      88,164,159 B  2a0ea0a930d063782e272920e222d12eb80b830586844ad18e0bcb03c4588038
```
（Web 两个包已被 §9.30 的软件壳重打覆盖，体积见该节。）

截图：`_shots/web_prod_dz.png`（现行项目 3,880 / 11 列）、`web_dz_cmp.png`（比对 6,766 · 差额 `+4.00` 涨红）、
`web_new_legacy.png`（旧版 6,589 · 状态着色 · 无导出按钮）、`web_new_gf.png`（技术规范 11,516 · 耗材三档）、
`web_new_lx.png`（区段列只在首行）、`web_new_lib.png`（序号承接「1–19，本区段从 20 起」）。

### 9.30 Web 端启动做成软件版：面板 + 托盘，不再开黑窗口（2026-10-30）

**起因**：用户截图指出启动横幅里的「工程根目录：…」「基层病种兜底：关（--jiceng 开启）」对最终用户没用，
并且要求「把这个启动做成软件版本」——绿色包现在双击就是一个控制台窗口，关了服务就停。

**一、启动横幅**

`src/Dip3Grouper.Web/Program.cs` 只留两行（访问地址 + 后台地址）。
`--jiceng` 是分组口径开关、默认关，只有真开了才多打一行「基层病种兜底: 开」，不再每台机器都念一遍。

**二、新增 `Dip3Grouper.WebShell`（绿色包的用户入口）**

`net8.0-windows` + WinForms + `OutputType=WinExe`，自绘一个小面板（600×410）：

```
┌ DIP 3.0 分组器 · 数据服务 ────────────────────┐
│ ● 服务运行中   端口 3210 · 局域网内其他电脑可直接访问 │
│ 本机地址      http://localhost:3210      [打 开]  │
│ 局域网地址    http://192.168.0.22:3210   [复 制]  │
│ [打开管理后台] [打开数据目录]        [退出服务]     │
└──────────────────────────────────────────────┘
```

- 启动即起 Kestrel（**单进程**，不是拉子进程），`AppState.Init` 的进度回调直接驱动进度条（载入立项指南 → 物价 → 分组引擎）；
- 起完自动开默认浏览器；关窗口只是 `Hide()` 到托盘，托盘双击回面板、右键可退出；
- 单实例 Mutex + `WebServer.IsAlive(port)` 双保险：重复双击不会再起一个进程抢端口，直接把页面亮出来；
- `--no-browser` 给脚本/冒烟测试用（重复双击时也尊重它，不弹浏览器）；
- 未处理异常落 `data\_logs\shell-error.log`（WinExe 没有控制台，否则只看到「闪一下没了」）。

`WebServer.Build(port, state)` 从原 `Program.cs` 抽到 `src/Dip3Grouper.Web/WebServer.cs`，
命令行版与软件壳共用同一份 Kestrel 配置，不各写一套。

**三、绿色包两个入口（`publish-web.bat` 改成两步发布）**

```
[2/8] publish Dip3Grouper.Web      → 命令行入口 Dip3Grouper.Web.exe（服务/计划任务/看日志）
[3/8] publish Dip3Grouper.WebShell → 软件入口 Dip3Grouper.WebShell.exe（双击即用）
```

顺序不能反：壳项目 `ProjectReference` 到 Web 项目，第二步会把 `Dip3Grouper.Web.exe` 再复制一份，
此时第一步生成的 `.deps.json` / `.runtimeconfig.json` 已经在目录里，命令行入口照样能跑。
`packaging/web/启动 DIP3 数据服务端.bat` 也改为转发到壳（`start "" WebShell.exe --port %PORT%`）。

**四、两个坑**

1. **裸 `Control` 不支持透明背景**：状态圆点继承 `Control` 后直接 `BackColor = Color.Transparent`
   → `ArgumentException("控件不支持透明背景色")`，进程启动即崩。必须先
   `SetStyle(ControlStyles.SupportsTransparentBackColor, true)`。Label/PictureBox 天生支持，只有 `Control` 不行。
2. **`CloseReason` 不能只看 `UserClosing`**：WinForms 只把「点标题栏 ×」（`SC_CLOSE`）标成 `UserClosing`；
   Alt+F4、任务栏右键、外部 `WM_CLOSE` 全被归到 `TaskManagerClosing`/`None`。
   写成 `if (reason == UserClosing) { 隐藏 }` 或把 `TaskManagerClosing` 放进放行名单，
   结果都是 `Application.Run` 返回、**整个进程退出**——正是用户说的「启动后窗口一关就没了」。
   正确写法是反过来判：只有本程序主动退出（`_exiting`）和 `WindowsShutDown` 才放行，其余一律 `Cancel + Hide`。

**五、验证**（发布版 `dist\DIP3-Web`，非源码目录）

| 用例 | 结果 |
|---|---|
| 双击启动 | 面板出现、`0.0.0.0:3408` LISTENING、`/` 200 |
| 点标题栏 × | `process alive=True`、`mainWindowHandle=0`（已隐藏到托盘）、`/` 仍 200 |
| 再双击一次 | 立即 `exit=0`，不产生第二个进程，`/` 仍 200 |
| 托盘图标 | 已注册（Windows 默认折叠进溢出区，点 ^ 可见） |
| 30 秒挂机 | 进程与端口稳定，内存 170 MB |

截图：`_shots/shell_panel.png`（面板）、`shell_dist.png`（发布版 · 端口 3408）、`shell_tray.png`（托盘区）。

**六、产物**（2026-10-30 22:36 重打，SHA-256）

```
DIP3-Desktop-win-x64.zip  83,277,844 B  a65dfeb6a3611527bf2f9fe6195a7f1008123268742055ae7fd5552e101b801b
DIP3-Desktop-win-x86.zip  77,643,462 B  7a2e8e2b13269f077ba001b81db22d8fe7a0f46b3cb197dcb1adcd194515b825
DIP3-Web-win-x64.zip      94,515,098 B  c06508cc2189ea2d8b60ccf75397e79ca54c5bc83dc2cc3d0a40c0042a45490e
DIP3-Web-win-x86.zip      88,164,159 B  2a0ea0a930d063782e272920e222d12eb80b830586844ad18e0bcb03c4588038
```

> Web 包从 63.7 MB 涨到 94.5 MB：软件壳带上了 `Microsoft.WindowsDesktop.App`（WinForms 运行时，约 30 MB）。
> 这 30 MB 换的是「双击即用、不弹黑框、不装 .NET」，绿色包不做 framework-dependent，没得省。

---

### 9.31 网页版式重做 + 展开项修复 + 启动器自动识别局域网地址（2026-10-30）

**一、展开项为什么「版式有问题」**

德州物价点开任意一行，标签和值各占一行、竖着堆下来，像调试输出。根因是 CSS 的选择器写在了错误的作用域：

```css
/* 改之前 */
.lx-detail .dgrid { display: grid; grid-template-columns: 132px 1fr; }  /* 生效范围只有立项指南 */
.dz-detail .dgrid { grid-template-columns: 118px 1fr; }                  /* 只覆盖了列宽，没有 display:grid */
```

`.dz-detail .dgrid` 把 `grid-template-columns` 改了，却没继承 `display: grid` —— 那个属性只写在
`.lx-detail .dgrid` 上。物价页的 `.dgrid` 因此始终是块级容器，每个 `div` 独占一行。
现在把结构样式提到通用类：

```css
.dgrid { display: grid; grid-template-columns: 112px minmax(0,1fr) 112px minmax(0,1fr); gap: 9px 24px; }
.dv.wide { grid-column: 2 / -1; }        /* 长文本（服务产出 / 价格构成 / 使用说明）占满整行 */
@media (max-width: 1180px) { .dgrid { grid-template-columns: 100px minmax(0,1fr); } }
```

配套把生成逻辑统一成一个 `dcell(k, h)`：值超过 40 字或含换行就加 `.wide`。
标题行从 `.dtitle`（编号 + 名称 + 单位挤在一行）改成 `.dhead`（编号胶囊 / 名称 / 右侧单位），
两者在 6 处生成点全部对齐（立项指南、物价旧版、物价行内展开、解读辅导）。

**二、表宽：两个极端都不对，取中间**

| 做法 | 现象 |
|---|---|
| `width:100%` + 全列固定宽 | 浏览器按比例重分配，300px 名称列实测被压到 85px |
| `width:列宽之和`（上一版的做法） | 列不压了，但宽屏右侧留一条空白（立项指南实测留 200px） |
| **`width:100%` + `min-width:列宽之和`** | 容器宽 → 列按比例放大铺满；容器窄 → 横向滚动，列不被压 |

`setColWidths()` 里同时统计「没有 width 的列」数量，按每列 280px 计进 `min-width`，
这样自适应列（立项指南的「使用说明」、解读辅导的「解读问题」）不会在窄屏被挤成一条缝。

**三、其余版式调整（「大气」不是加阴影，是给信息留层次）**

- 容器 1380 → **1560**，导航与内容区同一基线；`.wrap` 内边距 26px。
- 页头：`21px` 标题 + 13px 副标题 + 右侧视图切换（`margin-left:auto`），只占一条基线。
- 主按钮去掉渐变和投影（`height:34px` 实色），与工具栏内所有控件同高；下拉补回自绘三角。
- 表头 36px / `var(--row-h)` 行高；斑马纹、hover 保持；展开行来源行加左侧 3px 主色条。
- `.tbl-wrap` 高度从写死的 440/480/520（4 处在 HTML 内联）改成
  `clamp(320px, calc(100vh - 300px), 1000px)` —— 大屏不再「表格只有半截、下面一片白」。
- 病种库 6 个长文本列 240 → 232：`64 + 6×232 = 1456` 正好等于容器内宽，横向滚动条消失。
- 解读辅导的 `<colgroup>` 原本写在 `<thead>` 里（会被解析器丢弃，列宽静默失效），
  改为 `setColWidths('jdTable', …)` 挂到 `<table>` 下。

**四、启动器：局域网地址按网卡打分选，面板给出四条信息**

原来取「第一块 Up 网卡的 IPv4」。装了 VMware/VirtualBox/Hyper-V 的机器上，枚举顺序常把
`VMnet1(192.168.x.1)`、`vEthernet(172.x)` 排在真正的 Wi-Fi 前面 —— 发给同事必然连不上。

```csharp
if (hasGateway) score += 60;                                  // 有默认网关 = 真连着路由器
score += type switch { Ethernet or GigabitEthernet => 40, Wireless80211 => 35, _ => 0 };
if (IsPrivate(ip)) score += 25;                               // 10./172.16-31./192.168.
if (ip.StartsWith("169.254.")) score -= 200;                  // 没拿到 DHCP，等于不可用
if (virtualCard) score -= 120;                                // 描述含 vmware / vbox / hyper-v / tap / vpn …
```

面板（660×548）四行，每行「标签 / 值 / 副注 / 打开 / 复制」：

```
本机访问    http://localhost:3210
局域网访问  http://192.168.0.22:3210      以太网 · 另有 1 个网卡地址
后台管理    http://192.168.0.22:3210/admin
数据目录    …\DIP3-Web\data                数据与备份所在目录
```

插拔网线、切 Wi-Fi 后点「刷新地址」重算；托盘菜单同步加了「打开管理后台 / 刷新局域网地址」。

**五、「其他电脑没装任何环境能跑吗」——能，且做了两项证据**

1. **PE 导入表**（自己解析，不依赖 dumpbin）：

   ```
   Dip3Grouper.WebShell.exe -> KERNEL32 USER32 SHELL32 ADVAPI32 + api-ms-win-crt-*
   coreclr.dll              -> 同上（没有 msvcp140 / vcruntime140）
   ```

   只有系统 DLL 和 Windows 10+ 内置的 UCRT API set —— 不需要 VC++ 运行库，也不需要 .NET。
2. **把 DOTNET_ROOT 指向不存在的目录**、并禁用多级查找后启动发布版：

   ```
   DOTNET_ROOT=C:\no-such-dotnet DOTNET_MULTILEVEL_LOOKUP=0 ./Dip3Grouper.Web.exe --port 3420
   → 0.0.0.0:3420 LISTENING，/api/dezhou 200
   ```

   说明运行时全部来自包内的 `hostfxr.dll` / `coreclr.dll` / `System.Private.CoreLib.dll`。

边界要说清楚：**Windows 10 1809+ / Windows 11**，Win7/8.1 跑不了（.NET 8 不支持）。
客户端机器只要浏览器，整个科室只有服务端那一台需要解压。

**六、验证与产物**（2026-10-30 23:0x）

`tools/verify-web-parity.mjs` 增 **G 组 10 项版式回归**（`.dgrid` 是否通用 grid、`.dz-detail .dgrid`
是否复活、`.dv.wide` 是否跨列、表宽是否 100%+min-width、colgroup 是否挂在 table 下、
启动器是否排除虚拟网卡 / 网关加权 / 面板含后台与目录），总计 **59 项**。
对**发布版** `dist/DIP3-Web`（3210）跑：**59/59 全过**。

截图：`_shots/prod_dz_open.png`（物价展开两列）、`n_lx2.png`（立项展开）、`n_gf2.png`（技术规范铺满）、
`n_lib.png`、`n_single.png`、`shell_prod.png`（面板四条地址，端口 3210）。

```
DIP3-Desktop-win-x64.zip  83,277,844 B  a65dfeb6a3611527bf2f9fe6195a7f1008123268742055ae7fd5552e101b801b
DIP3-Desktop-win-x86.zip  77,643,462 B  7a2e8e2b13269f077ba001b81db22d8fe7a0f46b3cb197dcb1adcd194515b825
DIP3-Web-win-x64.zip      94,520,124 B  55a548c5b48076a1aa96a667bcab8e573cbc132d3ce8de4b6f6e67d4739295e8
DIP3-Web-win-x86.zip      88,169,194 B  30f9783eca99deddc6502429466801c93f8f403abbb39544ecbcb90467da65e5
```

> 桌面端两个包没重打：WPF 工程不含 `web\`，这次改的都是网页资源与 Web 壳。

> **⚠ 本节的两列方案已被推翻**：用户明确「网页版参照 .NET 版」，详情卡回到单列 132px，见 §9.32；
> 字段口径的进一步收敛见 §9.33（**最新包哈希以 §9.33 为准**）。

---

### 9.32 网页版详情卡改为「以桌面端为准」的单列（2026-10-30 23:2x）

§9.31 里我把网页版详情卡做成了 `112px 1fr 112px 1fr` 的两列网格，桌面端仍是「132px 标签 + 值」的单列 ——
同一个项目，网页一行放两个字段、卡片矮一半，用户对照截图后要求**反过来对齐：网页版参照 .NET 版**。
桌面端代码一行未动，全部改动收在 `web/index.html`。

**现在的口径（逐项与 `DataGrid.RowDetailsTemplate` 对齐）**

| 元素 | 桌面端 | 网页端（改后） |
|---|---|---|
| 卡片 | `B.SoftBg` 底 + `SoftBorder` 1px + 10 圆角 + `Padding 14,12` + `Margin 10,4,10,10` | `--soft-bg` 底 + `--border` 1px + 10 圆角 + `12px 14px` + `4px 10px 10px` |
| 标题行 | 编号 Chip（11px mono）+ 名称 13.5 粗 + 右侧「收起」，无分隔线，下边距 8 | `.dhead` flex + `.dcode` 同尺寸 + `.dname` 13.5 + `.dclose` |
| 字段 | `Block.Tpl`：132px 标签列 + 值 12.5，块距 4 | `.dgrid` 单列 `132px minmax(0,1fr)`，`row-gap 4px` |
| 量表 | `Meter.Bar` 6px 圆角轨道，蓝 `#1e88e5` / 橙 `#f59e0c` / 绿 `#2ea44f` | `.dmeter` 132 标签 + 右对齐数字 + 同色 6px 条 |
| 胶囊语义 | 立项无胶囊；物价 `add→加收项`、`ext→扩展项`；规范 类别 + 「单位 次」 | 同步改成这三条规则 |

**顺手修掉的一个真 bug**：技术规范页的 `gfToggle()` 里 `colspan="${cols.length}"` 用了不存在的 `cols`
（该变量只在物价的 `dzToggle` 里声明），点行直接抛 `ReferenceError` —— 网页版规范详情卡从来没展开过。
改成 `GF_COLS.length` 后正常，并把 `tr.open` 高亮补进 `#gfTable`。

**验证**：`node tools/verify-web-parity.mjs` 扩到 **62 项**（G 组新增：单列 132 轨、`.dv.wide` 只占值列、
卡片外观三项、`.dclose`、量表存在），对开发实例 3399 与发布版 3210 均 **62/62 通过**。

截图：`_shots/w_lx_final.png`、`_shots/w_gf_open4.png`、`_shots/w_dz_open.png`（网页版）；
`_shots/d_rollback2.png`（桌面端同页，对照用）。

---

### 9.33 详情卡字段口径收敛到桌面端（2026-10-30 23:4x）

§9.32 只统一了「长什么样」（单列 132px），没统一「放什么」。用户再对照截图，同一行项目：

- **桌面端**：三档价 / 落地文件 / 医保支付 / 器官移植类·使用说明 —— 4 行；
- **网页端**：项目编码、类别、项目性质、计价单位、三档价、医保支付、自付比例、服务产出、价格构成、
  计价说明、备注、扣减基本物耗、落地文号、执行日期 —— 14 行，其中 8 行是 `—`。

根因不是排版，是字段清单。桌面端 `DezhouPriceView.BuildBlocks()` 里有一条 `Add()`：**空值直接 return**，
并且「表格里已经有的列」不算字段；网页端把接口返回的每个键都摆了一遍，空值还统一打 `—`。

**现在的口径**（逐条照 `BuildBlocks`）

| 桌面端写法 | 网页端 |
|---|---|
| `Add("落地文件", $"{Doc}　执行日期 {Exec}")` | 合并成一行，两个都空则整行不出现 |
| `Add("医保支付", $"{Insurance}　自付比例 {SelfPay}")` | 合并成一行 |
| `Add("扣减基本物耗价格", $"{Deduct} 元　（…收费时按此金额扣减）")` | 带括号说明，无值不出现 |
| `Add("限定支付范围", Note)` / 服务产出 / 价格构成 / 计价说明 | 同名同序，空值不出现 |
| `UsageOf(doc, category)` 前 2 条 + 「本类共 N 条」 | `dzFillUsage()` 异步取，就地「点击展开全部」 |

去掉的四项（编码 / 类别 / 性质 / 单位）在表格列里已经有了；`kind` 仍在标题胶囊上体现（加收项 / 扩展项）。

**统一成一个 `dGrid()`**：`dGrid(rows, extra)` 先过 `dash()`（比对表缺价是 `\`、`-` 这类占位符），
再滤掉 `—`，再拼 `.dgrid`。物价详情、技术规范详情都改成调它；立项指南本来就自带 `filter(([, h]) => h)`。

**新增接口** `GET /api/dezhou-usage?doc=&category=` → `{ lines: [...] }`，直接转 `S.Dezhou.UsageOf()`
（与桌面端同一个方法），并加入 `IsPublicDataPath` 白名单（与 `/api/dezhou` 同一套共享口令）。
桌面端这里写「点此定位查看全部 ▸」，跳页面上方的使用说明面板；网页版物价页没有那个面板，
所以改成就地展开全文（再点「收起 ▴」回到前两条），不留一句点不动的死文案。

**顺带修掉的两处**

1. 新旧比对视图里「新三级价」显示成反斜杠 `\` —— 中间那层 `rows.map` 直接 `esc(v)` 没过 `dash()`；
2. 作废 / 旧版 / 比对三个视图也会插一个「类别·使用说明」空格子，但只有现行视图会去取数，
   永远停在 `…`。现在按视图判断，只在 `live` 挂这块。

**验证**：`tools/verify-web-parity.mjs` 新增 **H 组 7 项**（字段收敛、落地文件合并、医保支付合并、
空值不渲染、使用说明块、`/api/dezhou-usage` 路由 + 白名单 + 同源 UsageOf），总计 **69 项**；
开发实例与 `dist/DIP3-Web`（3210）各跑一次，**69/69 全过**。

截图：`_shots/w_dz_same.png`（与桌面端同一行，4+1 行）、`_shots/w_dz_useall.png`（展开 9 条说明）、
`_shots/w_gf_check2.png`（规范页空值行消失）、`_shots/w_prod_dz_final.png`（3210 生效）。

```
DIP3-Web-win-x64.zip      94,522,774 B  9cc465e52ed7ff222186793e3ed2d6eeda091c24f391230f67e5da108481d39c
DIP3-Web-win-x86.zip      88,171,863 B  be66c09c2d305eaa4ec41676f30f0ba3e99938e1cca28eb8b5a550c04cfd116f
```

> 桌面端两个包仍未重打：WPF 代码这次没有任何改动（§9.32 的回滚保持有效）。

---

---

### 9.34 互相关联：算法下沉 Core，网页版三张详情卡补齐（2026-10-30 23:5x）

用户接着指出第二个差异：**「网页版项目 缺互相关联」**。桌面端每张详情卡末尾都有
`CrossMap` 生成的「关联·立项指南 / 关联·技术规范」（同名直连 + 相似候选），网页版一条都没有。

**根因**：`CrossMap` 写在 `Dip3Grouper.Desktop/CrossMap.cs`（WPF 工程），它依赖 `LxBlock` / `LxLine`
这类视图类型，Web 工程引用不到 —— 算法本身其实与 UI 无关，只是住错了地方。

**做法：把算法搬到 Core，两端共用一份**

- 新增 `src/Dip3Grouper.Core/CrossLinker.cs`：`AppState` 参数化（不再吃 `App.State` 单例），
  返回 `CrossLinkGroup { Label, Hits: [{Chip, Text, Lib, Id}] }`；
- `AppState` 实例也参与缓存判定（`ReferenceEquals(_owner, state)`）—— 同一进程换实例时要重建索引，
  否则会拿旧实例的 Id 去新实例里跳转；数据戳仍是「区段数/指南行数 | 物价条数」；
- 桌面端 `CrossMap.cs` 从 266 行压到 60 行，只剩「`CrossLinkGroup` → `LxBlock`」的转换，
  `Warm()` / 三个方向的公开签名一字未改，调用点零改动；
- Web 新增 `GET /api/cross?lib=dezhou|lixiang|guifan&name=` → `{ groups }`，加入 `IsPublicDataPath`。

这样 Dice 0.62 阈值、top3、`Norm()` 归一化、**立项指南用 Gid 当跳转键**（区段内 Id 会串号，§9.25）
这些细节只有一处实现，不会再出现「桌面端命中三条、网页端命中两条」。

**网页端呈现**

- 三张卡（物价 / 立项 / 技术规范）末尾都挂 `<div class="dxref">`，展开后异步取，**取不到就整块移除**，
  不留空标题；
- 每块沿用字段区的 `132px 标签 + 值`，值里是「`同名`/`相似` 胶囊 + 名称　摘要」，与桌面端 `Block.Tpl` 一致；
- 关联块与字段区之间一条虚线分隔；
- **点击跳转**：桌面端是弹 `RelatedWindow` 详情窗，网页端没有那个窗，等价落点是
  「切到目标库 → 搜索框填名称 → 查一次」，已实测（点「心脏移植术」跳立项指南，1 条命中）。

**顺带修的**：使用说明那一格原来用固定 `id="dzUseCell"`，同页展开多张卡时会互相串（第二个卡填到第一个卡里），
改成 `class="dzuse"` + 按当前卡的元素定位。

**验证**

- `tools/verify-web-parity.mjs` 新增 **I 组 12 项**（Core 三方向、阈值唯一、Gid 跳转键、桌面端已委托、
  三个库的接口实测命中、lib+id 齐全、三卡都挂关联位、跳转函数、白名单），总计 **81 项**；
- 开发实例与 `dist/DIP3-Web`（3210）各跑一次：**81/81 全过**；
- **桌面端自检 111/111 PASS**（`--selftest`，退出码 0）—— 委托改造行为不变。

截图：`_shots/w_dz_cross.png` / `_shots/w_prod_cross.png`（物价两张关联块）、
`_shots/w_lx_cross.png`（立项→物价+规范）、`_shots/w_gf_cross2.png`（规范→立项+物价）、
`_shots/w_cross_jump.png`（点关联跳到立项指南并检索）。

```
DIP3-Web-win-x64.zip      94,527,925 B  dc10bb6acba575a446a936faa399fe3f2fbc3b31d594488d23de30e4a956f1b5
DIP3-Web-win-x86.zip      88,177,005 B  51bb8e9e7b2ae4b29a223ef36520ea4cc042704d8f476b5162b112aee4c89cac
```

> 桌面端两个 zip 未重打：`CrossMap` 改成委托后行为与旧包完全一致（自检 111/111），
> 下次统一打包时会自然带上。

---

### 9.35 废止表里「划横线」的两行其实不作废（2026-11-01 19:5x）

用户拿来 25 号文 PDF 指出：**「作废项目中有划横线的是非作废，查找修改」**。

**怎么从 PDF 里认删除线**

全文 266 页，附件 10–18 是 9 张废止表（p158–230）。删除线不是文字，是**描边横线**
（`page.get_drawings()` 里 `type == "s"`、线宽 0.6pt）；表格行分隔线是 `type == "f"` 的填充矩形，
两者必须分开判，否则合并单元格（如「批次 / 类别」跨行）里的序号会被当成划线。
判定规则：线的 y 落在词框 25%~75% 区间、且横向覆盖该词 50% 以上。

全文档只有两处命中：

| 附件 | 页 / 序号 | 原编码 | 原名称 | 映射新编码 |
|---|---|---|---|---|
| 13 血管、淋巴 | p186 / 4 | `311400031` | 血管瘤硬化剂注射治疗（下肢血管曲张注射） | `013107000110000` 肢体血管药物封闭费 |
| 17 药学 | p218 / 16 | `PCAA0101` | 中药普通饮片调配 | `011400000070000` 中药配方颗粒调剂费 |

> p191 的「82」是跨页被拆开的合并序号（上半截落在上一页），不是划线，已排除。

**数据**：`data/dezhou-price.json` 的 `cancels` 里这两行加 `keep: true`。
总行数仍是 **5,107** —— 这两条在附件 22 / 26 的映射关系表里原文**没有划线**，
映射要保留，只是不再算作废。写回时保持原文件格式（无 BOM、CRLF、indent=1），
与备份逐行 diff 只多两行 `"keep": true`。

**口径（Core 一处实现，双端同源）**

- `CanceledSet` / `CancelIndex` / `CancelOf` 跳过 `keep`：旧版物价里这两码回到「现行」，
  不再给「现行标准」链接（旧项目没作废，谈何「替代」）；
- `CancelStat` 增加 `Kept`；`Mapped + Dead = Total` 的拆分不变（保留是子集，不参与拆分）；
- 新旧比对的 `_oldByNew` **不跳过** `keep`：映射表原文有这两行，新旧价并列比对仍成立；
- `ImportCancels` 整表重导时按「(文号, 旧编码)」把 `keep` 搬回新表 —— 否则一次重导就把标记打回作废。

**双端呈现**：作废对照表新增「状态」列（作废红 / 保留绿，悬停写明「原文划线 = 不作废」），
统计条改成「作废对照 5,107 行（其中保留 2·原文划线不作废）」。桌面端「对应新编码」列
126 → 148px：15 位编码 12px 等宽约 138px，126 会折成两行。

**验证**

- 桌面端 `--selftest`：**112/112 PASS**（新增第 13 项「划线保留不作废 / 不给现行标准 / 统计单列」，
  实跑：2 行保留（311400031 / PCAA0101）· 均未判作废 · 旧版物价联动「现行」2 行）；
- 网页对拍 `tools/verify-web-parity.mjs`：**81/81**（物价 cancel 8 列桌面端 / 网页端逐字一致）；
- 3210 实测：`view=cancel&q=311400031` → `keep:true / state:"保留"`；
  `view=legacy&q=311400031|PCAA0101` → 状态「现行」、`currentStd` 为空；
- 数据前后：旧版物价「已作废」**3,807 → 3,805**，总条数 6,589 不变。

截图：`_shots/w_cancel_keep.png`（网页作废对照保留态）、`_shots/d_cancel_keep.png`（桌面同视图）、
`_shots/w_legacy_live.png` / `_shots/d_legacy_live.png`（旧版物价回到「现行」）。

```
DIP3-Web-win-x64.zip      95,281,292 B  ed8da6faafe8b307c6bd5e9e7f77c658f41a49f573011b6d32cf2763370b04a9
DIP3-Web-win-x86.zip      88,930,124 B  5d9eeedfb705b7f2c5e0dc216907acb199b56d3279e64cfd8d4a288046ce25b9
DIP3-Desktop-win-x64.zip  84,032,664 B  7efce06902477ab1d0d604c8af5b25c140fec8588ad3eca39731d521b7b26640
DIP3-Desktop-win-x86.zip  78,397,801 B  37f009edc6087ca857637d6d79765c5da91ed9247e3573534edc4c1c0cc56f30
```

> 这次的四个包都是新打的（不像 §9.34 只重打网页端）：桌面端多了「状态」列与统计口径，
> 网页端多了状态列，数据文件也改了，必须整包替换。**最新哈希以本节为准。**

### 9.36 「组织病理·送检指引」搬进软件（2026-11-01 20:0x–20:4x）

用户发来一张安谱（ADICON）《组织病理·送检指引》海报截图：

> 「改图融合进软件，病历开具比较困难，这个是个说明」

开病理申请单时最费劲的两件事 —— **这个标本该选哪个档位**、**算几个蜡块收多少钱** —— 海报上其实都写了，
但贴在墙上/存在微信里翻起来慢。这次把它做成两端可查的速查表。

**原图从哪儿来（关键）**

微信截图只有 480×679，蓝标签里的「常规病理 4蜡块」根本认不出。翻同期的
`关于病理科新物价收费的切换说明.docx` 发现海报就以 `word/media/image6.jpeg`（878×1245）
整张嵌在文档里 —— 正文那句「其他常见病理组织样本类型，与蜡块对应关系见下图」指的就是它。
改用原图逐块放大核对，47 条档位全部按原文录入（连「血管·腹膜腹壁」「椎间盘、髓核、腘窝囊肿」
「子宫内容物」这些容易看错的词都放大确认过）。

**数据** `data/zhijian.json`（新增，已进 `DataCatalog` 白名单，可随局域网更新下发）

- 15 个疾病系统 / **47 条档位**：女性生殖 10、消化 5、头颈 5、男性生殖 4、骨与关节 4、
  泌尿 3、内分泌 3、乳腺 3、血液 2、呼吸 2、血管 2、皮肤 2、神经 1、根治术 1、多部位 1；
- `pricing`：诊断费 130（10 蜡块内，第 11 块起每块 +6.75，最多到第 30 块）、
  标本处理费 45/蜡块（常规 / 复杂两档）、染色费前 3 块 45（减收 5 元）第 4 块起 50；
- `rules`：海报右下角四条蜡块核定规则；`special`：免疫组化 162/167、骨髓活检、细胞病理。

**算法（Core 一处实现，两端共用）** `src/Dip3Grouper.Core/Zhijian.cs`

- `ZhijianStore.Quote(wax, complex)` 出明细行（处理费 / 染色减收 / 染色原价 / 诊断费 / 超 10 张加收）；
- `ZhijianStore.WaxCount(bags, piecesPerBag, maxCm)` = 三大规则的算术化：
  1 袋 1 块、每袋满 4 块进位、体积超 2cm 每超 1cm 加 1 块。
- 自检直接对《切换说明》里的价格表：**1/2/4/5 蜡块 = 220 / 310 / 495 / 590**，
  11 蜡块 = **1166.75**；速算复现三个案例（4 点位=4、5 块+2 块=3、9cm=8）。

**双端呈现**

| | 入口 | 形态 |
|---|---|---|
| 桌面端 | 顶栏「送检指引」（Ctrl+G 亦可） | `SendGuideWindow` 弹窗：左系统 / 中档位表 / 右蜡块数与收费 / 底部规则 |
| 网页端 | 顶栏「🧪 送检指引」（Ctrl+G 亦可） | `#zgBox` 遮罩弹窗，同版式；金额一律回 `/api/zhijian` 算，前端不自己乘 |

两端的共同细节：选中条目自动把档位（比如 4 蜡块）填进蜡块数；双击一行复制
`常规病理 4蜡块｜宫颈活检（分装）、子宫次切` 直接贴进病历；「蜡块速算」按袋数/每袋块数/最大边长
估蜡块数，一键套用到报价。

**踩到的两个坑**

- `DataGrid.RowHeight` 是 `double` 不是 `DataGridLength`，XAML 里写 `Auto` 会直接抛
  `TypeConverterMarkupExtension` 异常；要自适应只能在代码里置 `double.NaN`（否则标本名第二行被裁掉）。
- 星级列 `Width="*"` 在离屏 Measure 阶段会被算成 0，截图里看不见第三列；给个 `MinWidth` 才稳定。

**验证**

- 桌面端 `--selftest`：**113/113 PASS**（新增第 14 项「送检指引·档位数据与蜡块/收费速算」，
  实跑：15 个系统 / 47 条档位 · 220/310/495/590 元 · 11 蜡块 1166.75 · 速算 4 / 3 / 8）；
- 网页对拍 `tools/verify-web-parity.mjs`：**93/93**（新增 J 组 12 项：条数源对拍、15 系统、
  价格表逐档、超 10 张加收、复杂档、两个速算案例、弹窗结构、两端同源、数据文件名白名单）；
- 3210 实测 `/api/zhijian?wax=4&q=宫颈` → 5 条命中、`quote.total = 495`；
  页面弹窗搜索「甲状腺」→ 2 条，蜡块 4 → ¥495。

截图：`_shots/d_guide4.png`（桌面端按系统浏览）、`_shots/d_guide5.png`（桌面端搜 ESD → 2 蜡块 ¥310）、
`_shots/w_guide3.png`（网页端同视图）、`_shots/w_guide4.png`（网页端搜「甲状腺」→ ¥495）。

**发布包（四个都重打：数据、页面、双端程序集全变了）**

```
DIP3-Web-win-x64.zip      95,297,102 B  24ad087c…a89a03
DIP3-Web-win-x86.zip      88,945,942 B  eabeaccf…bb67fd
DIP3-Desktop-win-x64.zip  84,048,543 B  eb632093…3a64b4
DIP3-Desktop-win-x86.zip  78,413,613 B  5d2d9721…032354
```

> 新增 `data/zhijian.json` 已同时写进四份 dist；桌面端顶栏按钮与 Ctrl+G 都在
> `Dip3Grouper.Desktop.dll` 里，网页端弹窗在 `web/index.html` 里，**必须整包替换**，
> 单换 dll 或单换页面都会缺入口。**最新哈希以本节为准。**

### 9.37 改叫「病理开具指引」· 挪到右侧竖栏 · 价格换成德州二级/三级（2026-11-01 20:3x–21:0x）

> 「送检指引改为病理开具指引，移除导航，增加侧边按钮，放到侧边栏。价格不正确，
> 参考 关于病理科新物价收费的切换说明.docx / 德州市病理一二三级物价.xlsx」

**① 价格为什么不对：上一版用的是济南口径**

9.36 的 130 / 45 / 45 来自《切换说明》里「以济南市医保收费为例」那一段，而本院执行的是
**《德州市病理一二三级物价》**。两者差得不少（1 蜡块：济南 220，德州二级 168、三级 198）。
新表逐行核对后重建 `data/zhijian.json`：

- `levels[]` 两档 —— 二级：诊断 104（减收 5 → **99**）/ 常规处理 **35** / 复杂处理 **50** /
  染色 **39**（前 3 块减收 5 → **34**）/ 细胞处理 **102** / 细胞蜡块 **31** / 免疫组化 129；
  三级：122（→117）/ 41 / 59 / 45（→40）/ 119 / 36 / 150。
- `base`：10 个蜡块内诊断费只收 1 次，第 11 块起每块加收 **5% 诊断费**（二级 5.2 / 三级 6.1），
  最多加收到第 30 块；染色费前 3 块每块减收 5 元。
- `special`：细胞病理 / 细胞蜡块 / 免疫组化 / 疑难会诊 / 基因检测·FISH·p16，**二级、三级各一套金额**
  （`LinesFor(level)`），德州表里没有的（FISH、p16 双染、分子病理）单列并注明「按上级文件、开单前与物价科确认」。

算法改了报价模型：`Quote(count, mode, levelId)`，`mode` = 常规 / 复杂 / **细胞** / **TCT**
（细胞按涂片张数收处理费+染色费+诊断费；TCT 只收诊断费+细胞处理费，德州 50503145894 口径）。
单价一律存「未减收原价」，减收额单独存，由 `Quote` 统一扣 —— 表里一个数、代码里一个数就不会打架。

**② 入口搬家：导航 → 右侧竖排侧边按钮 + 抽屉**

| | 原来 | 现在 |
|---|---|---|
| 桌面端 | 标题栏「送检指引」按钮 → `SendGuideWindow` 独立弹窗 | 标题栏按钮**删掉**；窗口右边缘挂一条竖排「病理开具指引」，点开从右侧滑出 782px 抽屉（`GuidePanel` 用户控件），Ctrl+G 开合、Esc 收起 |
| 网页端 | 导航右侧「🧪 送检指引」→ 居中遮罩弹窗 | 导航按钮**删掉**；`position:fixed` 右边缘竖排标签，点开 `translateX(100%)→0` 滑出 784px 抽屉 |

抽屉版式（两端一致）：头部标题 + **价格档 二级/三级** 分段 + 收起；检索条；主体左 15 个疾病系统 /
右档位表（档位 + 标本，省掉系统列，同系统内不重复）；底部左收费明细（逐条物价名称 + 数量 + 金额）、
右「计价类型 / 蜡块数（细胞模式自动改叫涂片数）/ 合计 / 速算」；页脚「蜡块核定规则 / 专项价目 / 来源」。

**踩到的坑**

- 竖排中文别用 `RotateTransform 90` —— 字是躺着的，得歪头看。桌面端改成 `Width=14 + TextWrapping=Wrap`
  一个字一行；网页端用 `writing-mode: vertical-rl`。
- 抽屉滑入动画必须「先清动画、赋基值、再 BeginAnimation」，且 `Completed` 里要再认一次 `_guideOpen`
  （连点两下时旧动画的 Completed 会把刚开的抽屉关掉）。
- `GuidePanel` 要能被 XAML 直接实例化（`<local:GuidePanel/>`），所以留了无参构造，`App.State` 推迟到
  `Init()`（Loaded）里再取；Bench 截图走 `new GuidePanel(App.State)`。
- `--shot-page` 的 `@宽x高` 只认 `seg[0]`，写成 `guide:系统::细胞@782x820` 是无效的，
  必须 `guide@782x820:系统::细胞`；选中行会按条目档位回填，所以截图里的「计价类型」要在选中之后再切。
- 网页端 `zg-sysitem` 里的长系统名（头颈部·耳鼻咽喉·口腔）会折行把条目数挤上去，改 `flex:1 + ellipsis + title`。

**验证**

- 桌面端 `--selftest`：**113/113 PASS**，第 14 项改成对德州表逐行核：常规 1/2/3/4/5/6/7/8/10/12/13 蜡块、
  复杂 6/8/10/12/15、细胞 1–2 张、TCT，二级 + 三级共 38 个价格点全部相等（1 蜡块 168/198、
  4 蜡块 380/446、12 蜡块 982.4/1146.2、复杂 15 蜡块 1445/1692.5、细胞 1 张 235/276、TCT 201/236），
  另加「第 31 块起诊断费不再加收」。
- 网页对拍 `tools/verify-web-parity.mjs`：**97/97**（J 组扩到 16 项，新增两档价格档、逐档对表、
  专项按档取、导航里不再有「送检指引」等断言）。
- 3210 实跑：`/api/zhijian?wax=4&level=3` → 三级 ¥446；页面抽屉二级 1 蜡块 → ¥168。

截图：`_shots/d_win.png`（右侧竖排标签）、`_shots/d_drawer.png`（抽屉全貌）、
`_shots/d_guide_lv2.png` / `d_guide_lv3.png` / `d_guide_cell.png`（二级·三级·细胞）、
`_shots/w_index_tab.png` / `w_drawer.png`（网页端同款）。

**发布包（四个都重打：数据、页面、双端程序集全变了）**

```
DIP3-Web-win-x64.zip      95,303,089 B  4cfecf78…bc8167
DIP3-Web-win-x86.zip      88,952,185 B  a63d3686…1c8182
DIP3-Desktop-win-x64.zip  84,052,258 B  2294e261…f02f6b
DIP3-Desktop-win-x86.zip  78,417,973 B  3f45abe7…9e71c0
```

> `data/zhijian.json` 的 `pricing` 段已被 `levels[]` + `base` 取代，**旧版程序读新数据会算成 0**；
> 反之新程序读旧数据会回落到单档。所以程序与数据必须整包一起换，不能只换 `web/index.html`。
> **最新哈希以本节为准。**

### 9.38 侧边栏改叫「工具栏」：病理变成其中一个工具，另留 3 个位（2026-11-01 21:0x–21:2x）

> 「侧边栏名称改成工具栏，点击里面包含病理，预留位置便于以后增加新工具」

**① 一个壳 + N 个工具**

右侧竖排标签由「病理开具指引」改成「工具栏」。点开先落在**工具列表**（2×2 卡片：病理开具指引 +
3 个虚线预留位），点病理卡才进病理本体，病理头部左侧多一个「‹」回列表。
Ctrl+G 维持老习惯 —— **直接落到病理**（少点一次），点标签才停在列表。

| | 桌面端 | 网页端 |
|---|---|---|
| 壳 | `ToolPanel`（`HomeView` / `GuideView` 两层叠放，切 Visibility） | `#zgHome` / `#zgGuide` 两个 `.zg-view`，切 `.hidden` |
| 工具 id | `ToolPanel.ToolPathology = "pathology"` | `zgShowTool('pathology')` |
| 返回 | `GuidePanel.CanGoBack = true` + `BackRequested` 事件 | 「‹」→ `zgShowTool('')` |
| 快捷键 | 标签 → `OpenGuide(deep:false)`；Ctrl+G → `OpenGuide(deep:true)`；Esc 关抽屉 | 标签 → `zgOpen()`；Ctrl+G → `zgOpen('pathology')`；Esc 关抽屉 |

**② 以后加工具，只动三处**（导航、抽屉开关、快捷键都不用碰）

1. 桌面：`ToolPanel.xaml` 里把一格虚线换成 `Button`（`Click` 里调 `ShowXxx()`），再加一个
   `<Grid x:Name="XxxView" Visibility="Collapsed">` 放该工具的面板；`ToolPanel.xaml.cs` 的
   `OpenTool()` 加一个 id 分支。
2. 网页：`#zgHome` 里加一张 `.zg-tool` 卡（`onclick="zgShowTool('xxx')"`），再并列加一层
   `<div class="zg-view hidden" id="zgXxx">`；`zgShowTool()` 里补该 id 的加载。
3. 数据与算法仍然只写在 Core，两端共用（同 §9.36 的做法），别在页面里各算一套。

**踩到的坑**

- XAML 里 `x:Name="ToolHost"` 与旧的 `GuideHost` 是两个字段：`MainWindow.xaml.cs` 里
  `CloseRequested` 订阅、`HandleKey`、`FocusSearch` 三处都要跟着改名，漏一处就「✕ 关不掉 / Esc 无反应」。
- `GuidePanel` 的返回按钮默认 `Collapsed`（单独截图时不该出现），由 `ToolPanel` 构造里
  `Guide.CanGoBack = true` 打开 —— 反过来在 `GuidePanel` 里写死 Visible，截图里就会多一个死按钮。
- 抽屉首页用 `margin-top:auto` 把说明压到底部，否则矮抽屉里只剩两张卡浮在顶部，看着像没加载完。
- 预留位用 `Rectangle` + `StrokeDashArray="3 3"`（`Border` 不支持虚线），三格与病理卡同高 `MinHeight=66`。

**验证**

- 桌面端 `--selftest`：**114/114 PASS**（新增第 15 项：反射确认主窗口 `ToolHost` 字段类型是 `ToolPanel`、
  `OpenTool(string)` 存在、`ToolPathology == "pathology"`、`GuidePanel.OnBack` 存在）。
- 网页对拍 `tools/verify-web-parity.mjs`：**103/103**（新增 5 项：竖排标签改名、两层视图可互切、
  3 个预留位、Ctrl+G 仍直达病理、桌面 `ToolPanel` 结构）。
- 实跑：点标签 → 工具栏首页；点病理卡 → 病理（带 ‹）；‹ → 回首页；Ctrl+G → 直接病理；Esc → 收起。
  网页端 playwright 走同一条链路（`#zgTab` → `.zg-tool` → `.zg-back`）。
- 3210 生产页已引用新页面（`curl | grep zgHome` 命中 4 处），playwright 实拍抽屉正常。

截图：`_shots/d_live_home.png`（桌面工具栏首页）、`d_final_guide.png`（病理层，含 ‹）、
`_shots/w_tool_home.png` / `w_tool_guide.png`（网页端同款）、`d_tab_crop2.png` / `w_tab2.png`（竖排标签）。

**发布包（四包重打：页面 + 桌面程序集都变了）**

```
DIP3-Web-win-x64.zip      95,304,198 B  902bd4b3…1d7615
DIP3-Web-win-x86.zip      88,953,294 B  46f96157…1705a2
DIP3-Desktop-win-x64.zip  84,057,838 B  5ccf31b1…bd8852
DIP3-Desktop-win-x86.zip  78,423,538 B  32563a73…e63662
```

> 后端接口一行没改（`/api/zhijian` 照旧），本次只有 `web/index.html` 与桌面端程序集变化；
> 但桌面包必须整包换（`MainWindow` / `GuidePanel` / 新增的 `ToolPanel` 在同一个程序集里）。
> **最新哈希以本节为准。**

### 9.39 工具栏加第二个工具「基本物耗指引」：哪些不能收、哪些可以另收（2026-11-01 21:2x–22:0x）

> 「工具里加入基本物耗，哪些是不能收费的，哪些是可以收费的」

**① 这个工具回答什么**

一个耗材到底能不能向患者另收，一线最容易被两头夹：收费室说「系统里能收」，医保办说「这是基本物耗」。
工具把判断压成一句话 —— **在本科室项目所属类别的基本物耗清单里，就不能收；不在，才轮到「除外内容 /
可另收费材料目录」**。四个页签：

| 页签 | 内容 | 用途 |
|---|---|---|
| 判定 | 输入耗材名称，边打边判：不可另收 / 可另收 / 需核对 / 清单外 | 护士站、收费窗口一句话问「这个能收吗」 |
| 不可收费 | 137 条基本物耗，按 10 组归类（消杀 / 防护 / 敷料铺巾 / 注射输液 / 一般器具 / 中医 / 病理检验 / 口腔 / 专科 / 影像） | 自查清单，看自己科室天天在用的是哪几样 |
| 可收费 | 40 条边界清单：15 条可另收、3 条可另收减收、12 条不可另收（易错）、10 条需核对（条数以 §9.42 为准） | 「钢板能收、输液器不能收」一眼看到差别 |
| 口径依据 | 6 条判断口径 + 36 类指南条款原文 | 被质疑时直接念原文，不靠记忆 |

**② 数据从哪来（不自己编目录）**

- 36 类立项指南「使用说明」里的**基本物耗条款原文**（`data/lixiang.json` 的 `guideUsage`），
  脚本 `tools/gen-wuhao.py` 从「本指南所称『基本物耗』…不另行收费」整句切出，再逐类做别名匹配，
  所以每条物耗都能说出「哪几类指南列了它」，不会手抄跑偏。
- 德医保发〔2026〕25 号通知正文：**「除附件 1-9『使用说明』中纳入基本物质资源消耗的一次性耗材外，
  映射原价格项目的除外内容与现行可另收费的一次性材料目录继续执行。」**
- 各类指南共同尾句：**「除基本物耗以外的其他耗材，按照实际采购价格零差率销售。」**

产出 `data/wuhao.json`：`meta` / `rules`(6) / `groups`(10) / `items`(137) / `edges`(35) / `sources`(36 类条款原文)。

**③ 几条查出来的硬口径（本次顺带纠偏）**

| 耗材 | 结论 | 依据 |
|---|---|---|
| 玻片 / 包埋盒 / 石蜡 / 试剂 | **不可另收** | 病理类明确把「玻片、包埋介质、容器、试剂（含固定液等辅助试剂）」列入基本物耗 —— 病理按蜡块计价（二级 168 / 三级 198 起）已含，不能再挂材料费 |
| 免疫组化抗体 / 特殊染色试剂 | **不可另收** | 同上；免疫组化按「项」收项目费（二级 129 / 三级 150），抗体不再单列 |
| 一次性输液器 / 输液管路 / 输液贴 | **不可另收** | 一般治疗类把「输液管路」「输液贴」写进基本物耗 —— 门诊输液室最常误收的一项 |
| 耦合剂、一次性电极片 | **不可另收** | 超声检查、物理治疗类明确列入 |
| 检验试剂 / 质控液 / 校准液 | **不可另收** | 检验类第 6 条：试剂、样本采集器、操作器具成本计入项目价，**不得另行收费** |
| 内固定钢板 / 螺钉 / 髓内钉、人工关节 | **可另收** | 植入物不是「不应或不必要与项目分割的易耗品」，不属基本物耗；零差率按实收 |
| 灸材（艾条 / 艾炷 / 艾绒）、隔物灸间隔物 | **可另收** | 中医灸法类：施灸制品按材料收；**同一次治疗用几种间隔物不叠加收费** |
| 手术标本送检费 | **不可另收** | 骨科类第 16 条：手术项目价格构成已含标本留取与送检 |
| 留置针 / 超声刀头 / 可吸收缝线 | **需核对** | 未列入基本物耗，能否收取决于省/市可另收费材料目录与本院耗材编码 |

**④ 判定逻辑（Core 一处实现，两端共用）**

`WuhaoStore.Judge(q)` 三步，网页端 `/api/wuhao?q=` 与桌面端判定框都只调它，**不在页面里各写一套**：

1. 命中基本物耗清单（名称 / 别名精确 → 别名包含 → 输入包含别名）→ `no` 不可另收；
2. 否则看边界清单（`可另收` / `不可另收` / `需核对`）→ `yes` / `no` / `check`；
3. 都不命中 → `miss` 清单外，提示「过三关」：① 原项目除外内容 ② 可另收费材料目录 ③ 本院耗材编码。

**⑤ 两端实现**

| | 桌面端 | 网页端 |
|---|---|---|
| 面板 | `WuhaoPanel.xaml(.cs)`，挂在 `ToolPanel.WuhaoView` | `#zgWuhao`，四个 `.zg-view` 切 `.hidden` |
| 入口 | 工具栏第二张卡（`ToolWuhao = "wuhao"`） | `zgShowTool('wuhao')` |
| 判定 | 本地调 `WuhaoStore.Judge`（防抖 140ms） | `fetch('/api/wuhao?q=')`（防抖 160ms） |
| 截图 | `--shot-page wuhao@784x820:judge:输液器`（页签 judge/basic/charge/rule） | `shot-cdp.mjs` 注入 `zwAsk('…')` / `zwTab('…')` |

**踩到的坑**

- **条款起始位置**：一开始按「句号回退」找定义句开头，结果病理、检验这类条款结尾没句号，把上一句
  「扩展项」整段带了进来（192 字变 277 字）。改成「从『所称』后面第一个定义词起切」，36 类全部干净。
- **网页清单筛选恒空**：`zwState.group` 初始化成了 `'全部'`（照抄桌面端的 `All`），而网页里「全部」用空串表示，
  于是 `i.group !== '全部'` 把所有条目都过滤掉了。桌面端 `g == All ? null : g`、网页端必须 `''`。
- **视图插错层**：`#zgWuhao` 插到了 `#zgBox` 闭合之后（正好在「病种库右键菜单」注释前），
  抽屉里空空如也、内容却渲染在页面左下角。要插在 `#zgGuide` 闭合之后、`#zgBox` 闭合之前。
- **只读属性绑定**：`<Run Text="{Binding CatText}">` 会按 TwoWay 去写只读属性而抛
  「无法对只读属性…进行 TwoWay 绑定」，必须 `Mode=OneWay`。
- **数据分发白名单**：`wuhao.json` 加进了 `DataCatalog`（与 `zhijian.json` 同组「资料」），
  否则局域网客户端更新后拿不到这份清单，工具会空着。
- **口径边界要敢写「需核对」**：留置针、超声刀头、可吸收缝线这类，指南没列但目录里可能有，
  一律判「需核对」并让人查目录，比硬给一个「能收 / 不能收」安全。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（新增第 16 项：36 类条款 ≥30、病理条款必须含玻片/包埋介质/试剂/活检器具、
  无孤儿条目、7 个判定用例（纱布/输液器/耦合剂/玻片/免疫组化抗体=不可另收，钢板=可另收，留置针=需核对）、空输入不误判）。
- 网页对拍 `tools/verify-web-parity.mjs`：**118/118**（新增 K 组 15 项：接口条数=文件条数、36 类条款下发、
  病理条款关键词、分组计数、6 个「不可另收」+ 1 个「可另收」+ 1 个「需核对」、清单外提示过三关、
  三种结论齐备、6 条口径、网页四页签、判定走后端、桌面挂载、白名单）。

截图：`_shots/w_judge.png` / `w_basic_试剂.png` / `w_charge.png` / `w_rule.png` / `w_tool2.png`（桌面端），
`_shots/web_wuhao_judge3.png` / `web_wuhao_basic3.png` / `web_wuhao_charge.png` / `web_wuhao_rule.png` /
`web_wuhao_yes.png` / `web_tool_home2.png`（网页端）。

**发布包（四包重打：新增数据文件 + 接口 + 两端面板）**

```
DIP3-Web-win-x64.zip      95,330,473 B  cce641a5…c8f61a
DIP3-Web-win-x86.zip      88,979,582 B  91a86c5d…f32ccb
DIP3-Desktop-win-x64.zip  84,082,498 B  c0484f55…bbabad
DIP3-Desktop-win-x86.zip  78,448,194 B  91725215…29f99f
```

> 本次新增 `data/wuhao.json`（82,331 B）与 `/api/wuhao`，两包 `data/` 与 `web/` 都要跟着换；
> 桌面包因新增 `WuhaoPanel` 程序集同样必须整包替换。已确认 `dist/DIP3-Web{,-x86}/data/wuhao.json`
> 均为 82,331 B、`web/index.html` 命中 `zgWuhao` 3 处。**此四包已被 §9.40 取代，最新哈希见该节。**

### 9.40 一线反馈三处纠正：导尿包、避光/精密输液器、半截名称（2026-11-01 22:0x）

> 「导尿包不能收费，留置针 避光输液器 精密输液器都检索不到，另外输入精密 或者不全的名称也提示可以」

**① 三个问题的根因**

| 现象 | 根因 | 后果 |
|---|---|---|
| 导尿包判「清单外」 | 清单里只有「导尿管」，条款原文也只写导尿管 | 口径变成「原则上可零差率另收」，与一线实际相反 |
| 避光 / 精密输液器「检索不到」 | 别名只有「输液器」「一次性输液器」，条款里没有这两个型号词 | 判定落到清单外；清单页搜也搜不到 |
| 输入「精密」提示可收 | 半截词没有任何匹配 → 走 `miss`，标题写「原则上可零差率另收」 | 最危险的一格：只写型号就说能收 |

**② 改法（数据 + 判定 + 检索出口）**

- **数据**：`输液管路` 补别名 避光输液器 / 精密输液器 / 精密过滤输液器 / 微量泵管路；
  新增 `导尿包（一次性）`，并给 `gen-wuhao.py` 的条目表加第 5 项「条款匹配词」——
  成套包在条款里只写包内组件，不给匹配词就会成孤儿条目；边界清单补两条
  （避光·精密输液器、一次性导尿包，当时都判不可另收 —— **避光/精密那条已被 §9.41 纠正为「可另收（减收）」**），
  136→**137** 条、33→**35** 条。
- **判定**：`IsModifier` 命中型号 / 修饰词（精密、避光、一次性、医用、微量…）时**短路**，
  一律 `check`「名称不全 · 请写耗材全称」，不再往下走成「可收」；
  名称 ≤3 字且两份清单都没命中，同样判 `check` 而不是 `miss`。
- **措辞**：`miss` 的标题从「清单外 · 原则上可零差率另收」改成「清单外 · 先过三关再谈收费」，
  依据里明写「不在清单 ≠ 可以直接收」。
- **检索出口**：清单页搜不到时，`EdgeHit` 把边界清单的结论抬出来（桌面端给「去『可收费』页看这 N 条 →」按钮，
  网页端直接列出条目与结论）。「搜不到」不再等于「工具不认识」。

**③ 现在的判定（都已写进自检与对拍）**

| 输入 | 结论 | 说明 |
|---|---|---|
| 导尿包 / 一次性导尿包 | 不可另收 | 2 类指南（综合诊查、一般治疗）列了导尿管；包内组件逐项都在清单里 |
| 避光输液器 / 精密输液器 | 不可另收 | 都是「输液管路」的型号变体，换名字不换性质 |
| 精密 / 避光 / 一次性 | 需核对（名称不全） | 不硬判，提示补全全称；近义条目里能看到「输液管路」 |
| 留置针 | 需核对（边界清单） | 清单页搜到时给出口，不再显示「没有匹配」 |
| 某某专用导丝 | 清单外 · 先过三关 | 名称已写全、确实没收录，才走这一格 |

**踩到的坑**

- **别名会污染类别归属**：导尿包一开始把「治疗护理盘(包)」「护（尿）垫」也当匹配词，结果挂到 **32 类**
  指南上（这两样几乎每类都列），页面上写「列入 32 类」很吓人也不准。改成只按「导尿管」匹配 → 2 类。
- **列表刷新顺序**：从空结果跳「可收费」页时，先把 `EdgeSel.SelectedIndex` 归位、再盖 `ItemsSource`；
  顺序反了会被 `SelectionChanged` 触发的全量刷新覆盖，看着像跳转没生效。
- **空结果的底部详情**：网页端 0 条时如果不清 `zwPickName/zwPickBasis`，会留着上一次选中的条目，
  看着像「搜到了别的」，必须清成提示语。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（第 16 项新增 导尿包 / 避光 / 精密输液器 / 精密 四个用例，
  外加「半截词不得判可收」「清单外必须写三关」「EdgeHit 能捞回留置针」三条断言）。
- 网页对拍 `tools/verify-web-parity.mjs`：**122/122**（K 组 118→122：导尿包·避光·精密输液器不可另收、
  半截词不判 yes、半截词提示名称不全、清单外标题含三关）。

截图：`_shots/w_judge_daoniao.png` / `w_judge_precise.png` / `w_basic_liuzhi.png`（桌面端），
`_shots/web_wuhao_daoniao.png` / `web_wuhao_precise.png` / `web_wuhao_liuzhi.png`（网页端）。

**发布包（四包重打）**

```
DIP3-Web-win-x64.zip      95,332,602 B  4b78afbf…08fc64
DIP3-Web-win-x86.zip      88,981,712 B  f3c202eb…080b13
DIP3-Desktop-win-x64.zip  84,084,856 B  9dfdf41c…7e9301
DIP3-Desktop-win-x86.zip  78,450,514 B  c77322fa…8f7c98
```

> 只改了 `data/wuhao.json`（83,903 B）、`web/index.html` 与 Core / 面板程序集；接口路径未变。
> 3210 已用新 `dist/DIP3-Web` 重启，`total=137`、`edgeCount=35`，对拍 122/122 全过。
> **此四包已被 §9.41 取代（减收口径调整），最新哈希见该节。**

### 9.41 纠 9.40 的一处判断：避光 / 精密输液器「可以收，但要减收物耗」（2026-11-01 22:3x）

> 「精密输液器 避光输液器可以收费，需要扣减物耗，实在搞不明白这规则了」

**① 9.40 判错了**

上一节把避光、精密过滤当成「输液管路的型号变体」，一律判不可另收 —— 只看到前半句
「基本物耗计入项目价」，漏了用户这次贴出来的最后一句：

> 临床必需且经患者知情同意后，选用特殊功能的医用耗材并单独付费的，对应的医疗服务价格项目应同步执行
> 减收政策，减收标准参照本地区常规使用普通耗材的采购均价合理确定。

**② 整件事其实就三层**

| 情形 | 能不能另收 | 项目价怎么办 | 例 |
|---|---|---|---|
| 基本物耗 | **不能**，已含在项目价里 | 全额收 | 普通输液器、纱布、棉球、导尿包 |
| 特殊功能耗材（基本物耗的升级替代） | **能**，按实零差率 | **同步减收**：按本地普通耗材采购均价扣减项目价 | 避光输液器、精密过滤输液器、特殊功能敷料 |
| 清单外的器械材料 | **能**，按实零差率 | 不减（项目价里本来就不含它） | 钢板、人工关节、支架 |

一句话：**项目价里含的是「普通耗材」的钱。你换成贵的、又单独收了材料费，就得把那部分钱从项目价里退出来 ——
只收不减，等于同一件耗材收了两遍。**

**③ 改动**

- 新增第四种结论 `deduct`「可另收（减收）」，青绿色，与「可另收」(蓝) /「需核对」(橙) /「不可另收」(红) 区分；
  桌面端徽标、卡片底色、清单标签、筛选下拉都加了一项，网页端同步（`.zgw-verdict.ded`）。
- 数据：`避光/精密/精密过滤输液器` 从「不可另收」改为「可另收（减收）」；`特殊功能敷料` 同改；
  判断口径加第七条（原文照录）；当时 35 条边界 = 可另收 15 · 可另收减收 2 · 不可另收 11 · 需核对 7（**9.42 已补到 40 条**）。
- 判定：`Edge.Verdict` 为「可另收（减收）」→ `ToneDeduct`，标题「可另收 · 但项目价须同步减收」，
  提示明写「只收不减 = 重复收费；病历记明知情同意与型号」。
- 型号词单独查（「精密」「避光」）仍是「名称不全」，但依据改成说明**两种型号口径不同**，
  不再说「型号不改变性质」（那是 9.40 的错误说法）。

**踩到的坑**

- **结论列被截断**：「可另收（减收）」6 个字在 82px 列里只显示成「可另收（」。截图才发现，列宽改 108。
  自检断言只看数据，看不出这种问题 —— 新结论一定要配截图。
- **旧文案会自相矛盾**：`IsModifier` 分支原来写「属基本物耗，不能另收」，与新口径直接打架；
  改了判定不改文案，一线读到的还是老结论。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（用例改为 避光/精密输液器 = `deduct`，并断言依据里必须出现「减收」、
  口径里必须有「减收政策」原文）。
- 网页对拍：**126/126**（新增：三个特殊功能输液器 = deduct、减收写进依据、第七条口径、第四种结论在清单与页面上都有位置）。

截图：`_shots/w_judge_deduct.png` / `w_charge_deduct.png`（桌面端），`_shots/web_wuhao_deduct.png`（网页端）。

**发布包（四包重打）**

```
DIP3-Web-win-x64.zip      95,332,933 B  48de03f7…12413d
DIP3-Web-win-x86.zip      88,982,040 B  9ff64dcd…6bde58
DIP3-Desktop-win-x64.zip  84,085,903 B  5acd939b…643977
DIP3-Desktop-win-x86.zip  78,451,611 B  c87b7780…98fea8
```

> 数据（`data/wuhao.json` 84,708 B）、Core、桌面面板、网页页面都变了；接口路径未变。
> 3210 已重启：`total=137`、`edgeCount=35`，精密输液器返回 `deduct`，对拍 126/126。
> **此四包已被 §9.42 取代（分组修复 + 三腔导尿管），最新哈希见该节。**

### 9.42 分组点开空白 + 三腔导尿管该收（2026-11-01 22:5x）

> 「点开空白，另外三腔导尿管可以计费，收录不全」

**① 分组点开空白：桌面端拿显示名去比 id**

`GroupList` 里存的是 `GroupRow(g.Name, n)`，而 `ApplyBasicFilter` 把 `Name` 当分组条件传给 `Search`，
条目里的 `group` 却是 `g1…g10`。于是除「全部」之外，点任何分组都是 0 条 —— 屏幕上就是空白，
不是没数据（`data/wuhao.json` 里 137 条都在）。

修法：`GroupRow(Id, Name, Count)` 三个字段分开，筛选只传 `Id`；「全部」的 Id 是空串，
不再用 `All` 常量去比。空结果时提示带上分组名（「『病理 · 检验』里没有匹配的条目」），
让人知道是分组内没有，不是整个工具坏了。截图入口 `--shot-page "wuhao@784x860:basic:g7"` 直接选中第 7 个分组，
用来验证这条不再回归。

网页端还有一处隐性问题：`zwPaint` 里写的是 `g.count`，而 JSON 的 `groups` 只有 `id/name/desc`，
左侧每个分组后面的数字一直是空的。现在 `WuhaoStore.Load` 按 `items` 实算 `WuhaoGroup.Count`，
网页再按同样的口径兜一层 —— 数字不再依赖数据文件里有没有这个字段。

**② 三腔导尿管：可另收（减收），不是「不能收」**

上一轮把整套导尿管都算进基本物耗，这对常规双腔是对的，对三腔是错的。按 9.41 定下的三层口径：

| 耗材 | 结论 | 理由 |
|---|---|---|
| 普通导尿管 / 双腔气囊导尿管 | 不可另收 | 指南所称「导尿管」就是常规双腔型，含在导尿、留置导尿项目价内 |
| **三腔导尿管 / 三腔气囊导尿管** | **可另收（减收）** | 多一个冲洗腔，用于持续膀胱冲洗、前列腺术后压迫止血，属特殊功能耗材；可单独付费，但项目价须按本地常规双腔导尿管采购均价同步减收 |
| 超滑 / 亲水涂层导尿管 | 需核对 | 多用于间歇导尿，是否属特殊功能看省·市目录 |
| 一次性导尿包 | 不可另收 | 包内组件逐项都在清单里 |

顺带补了两条一线常问、条款确实没列的：`胃管 / 鼻饲管 / 胃肠减压管`、`吸痰管` 判「需核对」，
依据写明「清单未列 → 按清单外耗材零差率销售 → 过三关」，没有硬塞进基本物耗清单。

**③ 边界匹配改成逐段比**

`三腔导尿管 / 三腔气囊导尿管` 这种一格多名的写法，原来只有整串参与比较，输「三腔气囊导尿管」就落回宽条目
「导尿管」判不可另收。现在 `EdgeWords` 把整串和 `/`「、」分隔的每一段都拿出来比，取最长的一段；
边界条目**完全相等**时优先于清单命中（专业口径压过宽别名）。

副作用（正向）：「某某专用导丝」原来判「清单外」，现在能落到底边界清单的「导丝」判「可另收」。
自检与对拍里把它当「清单外」的两条用例因此失效，换成 `一次性使用黏膜切开刀`。

数据：**137 条基本物耗 / 40 条边界** = 可另收 15 · 可另收减收 3 · 不可另收 12 · 需核对 10。

**踩到的坑**

- **同名不同义**：分组是「id + 显示名」两个字段，界面上只看得见显示名，代码里一顺手就拿错了。
  这类 bug 自检看不见（`ByGroup(Name)` 返回空列表不抛异常），只有点一下界面才发现 —— 分组类断言要按 id 写。
- **对拍用例会因「收录变全」而失效**：拿具体耗材名当「清单外」样本，一补数据就翻案。
  清单外断言要用明显编造或极冷门的名字。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（新增：分组 id 覆盖全部条目、每个分组按 id 取得到条目、
  三腔 = deduct / 双腔 = no / 胃管 = check、专用导丝落到边界清单）。
- 网页对拍：**132/132**（3398 与 3210 都跑过；新增三腔 / 双腔 / 胃管 / 吸痰管、分组 count 与 items 实算一致、
  桌面端分组存 id 且网页 `data-g` 用 id 的源码断言）。

截图：`_shots/w_basic_g7.png` / `w_judge_sanqiang.png`（桌面端），
`_shots/web_wuhao_g7.png` / `web_wuhao_sanqiang.png`（网页端）。

**发布包（四包重打）**

```
DIP3-Web-win-x64.zip      95,334,750 B  77ed9747…077694
DIP3-Web-win-x86.zip      88,983,849 B  ee94adc3…9ae765
DIP3-Desktop-win-x64.zip  84,088,192 B  d7a58183…d5022a
DIP3-Desktop-win-x86.zip  78,453,884 B  79468d72…f8696e
```

> `data/wuhao.json` 87,260 B、Core 判定、桌面分组列表、网页分组计数都变了；接口路径与字段未变（`groups` 多了 `count`）。
> 3210 已用新 `dist/DIP3-Web` 重启：`total=137`、`edgeCount=40`，三腔导尿管返回 `deduct`，对拍 132/132。
> **此四包已被 §9.43 取代（双击与内容扩增），最新哈希见该节。**

### 9.43 「双击判定」是假的 + 清单内容扩增（2026-11-01 23:2x）

> 「扩增内容，双击无效」

**① 双击无效：只写了文案，没接事件**

清单下方提示一直是「N 条 · 双击判定」，但 `ItemGrid` 从头到尾没有 `MouseDoubleClick` —— 提示是假的。
这种「文案承诺了、代码没实现」比没有提示更糟：一线会以为工具坏了。

现在两端都接了：

- 桌面端 `MouseDoubleClick="OnItemDoubleClick"`，只认行内双击（表头、空白、滚动条不跳），选中后按 **Enter** 也行；
  统一走 `GotoJudge(name)`：切到判定页 → 填搜索框 → 立刻判这一条。
- 网页端行上 `ondblclick="zwJudgeItem(this)"`，跳到「判定」页并 `zwAsk(name)`，光标落回输入框。

**② 扩增内容：详情区直接出条款原文**

以前选中一条只显示「说明 + 出现在哪几类（条款原文见「口径依据」页）」，等于让人再去翻一遍。现在：

- 详情区直接列出该条的说明、`出现在 N/36 类`、以及前 2 条**条款原文**（超过 160 字截断），
  还有几类就提示「另有 N 类条款见「口径依据」页」；鼠标悬停可看全部类别原文。
- 桌面端详情区改成 `ScrollViewer`（MaxHeight 102），不再被 `MaxHeight="56"` 切掉半句；
  网页端 `.zgw-foot` 加 `max-height:150px; overflow-y:auto`。
- 列表「说明」列加了 `ToolTip`，长说明不用拉宽列也能看全。

**③ 扩增清单：40 → 51 条边界，138 条基本物耗**

条款里唯一还没收录的列举词是「固定带」（护理、综合诊查、康复、精神治疗、辅助操作等 6 类都列），已补进
`g3 敷料 · 铺巾`。其余条款列举词早已覆盖，所以扩增主要落在**边界清单**（一线高频、条款没逐字列的成套包与附件）：

| 结论 | 新增条目 |
|---|---|
| 不可另收 | 换药包 / 拆线包 / 备皮包、灌肠包 / 灌肠器、雾化面罩 / 雾化器 |
| 需核对 | 三通 / 延长管 / 输液接头、透明敷贴 / 留置针贴膜、真空采血管 / 采血针、血糖试纸、吸氧管 / 鼻导管、一次性口腔器械盒、引流袋 / 集尿袋、无菌手术包 / 一次性手术衣 |

每条都写了「为什么」和「怎么不收错」，而不是给一个结论了事。例：换药包——包内纱布、棉球、棉签、治疗巾、手套、
固定带逐项都在基本物耗清单里，换药费里再挂「换药包」就是重复收费；三通、延长管——一般治疗类只列输液管路、
输液贴，附件能否另收要看目录，一次输液同时收多件附件最容易被认定为分解收费。

数据：**138 条基本物耗 / 51 条边界** = 可另收 15 · 可另收减收 3 · 不可另收 15 · 需核对 18。

**踩到的坑**

- **截图后门和截图参数撞分隔符**：`--shot-page` 用 `:` 分段，`dbl:玻片` 被切成两个参数，只传了 `dbl`。
  改成 `dbl@玻片`。自检后门自己也要能跑通，否则「验证过」是假的。
- **详情区没有滚动**：内容一扩，网页底部详情被抽屉下沿裁掉最后一行；桌面端则被 `MaxHeight` 切掉。
  扩内容必须同时扩容器，两个端都是。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（新增 固定带 = no / 换药包 = no / 三通 = check / 雾化面罩 = no）。
- 网页对拍：**136/136**（新增：固定带·换药包·雾化面罩 / 三通·真空采血管；两端「双击事件已真的绑定」与
  「详情区带条款原文」的源码断言 —— 光有文案不算）。
- 双击链路实拍：`--shot-page "wuhao@784x860:basic:dbl@玻片"` → 判定页、搜索框「玻片」、卡片「不可另收」。

截图：`_shots/w_basic_g7_v2.png` / `w_dbl_bo.png`（桌面端），`_shots/web_wuhao_pick3.png` / `web_wuhao_dbl.png`（网页端）。

**发布包（四包重打）**

```
DIP3-Web-win-x64.zip      95,336,163 B  8cf5ae6c…f11e53
DIP3-Web-win-x86.zip      88,985,262 B  ca7e9435…6d1ad6
DIP3-Desktop-win-x64.zip  84,090,305 B  c05d5148…506270
DIP3-Desktop-win-x86.zip  78,456,004 B  f82a9a9f…4b6ea66
```

> 数据（`data/wuhao.json` 91,532 B）、Core、桌面面板（XAML 改了双击与详情区）、网页页面都变了；接口路径与字段未变。
> 3210 已用新 `dist/DIP3-Web` 重启：`total=138`、`edgeCount=51`，固定带 `no`、换药包 `no`、三通 `check`、雾化面罩 `no`，
> 三腔导尿管仍 `deduct`，对拍 136/136。
> **最新哈希以本节为准。**

> **此四包已被 §9.44 取代（扣减物耗条件列），最新哈希见该节。**

### 9.44 「一般治疗」列表没有扣减物耗列了（2026-11-01 23:4x）

> 「一般治疗 列表扣减物耗列没有了」

**① 来龙去脉：上一轮是我自己删的**

10-30 把「扣减基本物耗价格」列从现行项目主表移除了：3,880 条里只有 25 号文「一般治疗」类 11 条有值，
理由写在 `Cols` 的注释里 —— 「一列 128px 只服务 11 行，把名称列挤窄不值得」，值留在详情卡。
这个取舍对「全部类别」成立，对**专门筛到一般治疗**的物价人员不成立：那一屏全是带扣减额的项目，
扣减额却要点开每行详情才看得见。用户正是这么用的，所以报「没有了」。

**② 改成条件列：有值才出现，没值不占宽**

两端同一套口径（不是各写一份规则）：

- 触发：`_cat == "一般治疗"`，**或**当前页任一行 `Deduct` 非空（如直接搜「换药费」也能看到）。
- 桌面端：`BuildColumns` 在「一级价格」后插 `Deduct` 列（宽 100、等宽、居中），默认 `Collapsed`；
  `SyncDeductCol(items)` 在每次 `Load` 后决定显隐，并重算 `FitNameColumn()`。
- 网页端：`dzCols(view)` 统一出列（表头 / `colgroup` / 行渲染 / 详情 `colspan` 全走它），
  `dzLoad` 里**先拿到本页数据再算列**，否则「搜换药费」首帧拿不到 `deduct` 判断会漏列。
- 静态列数组 `Cols` / `DZ_COLS.live` 保持原样，条件列是运行时插入 —— 对拍「列逐字一致」不受影响。

**③ 顺手修的两个真 bug**

- **名称列没扣掉条件列宽度**：`FitNameColumn` 的 `fixedSum` 只按 `Cols` 求和，多出 100px 后列合计超宽，
  而三张表都禁了横向滚动 —— 超宽部分直接被裁。现在 `SyncDeductCol` 改显隐后重算，宽度把 100 计进去。
- **离屏截图后门的选择器是假的**：`DiagSelect` 只设 `SelectedItem`，离屏下 `SelectionChanged` 不派发，
  截图出现「下拉写着一般治疗、行还是器官移植」。「验证过」的截图可能根本没验证到，已改成同步 `_doc/_cat` 后 `Load(1)`。

**验证**

- 桌面端 `--selftest`：**115/115 PASS**（「扣减物耗」列并入数值列居中 / 表头样式的守门断言）。
- 网页对拍：**140/140**（新增 4 项：两端条件列源码断言、一般治疗 133 条里带扣减额恰 11 条、注射费 1.5 元）。
- 实拍：桌面 `--shot-page "dz::一般治疗"`；网页 3210 筛「一般治疗」→ 表头 `…一级价格 | 扣减物耗 | 医保支付…`，
  注射费（静脉输液）1.5、清创缝合费（小/中/大/特大）2.5/3.3/4.6/9.2、换药费（小/中/大）2.4/3.6/7.2；
  切回「全部类别」列即消失。

截图：`_shots/dz-deduct.png`（桌面端）、`_shots/web_dz_deduct.png`（网页端）。

**发布包（四包重打）**

```
DIP3-Web-win-x64.zip      95,336,590 B  900fc0f9…9e5924ec4b4da
DIP3-Web-win-x86.zip      88,985,689 B  2af8d72e…ca8895d3df9df
DIP3-Desktop-win-x64.zip  84,090,542 B  5bd24a5e…14d11845b56ea
DIP3-Desktop-win-x86.zip  78,456,243 B  31991b15…cdda12b5ffb80
```

> 网页包只有 `web/index.html` 变了（Web 服务端代码未动，3210 已用新页面重启，`total=3,880`，对拍 140/140）；
> 桌面两包是 C# 改动后重编（自检 115/115）。

### 9.45 基本物耗加了「本机手工维护」：补本院叫法，只在本机生效（2026-11-07 21:0x–21:4x）

**① 为什么不是直接改 wuhao.json**

清单是从 36 类立项指南条款里切出来的，全院一份、随数据分发下发（`DataCatalog` 白名单 `wuhao`）。
一线要补的却是**本院口径**：手术室叫「切口保护套」、换药室把纱布按包计、个别耗材在不在基本物耗里各家理解不同。
直接改基础库有三个后果：下次数据更新被覆盖；一台机器的改动被同步到全院；审核时也说不清哪条是指南原文、哪条是本院补的。

所以做成**覆盖层**：`data/wuhao.user.json` 只存增 / 改 / 删的差异集，载入时盖在 `wuhao.json` 之上 ——

- 基础库仍是唯一权威，`wuhao.json` 一个字节都不动；
- 差异集登记进 `DataCatalog.PrivateRelPaths`，**不进** `Specs` —— 数据分发永远拿不到它；
- 每台机器各自维护、互不影响；两个发布脚本的 `KEEPFILES` 都加了它，重发布不会把它清掉。

**② 差集语义（三处最容易写错的地方）**

- **先删、再改、后增**（`WuhaoStore.ApplyUserCover`）：顺序反了，删除列表里的条目会被同名覆盖「救回来」。
- **最小差异**：保存时逐字段比（条目比名称 / 分组 / 别名 / 说明；边界比名称 / 类别 / 结论 / 依据 / 提示），
  改回原样就不写；删掉本机新增的条目只从 `items` 里去掉，不写删除标记。文件里永远只有真变了的东西。
- **边界条目按 `Name` 匹配**：改名必须写成「删旧 + 增新」，否则旧名称会一直留在清单里（条目按 `Id`，没有这个问题）。

**③ 界面（面板头部 ✎）**

- 两页签：清单条目 / 边界条目；左表三列「名称 / 结论 / 覆盖」，覆盖列标 `新增` `已改` `待删`。
  本机新增的行**置顶** —— 追加在 130 多行末尾等于看不见。
- 右侧按类型换字段：条目 = 名称 / 分组 / 别名 / 说明；边界 = 名称 / 结论 / 类别 / 依据 / 提示。
- 删除是**标记**而不是立即移除：再点一次恢复，保存后才真正落地。
- 「撤销编辑」丢弃未保存改动（从磁盘重读）；「重置全部改动」清空差异集（二次确认，只删 user 文件，不动基础库）。
- 保存前挡空名称：空名字进清单只会让判定多一条「不可另收」的幽灵。
- 保存后判定器、条数、左侧分组计数一起刷新（`RefreshCounts`）—— 不刷新就是「改完还是旧结果」，等于没生效。

**④ 顺手修的一个真 bug**

「保存到本机」一开始套的是页面内的 `Chip` 样式，而 Chip 的 `ControlTemplate` 把 `Background` 写死成
`Transparent`（不绑 `TemplateBinding`），按钮上设的 `Background=Accent` 被无视 → 白字落在透明底上，肉眼几乎看不见。
改用全局 `Btn.Primary`（实心蓝底），并把原因写进 XAML 注释，免得以后又被套回 `Chip`。

**验证**

- 桌面 `--selftest`：**116/116 PASS**。新增第 17 项「本机维护差异集」：增 / 改 / 删各一条 →
  判定命中、`wuhao.json` 逐字节未变、差异集三条记录齐全、`wuhao.user.json` 不在分发白名单且已登记为本机私有文件；
  跑完**逐字节还原**原文件（这台机器上可能真存着操作员补的条目，不能替人重置）。
- 实拍：`_shots/wuhao_maint.png`（空维护）、`_shots/wuhao_maint_user.png`（1 新增 / 1 改 / 1 删，
  计数「138 条生效 · 新增 1 · 已改 1 · 待删 1」，首行「一次性切口保护套 … 新增」）。
- 网页端不改：手工维护只在桌面端做（网页是分发面，也不该有免鉴权的写入口）；
  若服务端本机存在 user 文件，`/api/wuhao` 会照常应用，两端判定口径仍然只有一份。

### 9.46 全量体检：精简死代码 / 清缓存 / 别名体检（2026-11-10 11:1x–12:5x）

四路扫描（编译警告、重复代码、重复数据、死代码与垃圾文件）后的处理，按「先出报告再改」执行了 1→2→3。

**① 精简与去重（低风险）**

- `Infrastructure.cs` 删掉 5 个全库零引用成员：`Js.ToFile`、`Js.AtomicWrite`、整个 `DictEx`
  （`Put` / `PutDefined` / `Obj`）。**`Js.ToJsonElement` 不能删**：`tests/Dip3Grouper.Tests/GroupingTests.cs`
  在用，只扫 `src/` 会把它误判成死代码 —— 死代码扫描必须把测试工程算进来。
- 三个后台视图各复制一份的 `Run(Func<string>)`（AdminDezhou / AdminDiseases / AdminLixiang，共 17 处调用）
  抽成 `Views/Admin/AdminUi.cs` 的单一实现，视图只保留一行包装，调用点签名不变。
- `Grouper.cs` 里 12 行诊断匹配循环自重复**不动**：分组引擎与判定矩阵强耦合，收益小于风险。
- 大数据文件体检：`guifan.json` 11516 条 / `mappings.json` 35587 条 / `dezhou-price.json` 3880 条，
  **整行重复全部为 0**；物价「眼袋整形费」同名 2 条但整行不同，属不同版本批次，暂不动。

**② 清缓存（释放约 5.2GB）**

- 5 个 `src/*/bin` 共 5.1GB + `.tmp` 146MB 全部清掉，重建后 Desktop bin 仅 455MB，**净释放约 4.7GB**。
- `.tmp` 里混着原始数据备份（`guifan/raw.json`、改稿前物价、立项指南备份等），
  删之前按原路径迁到 `_backups/tmp-data/`（58 个文件 / 83MB），**不丢数据**。
- `dist/` 与 `_shots/` 是产物和证据，保留。

**③ 别名体检：上一轮「99 条冲突」是误报**

- 更正：那 99 条（归一化后 100 条，多出 i062「冷 / 热治疗物品」仅空格差异）全是
  **条目把自己的主名又列进 alias 数组**；跨条目撞名 / 别名互撞 / 同名条目实际都是 **0**。
  `HitItem` 每条先比主名再比别名，自名别名不影响判定，只是冗余。
- 另有 3 条「别名撞边界条目」（耦合剂、一次性导尿包、一次性电极片），边界结论同为「不可另收」，无口径冲突。
- 新增可复跑的只读审计：`python tools/audit-wuhao-alias.py` → `docs/wuhao-alias-audit.md`，
  含 100 条冗余别名逐条明细与判定顺序说明；脚本会叠加 `wuhao.user.json` 差异集一起体检。
- 维护页页签行新增「别名体检」按钮：显示「冗余 100 · 冲突 0 · 边界 3」，点开看 5 列明细
  （类型 / 别名 / 所属条目 / 冲突对象 / 说明，长说明省略号 + ToolTip）。
  **真冲突置顶**（否则被 100 条冗余埋到末尾），真冲突 >0 时按钮标红；只读，不自动改任何数据。
- 离屏截图入口：`--shot-page wuhao:aliaudit`（`ToolPanel.DiagRoot` 直接返回明细视图，不弹窗）。
- 自检新增第 18 项「别名体检基线」：直接读基础库断言真冲突为 0，防止以后改源表引入撞名。

**验证**

- 两个工程 Release 编译 **0 警告 0 错误**；`--selftest` **117/117 PASS**。
- 截图：`_shots/wuhao_maint.png`（干净态）、`_shots/wuhao_alias_audit.png`（明细）、
  `_shots/wuhao_maint_conflict.png` / `_shots/wuhao_alias_audit_conflict.png`（临时造 1 处别名撞名 →
  按钮标红「冗余 99 · 冲突 2 · 边界 4」，冲突行置顶）。临时差异集已删除，干净态复截。
- 待办：100 条自名别名若要精简，改 `tools/gen-wuhao.py` 的 ITEMS 源表后重生成；
  不要直接改 `wuhao.json`，否则下次重生成会回来。

### 9.47 物价解读 AI：按立项指南条文回答，模型由用户自配（2026-11-10 14:3x–15:0x）

工具栏右下那个预留位换成了**「物价解读 AI」**：先在本机资料库里检索条文，再把带编号的证据交给
用户自己配置的模型作答。不是「把问题直接丢给大模型」——那样会凭常识编出文号、价格和自付比例。

**三层都在 Core，界面只做展示**

| 文件 | 职责 |
|---|---|
| `PriceAiSettings.cs` | 本机私有配置 `data/price-ai.json`：服务商预设 / BaseUrl / API Key / 模型 / 地区 / 检索与生成参数；归一化、校验、密钥遮盖、Endpoint 拼接 |
| `PriceAiKnowledge.cs` | 本地检索（立项指南 / 解读辅导 / 地区落地价）→ 编号 `[G]/[J]/[P]` → 构造强约束 SystemPrompt 与多轮 messages |
| `PriceAiClient.cs` | OpenAI 兼容 `POST {BaseUrl}/chat/completions`（非流式）：Bearer 鉴权、超时/取消、错误码人话映射、响应与 usage 解析、密钥脱敏 |
| `Desktop/PriceAiPanel.xaml(.cs)` | 咨询对话 / 本轮引用面板 / 设置面板；ToolPanel 第三个工具卡 |

**一次提问的数据流**

1. 从问题里抽中文 2/3/4-gram 关键词（排掉「项目 / 价格 / 包含 / 内容」等万能词）。
2. 检索 `data/lixiang.json`（40 类 / 2627 行）、同文件的解读辅导（13 期 / 224 条），
   以及 `data/dezhou-price.json`（3880 条，**仅地区命中「德州」时**）。
3. `项目名称命中优先、正文命中封顶 30`：问「换药」首条就是「换药费（大）」，
   不会被「价格构成/使用说明」很长的无关项目刷分顶掉。
4. 证据统一编号、单条 ≤900 字、总量 ≤12000 字；SystemPrompt 规定：
   每个结论回引编号、证据里没写的金额/编码/文号一律不得给出、口径冲突按 `[G] > [J] > [P]` 说明、
   证据不足直接说「未检索到相关条文」，**不许凭常识补全省市价格**。
5. 每轮都用最新检索结果当 system，历史只带最近 N 轮纯文本（不带旧编号，避免回引到过期证据）。

**「精准」的三道防线**

- **名称优先排序**：见上，已加自检回归（问「换药」前三条必须含「换药」）。
- **未找到同名项目**：若问题里的项目名在库里根本不存在（例如「骨折手法复位术」，
  字面最像的是「子宫内翻手法复位费」），`FuzzyOnly` 会在提示词里强制模型
  **先声明「未检索到同名条目，以下为近似条文」，严禁张冠李戴**；状态条同步显示「近似条文」。
  判定用 80% 主干覆盖 + 24 条候选池，避免真命中的条目排在 TopK 之外被误判。
- **地区口径**：地区可在状态条下拉或设置里自由填写。只有命中「德州」才注入本地落地价；
  其它地区在提示词里明确「本地区落地价未收录，只解读计价规则、不报金额」。
  地区设置是真实生效的，不是摆设。

**模型配置（用户自行配置，程序不内置任何密钥）**

设置面板 ⚙ 里选服务商预设后自动填常用值（仍可手改）：

| 预设 | Base URL | 默认模型 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-flash`（旧名 deepseek-chat 已停用） |
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| 腾讯混元 | `https://api.hunyuan.cloud.tencent.com/v1` | `hunyuan-turbos-latest` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.6` |
| 月之暗面 Kimi | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| 本机 / 内网 Ollama | `http://127.0.0.1:11434/v1` | `qwen2.5:7b`（密钥可留空） |

`Endpoint()` 自动补 `/chat/completions`，填完整路径也不会重复拼；`⚙ → 测试连接`
发一条固定短问句核对地址 / 密钥 / 模型名。401/404/429/5xx 与超时都翻译成中文可操作提示。

**安全边界（重点）**

- `data/price-ai.json` 登记进 `DataCatalog.PrivateRelPaths`、**不在 `Specs` 分发白名单**里；
  `publish-desktop.bat` / `publish-web.bat` 的 `KEEPFILES` 都加了它，重发布不会冲掉用户填的密钥。
- 密钥为能直接调用只能本机明文保存（不像后台口令可存哈希）：界面用 PasswordBox、
  状态条只显示 `前3 + •••••• + 后2`，日志与所有异常文案过 `Redact()`，请求体里**没有**密钥（只在 HTTP 头）。
- 请求只带「用户当轮输入的问题 + 本机检索出的政策条文」，不上传整库、不上传病历、不上传业务数据。
  会话历史只在内存里，关掉面板即消失，不落盘。
- **网页端不加这个模块**：网页是分发面，服务端一份密钥会被所有人共用并消耗额度，
  且免鉴权写接口风险更大。桌面端「每人自己申请、自己填、只留本机」才符合「模型 API 需用户自行配置」。

**自检与截图**

- 自检新增 3 项（共 **122/122**）：① 配置往返 / 越界归一 / Endpoint / 密钥遮盖 / 真实路径落盘并逐字节还原 /
  白名单反向断言 / 两个发布脚本 KEEP；② 立项指南检索（精确命中 / 换药排序 / 近似条文强制声明 /
  德州 vs 北京地区口径 / 零证据禁止臆断 / 多轮只带最新证据）；
  ③ OpenAI 协议端到端——本地 `HttpListener` 假模型服务，核对路径 `/v1/chat/completions`、
  `Authorization: Bearer`、请求体含地区与证据且不含密钥、响应/usage 解析、401/404/429、思考型模型、密钥脱敏。
- 另加 `Ui("工具栏抽屉")` / `Ui("物价解读 AI 面板")` 两项构造自检，覆盖 XAML 资源解析与面板初始化。
- 离屏截图：`--shot-page priceai` / `priceai:set` / `priceai:ask:关键词`
  （ask 只跑**真实本地检索**、不发网络请求，截图里能看到气泡与证据编号）；
  加 `@782` 后缀按真实抽屉宽度核对窄屏，如 `priceai@782:set`。

**未做**：网页端入口（见上）、流式输出（当前非流式，长回答需等待）、会话落盘（隐私考虑）。
x86 桌面包若在用，需再跑一次 `publish-desktop.bat x86`。

### 9.48 病种库表格：列宽跟着窗口走，表头可拖（2026-11-12 23:4x–00:1x）

**症状**：病种库 1086 宽窗口里 7 列只占 660，右侧空一大条，而「诊断名称」还在用 150px
把「未特指病因的胃肠炎」截成「未特指病因的胃…」；鼠标放到表头边界光标都不变，拖不动列宽。

**两个独立根因**

1. **拖不动列宽** —— 不是 `CanUserResizeColumns` 的问题（默认就是 true）。
   自定义 `DgHead` 表头模板时只放了 `Border + ContentPresenter`，漏掉了
   `PART_LeftHeaderGripper` / `PART_RightHeaderGripper` 两个 Thumb。
   `DataGridColumnHeader.OnApplyTemplate` 靠 `GetTemplateChild` 取这两个部件再挂 `DragDelta`，
   取到 null 就整条链路静默失效 —— 光标、拖动、双击自适应全都没有。
   现在模板里补回这两个 Thumb（`ColGripper` 样式：平时全透明只占 8px 热区，
   悬停浮一条浅竖线，拖动时变强调色）。
2. **右侧留白 + 名称被截** —— `FlatGrid.BuildColumns` 给每列 `clamp(c.Width ?? 120, 64, 280)`，
   全是固定像素，窗口多宽都只占 660。
   现在改为**内容感知的像素布局**：用 `FormattedText` 量表头和前 60 行样本，
   短列（序号 / 编码 / 布尔）给固定像素，名称 / 说明 / 备注类列记进 `FlexCol` 清单，
   建完列后由 `FlatGrid.FillFlex` 按 `DataGrid.ActualWidth` 把余量按权重摊给它们；
   `Grid.SizeChanged` 时重算一次，窗口拉伸即时跟随。

**为什么不用 DataGrid 的 Star 列**：本项目的表统一开 `HorizontalScrollBarVisibility="Auto"`，
ScrollContentPresenter 按无限宽测量内容，Star 列拿不到视口基准，实测会一路塌到 `MinWidth`
（1086 宽里三列各卡 140，右侧仍空 90）。自己按 `ActualWidth` 算像素，结果可预测。

**细节取舍**

- 编码列封顶 150（「相关手术编码」这类查证列 130）：多值编码串（`44.1300x001|45.1300x004`）
  按内容量能到 250+，一条就吃掉名称列口粮。编码有悬停提示、可整段复制，宁可截断。
- 长表头按左括号主动断行：`手术编码（空=保守行）` 交给 WPF 自动折行会断成
  「手术编码（空=保守 / 行）」，一个孤零零的「行）」挂在第二行。现在断在括号前，
  两行整齐，列宽需求从 160 降到 110。`RowMenu` 复制表头时再把 `\n` 去掉，避免一列名占两行。
- 列宽**不重建就不重置**：`BuildColumns` 用「区段 + 列定义」做签名，翻页 / 重查 / 清空都不 `Clear()`，
  用户刚拖好的宽度和横向滚动位置不会被打回原形。切区段才重建。
- 拖过的列按区段单独记（`_colWidths`），并立刻从 `FlexCol` 清单里摘掉 ——
  否则窗口一缩放就被 `FillFlex` 算回去，用户的动作白做。
- 表头高度这页单独设 38（两行表头需要呼吸位），行高仍跟随全局 `DgRowHeight` 偏好。

**自检**：第 114 项新增三条断言 —— `CanUserResizeColumns` 为真、`DgHead` 模板里两个 gripper 部件存在、
病种库所有列宽都是 Pixel（出现 Star 列即视为塌陷回归）。共 **122/122** 通过（源码版与发布版）。

**离屏截图**：`--shot-page "lib@1086x700:bingxiang"`（`@宽x高` 必须写在页名后、
区段名前，写成 `lib:jiceng@1086x700` 会被当成 1500 默认画布，`@` 解析只认第一段）。

### 9.50 旧版物价「现行标准」两行被表头压住：固定行高 vs 换行链接（2026-11-17 17:4x）

**症状**：用户截图 —— 盖髓术那行「现行标准」的 `013105010100001 活髓保存治疗费-间接盖髓（减收）`
换到第二行时，**第一行上半截被表头盖住**，看着像整行错位。

**根因**：2026-10-30 为了防「两行字压进一行互相重叠」，把三个副表改成**固定行高**
（`RowHeight="{DynamicResource DgRowHeight}"` = 紧凑 26 / 标准 34），并要求文本列单行 + 省略号。
但「现行标准 / 对应新编码」是**模板列**：`ItemsControl(WrapPanel)` + `TextBlock TextWrapping=Wrap`，
内容一换行就有 2×17≈34px 高，加上上下边距超过 34px 行高，`VerticalAlignment=Center` 让内容上下都溢出，
上沿被表头压住 —— 固定行高与「必须换行的链接列」本身就是矛盾的。

**修法**（`Views/DezhouPriceView.xaml`）：这两张表换成「**最小行高 + 自适应**」：

```xml
RowHeight="NaN" MinRowHeight="{DynamicResource DgRowHeight}"
```

- `MinRowHeight` 继续跟随紧凑/标准密度：单行行仍是 26/34，视觉密度不变；
- `RowHeight=NaN` 让链接换行的那几行自己长高（实测 34 → 约 44），文字不再被裁；
- 链接单元格上下边距 1 → 3px，两行时不贴上下边框；
- **新旧比对**不加：它的编码是短码、名称列单行省略，保持固定行高（不动 2026-10-30 的结论）。

**自检同步改口**：原来断言「三个副表 RowHeight 必须等于密度值」，现改为
「作废对照/旧版物价 RowHeight 必须是 NaN 且 MinRowHeight 等于密度值，文本列仍不得 Wrap；
新旧比对仍必须等于密度值」。这条断言就是防回退的锁。

**验证**：`--selftest` **122/122**；截图 `dzlegacy@1500x760:盖髓术`（两行完整、不压表头）、
`dzlegacy@1500x760:隆乳`（第二行比第一行高）、`dzcancel@1500x760:隆乳`（批次两行也不裁）。
`dist/DIP3-Desktop` 已重打，自检在发布版同样通过。

### 9.49 旧版物价「已作废却显示现行」：映射表省对码与本地后缀错位（2026-11-17 13:3x–14:0x）

**症状**：旧版物价里「醋酸白试验」「颞肌颞浅动脉贴敷术」「镍铬铸造冠制作费」等仍标绿「现行」，
可作废对照里明明已经把同名项目作废并映射到新编码。

**根因**：作废判定只做**旧码字符串全等**（`CanceledSet().Contains(it.Code)`）。
映射关系表引的是**省对码**，本地 45 号对码表为了区分同名条目会在尾部加小写字母后缀，
两边经常错位一位：

| 作废对照 oldCode | 旧版物价实际行 | 结果 |
|---|---|---|
| `311400012` 醋酸白试验 | `311400012a` 醋酸白试验 | 编码不相等 → 漏判「现行」 |
| `210101003a` 床旁透视与术中透视 | `210101003` 床旁透视与术中透视 | 反向错位，同样漏判 |

**修法**：`LegacyCanceled` / `LegacyCurrentStd` / `CancelOf` 增加**整行重载**（`DezhouLegacyItem`），
编码直接命不中时按「**同基础码 + 同名称**」回查作废对照：

- 基础码 = 剥掉尾部小写字母后缀（`BaseCode`）；名称 = `NFKC`（全角括号/字母转半角）后只留汉字与字母数字（`NormText`）；
- 补判索引懒构建（`_cancelFamily`），同样跳过 `pending`（拟作废）与 `keep`（划线保留）；
- 为什么不放宽到「只按名称」：`知名专家` 在 `110200002c`（作废）/ `111000002d` / `210400003`
  三个不同基础码下同名，只按名称会把仍在执行的两条一起误杀。加基础码约束后
  新增判定 **19 行**，逐条人工核对全部是真作废。

**第二批：括号全半角**（同日 14:4x）—— 第一版 `NormText` 只去空白，而映射表统一用全角
`（加收）`、对码表用半角 `(加收)`，又漏了 **65 行**（`210102d X线摄影(床旁摄片加收)` ↔
`210102 X线摄影（床旁摄片加收）`、`310701010a 平板运动试验加收` 等）。改成 `NFKC` +
只留字母数字汉字后补齐。合计新增 **84 行** 判定。

**影响面**：旧版物价「已作废」**3,805 → 3,889**、「现行」2,784 → 2,700，总条数 6,589 不变；
两类状态仍严格互斥（`LegacyStat` 与 `LegacyRows` 同源判定）。`LegacyStat` 原来单独写的
编码命中循环也改成整行判定，避免统计与列表两套口径。

**双端**：判断都在 Core，桌面端 `ToLegacyVm` 与网页端 `WebRouter` 的 legacy 分支改用整行重载，
「现行标准」列与可点击链接对补判行同样生效（如 `311400012a` → `012416000070000 生殖道衣原体抗原检测`）。
**未做**：新旧比对的旧价反查仍按精确旧码，补判行在「新旧比对」里可能
只显示「仅作废对照·无旧价」，下次一并处理。

**验证**：`--selftest` **122/122**（第 113 项加三条：`311400012a` 必须作废、`111000002d` 不得跟着作废、
整行版 `CancelOf` 与 `LegacyCurrentStd` 一致）；发布版同跑通过。
截图 `--shot-page "dzlegacy@1400x760:醋酸白试验"`（两行均红「已作废」+ 新码）、
`dzlegacy@1400x760:知名专家`（`110200002c` 红、另两条绿）。

**第三批：映射表缺行的人工补录（14:3x–15:0x）**

用户指出 `310701001g 常规心电图检查(床旁心电图加收)` 实际已废止。核对 11 号文映射行：

- 主项 `310701001`、`a`–`c`、`f` → `012408000030000 常规心电图检查费`；
- `d`（十五导联）、`e`（十八导联）→ `012408000030001 十二导联以上（加收）`；
- **`g` 整条没有出现**，而新项目也没有「床旁加收」这一项（其他检查类的床旁加收都保留成了新加收项）。

保留下来的加收项在同一份表里是**逐条列出**的（d/e 就有），`g` 不列即未保留 —— 属映射表漏行，
不是判定逻辑问题。已在 `data/dezhou-price.json` 的 `cancels` 补一条：

```json
{ "doc": "德医保发〔2026〕11号", "exec": "2026-05-01", "category": "心血管系统类",
  "oldCode": "310701001g", "oldName": "常规心电图检查(床旁心电图加收)",
  "newCode": "", "newName": "", "note": "随主项 310701001 废止；映射关系表未单列，人工核对补录",
  "pending": false }
```

`newCode` 为空 = 直接废止无替代，旧版物价显示红「已作废」+「直接废止，无替代项目」。
`cancels` 5,107 → 5,108；旧版物价「已作废」3,889 → **3,890**、「现行」2,700 → **2,699**。
补录前已备份 `data/_backup/dezhou-price.20261117-143942.json`，写回保持无 BOM + CRLF + indent=1。

**顺带修的检索 bug**：作废对照页搜 `310701001g` 一条都搜不到 —— `CancelMatches` /
`LegacyMatches` 把关键词转大写后用 `Ordinal` 前缀比对，而旧码后缀是小写字母，必然不匹配
（搜 `310701001` 能中、搜完整码反而空）。改成 `OrdinalIgnoreCase`，并加两条自检断言锁死。

**第四批：按废止项目表全量补录 398 行（15:0x）**

用户发来 11 号文《附件26 废止心血管系统类项目表》截图，`310701001g` 赫然在列 —— 说明第三批的判断没错，
也说明**漏行是成片的**。于是把 11 号文 15 张废止表（附件16–30，第 166–239 页）全部提取比对：

| 口径 | 行数 |
|---|---|
| 废止表编码行（去重） | 2,245 |
| 不在作废对照（任何文号）里 | 459 |
| 其中旧版物价里确有该行（用户可见） | **398** |
| 带字母后缀（加收子项） | 356 |
| 纯数字码（整项废止） | 42 |

**为什么成片漏**：作废对照是按**映射关系表**（附件31–44）建的，而映射表只列「有承接项的旧码」，
加收子项与整项废止的旧码大量不在表里。抽查 `330100005 全身麻醉`：映射表只有 `005a`–`h`，
主行压根没印；`310701003a/b 动态心电图加收` 同样只有主项 `003` 在表里。

**补录规则**（`data/dezhou-price.json` 的 `cancels`，doc 11 号、exec 2026-05-01、category 取附件标题）：

- `oldName` 一律取**旧版物价原行名称**（废止表 PDF 抽出的名称会把「项目内涵」粘进来，只用于交叉校验；
  398 行里除 1 行 `311400058a` 因内涵粘连外全部一致）
- `newCode` 优先用「同主项归并」推定：加收子项取父码在作废对照里的映射（353 行）；
  整项废止的 45 行不猜，留空 = 直接废止无替代
- `note` 写明来源：`附件26废止项目表补录，映射表未单列，按主项归并` / `…，映射表未列对应新项目`
- 不补 `keep`（划线保留）—— 11 号文数据里 0 条 keep，且这 398 条都不在映射表，不存在「划线又列映射」的情形

**影响面**：作废对照 5,108 → **5,506** 行（有替代 4,999 / 直接废止 507）；
旧版物价「已作废」3,890 → **4,263**、「现行」2,699 → **2,326**，总数 6,589 不变。
备份 `data/_backup/dezhou-price.20261117-145643.json`。

**没补的 61 条**：废止表里有、旧版物价数据里没有对应行（`310701019a` 之类），补了也不改变任何显示，
且名称只能从 PDF 抽（粘连严重），暂不写入，等旧版物价数据补齐再一起处理。

**验证**：`--selftest` **122/122**（第 113 项再加三条断言：`310701003a` / `330100005` / `331601011` 必须作废）；
`dist/DIP3-Desktop`、`dist/DIP3-Web` 均已重打，两包的 `data/dezhou-price.json` 均为 5,506 行。
截图 `dzlegacy@1400x760:310701003a`（红「已作废」+ `012408000060000 动态心电图检查费`）、
`dzlegacy@1400x760:隆乳术`（`331601011` 红、`331601012` 红）、`dzcancel@1400x760:310701003a`（补录行在作废对照里可查）。

**第五批：给 6 条补上对应新项目，其余注明原因（15:2x）**

第四批的 44 条留空行里，按「新项目表（附件15、1–15）里有没有同名主项」再筛一遍：

- **11 号文没有美容整形类映射表** —— 附件 31–44 只覆盖 14 个类别，美容整形类整类缺失，
  所以 23 条美容整形旧码在任何表里都查不到对应新项目（`cancels` 里 11 号文也一条美容整形映射都没有）。
- 其余 21 条（麻醉 2 / 妇科 2 / 眼科 8 / 耳鼻喉 5 / 神经 3 / 泌尿 1）所属类别有映射表，只是没列这些编码。

**推定规则**（只认「同类别 + 主项 + 去后缀同名」唯一命中，宁缺勿滥）：

| 旧码 | 旧名称 | 新码 | 新名称 |
|---|---|---|---|
| `330601022` | 隆鼻术 | `016200000260000T` | 隆鼻费 |
| `331602011` | 腋臭切除术 | `016200000530000T` | 腋臭切除费 |
| `331602011a/b` | 腋臭切除术（切除缝合 / 原位植皮） | `016200000530000T` | 腋臭切除费（随主项归并） |
| `331601012` | 隆乳术后继发畸形矫正术 | `016200000590000T` | 隆乳术后继发畸形修整费 |
| `331604014` | 除皱术 | `016200000050000T` | 除皱费 |

推定行 `note` 一律写 `附件30废止项目表补录；文内未附本类映射表，按项目名称推定`，便于日后复核。

**没推的 38 条**及原因（都写进 `note`）：美容整形 17 条（文内无该类映射表，如 `隆乳术` 在新表里拆成
假体置入 / 脂肪注射两项，旧码是笼统主项，硬指一项就是错）、其余 21 条（映射表未列该编码）。

**刻意没做的**：`330100005 全身麻醉` 的子项 5 条指向 `013301000050000`（无插管）、3 条指向 `013301000060000`
（插管或喉罩），旧主项同时覆盖两者，按「多数子项」猜一边会误导取价，保留空值。

**影响面**：作废对照 5,506 行不变（有替代 4,999 → **5,005**、直接废止 507 → **501**）；
旧版物价统计不变（4,263 / 2,326）。备份 `data/_backup/dezhou-price.20261117-152116.json`。
重写整文件时用 `json.dump(indent=1, newline='\r\n')`，与原文**逐字节一致**（5,752,023 字节），
只是改了 44 行的字段值。自检 122/122，两包已重打。

**第六批：美容整形类逐条推定，先定 `水光注射 → 美容注射费`（15:3x）**

美容整形类 17 条没推的旧码里，唯一属于「注射」的是 `311400069 水光注射`（旧价自主定价、单位「次」）。
新项目 `016100000090000T 美容注射费` 的项目内涵是「通过注射物质，改善皮肤状态或容貌外观」，
产出与计价单元（次）都对得上，按名称与内涵可唯一确定，遂补上承接：

```json
{ "oldCode": "311400069", "oldName": "水光注射",
  "newCode": "016100000090000T", "newName": "美容注射费",
  "note": "附件30废止项目表补录；按项目内涵（注射物质改善皮肤状态）推定承接美容注射费，待医保确认" }
```

**顺手排掉的两个坑（收费端要注意）**：

- `310100032 肉毒素注射治疗`（50 元/次）**不在**附件16–30 任一废止表里，仍是现行项目。
  打咬肌 / 斜方肌 / 腓肠肌肉毒素时，若同时收 `310100032` 和「美容注射费＋特殊部位加收」
  （`016100000090001T`，特殊部位仅限咬肌、斜方肌、腓肠肌），属重复计价，须二选一。
- 「美容注射费」的**次 = 每次注射的一个部位**（眉间纹、鱼尾纹、眼袋纹、额纹、鼻背纹、颏部、
  颈阔肌、腋窝、手足等），不是「一次就诊」。填充、溶解另有独立主项
  （`016100000100000T 填充注射费` / `016100000110000T 溶解注射费`，每位点），
  不能用美容注射费替代；同时做微针 / 药物导入也不得与之叠加。

**影响面**：作废对照仍 5,506 行（有替代 5,005 → **5,006**、直接废止 501 → **500**）；
旧版物价 4,263 / 2,326 不变 —— `311400069` 本来就算「已作废」，这批只把「无替代」换成可点的承接链接。
备份 `data/_backup/dezhou-price.20261117-153317.json`。自检 122/122（第 113 项加两条断言：
`311400069` 必须作废、`CancelOf` 必须给出 `016100000090000T` 且不得带空替代行）。
截图 `dzlegacy@1500x760:水光注射` → 红「已作废」+ 蓝链「016100000090000T 美容注射费」。

**第七批：美容整形类剩下的 16 条一次推完（15:3x–15:4x）**

按「同类别 + 主项 + 名称/内涵可唯一确定」逐条过，推 8 条、留 8 条：

| 旧码 | 旧名称 | 旧计量 | 新码 | 新名称 | 判定依据 |
|---|---|---|---|---|---|
| `311400024` | 药物面膜综合治疗 | 次 | `016100000080000T` | 药物面膜美容费 | 名称与内涵几乎逐字对应 |
| `311400035` | 激光除皱术 | 每个光斑 | `016100000010000T` | 美容治疗费（光/激光） | 旧项按光斑计价，与新项计量单元完全一致；**不能推「除皱费」**（那是切开术式） |
| `331604011` | 酒窝再造术 | 每侧 | `016200000180000T` | 酒窝整形费 | 同名主项唯一命中，计量同为单侧 |
| `331604021` | 毛发移植术 | 每根 | `016200000090000T` | 头发移植费 | 旧项含种发、头皮游离移植；计量由每根折为次（100 毛囊单位/次） |
| `331604007` | 胡须再造术 | 次 | `016200000120000T` | 体毛移植费 | 新项明确「体毛＝除头发、眉毛、睫毛以外的各种体毛」，胡须属之 |
| `331601013` | 乳腺假体取出术 | 单侧 | `016200000780100T` | 材料置入整形费-人工材料取出（扩展） | 假体属人工材料，取出走扩展项；计量由单侧折为个 |
| `331604036` | 双向倒刺线植入悬吊术 | 每根线 | `016200000780000T` | 材料置入整形费 | 倒刺线属人工材料置入，每根线计 1 个 |
| `331604037` | 线雕取出术 | 每根线 | `016200000780100T` | 材料置入整形费-人工材料取出（扩展） | 取线材/面部植入假体＝人工材料取出；每根线计 1 个 |

**没推的 8 条**（`newCode` 留空 = 直接废止，`note` 写明原因）：

- `331601007 乳房再造术` / `331601011 隆乳术`：新表拆成假体置入、脂肪注射、自体组织皮瓣三项，
  旧主项笼统覆盖，硬指一项就会把取价引错。
- `331601010 乳头乳晕整形术`：新表拆成乳头整形费、乳晕整形费两项，旧项按合并计价。
- `331604003 隆颞术` / `331604004 隆额术`：可用人工材料或自体组织，新表分列材料置入 / 组织置入 /
  脂肪移植，旧项未区分。
- `331604010 颌下脂肪袋整形术`：新表没有脂肪抽吸（脂肪袋切除）主项，无对应项目。
- `331604014a 除皱术(激光除皱加收)`：除皱费没有激光加收项，激光除皱改按「美容治疗费（光/激光）」
  按光斑计价，这条加收直接废止 —— **手术除皱 + 激光除皱不要再叠加收**。
- `331604023 纹饰美容术`（纹眉/纹眼线/唇线）：新表无纹饰主项，「美容治疗费（微针）」内涵是
  微针机械刺激，不等同色素植入。

**影响面**：作废对照仍 5,506 行（有替代 5,006 → **5,014**、直接废止 500 → **492**）；
旧版物价 4,263 / 2,326 不变（这 8 条本来就算已作废）。新旧比对 7,122 → **7,123 行**
（可比 4,880 → 4,888，8 条旧码进入可比行；对应 7 个新项从「纯新增」转为有旧价可比）。
备份 `data/_backup/dezhou-price.20261117-154*.json`。

**验证**：`--selftest` 122/122。新增断言两条：① 8 条推定行的 `newCode` 必须逐条等于预期值，
且该编码必须真实存在于现行项目库（防止写成不存在的码，收费员点过去空白）；② 美容整形类
所有带 `newCode` 的作废行不得存在悬空编码。做了负向测试：把 `331604036` 的新码改成
`016200000780999T`，自检立刻报「美容整形类推定行对不上」，改回后恢复 122/122。
截图 `dzlegacy@1500x760:激光除皱`（`311400035` → 光/激光；`331604014a` → 直接废止）、
`dzlegacy@1500x760:线雕取出术`（→ 人工材料取出扩展）。

**顺带发现的数据缺口（已修，16:2x）**：口腔类 `310511011 盖髓术` 的 `newCode` 写成 `013105010100011`，
而新项目库里「活髓保存治疗费」只有 `…000`（主项）与 `…001`（间接盖髓 减收），旧版物价点过去是空链接。

回查 11 号文 PDF 原件，是**文件自身前后不一致**：

| 位置 | 内容 |
|---|---|
| 第 44 页 计价表（正文） | `013105010100001 活髓保存治疗费-间接盖髓（减收）` 牙 62/55/47；无 `…011`，也无「儿童（加收）」 |
| 第 264 页 映射关系表 | `013105010100011 活髓保存治疗费-间接盖髓（减收）` ← `310511011 盖髓术`，并把 `…001` 印成「儿童（加收）」 |

正文计价表是收费依据，本地 `items` 与第 44 页逐字一致，故映射表那行是**后缀笔误**（001 ↔ 011 串位）。
已把 `310511011` 改指 `013105010100001`，`note` 记录「映射表与正文不一致，按正文计价表为准」。

**自检同时升级**：原来只查「美容整形类不得有悬空承接码」，现在改成**全库兜底** ——
所有带 `newCode` 的作废行都必须能在现行项目库找到该编码，另加一条锁死
`310511011 → 013105010100001`。修完全库悬空数 **1 → 0**，自检 122/122，两包已重打。

## 10. HTTP 接口一览

前台：`/api/group`、`/api/group-trace`（分组溯源，独立接口——见下）、`/api/batch`、`/api/batch-file`、`/api/batch-template`、
`/api/suggest-diag`、`/api/suggest-proc`、`/api/suggest-linked`、`/api/translate`、
`/api/lixiang/sections`、`/api/lixiang/items`、`/api/lixiang/jiedu`、`/api/download/*`

前台只读（2026-10-30 新增，网页端六页用；与 `/api/data/*` 同一套共享口令）：
`/api/diseases-counts`、`/api/diseases`、`/api/dezhou-meta`、`/api/dezhou`、
`/api/dezhou-export`、`/api/dezhou-usage`（类别使用说明，`doc`+`category` 取 `{lines}`）、
`/api/cross`（跨库关联，`lib`+`name` 取 `{groups:[{label,hits:[{chip,text,lib,id}]}]}`）、
`/api/wuhao`（基本物耗指引，一次下发 `meta`/`rules`/`groups`/`items`/`edges`/`sources`；
`q` = 判定词，回 `verdict{tone,head,basis,note,item,edge,near}`，`tone` = `no|yes|check|miss`）、
`/api/zhijian`（病理开具指引，`q`/`system`/`kind` 过滤 + `wax`/`mode`/`level` 出 `{quote}`、
`bags`/`pieces`/`size` 出 `{quick:{wax}}`，同时带 `meta`/`basePrice`/`levels`/`level`/`systems`/`rules`/`special`；
`mode` = `常规|复杂|细胞|TCT`，`level` = `2|3`，旧的 `complex=1` 仍兼容）、
`/api/guifan-meta`、`/api/guifan`

> `/api/dezhou` 参数：`view=live|cancel|legacy|cmp`、`doc`、`category`、`kind`、`level`、
> `insurance`、`q`、`scope`（比对范围 `priced|cancel|none`）、`codes`（仅看收藏，逗号分隔编码）、
> `page`、`pageSize`（上限 200，与 Core 一致）。`view=legacy` 的 `status / currentStd`
> 由服务端拿作废对照反查后下发，网页端只负责显示。


> **分组溯源**（2026-10 新增）：`POST /api/group-trace` 入参与 `/api/group` 完全一致，
> 返回 `{ result, trace }`，`trace` 按优先级逐层给出判定过程：
> 排除表判定(EXCL) → 先期分组(XQ) → 诊断辅助细分(FZ) → 并项规则(BX) → 基础规则(JC)
> → 基层病种(JICENG，仅 `--jiceng`) → 综合病组(ZH)；每层含 `status`
> （hit 命中 / miss 未命中 / skip 跳过 / warn 排除命中并产生影响 / ok 无影响）、
> `title` 结论与 `facts` 判定依据（编码级：命中哪个码、缺哪个码、卡在哪一步）。
> **为什么是独立接口**：`/api/group` 的返回体与 Node 版逐字节一致（§6.1 对拍基线），
> 溯源多一个键就破；进程内（桌面端）则直接挂在 `GroupResult.Trace`
> （`GroupResultConverter` 只写固定键，天然不参与序列化）。批量分组不生成溯源（省开销）。
> 一致性由 `tests/TraceTests.cs` 保证：全部 44 条既有用例断言「溯源命中层 = 结果层、行号 = 结果行号」。

后台：`/api/admin/status`、`/api/admin/config`、
`/api/admin/diseases`(+`-counts`/`-add`/`-update`/`-delete`/`-import`/`-template`)、
`/api/admin/mapping`(+`-add`/`-update`/`-delete`/`-import`/`-gray`)、
`/api/admin/zonghe`(+`-add`/`-update`/`-delete`/`-import`/`-import-empty`/`-template`)、
`/api/admin/lixiang`(+`-add`/`-update`/`-delete`/`-import`/`-template`/`-guide-usage`)、
`/api/admin/jiedu-import`、`/api/admin/jiedu-delete`、
`/api/admin/dezhou`(+`-batches`/`-add`/`-update`/`-delete`/`-import`/`-template`/`-export`/`-usage`)

登录与设置：`/api/admin/session`、`/api/admin/login`、`/api/admin/logout`、
`/api/admin/settings`、`/api/admin/settings-app`、`/api/admin/settings-login`、`/api/admin/settings-lixiang`、
`/api/admin/settings-share`（设置数据分发共享口令，保护 `/api/data/*`，留空＝不校验）、
`/api/settings/public`（免鉴权，回软件名称 / 副名 / 品牌短名 / 立项指南参数 ——
既供前后台渲染标题，也供桌面客户端「跟随服务端公共设置」，见 §3.1.2；
`GET /api/admin/settings` 另会回 `categoriesInUse`/`unitsInUse` 两个「现有数据里的取值」，供后台设置页在参数表为空时预填；
`shareToken`/`hasShareToken` 供「数据分发共享口令」卡回显）

鉴权范围：`POST /api/admin/*`（`login`/`logout` 除外）、`GET /api/admin/settings` 与
`GET /api/admin/notices`（含未发布草稿）需要会话，其余接口免登录；
`data/settings.json` 的 `loginEnabled` 关闭时全部放行。

数据分发（只读，供桌面客户端「数据更新」）：`/api/data/manifest`、`/api/data/file/{name}`
（若「数据分发共享口令」已设置，需带 `?token=` 或 `X-DIP3-Token` 头）

客户端更新（只读，供桌面客户端「客户端更新」卡，鉴权与数据分发共用同一口令）：
`GET /api/app/version`（未发布时回空版本号，客户端按「已是最新」处理）、
`GET /api/app/package`（下载 `data/app-update/package.zip`，404 = 未发布；
发布方法与 `manifest.json` 约定见 §9.8）

消息推送：`/api/notice`（前台，**免鉴权**，只回「已发布 + 生效期内」的消息，
**不含**生效排期与发布开关）、`/api/admin/notices`（后台列表 + 统计 + 服务器时间）、
`/api/admin/notice-save`、`/api/admin/notice-toggle`、`/api/admin/notice-delete`、`/api/admin/notice-clear`

## 11. GitHub 开源发布与软件更新流程（2026-11-18）

仓库：<https://github.com/libo770880/dip3-grouper-cs>（公开）。主分支 `main`，
单提交干净历史（曾推过含 `data/settings.json` 密码哈希的历史，已用孤儿分支重建 +
force push 清除；`settings.json` / `audit-log.json` 自此不入库，缺失时程序自动重建默认值）。

### .gitignore 要点
`bin/ obj/ dist/ _backups/ _shots/ data/_backup/` 及运行时状态
`data/settings.json`、`data/audit-log.json`。真正入库约 200 文件 / 30MB（大头是 data/*.json 数据）。

### 「检查更新」实现（v1.0.0 起）
- `Desktop/UpdateChecker.cs`：`CurrentVersion` 常量 + GitHub
  `releases/latest` API（8 秒超时，公开仓库免鉴权，UA 必带否则 403）；
  版本比较接受 `v1.2.3 / 1.2.3 / 1.2` 与后缀 tag，逐段补零比较，解析失败一律判「不更新」。
- 启动静默检查挂 `App.RunWithSplashAsync`（只有新版才弹框）；手动入口是
  主窗口右上「检查更新」按钮（`MainWindow.OnCheckUpdate`，结果弹窗明确告知，含已是最新）。
- 「打开下载页」用 `Process.Start(UseShellExecute)` 跳浏览器，不做自动替换安装
  （自替换要处理运行中 exe 解锁，后续版本再议）。
- 自检 #123 锁版本比较规则与 API 地址（改 owner 忘同步会报错）。

### 发新版的固定动作
1. 改 `UpdateChecker.CurrentVersion` → `publish-desktop.bat` 重打；
2. `dist/` 下 zip 安装目录为 `DIP3-Desktop-vX.Y.Z.zip`
   （**排除** `data/settings.json`、`data/audit-log.json`，勿带密码哈希出厂）；
3. GitHub 新建 Release：tag 用 `vX.Y.Z`，上传 zip 资产；
4. 用户端启动即收到新版提示。

### 11.1 解读辅导按国家局原文重导入（2026-11-18，v1.0.1）

国家医保局解读辅导原文（<https://www.nhsa.gov.cn/module/download/downfile.jsp?classid=0&filename=976ce73a34c94bdab08920c94d2b5df6.wps>，
WPS 二进制）重新导入：仍为 13 期，但**条数 224 → 226**。

根因：解析器 `Lixiang.QRe` 只认全角「？」作题面结尾，而原文第 5 期
第 4 问（多体位脊柱全长 X 线摄影拼接成像）、第 11 问（单侧上肢多部位 CT 收费）
以**半角「?」**结尾 → 整题被吞进上一题答案。已改 `[？?]`，并把自检断言改成
「相对当前版本号推导」（升版不再需要改测试）。

- 修后：222 条与旧库逐字一致（证明文件内容未变，问题在导入），
  5 期 3、5 期 10 两条答案恢复干净，找回 5 期 4、5 期 11 两问；
  `HasDanglingNo` 残留 0。
- 导入走项目自己的管线（`AdminService.ImportJiedu`，mode=replace），不手工改 JSON。
- 备份 `data/_backup/lixiang.20261118-*.json`；自检 123/123。
