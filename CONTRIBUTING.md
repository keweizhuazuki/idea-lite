# Contributing

欢迎提交 issue 和 PR。优先改进真实的代码审查场景，避免默认引入常驻 JVM、浏览器内核或无限制索引。

1. 安装 Windows x64、.NET 10 SDK 和 Git。
2. `dotnet restore IdeaLite.slnx --locked-mode`
3. `dotnet build IdeaLite.slnx -c Release`
4. `dotnet run --project tests/IdeaLite.Tests -c Release`
5. `dotnet run --project src/IdeaLite -- "D:\path\to\project"`

提交时请说明触发问题的操作、修改后的行为，以及验证结果。Git、编码、冲突和路径边界的更改应增加真实行为测试；UI 调整请附截图。不要在 issue、fixture 或截图中加入公司源码和凭据。

项目分为无 UI 依赖的 `IdeaLite.Core`、WPF 工作台 `IdeaLite`，以及无需测试框架依赖的集成测试执行器。测试会创建本地临时仓库和 bare remote，不访问外部 Git 服务器。成功后只清理本次创建的目录，失败时保留现场；使用 `--keep` 可保留成功现场。

发布脚本 `scripts/Build.ps1` 生成依赖 .NET Desktop Runtime 的小包和自带运行时的便携包。包的大小和内存数据请实测，并写明项目规模和测量口径。
