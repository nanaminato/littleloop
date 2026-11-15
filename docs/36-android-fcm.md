# Android FCM 云推送

## 配置

1. 用 Google 账号创建 Firebase 项目，免费 Spark 套餐即可使用 FCM。Android 应用包名填写 `dev.littleloop.chat`。从项目设置下载 `google-services.json`，放到 `src/LittleLoop.Android/app/google-services.json`。该文件已被 Git 忽略，重新构建后才会启用「后台通知（云推送）」入口。[Firebase 价格](https://firebase.google.com/pricing) · [添加 Android 应用](https://firebase.google.com/docs/android/setup)
2. 后端设置 `ChatFcm__ProjectId` 为同一个 Firebase 项目 ID。开启 Firebase Cloud Messaging API，并给发送身份授予该项目的 FCM 发送权限。后端使用 Google Application Default Credentials。若使用 JSON 凭据，把文件放在仓库外并让 `GOOGLE_APPLICATION_CREDENTIALS` 指向文件。[FCM HTTP v1 授权](https://firebase.google.com/docs/cloud-messaging/send/v1-api)
3. 在有 Google Play 服务的模拟器或真机安装 APK，登录后到「设置 → 后台通知（云推送）」启用并允许 Android 通知。客户端启用云推送后停止基于常驻连接的本机模式。

## 投递与权限

- 普通消息与每枚待审批 nonce 进入 MySQL 的设备队列。服务端在入队和投递阶段检查会话静音，投递时还检查成员资格、已读、删除、屏蔽和类别偏好。只有仍有权批准且当前请求待处理的用户能解析审批通知。
- FCM 载荷只带通知编号。Android 收到后经 WorkManager 用设备会话取回内容。服务器在这一步重新校验权限。投递记录、Android 唯一工作项、持久去重和系统通知标签共同防止重复显示。
- 通知点击跳到原会话和消息序号，审批通知直接打开原任务卡片。执行决策仍需打开卡片，由原有审批接口校验。退出或撤销设备会取消绑定或失效该会话。

## 网关验收

后台测试时回到 Android 桌面，再从网页版发私聊、群聊或触发待审批任务。进程回收测试用 `adb shell am kill dev.littleloop.chat` 并核对进程退出。Android 设置中的「强行停止」会让系统阻止投递，须手动打开应用后再验收。通知到达后点击检查原消息或任务，再测试静音、不具备审批角色和同一通知重试三种情况。[Android 接收 FCM](https://firebase.google.com/docs/cloud-messaging/android/receive-messages)

## 已验证范围

用 Chrome 和真实 MySQL 验证了私聊和群聊的设备入队、会话静音、相同消息重试只入队一次、待审批只对审批人可解析，以及审批处理后通知失效。Android 17 模拟器使用本地测试意图，验证了进程重新启动时定位到原消息和打开原任务卡片。构建和 Android Lint 均通过。

配置真实 Firebase 项目后，再次使用 Chrome、真实 MySQL 和 Android 17 模拟器验收。应用退到桌面后收到私聊通知，点击进入原消息。用 `am kill` 回收后台进程并确认 PID 消失后，模拟器无需手动打开应用即可收到群聊通知，点击同样定位到原消息。重复提交同一条群聊消息仅产生一条设备通知记录，静音测试消息未入队或出现在系统通知栏。

真实 FCM 送达待审批通知，点击打开原任务并显示「等待审批」。未授权账号读取该通知返回 `404`，提交审批返回 `403`，复用已经消费的审批标识返回 `409`。本机验收记录位于 `data/chat-android-fcm/`。
