# Android 聊天同步架构

官方 Telegram Android 是交互参考，适合研究会话列表、聊天时间线和消息操作。它的 GitHub 仓库约有三万颗星，标注 GPL-2.0。[Telegram Android 源码](https://github.com/DrKLO/Telegram)

Telegram X 是官方的另一款 Android 客户端，GitHub 仓库约有 6,200 颗星，标注 GPL-3.0。它使用 TDLib 将网络、消息更新与本地数据封装在界面之外。这个边界适合 LittleLoop，TDLib 的协议则专用于 Telegram 平台。本次实现保留 Compose 和现有服务接口，自行实现本地存储和同步层。[Telegram X 源码](https://github.com/TGX-Android/Telegram-X) · [TDLib 文档](https://core.telegram.org/tdlib)

## 本次落地

- **本地读取**。SQLite 按登录会话隔离保存会话列表、设置快照和已访问会话最近 50 条消息。断网重启可打开缓存和未发记录。登录切换时旧会话的缓存及队列不进入新会话。退出登录或收到会话失效后，清除对应本地数据。Android Keystore 继续保护登录凭据，聊天缓存保存在应用私有数据库。
- **持久发送**。点击发送时先写入 SQLite，记录房间、正文、引用序号和固定的客户端 ID。允许前台或已开启后台连接的应用重试暂时性失败。服务端接受消息后移出队列。被服务拒绝的消息显示失败，用户可重试或移除。服务端按同一发送者、会话、客户端 ID 去重。
- **同步**。独立的 SignalR 客户端产生更新信号，仓库合并连续的信号，用 HTTP 补拉服务端序号和正文。Compose 渲染仓库状态，通知服务使用同一份会话状态。缓存用于显示，只有联网同步且消息在前台会话加载后才回写已读。
- **引用回复**。每条可回复的消息提供回复操作。发送前可查看或取消引用，收到消息时显示引用正文或消息序号。引用随发送队列持久化，复用网页版的 `replyTo` 字段。

离线读取范围限于本机已加载过的会话与最近消息。应用进程停止时，待发送消息保留，恢复前台或重新开启后台连接后继续处理。附件传输、消息搜索和机器人卡片可以逐步接到这一数据层。[Android 离线优先架构](https://developer.android.com/topic/architecture/data-layer/offline-first)

## 验收

Debug APK 和 Android Lint 均通过。在 Android 17 模拟器、Chrome 和现有 MySQL 实库上，Chrome 发出唯一测试原文，Android 切断到后端的连接后回复。强制停止并重开应用后，历史和待发送回复仍可见。恢复连接后，Chrome 收到一条回复，`replyTo` 指向原文序号 8。以相同客户端 ID 重交，服务端仍返回序号 10，历史只有一条该回复。截图和结果在本机 `data/chat-android-upgrade/`。
