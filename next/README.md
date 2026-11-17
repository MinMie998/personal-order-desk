# 重构工程

当前版本：3.0.0-preview.1。Windows 入口 desktop/src/main.rs，后台 rust/crates/server，Web web/src/App.vue，Android android/app/src/main/java/com/personalorderdesk/nativepreview/MainActivity.kt。

Windows: Tauri + Vue + Rust；Android: Kotlin + Compose + Room + Rust JNI。两端共用 sync-core 协议、加密、摘要与三方合并。

交付包位于根目录 releases/v3.0.0-preview.1；[使用与限制](../docs/发布/v3.0.0-preview.1.md)。Windows 打包 scripts/Package-Next-Windows.ps1；Android 构建 scripts/Build-Android-R0.ps1。

本版供用户自行测试，不标稳定。App 支持完整离线使用、远端首次下载、双向合并与冲突选择，写入前保护备份。当前没有扫码、系统通知或后台自动同步。
