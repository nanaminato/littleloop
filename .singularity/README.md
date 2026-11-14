# LittleLoop 的项目构建模型

本目录使用 Singularity 0.4 / format 3，模型为 [model.json](model.json)。协议与工具位于相邻的 singularity 仓库；该相邻路径是本地工作区约定，不是跨机器定位保证。

```powershell
python ../singularity/tools/singularity.py --project . validate
python ../singularity/tools/singularity.py --project . context IFACE-EVENT --depth 2
python ../singularity/tools/singularity.py --project . impact IFACE-EVENT --depth 3
python ../singularity/tools/singularity.py --project . tree VIEW-SYSTEM
```

模型描述如何构建认知系统，不保存运行时认知记忆。当前只建立首条认知循环/事件契约/重放验证链，未宣称覆盖全部模块。

从现有文档提取的意图保持 candidate。任务和实验均为规划，未创建 C# 实现，也未执行认知实验。EVIDENCE-DOC 仅说明现有文档包含某项设计，不证明功能已实现。

修改使用 apply 批量事务与 expected revision。新的人类确认必须有明确依据。发现表达困难时，将实际案例反馈到 singularity 模型、查询或编辑器的改进中。
