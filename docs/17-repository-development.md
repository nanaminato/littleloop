# 多文件真实项目开发

本阶段将开发闭环应用到 LittleLoop 自身的 C# 多项目代码。模型在受限副本中定位 CLI 入口、修改两个文件、运行真实 .NET 构建及行为验收；合并前由宿主审查差异。它仍是有界开发执行器，不代表任意仓库任务都能成功。

## 新能力

- `project.inspect(prefix, offset)`：分页列出目录中的源文件、写权限、项目类型及 `.csproj` 声明的 ProjectReference。XML 禁止 DTD 和外部解析，不运行 MSBuild；条件引用仅是声明证据。
- `project.search-page(query, prefix, offset)`：限定子树的字面搜索，返回行号和下一页位置，不再只能看到首批结果。
- `project.read-range(path, startLine, lineCount)`：读取指定行段及完整文件哈希，可定位长文件后部。行号从 1 开始，最多 80 行；工具参数中的数值使用字符串。
- 每轮规划保留累计修改文件、已观察哈希和测试证明；旧回执退出最近窗口后，不丢失跨文件修改清单。截断内容明确标记，长输出保留首尾，命令错误单独提供片段。
- 命令可配置 `description` 和 `relatedPaths`，帮助模型选择相关检查的顺序。它们只是提示，不授权新增命令，也不免除其他必需测试。

宿主仍指定可写文件、固定可执行文件和参数。多文件依次修改，每个补丁独立做哈希校验和回读；不是跨文件原子事务。测试证明绑定整个读取范围的最终快照，后续任一修改都会使旧证明失效。所有 `isTest: true` 命令都必须最终通过。模型可以追问、调整计划或拒绝，角色核心可独立否决。

受保护验收文件在目标开始时记录 SHA-256。工具无法修改它们，恢复及模型审议后还会检查基线；如果测试被外部改变，则停止，不能利用改过的测试宣称成功。旧运行状态没有这项基线时需新建目标，不能补造历史证明。新增策略字段可能改变旧策略哈希；旧审计文件仍保留，不直接重写以强行恢复。

## 真实项目实验

2026-11-14 的 `data/repository-demo-v2` 保存了实际 DeepSeek 规划和执行记录。自然语言要求：为 LittleLoop 增加只读 `dev-map <policy.json>`，不依赖学习状态或模型服务，保留既有命令、处理空参数并更新帮助。没有提供要修改的代码位置或补丁。

模型执行 20 个工具动作，修改 `DevelopmentCommand.cs` 和 `Program.cs`。先复现缺失功能的验收失败，随后一次精确补丁未匹配、一次 C# 局部变量重名导致构建失败；模型依据实际回执修正，最终构建成功，8 项行为验收通过，测试源码未变。审查后的修改已纳入本项目。

首轮 `data/repository-demo` 因清空环境后 NuGet 缺少系统目录信息而失败，核心拒绝原样重跑。宿主据此补充固定 Windows 安装目录变量，以及项目内临时 HOME、AppData、NuGet 缓存；不继承用户 PATH、模型密钥或用户配置目录。第二轮完整记录保留了正常的业务失败恢复，首轮失败没有删除或改写。

## 使用

```powershell
dotnet build src/LittleLoop.Cli -m:1 --disable-build-servers
./examples/development/prepare-repository.ps1 -Destination data/my-repository-task
dotnet run --no-build --project src/LittleLoop.Cli -- dev-map data/my-repository-task/policy.json
dotnet run --no-build --project src/LittleLoop.Cli -- learn-init data/my-repository-task/state.json
dotnet run --no-build --project src/LittleLoop.Cli -- tasks-enable data/my-repository-task/state.json data/my-repository-task/limits.json
# 为当前待开发功能提供自然语言目标：
dotnet run --no-build --project src/LittleLoop.Cli -- dev-run data/my-repository-task/state.json data/my-repository-task/policy.json data/my-repository-task/model.json task-id "你的开发目标"
```

准备脚本复制当前真实源文件，不复制凭证、Git 元数据或构建缓存；已有目录不覆盖。当前版本已经包含 dev-map，因此重复该目标不会再复现历史缺失功能。新任务应配置相应可写范围与独立验收；原加法示例继续保留作为最小诊断案例。

`dev-map` 输出第一页结构 JSON，不执行任何命令或调用模型；分页浏览在开发工具中通过 `nextOffset` 继续。项目策略依然要满足原开发策略约束（包括测试命令），不是新的无限制目录读取入口。

主回归：`dotnet run --project tests/LittleLoop.Checks`。CLI 行为验收：

```powershell
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
dotnet run --project tests/LittleLoop.RepositoryChecks -p:BuildInParallel=false -- data/my-repository-task/policy.json $dotnet
```

命令执行仍不是 OS 沙箱。项目代码可访问外部文件或网络；固定环境和测试基线只能降低部分风险、检测部分变化，不能隔离恶意程序。依赖下载、任意 shell、自动扩大权限、自动提交 Git、跨文件自动回滚均未开放。文件数量/大小、预算、模型上下文、超时仍有明确上限；未知副作用仍需对账。
