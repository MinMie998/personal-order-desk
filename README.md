# 个人接单工作台

面向个人的订单、工作进度、收支利润和快递记录工具。Windows为主要入口，Web方便浏览器和局域网访问，Android支持独立账本、直接连接PC和离线编辑PC副本并双向合并。

当前源码为 **2.0.0-preview.3**。这是三端预览版，原有Web维护界面仍提供Excel导入、备份恢复、局域网访问设置和历史资料核对。三端共用界面覆盖日常订单、进度、收支、物流和回收站。

- 类型区分实物制作、仿真、原理图/PCB、代码/报告等；非实物默认接单后一天截止，实物自行约定日期，提前提醒天数可在设置调整，非实物默认提前1天提醒。
- 工作完成、款项结清和物流签收分别记录。收款/利润使用绿色，待收使用红色，临期使用橙色；支持收款反馈动效。
- 收款与支出日期可留空，金额精确到分；微信、支付宝等方式下拉选择。未结款可按编号逐行复制，同编号金额合并。
- 工作项可移入回收站、恢复；收支和物流随项目保留。
- 手机独立账本与PC副本分开保存。连接模式直接编辑PC，首次同步建立电脑副本，之后可离线修改；同步时基于共同版本合并两端改动，冲突由用户选择。同步前先备份两端，断线后可继续确认结果，手机ZIP保护包支持预览恢复，恢复前再保护当前账本。手机独立账本不会自动混入电脑账本。
- Android手机包仅包含ARM64架构，约38 MB；系统返回会先关闭弹窗、返回上级页面，再回概览，概览页返回才回手机桌面。
- Windows和Android可检查GitHub发布版本，下载并校验SHA256后交由用户安装。Windows当前采用解压升级，Android由系统确认覆盖安装。

## 运行与安装

下载 [Releases](https://github.com/MinMie998/personal-order-desk/releases) 中的Windows压缩包或Android APK。详细说明见[安装与更新](docs/public/安装与更新.md)。程序源码和安装包不包含订单或数据库。

Windows完整解压后运行 `PersonalOrderDesk.Desktop.exe`。需要 Microsoft Edge WebView2 Runtime；后台服务随桌面端启动，关闭桌面窗口后继续运行。Web入口 `http://127.0.0.1:5059`，共用界面 `/Workbench`。退出后台在Web设置页完成。

## 开发

使用 `global.json` 指定的.NET 10 SDK。Windows桌面采用WPF + BlazorWebView，Android采用.NET MAUI + BlazorWebView，Web采用ASP.NET Core。Core中的SQLite/EF Core业务与迁移由三端共用。默认仅监听本机，局域网需在PC设置中开启并设置密码；Android通过HTTP API连接电脑，不直接连接数据库文件。

```powershell
dotnet test tests/PersonalOrderDesk.Tests -c Release
dotnet build src/PersonalOrderDesk.Desktop -c Release
dotnet workload install maui-android
dotnet build src/PersonalOrderDesk.Android -c Release
./scripts/Build-Release.ps1 -Platform Windows
```

Android构建还需要Android SDK及JDK21。部分Android构建工具不支持中文工程路径，请使用英文路径检出，或为现有项目建立临时英文盘符映射。主解决方案不包含Android，以便没有Android工作负载的Windows开发者运行业务测试；Android单独构建。

GitHub Actions对提交运行测试并生成Windows和Android产物。推送 `v*` 标签时，两个平台均成功后发布Release与SHA256SUMS。Android正式发布要求仓库Secrets `ANDROID_KEYSTORE_BASE64` 和 `ANDROID_KEYSTORE_PASSWORD`，密钥别名为 `personalorderdesk`。密钥需持续使用并在仓库外备份；无稳定签名时仅构建验证，不发布正式APK。

此仓库由整理后的源码建立，不携带原始开发Git历史、个人订单、原Excel、数据库、备份、本机配置、截图、连接凭据或私钥。更新不会上传账本。局域网HTTP仅适用于可信网络，请勿将端口暴露到公网。

