# LittleLoop 的项目构建模型

本目录使用 Singularity 0.4 / format 3，模型为 [model.json](model.json)。协议与工具位于相邻的 singularity 仓库；该相邻路径是本地工作区约定，不是跨机器定位保证。

```powershell
python ../singularity/tools/singularity.py --project . validate
python ../singularity/tools/singularity.py --project . context IFACE-EVENT --depth 2
python ../singularity/tools/singularity.py --project . impact IFACE-EVENT --depth 3
python ../singularity/tools/singularity.py --project . tree VIEW-SYSTEM
```

模型描述如何构建认知系统，不保存运行时认知记忆。当前只建立首条认知循环/事件契约/重放验证链，未宣称覆盖全部模块。

从现有文档提取的意图保持 candidate。图中的任务和实验状态仍记录原规划。首阶段模拟工单闭环及运行命令见项目 README。EVIDENCE-DOC 仅说明现有文档包含某项设计，不证明功能已实现。

修改使用 apply 批量事务与 expected revision。新的人类确认必须有明确依据。发现表达困难时，将实际案例反馈到 singularity 模型、查询或编辑器的改进中。

## Agent 阅读与修改顺序

1. 先读项目 README 和与任务相关的设计文档，再按 ID 用 `get` 读取单个节点、关系、文件引用或树视图。需要发现对象时用 `list`，需要沿关系找上下文时用 `context`，需要看父子层级时用 `tree <视图 ID>`。
2. 使用 `schema` 确认节点类型字段，用 `check-refs` 检查项目内文件路径。结合实际文件判断内容和验证状态。
3. 修改时记录 `get` 或 `list` 返回的 `revision`。编辑完整对象，将其放入 `put` 操作的 `value` 字段，并将操作数组保存为 JSON。集合名为 `nodes`、`relations`、`references`、`views`。
4. 先运行 `apply operations.json --expected <revision> --dry-run` 查看结果，再用同一 revision 正式写入。若版本冲突，重新读取并审查差异后再提交。
5. 写入后用 `get` 逐项回读，再运行 `validate`。改到树或文件引用时，再运行相应的 `tree` 或 `check-refs`。
