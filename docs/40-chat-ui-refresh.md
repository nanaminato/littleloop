# 对话界面组件化与多语言刷新

状态：已实现。范围是 [Telegram 式聊天界面](29-telegram-ui-implementation.md) 的 Web 客户端与 [原生 Android 客户端](31-android-chat.md)。本机验收截图位于 Git 忽略的 `data/ui-refresh/`。

## Web 客户端

原 `chat.ts` 同时承载页面状态、实时同步、表单与全部模板。本次拆成：

- `chat-store.ts`：账号、房间、消息、SignalR、发送队列、搜索、机器人和会话设置唯一的可写状态源。
- `chat.ts` + `chat.html`：只负责外壳、主题属性和子组件编排。
- `chat-sidebar`、`chat-thread`、`chat-message`、`chat-info`、`chat-drawer`、`chat-friends`、`chat-auth`：各自拥有模板和样式，通过注入 `ChatStore` 读取状态和调用动作。
- `chat-ui.css`：跨组件共享的设计令牌、按钮、输入框、头像、菜单与响应式栅格。组件样式只描述自身布局。

视觉上统一了浅色/深色、三种强调色、三种聊天背景和字号缩放；会话列表使用品牌头、圆角搜索、文件夹胶囊和悬浮新建按钮；消息区使用分组气泡、日期胶囊、独立时间线和悬浮输入卡；详情、主菜单、好友与设置改为分层卡片。桌面仍为双栏/三栏，窄屏按列表、会话、详情逐屏切换。

## 多语言

Web 由 `chat-i18n.ts` 提供 `ChatI18n` 服务和语言字典。中文是源文案键，英文缺失时自动回退到中文，避免暴露内部消息 id。支持跟随系统、简体中文和 English；切换即时生效并同步推送订阅语言。日期、时间和数字使用 `Intl` 按当前语言格式化。主菜单内提供快捷语言切换，设置中的「语言」页保持原有入口。

服务端返回的错误码 `invalid_username`、`username_taken` 等在中英文下分别映射；用户输入、群名、消息正文和工具回执保持原文。

## Android 客户端

`MainActivity` 只保留 Activity 生命周期、主题、语言上下文和通知入口。界面拆分到：

- `ChatApp`：返回栈、搜索、设置、好友、频道、话题和会话路由。
- `ChatHomeScreen`、`ConversationScreen`、`SettingsScreen`、`LoginScreen`：四个主页面。
- `ChatUi`：顶栏、品牌标记、头像、房间行、分组卡片、开关、空状态和日期格式化。
- `ChatTheme`：Material 3 浅色/深色配色、圆角和字体；补齐 `surfaceContainer*` 以免菜单和对话框落到默认紫色。
- `ChatDialogs`、`ChatSpaces`、`ChatSocial`、`MessageSearch`、`MessageActions`、`BotCards`、`ChatAttachments`：沿用原职责并接入统一组件。

会话页把静音、话题、成员和机器人入口收进顶栏溢出菜单；消息使用双侧分组气泡、日期分隔、回复/菜单触控区和独立输入卡。首页使用搜索、快捷动作胶囊、未读徽标和扩展新建按钮。

## Android 多语言

默认资源 `res/values/strings.xml` 为英文，`res/values-zh/strings.xml` 为简体中文；系统语言为中文时自动使用中文。新增「设置 → 语言」的跟随系统、简体中文和 English 三项：

- `Localization` 读取 `chat_preferences` 中保存的语言，按需创建带 Locale 的 `Context`。
- `MainActivity.attachBaseContext` 应用该上下文，切换语言后 `recreate()`。
- 仓库错误、通知渠道与前台服务文案通过同一 `Localization` 获取当前语言。
- 房间摘要新增 `previewDeleted` 解码，删除提示不再依赖服务端中文文案。

机器人卡片、附件、搜索、好友与群成员对话框的固定文案均已移入资源。用户输入、群名、消息正文和服务端原文不翻译。

## 验收

- Web：`npm run build` 通过；Chrome/Playwright 在 1440×900 与 390×844 下登录现有 MySQL 实库，检查中文、英文、浅色、会话、详情、主菜单、设置和移动端返回；英文界面固定文案未发现漏译（仅剩用户创建的文件夹名与「简体中文」选项）。
- Android：`:app:assembleDebug` 与 `:app:lintDebug` 通过；Android 17 模拟器用 `alice_settings` 登录，检查首页、会话、设置、溢出菜单、中文/English 切换和浅色/深色；语言重启后保持。
- 证据：`data/ui-refresh/` 内的桌面、移动端与 Android 截图。

## 后续边界

### Android 会话紧凑布局调整（2026-11-15）

根据真机截图反馈，移除消息内常驻的回复和更多按钮，时间与编辑标记靠右排列，短消息不再被操作栏撑宽。长按消息打开操作菜单，回复入口移入其中，并保留无障碍菜单操作。附件显示独立图标、文件名和本地化文件大小，点击仍打开现有预览/保存窗口。输入栏改为无边框多行输入和普通发送按钮，减少高度与阴影；标题和副标题相同时省略副标题。

本轮调试版、发布版构建及两种构建的 Lint 均通过。Android 模拟器覆盖安装后，使用既有测试账号 alice_settings 的个人收藏，实测发送 hello 和 44 B 测试附件；检查英文深色及中文浅色布局、长按菜单边界和回复入口。长按菜单完整显示，点击回复后关闭菜单并显示引用栏。实测发现收藏页跨语言副标题仍重复，按 saved 房间类型隐藏后重新构建、Lint 并覆盖安装复验通过。本轮未执行菜单中的删除、编辑或转发操作，也未在实体手机复验。

证据位于 Git 忽略的 `data/ui-refresh/compact-chat/`：`layout-final.png` 为中文浅色消息和附件布局，`menu-final.png` 为长按菜单，`reply-final.png` 为回复引用栏；`layout-dark.png`、`long-press.png` 和 `reply.png` 保留深色英文检查画面（副标题修复前）。对应 `*-full.png` 为原始分辨率截图。

尚未加入第三种语言。新增语言时，Web 只需在 `chat-i18n.ts` 增加一份以中文为键的字典并在语言选择器注册；Android 增加 `values-<locale>/strings.xml` 并在 `Localization` 中声明语言标签即可。服务端公告、机器人生成的正文和用户内容仍以原文显示。
