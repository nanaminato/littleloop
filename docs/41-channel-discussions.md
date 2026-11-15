# 频道管理员、讨论群与评论

频道所有者可在网页会话详情或 Android 频道设置中按用户名授予管理员权限。发布、编辑、删除、置顶、修改资料、管理订阅者和管理讨论分别授权。用户退订或被移除后，授权随成员关系清除。频道所有者可查看管理记录。

只有所有者可以删除频道，两端均要求确认。删除后，频道从目录和订阅者会话中移除，并关闭关联讨论线程。原讨论群继续可用，群成员仍可查看原有帖子副本和评论。

频道关联讨论群后，新帖及附件会同时写入该群。群成员能从群里的关联帖子进入讨论；从频道进入则同时需要频道订阅和讨论群成员资格。评论复用群消息，编辑和删除在两处显示同一条记录。更换或解除关联会关闭旧线程，原帖删除后也可从群里的删除占位查看历史讨论。群管理员可以处理群内评论，频道管理员的权限由频道单独分配。

评论支持文字、附件和引用回复。线程已读位置独立于频道已读，频道入口和群入口共用该位置。Android 首次打开线程定位到最新评论，轮询更新时保留用户滚动位置。Android 可在已加载的讨论中离线排队，重连后的发送仍受最新权限约束。服务端保存帖子关注状态，并在 Web Push 和 FCM 的候选投递中检查群通知、关注、回复、提及、静音、已读及成员资格。

LittleLoop 可通过标准聊天登录和消息接口接入。配置 `ChatAutomation__LittleLoopSecret` 后，该账号使用部署方配置的专用凭证登录。频道发布和群评论使用普通成员与管理员权限。源码问答、开发任务及审批仍按各自的扩展权限处理。

## 验收记录

下列文件位于本机 Git 忽略的 `data/channel-discussions/`。它们记录了不同轮次的测试，不代表每项场景都已在最终版本重新运行。

| 场景 | 实测证据 |
| --- | --- |
| 发布授权、撤权、管理记录 | `api-result.json`、`audit-result.json`、`web-publish-revoked.png`、`android-publish-revoked.png`、`web-audit.png`、`android-audit.png` |
| 评论双向同步、编辑、附件 | `sync-result.json`、`android-edit-result.json`、`web-sync.png`、`android-sync.png`、`web-comment-attachment.png`、`android-comment-attachment.png` |
| 群禁言与评论管理 | `moderation-result.json`、`web-group-moderation.png`、`android-group-moderation.png` |
| 替换讨论群、删除后阅读 | `lifecycle-result.json`、`group-entry-result.json`、`web-group-comment-history.png`、`android-group-comment-history.png` |
| 评论已读、关注与离线恢复 | `group-read-result.json`、`android-unread-result.json`、`web-follow.png`、`android-follow.png`、`offline-result.json`、`android-comment-revoked.png` |
| LittleLoop 普通账号访问 | `automation-result.json` |
| 只读频道前往讨论群 | `readonly-entry-result.json`、`web-readonly-discussion-entry.png`、`android-readonly-discussion-entry.png` |
| 讨论群申请与审批 | `web-joins-result.json`、`android-joins-result.json`、`join-cleanup-result.json`、`android-discussion-join-request.png`、`android-discussion-join-approval.png`、`web-after-android-join-approval.png` |
| 频道所有权转让 | `transfer-result.json`、`android-transfer-result.json`、`web-channel-transfer-confirm.png`、`android-channel-transfer-confirm.png`、`web-after-android-channel-transfer.png` |
| 频道删除权限、历史保留及实时移除 | `channel-delete-result.json`、`web-channel-delete-result.json`、`android-channel-delete-result.json`、`web-channel-delete-confirm.png`、`android-channel-delete-confirm.png`、`web-after-android-channel-delete.png` |
| 通知规则与评论跳转 | `notification-rules-result.json`、`push-resolution-result.json`、`web-notification-route-result.json`、`web-comment-link.png`、`android-cold-comment-link.png` |
| Android 真实 FCM 通知与系统点击 | `android-fid-live-result.json`、`android-fid-live-notification.png`、`android-fid-live-comment.png` |
| 两端查看同一条合成评论、Android 首次打开定位 | `isolated-comment-cross-client-result.json`、`web-external-push-comment-thread.png`、`android-latest-comment-visible.png` |

外部验收使用独立 MySQL 库和三个合成账号。Chrome 初次注册后，网关接受了加密测试通知，浏览器未记录接收。通过设置关闭推送并在同一 Chrome 配置重新注册后，GCM 连接变为 `CONNECTED`，Chrome 对真实外部测试通知记录了 `Push message received`、Service Worker 的处理事件和 `Notification displayed`。再次测试核对了通知正文、频道与帖子坐标，并在 Chrome 打开对应评论线程截图。证据见 `web-push-fresh-subscription-result.json`、`web-external-push-comment-thread.json` 和 `web-external-push-comment-thread.png`。

Android 安装包与测试服务的 Firebase 项目一致，模拟器发起注册时 Firebase Installations 返回服务不可用，尚未取得可用于验收的设备订阅。Android 37 和带 Google Play 服务的 Android 36 模拟器直连注册端点均超时，两个环境都能建立到消息端口的连接。`isolated-push-registration-result.json`、`android-firebase-network-result.json` 和 `android-isolated-push-registration-error.png` 记录了这些结果。评论跳转另有 Chrome 通知点击事件测试和 Android 冷启动测试。

Android 36 通过受限测试代理成功建立了到 Firebase 注册和设备配置端点的连接，应用完成了隔离账号登录，并保存了一条已认证的 Firebase Installations 记录。Firebase Messaging 获取设备令牌时返回嵌套的 `AUTHENTICATION_FAILED`，隔离服务未收到设备订阅。Android 诊断只记录固定错误码或异常类型，见 `android-firebase-token-diagnostic.txt`、`android-firebase-installation-state.json` 和 `android-firebase-provisioning-result.json`。

Android 36 运行时已对 Firebase Installations 和 FCM Registration 两个端点完成 TLS 握手，并收到根路径的 HTTP 404，证实这两个 HTTPS 端点可达。Google Play 消息组件随后仍记录 `AUTHENTICATION_FAILED`，但错误日志未指明是 LittleLoop 的具体注册请求。见 `android-firebase-https-probe.txt`、`android-firebase-gms-errors.json`。[Firebase 网络要求](https://firebase.google.com/docs/cloud-messaging/network-configuration)

最新版本重跑了 8 组本机 API 回归，覆盖权限和管理记录、关联生命周期、已读、禁言、入群审批、申请清理和频道删除。Android 的 `assembleRelease` 和 `lintDebug` 均通过。

后续版本启用 Firebase 安装 ID 注册。在新的 Android 37 测试模拟器中，实际客户端完成了注册，服务端保存了新类型的订阅。服务端兼容旧客户端提交的注册令牌，已验证旧格式注册和注销及无效安装 ID 拒绝。见 `android-fid-registration-experiment.json` 和 `fcm-registration-compat-result.json`。[Firebase 消息目标字段](https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages)

Android 最初把通知查询任务限制在已通过互联网验证的网络。隔离测试的局域网可访问聊天服务，但 Android 没有验证该网络，导致任务被延期。去掉这项约束后，查询故障仍由 WorkManager 退避重试。最终在正式源码构建的 Android 客户端实际收到通过 `fid` 投递的评论通知。点击系统通知打开了同一条评论。截图和验收结果见上表。
