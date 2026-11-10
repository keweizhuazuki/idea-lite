# 贡献指南

[English](CONTRIBUTING.md) · [项目主页](README.zh-CN.md)

欢迎提交 Issue 和 Pull Request。优先改进真实的代码审查流程，保持默认工作台轻量。

## 准备开发环境

在 Windows x64 上安装 .NET 10 SDK 和 Git，然后在仓库根目录运行以下命令。

```powershell
dotnet restore IdeaLite.slnx --locked-mode
dotnet build IdeaLite.slnx -c Release --no-restore
dotnet run --project tests/IdeaLite.Tests -c Release --no-build
dotnet run --project tests/IdeaLite.UiChecks -c Release --no-build
dotnet run --project src/IdeaLite -- 'D:\path\to\project'
```

`IdeaLite.Core` 包含无 UI 依赖的核心逻辑，`IdeaLite` 包含 WPF 工作台。集成检查使用临时仓库和本地 bare remote，无需访问外部 Git 服务器。失败时会保留测试现场。如需保留成功现场，传入 `-- --keep`。

## 提交改动

- 写清问题的复现步骤、修改后的行为和实际验证结果。
- Git、编码、冲突处理和路径边界的改动应补充行为测试，界面改动请附截图。
- 请确保 Issue、测试数据和截图中没有公司源码或凭据。

运行 `scripts/Build.ps1` 可生成依赖运行时的 ZIP 和自带运行时的便携 ZIP。报告包体积或内存占用时，请注明项目规模和测量方法。
