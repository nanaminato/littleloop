# 阶段 A · 多人聊天

聊天入口是 LittleLoop.Chat，默认监听 `http://0.0.0.0:5190`（所有 IPv4 网络接口）。本机访问 http://127.0.0.1:5190/chat；同一局域网的手机访问 `http://<电脑局域网 IP>:5190/chat`。可通过 `Chat__Url` 覆盖监听地址。它和旧工作台共用 Angular 构建，分别由独立 ASP.NET Core 进程提供入口。

## 启动

准备 MySQL 8.4 数据库，连接账号需能建表和读写。首次启动建立账户、会话、成员和消息表。在仓库根目录设置：

    npm --prefix src/LittleLoop.Workbench/Client ci
    npm --prefix src/LittleLoop.Workbench/Client run build
    $env:Chat__Client = (Resolve-Path src/LittleLoop.Workbench/Client/dist/littleloop-workbench/browser).Path
    $env:Chat__Keys = Join-Path (Get-Location) 'data/chat-keys'
    $env:Chat__Invite = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $env:ConnectionStrings__Chat = '<本地 MySQL 连接串>'
    dotnet run --project src/LittleLoop.Chat

分享邀请码给受邀者。浏览器选择注册，然后用独立账号和密码登录。私聊填写另一位用户的账号。建群填写群名和一个或多个已注册账号。点开会话查看历史与回复并更新已读；超过一页时可加载更早消息。当前邀请口令属于部署，由服务配置统一轮换。

## 同步和权限

MySQL 保存消息序号及成员的单调已读游标。消息以会话、发送人和客户端消息 ID 唯一约束。发送事务串行分配会话内序号，重试返回原消息，重复 ID 换内容返回冲突。近期历史与重连补齐使用同一排序和游标。

SignalR 在提交后向成员推送会话更新，客户端通过鉴权 HTTP 读取持久化消息；重连后从 MySQL 获取缺失记录。连接由账号 Cookie 验证，接收者由服务端的成员关系决定。当前采用单进程通知和持久化历史补齐，适合本机阶段 A；持久化通知队列和跨实例广播留待下一步。群成员在创建时确定。保存密钥目录以便重启后继续识别会话 Cookie。

## 实测

2026-11-15，使用 Chrome 的三个独立浏览器上下文和 MySQL 8.4.13。在名为「RelaxKonOS 项目群」的测试群与一个私聊中验证：

- 两个账号双向实时收发；私聊回复引用正确，未读出现并在打开会话后清除。
- 非成员的会话列表为空；已知会话 ID 也无法读历史、发送或设置已读，SignalR 未送出相关通知。
- 模拟数据库已提交但客户端响应丢失。点击重试后只保留一条，同 ID 换内容返回 409。
- 断开一个客户端期间发送两条消息。重连补齐，两条分别只出现一次。
- ASP.NET Core 从 PID 8024 重启到 31768，重新登录后两条私聊和五条群聊历史一致，再发一条群消息仍实时到达。

本机截图保存在 Git 忽略的 data/chat-phase-a 下。此次验收验证普通聊天，LittleLoop 卡片和历史工作台导入属于阶段 C。设计见 [聊天平台升级](23-chat-platform-design.md)。
