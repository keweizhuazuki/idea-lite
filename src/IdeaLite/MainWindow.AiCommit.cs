using System.Windows;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private CancellationTokenSource? _aiGeneration;
    private readonly AiCommitService _aiCommitService;
    private readonly AiCredentials _aiCredentials;

    private bool ShowAiCommitSettings()
    {
        var branch = (_gitStatus?.Branch ?? "").Split("...", StringSplitOptions.None)[0];
        var draft = CommitMessage.Text;
        var context = new CommitMessageContext(branch.Length <= 256 ? branch : branch[..256], "", draft.Length <= 1000 ? draft : draft[..1000], AmendCheck.IsChecked == true);
        return new AiCommitSettingsWindow(this, _layout, _aiCredentials, context).ShowDialog() == true;
    }
    private async void AiSettingsClick(object sender, RoutedEventArgs e) => await Guard(() => { ShowAiCommitSettings(); return Task.CompletedTask; });
    private async void GenerateCommitClick(object sender, RoutedEventArgs e) => await Guard(GenerateCommitMessageAsync);

    private void UpdateAiCommitButton()
    {
        if (AiGenerateButton == null) return;
        AiGenerateIcon.Kind = _aiGeneration == null ? "Sparkles" : "Stop";
        UiText.Set(AiGenerateButton, "ToolTip", _aiGeneration == null ? "AI 生成提交说明" : "取消 AI 生成");
    }

    private async Task GenerateCommitMessageAsync()
    {
        if (_aiGeneration != null) { _aiGeneration.Cancel(); return; }
        if (_git == null) { SetStatus("当前不是 Git 仓库。可在 Git 菜单中初始化。"); return; }
        if (_commandBusy) { SetStatus("另一个命令正在运行。"); return; }
        var paths = _commitChoices.Where(pair => pair.Value.Included).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        bool amend = AmendCheck.IsChecked == true;
        if (paths.Length == 0 && !amend) { SetStatus("请先勾选本次提交的文件。"); return; }
        var provider = _layout.CommitAiProvider;
        var apiKey = _aiCredentials.Read(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!ShowAiCommitSettings()) return;
            provider = _layout.CommitAiProvider; apiKey = _aiCredentials.Read(provider);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("请先在 AI 提交设置中填写 API Key。");
        }
        if (_documents.Any(document => document.Dirty && paths.Contains(Relative(document.File.Path))))
            throw new InvalidOperationException("勾选文件有未保存的编辑，请先保存后生成。");
        var git = _git;
        var draft = CommitMessage.Text;
        using var request = new CancellationTokenSource();
        _aiGeneration = request; UpdateAiCommitButton(); UpdateCommitSelection();
        SetStatus("正在生成提交说明…再次点击 AI 按钮可取消。");
        try
        {
            var context = await git.CommitContextAsync(paths, draft, amend, request.Token);
            var model = provider == AiProvider.OpenAI ? _layout.OpenAiCommitModel : _layout.DeepSeekCommitModel;
            var result = await _aiCommitService.GenerateAsync(provider, apiKey, context, _layout.CommitAiLanguage, model, request.Token, _layout.CommitAiPrompt);
            var message = result.Message;
            request.Token.ThrowIfCancellationRequested();
            var currentPaths = _commitChoices.Where(pair => pair.Value.Included).Select(pair => pair.Key).Order(StringComparer.Ordinal);
            if (!ReferenceEquals(git, _git) || amend != (AmendCheck.IsChecked == true) || !paths.SequenceEqual(currentPaths))
            { SetStatus("项目或勾选文件已变化，请重新生成提交说明。"); return; }
            var latest = await git.CommitContextAsync(paths, draft, amend, request.Token);
            if (latest.Changes != context.Changes || latest.Branch != context.Branch || _documents.Any(document => document.Dirty && paths.Contains(Relative(document.File.Path))))
            { SetStatus("文件改动已变化，请重新生成提交说明。"); return; }
            if (CommitMessage.Text != draft)
            {
                var reviewed = Dialogs.Prompt(this, "AI 提交说明", "生成期间你修改了原说明。确认后使用下方内容，取消则保留原说明。", message, multiline: true);
                if (reviewed == null) return;
                message = reviewed;
            }
            CommitMessage.Text = message;
            CommitMessage.CaretIndex = message.Length; CommitMessage.Focus();
            SetStatus(() => result.InputTokens is int input && result.OutputTokens is int output
                ? UiText.Current.Format($"已生成提交说明 · 输入 {input} / 输出 {output} tokens，请检查后提交。") : UiText.Current.Translate("已生成提交说明，请检查后提交。"));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { SetStatus("已取消 AI 生成。"); }
        finally
        {
            _aiGeneration = null; UpdateAiCommitButton(); UpdateCommitSelection();
        }
    }
}
