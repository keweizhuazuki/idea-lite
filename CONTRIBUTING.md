# Contributing

[简体中文](CONTRIBUTING.zh-CN.md) · [Project README](README.md)

Issues and pull requests are welcome. Focus on real code review workflows and keep the default workbench lightweight.

## Set up the project

Install the .NET 10 SDK and Git on Windows x64, then run the following from the repository root.

```powershell
dotnet restore IdeaLite.slnx --locked-mode
dotnet build IdeaLite.slnx -c Release --no-restore
dotnet run --project tests/IdeaLite.Tests -c Release --no-build
dotnet run --project tests/IdeaLite.UiChecks -c Release --no-build
dotnet run --project src/IdeaLite -- 'D:\path\to\project'
```

`IdeaLite.Core` contains the logic without UI dependencies. `IdeaLite` contains the WPF workbench. The integration checks create temporary repositories and local bare remotes, without contacting external Git servers. Failed runs keep their fixtures. Use `-- --keep` to keep fixtures from successful runs as well.

## Submit a change

- Include the steps that reproduce the problem, the resulting behavior, and what you verified.
- Add a behavior test for changes to Git, encoding, conflict handling, or path boundaries. Include a screenshot for a UI change.
- Keep company source code and credentials out of issues, fixtures, and screenshots.

Run `scripts/Build.ps1` to create both the framework-dependent ZIP and the portable ZIP. When reporting package size or memory use, include the project size and measurement method.
