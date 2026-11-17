# 原生 Android 工作台

当前版本 3.0.0-preview.1（versionCode 7），包名 com.personalorderdesk.nativepreview。Kotlin / Compose / Room + 共用 Rust 同步核心。包含 arm64-v8a、x86_64 JNI，Android 8.0+。

在仓库根目录运行 `./next/scripts/Build-Android-R0.ps1 -JavaHome <JDK目录>`，可加 `-RunChecks -BuildProbe`。需要 SDK 37、NDK 28.2.13676358、Java 17+，以及 Rust Android 两个 targets。

输出 app/build/outputs/apk/debug/app-debug.apk；交付归档 releases/v3.0.0-preview.1。当前开发签名与现有原生预览版保持一致；不同机器自行构建签名可能不匹配，请勿先卸载已有数据的应用。

功能：离线账本、项目草稿、两级任务及日期、收支利润、物流、回收站、本地备份恢复、GitHub 授权、恢复密钥导入、下载确认、三方合并提交、冲突选择与同步记录。账户由 Android Keystore 保护，不包含在业务备份中。

尚未完成全部真实双设备验收；系统通知、后台同步、扫码、iOS 暂不包含。本机探针仅操作隔离 Room 与合成数据，不得使用生产账本。
