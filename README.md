# 个人接单工作台

面向个人接单的离线工作台：管理项目、制作进度、任务、收支利润和快递。Windows 使用 Tauri + Vue + Rust，Android 使用 Kotlin + Compose + Room；两端共用 Rust 同步算法。

当前版本 **3.0.0-preview.1**，供自行试用反馈，尚非正式稳定版。Windows 与 Android 可以各自离线使用；电脑关机后手机仍能查看和记录自己的完整副本。

## 下载和运行

从 [GitHub Releases](https://github.com/MinMie998/personal-order-desk/releases) 下载附件：

- **Windows**：下载 `PersonalOrderDesk-3.0.0-preview.1-windows-x64.zip`，完整解压，双击 `desk-desktop.exe`。保留同目录的 `desk-server.exe` 与 `web` 文件夹。无需 .NET、Node 或 Rust；界面依赖系统 WebView2。
- **Android**：下载 `PersonalOrderDesk-3.0.0-preview.1-android.apk`。支持 Android 8.0 以上的 arm64 手机和 x86_64 模拟器。当前为开发签名预发布包；后续正式签名升级另行说明。覆盖安装前建议导出备份，遇到签名不兼容请停止安装，先备份，勿直接卸载旧应用。
- **Web**：Windows 后台启动后，在同一电脑浏览器打开 `http://127.0.0.1:15059`。本版没有对公网或局域网直接开放本地控制服务。

Windows 首次创建或打开账本目录；Android 自动使用应用私有目录。安装包不携带示例订单、个人账本、登录凭据或密钥。

## 功能

- 概览：订单数量、工作待办、已收款、实际支出、利润和待收款。
- 项目：分类、截止日期、当前进度、下一步、追加单关联、回收站；Windows 支持工程路径。
- 任务：阶段与子任务、计划日期、完成记录、应用内到期提醒、任务排序与阶段折叠。
- 收支：收款、支出、退款、作废与历史记录，金额按分计算；未知日期可留空。支持按订单编号复制待收款清单。
- 物流：快递单号、寄出日期、签收日期。
- 保护：本地备份恢复、同步前备份、加密远端快照、冲突预览、同步记录。
- 更新：设置中手动检查 GitHub 版本并打开安装包下载；程序更新不搬移或重建账本。

## 两端同步

本地编辑立即保存在当前设备；点击同步才访问 GitHub。GitHub 私有仓库存放加密快照，公开源码仓库不存业务数据。两者必须分开。

1. 在自己的 GitHub 账号创建**私有数据仓库**，把同步 GitHub App 安装到该仓库并允许 Contents 读写。
2. Windows 设置中连接 GitHub，填写 `所有者/仓库名`，检查同步摘要后确认首次上传。
3. Windows 导出 `.deskkey` 恢复密钥，安全传到手机。Android 连接 GitHub、检查同一个私有仓库、导入恢复密钥。
4. 新设备首次选择**下载远端副本**。已有本地记录会先备份，再要求确认替换，不会用空手机覆盖远端。
5. 有共同基准后选择**检查并合并同步**。独立新增记录按稳定 ID 合并；相同字段发生冲突需选择本机或远端。收支相似记录会提醒核对，不按金额擅自去重。
6. Windows 高级选项可从远端整本替换本机，或以本机重置远端。重置会更换代次，旧设备须重新下载，不能把旧副本混回新账本。

同步提交使用非强制 Git 更新；远端在预览后变化会停止本次操作。恢复密钥与 GitHub 登录不同，请独立保存；没有密钥无法解开远端账本。不要把密钥、数据库或凭据上传到公开仓库。

**其他账号注意**：当前使用的 GitHub App 由项目维护者管理；若安装页面不允许你的账号安装，暂时使用离线功能，不要授权所有私人仓库。跨账号 App 开放状态尚待确认。

## 更新旧版本

Windows 先备份，在旧工作台设置中退出后台，关闭窗口，再解压新版到新程序目录运行。打开原账本目录即可，不能仅覆盖 EXE 而保留旧后台。旧 .NET 版属于不同架构，本包不自动迁移其数据库。

Android 使用相同包名覆盖当前原生预览版；不要卸载清数据。早期 .NET Android 是不同应用，不自动覆盖或迁入。多设备请先升级到兼容同步规则 v2 的版本再合并。

## 构建

Windows：安装 Rust、MSVC C++ 构建工具和 Node 22，在仓库根目录运行 `./next/scripts/Package-Next-Windows.ps1`。

Android：安装 Android SDK（compileSdk 37）、NDK 28.2.13676358 和 Java 17+；安装 Rust targets `aarch64-linux-android`、`x86_64-linux-android`，执行 `./next/scripts/Build-Android-R0.ps1 -JavaHome <JDK目录> -RunChecks`。输出位于 `next/android/app/build/outputs/apk/debug/app-debug.apk`。自行构建的签名与发行包可能不同，不应直接覆盖已有数据的安装。

核心算法检查：`cargo test --locked --manifest-path next/rust/Cargo.toml -p desk-sync-core -p desk-sync -p desk-storage -p desk-server`。

本版尚未完成真实多设备全部场景的集中验收。后台自动同步、扫码连接、系统通知、iOS 与自动安装更新不在此预发布版范围内。
