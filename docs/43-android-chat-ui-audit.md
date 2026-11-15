# Android 对话界面审查（2026-11-15）

本轮是用户明确要求的原生 Android 全面审查，独立于 [Web 审查](42-chat-ui-audit.md)。使用实际调试 APK、ADB 触控和系统 UI hierarchy 检查 Compose 页面；接口只用于生成合成账号、会话和验证操作结果。没有用 Web 页面替代 Android 验证。

## 修复范围

- 切换账号时清空设置、搜索、好友、目录、成员和创建等导航状态，避免新账号进入上个账号残留页面。
- 新建会话、频道／话题、消息操作和频道评论弹窗允许内容滚动，保留确认／取消按钮，适应低高度横屏及键盘。
- 好友操作移到姓名下方；邀请／添加账号输入框使用完整宽度；好友和成员列表占用剩余可用高度，保留加入申请入口。
- 频道目录的订阅和打开操作移到名称下方，避免长名称和大字体被按钮挤压。
- 主题和语言选项自动换行。首页连接状态放到标题下方，避免大字体把应用名称拆成两行。
- 频道署名限制宽度并省略，保留时间标记。
- 机器人源码／补丁／回执证据、源码文件选择和转发目标使用适合多行内容的小圆角按钮，修复长路径被胶囊圆角裁切的问题。
- 已打开的编辑／删除确认重新检查最新权限；频道设置在权限改变后重新加载，讨论管理直接依赖当前权限；成员操作确认重新检查群主专属权限。评论删除复用同样的消息操作校验。
- 空成员建群和群管理员删除他人普通消息承接上一轮修复，本轮继续做原生验证。

## 检查矩阵

19 种会话／角色组合：私聊、个人收藏；群聊和话题各包含群主、管理员、普通成员、禁言成员；频道包含所有者、零权限订阅者，以及发布、编辑、删除、置顶、资料、订阅者、讨论七种单项授权。

| 配置 | 像素与密度 | 逻辑尺寸 |
| --- | --- | --- |
| 小屏 | 640×1136 / 320 dpi | 320×568 dp |
| 常规手机 | 1080×2400 / 420 dpi | 约 411×914 dp |
| 平板 | 1600×2560 / 320 dpi | 800×1280 dp |
| 横屏低高度 | 1280×720 / 320 dpi | 640×360 dp |

主矩阵检查时间线、可发言状态、消息菜单权限、会话菜单、群成员入口、频道设置全部滚动区域及评论。周边检查首页、好友、频道目录、新建私聊／群聊／频道／话题、话题列表、搜索空态和结果、设置各区；小屏周边检查使用 1.3 倍系统字体。

## 实测结果

- 三台模拟器使用 Android 16／17。主矩阵 **76 / 76** 组合通过，周边页面记录 **64** 个状态。所有者横屏设置实际滚动到底；建群成员框和可选说明另外滚动到底截图，不能仅以标签露出作为可达证据。
- **14** 项真实动作／状态通过：空群创建、随后邀请、发送、编辑、回复、置顶、收藏、转发、删除、管理员删除他人消息、打开编辑后撤权、频道编辑、断网入队、恢复连接补发。结果通过真实测试服务查询核对。
- **6** 项评论检查通过：发送、关注、关闭、重开、讨论群禁言、解除禁言。频道发帖权限与讨论群发言权限分别验证。
- **4** 项加入讨论群检查通过：非成员订阅者申请、横屏成员页申请入口、审核弹窗、群主实际批准；审批后核对群成员列表。
- 附件经 Android 系统文件选择器实际上传、原生文本预览、系统保存对话框下载。以 `adb exec-out` 获取原始字节与源文件比对一致；普通 `adb shell` 会转换输出换行，不能用于二进制一致性判断。
- 中文深色、英文浅色和小屏 1.3 倍字号截图已检查。账号退出再登录回到首页，未保留旧设置页面。
- 机器人问答、引用、任务及审批者／阅读者共有 **16** 个合成原生渲染场景，另打开四档尺寸的请求表单与源码选择器，展开源码、补丁、回执。未向真实机器人提交任务或审批。
- 历史工作台会话另外检查 **4** 档尺寸的原生时间线和任务详情，使用合成历史房间响应；不把它计作真实历史数据导入验收。

构建 `:app:assembleDebug` 和 `:app:lintDebug` 通过；lint 为 **0 errors / 39 warnings**，包含既有资源、依赖版本、SharedPreferences 与 Firebase 生命周期提示，不把它描述成零警告。APK 已安装到三台测试模拟器。原测试用户之外另建隔离 Android 用户，保留原用户账户数据；一台模拟器遗留 HTTP 代理造成的 403 已排查，不作为产品缺陷。

## 重跑与证据

脚本位于 `tests/AndroidUiAudit/`，截图、XML、结果 JSON 位于忽略目录 `data/android-ui-audit/`。凭证只从既有 `data/chat-phase-a/chat.local.env` 读取，不写入报告。

从仓库根目录执行，需要测试服务、ADB、已安装 APK 和合成测试账号：

```powershell
$env:PYTHONPATH='tests/AndroidUiAudit'
$env:ANDROID_SERIAL='emulator-5558'
python -X utf8 tests/AndroidUiAudit/fixtures.py
# 先在应用中登录 fixtures.json 中的 view 账号，再运行：
python -X utf8 tests/AndroidUiAudit/matrix.py
python -X utf8 tests/AndroidUiAudit/extras.py
python -X utf8 tests/AndroidUiAudit/actions.py
python -X utf8 tests/AndroidUiAudit/attachments.py
python -X utf8 tests/AndroidUiAudit/comments.py
python -X utf8 tests/AndroidUiAudit/joins.py
```

`ANDROID_AUDIT_BASE` 默认 `http://127.0.0.1:5193`；`AUDIT_SIZE` 可选择 `small,phone,tablet,landscape`。不要在同一设备同时执行两个 UI 脚本，也不要把修改角色的动作测试与同账号权限矩阵并行执行。矩阵结果是会话／角色／尺寸组合，不等于独立业务测试数量。

机器人代理 `bot_proxy.py` 只提供合成渲染响应，并拒绝任务执行和审批提交。机器人视觉结果不能作为真实任务执行或审批成功的证据。实体设备、系统推送实际送达不属于本轮模拟器 UI 证据。

结果文件为 `matrix-{small,phone,tablet,landscape}.json`、`extras-result.json`、`actions-result.json`、`attachment-result.json`、`comments-result.json`、`joins-result.json`、`bot-result.json`、`legacy-result.json`。对照截图包括 `baseline-landscape-create.png` / `landscape-create-group-bottom.png`、`baseline-small-friends.png` / `small-friends.png`、`baseline-bot-long-path.png` / `bot-small-question-reader.png`、`small-zh-dark-conversation.png`。
