# 参考与致谢

## pcl-patcher

- 仓库：https://github.com/Ryorin1337/pcl-patcher
- 许可证：MIT，Copyright (c) 2026 Ryorin1337

本项目参考了它的补丁目标划分，实现其中 4 处核心补丁：

- `PageDownloadInstall.LoaderInit` 的 loader 绑定
- `PageDownloadModify` 的 `CardMinecraft` 可见性
- `ModpackInstall(File, InstanceName, Logo)` 的整合包分发
- `ModpackInstall()` 的文件选择入口

`RestrictMcDownload` 是编译期常量，本项目不在运行期修改它，因此不属于已实现的补丁。
运行期实现（dnlib 语义定位、MetadataToken 映射、Harmony、原生跳转）为独立编写，
没有复制 pcl-patcher 的源码。2.99.0.1 的 6 处 UI 补丁由本项目自行分析 IL 实现。

## PCL

- 仓库：https://github.com/Meloong-Git/PCL
- 作者：龙腾猫跃
- 赞助：https://meloong.com/afd/a/LTCat

本项目不包含任何 PCL 源代码，所有 C# 代码为独立编写。
为保持行为兼容，复现了 PCL 的整合包类型判定顺序和少量提示文案（短语级）；
这些行为与文案的权益归 PCL 作者所有，本项目按《PCL 存储库合理使用指南》使用。

本项目是**第三方基于 PCL 独立进行的二次创作**，与 PCL 官方无关；
PCL 的许可与合理使用指南见 [PCL-LICENCE.md](PCL-LICENCE.md)。

## 依赖

- dnlib：MIT
- Lib.Harmony：MIT

两个依赖由 `fetch-deps.ps1` 从 NuGet 获取，不随仓库分发。
