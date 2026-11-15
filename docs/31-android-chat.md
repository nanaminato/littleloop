# 原生 Android 聊天客户端

`src/LittleLoop.Android` 是独立的 Kotlin／Jetpack Compose Gradle 项目。第一版沿用蓝色标题栏、会话列表、未读徽标和左右聊天气泡。现有 LittleLoop 账号可直接登录，支持创建私聊和群聊、发送文本、查看历史、会话静音和账户设置。

消息、已读、资料和通知偏好继续由现有 ASP.NET Core／MySQL 后端保存。SignalR 通知客户端拉取变动，HTTP 用于断线时补拉。发信重试沿用同一请求 ID。只有打开着的前台会话才会在加载历史后回写已读。主题与后台连接开关属于本机。登录会话以 Android Keystore 保护的密钥加密，应用不保存密码。

## 在 Android Studio 或模拟器运行

使用 JDK 21 与 Android SDK 36，在 Android Studio 打开 `src/LittleLoop.Android` 并运行 `app`。最低系统版本为 Android 8.0（API 26）。Windows 也可在仓库根目录运行：

```powershell
./src/LittleLoop.Android/gradlew.bat -p src/LittleLoop.Android :app:assembleDebug :app:lintDebug
adb reverse tcp:5190 tcp:5190
adb install -r src/LittleLoop.Android/app/build/outputs/apk/debug/app-debug.apk
```

上面的端口映射要求本机聊天后端监听 5190。调试版登录页默认使用 `http://127.0.0.1:5190`，也接受模拟器宿主地址 `http://10.0.2.2:5190`。发布版需要可从设备访问的 HTTPS 服务地址。

## 后台通知

在「设置 → 后台通知（本机）」启用并授予系统通知权限。客户端以前台服务保持聊天连接，Android 会显示一条常驻连接通知。退出聊天界面后，新未读消息依照账号的通知类别、会话静音、预览与声音偏好进入系统通知栏，点击可进入对应会话。进程被系统终止或被用户强制停止后，连接与接收随之停止。原生 FCM 云推送尚未接入。 [Android 前台服务停止行为](https://developer.android.com/develop/background-work/services/fgs/handle-user-stopping)

## 验收

在本机 Android 17 模拟器、Chrome 和现有 MySQL 实库上，测试账号 `bob_settings` 通过 Android 登录，`alice_settings` 从 Chrome 发出私聊和群消息。Android 的私聊回复出现在 Chrome 消息区。Android 新建群并发送消息后，受邀账号 `carol_c` 在 Chrome 打开了新群和消息。Android 打开原私聊及群聊后，Bob 的 Chrome 会话查询都返回未读数 0。Android 保存的显示名称可在 Chrome 读取，从 Chrome 修改后又出现在 Android 设置。

在模拟器退出聊天界面、桌面成为前台后，Chrome 通过群聊输入框发送唯一标记消息。Android 系统通知记录包含该消息预览，点击通知打开了同一个群。验收截图及记录保存在本机 `data/chat-android/`，包括 `result.json`、`android-created-group.png`、`android-background-final.png` 和 `final-notification-proof.txt`。

## 组件与多语言

界面已按 `ChatApp`、首页、会话、设置、登录和共享组件拆分，使用 Material 3 浅色/深色主题与中英文资源。语言可在「设置 → 语言」切换，重启后保持。实现与双端验收见 [对话界面组件化与多语言刷新](40-chat-ui-refresh.md)。
