# Android 会话与频道管理重构

2026-11-15，依据用户明确要求：先拆分 Android 会话页面和频道管理，分离组件与业务状态，统一权限判断，保持现有功能，并用现有测试和模拟器回归。本轮延续 [原生界面审查](43-android-chat-ui-audit.md)，不改变服务端接口、持久发送队列或同步协议。

## 职责边界

| 文件 | 职责 |
| --- | --- |
| `conversation/ConversationScreen.kt` | 页面装配、系统附件选择器和导航回调 |
| `conversation/ConversationState.kt` | 会话 UI 状态保存、附件加载、机器人状态、讨论摘要轮询和页面事件 |
| `conversation/ConversationHeader.kt` | 顶栏及会话菜单 |
| `conversation/ConversationTimeline.kt` | 消息列表、滚动定位、待发消息呈现 |
| `conversation/MessageBubble.kt` | 消息气泡、附件／机器人入口、待发状态 |
| `conversation/ConversationComposer.kt` | 受控输入框、引用、附件、只读提示 |
| `conversation/ConversationDialogs.kt` | 按页面状态装配已有功能弹窗 |
| `channels/ChannelAdministrationState.kt` | 频道表单状态、请求、忙碌／错误状态、最新权限 |
| `channels/ChannelAdministration.kt` | 资料、授权、所有权、订阅者、日志、讨论群及确认组件 |
| `conversation/ChatPermissions.kt` | 消息动作、频道权限、所有者身份、转发目标的共享规则 |

路径均相对 `src/LittleLoop.Android/app/src/main/java/dev/littleloop/chat/`。组件通过值和事件回调工作，不在显示组件中发起仓库请求。页面装配层仍复用现有 Repository；本轮没有为每个小控件增加类或 Gradle 模块。

会话状态与协程作用域按会话 ID 隔离。配置变化时保留原先 `rememberSaveable` 保存的草稿、引用及详情定位；文件选择、消息菜单和未提交操作仍为临时状态。保存键使用稳定会话 ID，避免依赖 Repository 的进程内对象身份。发送确认仅清除与本次提交相同的输入，避免覆盖用户后来输入的草稿。

频道表单只发送动作，控制器负责请求和刷新。写操作串行执行，权限变化触发的刷新不会因为已有请求忙碌而被丢弃；取消协程继续传播。转让和删除使用后端返回的所有者身份，不能把权限位 `127` 当作所有者。

消息菜单、确认弹窗和评论操作共用同一权限策略：频道独立授权、本人消息、群管理权限、已删除消息和不可编辑的机器人／镜像消息均集中判断。确认界面始终读取当前权限，服务端仍负责最终授权。

## 回归方式

```powershell
cd src/LittleLoop.Android
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --offline --console=plain
```

新增 9 项 JVM 测试覆盖权限与状态保存。原有 `tests/AndroidUiAudit` 脚本复用真实调试 APK、ADB 触控和 UI hierarchy；接口用于建立合成账号及验证后台效果。`ANDROID_AUDIT_OUT=data/android-refactor` 将本轮截图、XML 和结果与上轮隔离。`refactor.py` 补充状态恢复、会话切换和频道控制器写操作。

已完成构建、9 项 JVM 测试和 lint（0 errors、39 warnings，与上轮数量一致）。小屏、手机、平板、低高度横屏各 19 项会话／权限组合，合计 76/76 通过；范围与上一轮主矩阵一致。14 项真实消息操作通过，包括空成员建群、后续邀请、编辑／删除／引用／置顶／转发／收藏、权限即时撤销和离线恢复发送。

会话保存键的稳定性调整后，重新构建并运行单元测试，同时在最终 APK 上执行状态恢复与频道写操作，以及附件、评论和机器人入口回归；没有把之前 APK 的状态恢复结果替代最终版本验证。

最终 APK 的 13 项补充回归全部通过：配置变化保留草稿、切换会话隔离草稿、保存频道资料、授予／撤销管理员权限、移除订阅者、加载管理日志、关联／解除讨论群、取消／确认转让、取消／确认删除并保留讨论群。频道写操作均在新建合成会话上通过原生界面完成，再通过真实接口核对结果。

附件经系统选择器上传、预览、下载成功，下载内容与原文件逐字节相同。评论发送／关注／关闭／重开／禁言／解除禁言共 6 项通过；申请加入讨论群、横屏审批入口、审批弹窗、确认加入共 4 项通过。

机器人卡片的 16 种尺寸／角色组合通过，使用合成响应验证原生展示、源码／补丁／回执展开和审批按钮权限，未提交真实机器人任务或审批。历史会话入口及详情的 4 种尺寸也通过合成展示检查，未执行历史数据导入。

测试中排除了旧登录状态、中文输入法全角字符、系统文件选择器语言／用户存储和 ADB 端口映射丢失造成的脚本中断，受影响用例重新执行后才计入结果。回归辅助函数增加独立输出目录、等待真实应用窗口以及空文本清除支持。

结束后恢复屏幕尺寸、密度、字体和原有代理；两台辅助模拟器切回原主用户并停止隔离测试用户，合成响应服务已关闭。

本地证据保存在忽略目录 `data/android-refactor/`，包含截图、UI XML、各项 JSON 结果和 `regression-summary.json`。最终 APK SHA-256 为 `8fcf0f14e54261b82b79f43d7f03beb1a6f864c27f97f713aeeebaac20671204`。本轮为 Android 16/17 模拟器回归，未重新验收物理设备或推送送达；先前的 64 项首页／设置等周边布局检查没有整套重跑，本轮集中验证发生重构的会话与频道管理边界。
