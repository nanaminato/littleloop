# LittleLoop 的项目构建模型

本目录使用 Singularity 0.4 / format 3，模型为 [model.json](model.json)。工具链作为 Git submodule 固定在 `tools/singularity` 的具体提交；不再依赖相邻目录，也不在每次查询时下载最新工具。

## 首次安装与版本

需要 Python 3.11+；仅开发阶段的项目模型工具需要 Python，LittleLoop 的 C# 运行时不依赖它。

```powershell
# 新工作区
git clone --recurse-submodules https://github.com/nanaminato/littleloop.git
cd littleloop

# 已有工作区，或主仓库更新了工具版本后
git submodule update --init --recursive

# 查看固定版本和实际版本（前缀 + 表示实际版本偏离固定版本）
git ls-tree HEAD tools/singularity
git submodule status tools/singularity

python tools/model.py validate
python tools/check_model.py
```

子模块安装需要一次网络访问；后续 CLI 查询、校验、事务写入均在本地运行。克隆 ZIP 不包含子模块内容，请用 Git 克隆。不要用 `git submodule update --remote` 隐式追踪最新版。升级工具时应明确选择并审查上游提交、运行测试，再提交 LittleLoop 的 gitlink 变化。根目录启动脚本从自身位置确定默认项目，所以从其他目录调用也不会误改另一个项目；高级用途可显式传 `--project <path>`。

## 按需读取

```powershell
python tools/model.py find "SQLite" --limit 10
python tools/model.py context MOD-CHAT-COMMITMENTS --summary --max-nodes 12 --max-bytes 8192
python tools/model.py get MOD-CHAT-COMMITMENTS
python tools/model.py impact IFACE-EVENT --depth 3
python tools/model.py tree VIEW-SYSTEM
```

从摘要找到稳定 ID，再按需取正文；不要每次都将整个模型放入上下文。预算截断不是“其他约束不存在”，必须关注省略提示、授权依据、验证依据和冲突。需要完整快照时仍可显式使用 `list`；旧命令保持兼容。

模型描述如何构建认知系统，不保存运行时认知记忆。当前覆盖初始认知循环/事件契约/重放规划，以及插件、角色审议、执行反馈学习、多任务运行时、真实工作区工具、多步目标规划、自然语言追问和可替换大模型语义适配器的增量实现与验证链，未宣称覆盖全部模块。

从现有文档提取的意图保持 candidate；后续用户明确确认的方向单独记录确认依据。初始任务和实验节点保留原规划，后续实现节点依据实际检查记录状态。运行命令见项目 README 及各增量设计文档。EVIDENCE-DOC 仅说明现有文档包含某项设计，不证明功能已实现。

修改使用 apply 批量事务与 expected revision。新的人类确认必须有明确依据。发现表达困难时，将实际案例反馈到 singularity 模型、查询或编辑器的改进中。

## Agent 阅读与修改顺序

1. 先读项目 README 和与任务相关的设计文档，再按 ID 用 `get` 读取单个节点、关系、文件引用或树视图。需要发现对象时用 `find`，需要沿关系找上下文时用 `context`，需要看父子层级时用 `tree <视图 ID>`。
2. 使用 `schema` 确认节点类型字段，用 `check-refs` 检查项目内文件路径。结合实际文件判断内容和验证状态。
3. 修改时记录 `get` 或 `validate` 返回的 `revision`。编辑完整对象，将其放入 `put` 操作的 `value` 字段，并将操作数组保存为 JSON。集合名为 `nodes`、`relations`、`references`、`views`。
4. 先运行 `apply operations.json --expected <revision> --dry-run` 查看结果，再用同一 revision 正式写入。若版本冲突，重新读取并审查差异后再提交。
5. 写入后用 `get` 逐项回读，再运行 `validate`。改到树或文件引用时，再运行相应的 `tree` 或 `check-refs`。


## 写入与验证

准备操作文件只是在内存或独立文件中构造修改，不是直接重写 `model.json`。例：

```json
[
  {
    "action": "put",
    "collection": "references",
    "value": {
      "id": "REF-EXAMPLE",
      "kind": "project_file",
      "target": "README.md",
      "purpose": "项目入口",
      "tracking": "live"
    }
  }
]
```

```powershell
python tools/model.py validate
python tools/model.py apply operations.json --expected <revision> --dry-run
python tools/model.py apply operations.json --expected <revision>
python tools/model.py get REF-EXAMPLE
python tools/check_model.py
python -m unittest discover -s tests/model_tools -v
```

事务默认 `actor=AI`。修改已确认意图需要真实授权及依据，不能为了让命令通过而虚构 `--actor Human --basis`。人类意图和 AI 工程记录应按相应身份分批处理。角色字段不是身份认证，验证成功也不是授权证明。

CI 校验模型结构、本地文件引用和工具链事务测试，不能证明历史或当前变更一定经由 CLI，也不能证明引用里的测试结论真实。Git 历史保留模型变化，但现有格式没有不可篡改的写入入口审计。上游 [LittleLoop 验证记录](https://github.com/nanaminato/singularity/blob/ef44dd3363de1c5c45f8bc570c660712fdc99760/docs/littleloop-validation.md) 记录了 2026-11-14 拆分 Human/AI 事务及逐项回读的实例；这支持该实例使用了工具链，不能外推所有历史写入方式。

模型只保存有长期作用的意图、边界、关系、决策和证据索引。详细操作步骤、完整测试输出和重复运行结果留在设计文档或报告中。不得仅为减小文件而删除授权来源、未解决约束或验证差异。
