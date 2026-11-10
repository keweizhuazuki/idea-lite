# Idea Lite

**A lightweight Windows code editor and Git workbench with an IntelliJ IDEA–style layout.**

[简体中文](README.zh-CN.md)

[![Build and verify](https://github.com/keweizhuazuki/idea-lite/actions/workflows/ci.yml/badge.svg)](https://github.com/keweizhuazuki/idea-lite/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-7de2b2)](LICENSE)
[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-8dcbe8)](https://github.com/keweizhuazuki/idea-lite/releases/latest)

Review changes, edit source files, resolve conflicts, and commit from one window. The workbench has a project tree and Commit panel on the left, file and diff tabs in the center, Maven on the right, and Git Log, Build, Services, and Terminal at the bottom.

![Commit panel, side-by-side diff, and Git Log](docs/images/git-diff.png)

![Java editor and Maven project tree](docs/images/editor-project.png)

Both screenshots use synthetic sample data.

## Download and open a project

Download a Windows x64 ZIP from [GitHub Releases](https://github.com/keweizhuazuki/idea-lite/releases/latest), extract it, and open `IdeaLite.exe`.

| Package | Requirements |
| --- | --- |
| `IdeaLite-win-x64-framework.zip` | [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `IdeaLite-win-x64-portable.zip` | Bundles the .NET runtime |

Both packages offer the same features. The release includes `SHA256SUMS.txt` for checksum verification. Install [Git for Windows](https://git-scm.com/downloads/win) for Git actions and the embedded Git Bash terminal. To execute Maven tasks, configure a JDK and Maven or use the project's Maven Wrapper.

1. Press **Ctrl+O** and select a project directory. For a Git subdirectory, Idea Lite opens the repository root. Opening another project or a recent project starts a new process and keeps the current window and drafts open.
2. Press **Shift twice** to search for files, classes, symbols, actions, or text. Click outside the popup or press **Esc** to close it.
3. Press **Ctrl+K** to open Commit. Click a changed file to review its diff and select the files to include.
4. Switch between English and Simplified Chinese from **Settings → Language** or the **EN / 中** button. The language switch preserves open documents, drafts, and layout.

You can also pass a directory on the command line.

```powershell
& 'D:\Apps\IdeaLite\IdeaLite.exe' 'D:\code\your-project'
```

## Editing and navigation

- The editor uses bundled JetBrains Mono, Java syntax highlighting, and indentation guides. It reads common `.editorconfig` settings and preserves file encoding, including BOM and GBK. The project tree groups Maven modules, Java source roots, and compact package paths.
- When tabs overflow, scroll the tab strip with the mouse wheel or arrows. Use the menu on the right to switch to any open tab, or right-click a tab to close it, close others, or close all. Bulk closing asks before discarding drafts.
- **Ctrl+click** or **Ctrl+B** jumps to an indexed definition. Click the usage count on a Java type declaration to choose a reference. Explicitly imported types can also open read-only source from a direct Maven dependency's local `-sources.jar`.
- The editor gutter marks added, modified, and deleted lines relative to Git HEAD, including unsaved changes. **File → Reload All from Disk** refreshes the file list and index while preserving unsaved edits. Search Everywhere picks up new untracked files while respecting Git ignore rules.

## Git review and branch management

- **Commit** shows staged, unstaged, unversioned, and conflicting files. A file can appear in both staged and unstaged groups. The staged diff compares HEAD with the index. The working-tree diff compares the index with the working file.
- Side-by-side diffs highlight changed characters and provide clickable overview markers. Edit the right side of a working-tree diff and press **Ctrl+S** to save. It shares the draft and undo history with the editor tab. Staged and historical snapshots are read-only.
- Check working-tree files to commit their contents. With no working-tree files checked and changes in the index, **Commit Staged** commits the index and preserves unstaged edits. **Amend last commit** fills the complete previous message and asks for confirmation before rewriting the commit.
- Select a branch in the branch popup for actions such as checkout, create, compare, merge, rebase, worktree, push, rename, and delete merged branches. Actions vary by reference type. Checkout preserves compatible local changes and stops when Git detects an overwrite risk. **Stash and Checkout** stores staged, unstaged, and untracked changes. Restore by hash from the Git menu to restore the staging state as well.
- **Git Log** shows the commit graph, branch and text filters, and historical diffs. Right-click a commit to cherry-pick it onto the current branch. The Git menu offers **Continue Cherry-pick** and **Abort Cherry-pick**. A failed push leaves local commits available in Git Log on their original branch.
- For a text conflict, open the three-pane merge editor and choose local, incoming, or both for each block. Edit the center result as needed, then use **Apply and Stage**. Continue or abort the Git operation from the Git menu.

For other Git operations, **Git → Run Git Command** accepts arguments after `git`, such as `reflog` or `blame -- src/App.java`. Use the embedded terminal for interactive commands and credential prompts.

### AI commit messages

Click the sparkle button in **Commit** to generate a draft. In **Settings → AI Commit Settings**, choose OpenAI or DeepSeek and enter the provider's API key. Each key is stored separately in Windows Credential Manager. You can choose the output language, change the model, and edit or preview the built-in prompt. The defaults are `gpt-5.4-mini` and `deepseek-flash`.

When you request a draft, Idea Lite sends the branch name, existing message, and selected diff to the provider. **Commit Staged** uses the index diff, and **Amend** adds context from the previous commit. Requests are limited to 100 files and 16,000 characters of change context, with an output limit of 256 tokens. The status bar shows usage when returned by the provider. Review the draft before committing.

The prompt supports `{task_rule}`, `{task_id}`, `{language_rule}`, and `{language}`. The task ID comes from an AIDF or AIGC key in the existing message or branch name, in that order. Remove `{task_rule}` to omit the default instruction to prefix the message with that key.

## Maven and terminal

- In **Settings → Build Tools and Environment**, choose Windows or WSL and browse for the Maven executable, `settings.xml`, local repository, and `JAVA_HOME`. WSL mode fills in Linux paths. Default goals and arguments can be set per project.
- Use the Maven tool window to run a lifecycle task. Build streams output in batches, groups modules and errors, and can jump from a Java compiler error to the source line. The toolbar can rerun or stop a task.
- Press **Alt+F12** to open Git Bash in the project directory. Click **+** to start another session. Each tab keeps its own working directory and command history. The embedded terminal uses Windows ConPTY and supports ANSI colors, Unicode, clipboard shortcuts, and Ctrl+C.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open a project in a new window |
| Shift twice / Ctrl+P | Search Everywhere |
| Ctrl+N / Ctrl+Shift+N / Ctrl+Alt+Shift+N | Search types / files / symbols |
| Ctrl+Shift+F | Find in project |
| Ctrl+F / Ctrl+H / Ctrl+G | Find / replace / go to line in the editor |
| Ctrl+B or Ctrl+click | Go to definition |
| Alt+Left | Go back to the previous navigation point |
| Ctrl+S / Ctrl+Alt+Y | Save / reload from disk |
| Ctrl+D / Ctrl+/ / Ctrl+Alt+L | Duplicate line / toggle line comment / normalize indentation |
| Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab | Close tab / next tab / previous tab |
| Ctrl+K / Alt+0 | Open Commit and focus the message / changes list |
| Ctrl+Enter / Ctrl+Shift+K | Commit from the Commit panel / preview push |
| Ctrl+D / F4 in Commit | Show diff / open source |
| F7 / Shift+F7 | Next / previous difference |
| Alt+1 / Alt+7 / Alt+8 / Alt+9 | Project / Structure / Services / Git Log |
| Alt+4 / Alt+F12 | Build or run output / terminal |
| Ctrl+F9 / Shift+F10 | Compile / rerun the active Maven task |
| Ctrl+Shift+F12 | Hide or restore tool windows |

## Scope and resource limits

Idea Lite uses WPF, AvalonEdit, and the system Git executable. Browsing source and reviewing Git changes require no JVM. Java and basic Kotlin navigation use a bounded source index. Maven metadata comes from local POMs, and Spring discovery uses source annotations and configuration filenames. Compiler-level resolution, a full Maven dependency graph, and runtime Spring wiring are outside this scope.

| Resource | Default limit |
| --- | --- |
| File list | 50,000 files |
| Source index | About 64 MiB read, 80,000 symbols, 512 KiB per source file |
| Maven POMs | 500 POMs, 1 MiB per file |
| Open documents | 12, with unmodified documents eligible for automatic closing |
| Text editor | 4 MiB per file |
| Side-by-side diff | 1 million characters and 40,000 lines across both sides |
| Project text search | 250 results, 512 KiB per file |
| Captured command output | 8 MiB per stream |

The status bar displays the process working set. In a historical development run with 2,000 synthetic Java files and 10,000 symbols, the portable build used about 193 MiB of working set and 161 MiB of private memory after indexing. The window appeared after about 0.69 seconds. The figures are for the Idea Lite process on one Windows machine and exclude child processes. Use the script below to measure on your machine.

```powershell
.\scripts\New-BenchmarkWorkspace.ps1 -Files 2000
# Open D:\Codex\work\idea-lite-benchmark in Idea Lite
Get-Process IdeaLite | Select-Object Id, WorkingSet64, PrivateMemorySize64
```

## Build and test from source

Use Windows x64, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and Git.

```powershell
git clone https://github.com/keweizhuazuki/idea-lite.git
cd idea-lite
dotnet restore IdeaLite.slnx --locked-mode
dotnet build IdeaLite.slnx -c Release --no-restore
dotnet run --project tests/IdeaLite.Tests -c Release --no-build
dotnet run --project tests/IdeaLite.UiChecks -c Release --no-build
dotnet run --project src/IdeaLite -- 'D:\code\your-project'
```

The core tests use temporary local Git repositories. WPF checks briefly open test windows and use local fixtures for Maven output and AI responses. To include the WSL process checks in the core suite, pass `-- --with-wsl --keep`.

```powershell
.\scripts\New-DemoWorkspace.ps1
# Open D:\Codex\work\idea-lite-demo in Idea Lite
.\scripts\Build.ps1
# Writes both ZIP packages and SHA256SUMS.txt to artifacts/
```

The app stores layout and recent projects in `workbench.json` under its settings directory, normally `D:\Codex\IdeaLite` when the drive is available, or `%LOCALAPPDATA%\IdeaLite` otherwise. Set `IDEALITE_CONFIG_DIR` to use a different directory. For operation failures, the error window shows a stack trace. The local `last-error.log` records the exception type and stack trace.

| Path | Contents |
| --- | --- |
| [src/IdeaLite.Core](src/IdeaLite.Core) | Git, encoding, conflict parsing, source indexing, Maven, and Spring |
| [src/IdeaLite](src/IdeaLite) | WPF workbench, AvalonEdit editor, DiffPlex, and terminal |
| [tests](tests) | Core integration and WPF checks |
| [samples/spring-shop](samples/spring-shop/README.md) | Synthetic Java and Spring example |
| [scripts](scripts) | Build, packaging, demo, and benchmark scripts |

See [Contributing](CONTRIBUTING.md), [Security](SECURITY.md), and [Third-party notices](THIRD-PARTY-NOTICES.md). To report a usage issue, open a [GitHub issue](https://github.com/keweizhuazuki/idea-lite/issues).

Idea Lite is an independent project released under the [MIT License](LICENSE). Its workbench layout takes inspiration from the [IntelliJ IDEA interface](https://www.jetbrains.com/help/idea/guided-tour-around-the-user-interface.html), [Commit window](https://www.jetbrains.com/help/idea/commit-and-push-changes.html), and [Git Log](https://www.jetbrains.com/help/idea/log-tab.html).
