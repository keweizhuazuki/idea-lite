using System.Windows;
using System.Windows.Controls;
using IdeaLite.Core;

namespace IdeaLite;

/// <summary>
/// AI 提交说明设置；接口与模型内置，日常使用只需选择服务商并填写 Key。
/// </summary>
public sealed class AiCommitSettingsWindow : Window
{
    public AiCommitSettingsWindow(Window owner, WorkbenchLayout settings, AiCredentials? credentials = null, CommitMessageContext? context = null)
    {
        credentials ??= new();
        context ??= new("", "", "", false);
        Owner = owner; Title = "AI 提交设置"; Width = 620; SizeToContent = SizeToContent.Height;
        MaxHeight = 680; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }.Localize("Content");
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)owner.FindResource("Primary") }.Localize("Content");
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var form = new StackPanel(); panel.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Label(string value) => form.Children.Add(new TextBlock { Text = value, Margin = new Thickness(0, 12, 0, 5) }.Localize("Text"));
        Label("服务商");
        var provider = new ComboBox { SelectedIndex = settings.CommitAiProvider == AiProvider.DeepSeek ? 1 : 0, Style = (Style)owner.FindResource("AiChoice") };
        provider.Items.Add("OpenAI (ChatGPT API)"); provider.Items.Add("DeepSeek"); form.Children.Add(provider);
        Label("OpenAI API Key");
        var openAiKey = new PasswordBox { Password = credentials.Read(AiProvider.OpenAI), MaxLength = 1200, Style = (Style)owner.FindResource("ApiKeyField") }; form.Children.Add(openAiKey);
        Label("DeepSeek API Key");
        var deepSeekKey = new PasswordBox { Password = credentials.Read(AiProvider.DeepSeek), MaxLength = 1200, Style = (Style)owner.FindResource("ApiKeyField") }; form.Children.Add(deepSeekKey);
        form.Children.Add(new TextBlock { Text = "只需填写所用服务商的 Key。Key 保存在当前 Windows 用户的凭据库；清空并保存可删除。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) }.Localize("Text"));
        Label("提交说明语言");
        var language = new ComboBox { SelectedIndex = settings.CommitAiLanguage == "zh-CN" ? 1 : 0, Style = (Style)owner.FindResource("AiChoice") };
        language.Items.Add("English"); language.Items.Add("简体中文"); form.Children.Add(language);
        var promptHeader = new DockPanel { Margin = new Thickness(0, 12, 0, 5) }; form.Children.Add(promptHeader);
        var prompt = new TextBox { Name = "CommitPromptInput", Text = settings.CommitAiPrompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = AiCommitService.MaxPromptCharacters };
        form.Children.Add(prompt);
        form.Children.Add(new TextBlock { Text = "可用变量：{task_rule} 任务号前缀规则，{task_id} 任务号，{language_rule} 输出语言规则，{language} 语言名称。任务号优先从现有说明提取，再查分支名。删除 {task_rule} 可取消自动前缀要求。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }.Localize("Text"));
        var promptActions = new StackPanel { Orientation = Orientation.Horizontal };
        var reset = new Button { Content = "恢复默认提示词" }.Localize("Content");
        reset.Click += (_, _) => prompt.Text = AiCommitService.DefaultPrompt;
        var preview = new Button { Content = "预览实际提示词" }.Localize("Content");
        preview.Click += (_, _) =>
        {
            try { Dialogs.Information(this, "实际发送的提示词", AiCommitService.BuildPrompt(context, language.SelectedIndex == 1 ? "zh-CN" : "en", prompt.Text), localizeMessage: false); }
            catch (InvalidOperationException error) { Dialogs.Error(this, error); }
        };
        promptActions.Children.Add(reset); promptActions.Children.Add(preview);
        DockPanel.SetDock(promptActions, Dock.Right); promptHeader.Children.Add(promptActions);
        promptHeader.Children.Add(new TextBlock { Text = "内置提示词（可编辑）", TextWrapping = TextWrapping.Wrap }.Localize("Text"));
        var advanced = new StackPanel();
        advanced.Children.Add(new TextBlock { Text = "OpenAI Model", Margin = new Thickness(0, 8, 0, 4) });
        var openAiModel = new TextBox { Text = settings.OpenAiCommitModel }; advanced.Children.Add(openAiModel);
        advanced.Children.Add(new TextBlock { Text = "DeepSeek Model", Margin = new Thickness(0, 8, 0, 4) });
        var deepSeekModel = new TextBox { Text = settings.DeepSeekCommitModel }; advanced.Children.Add(deepSeekModel);
        form.Children.Add(new Expander { Header = "高级：模型", Content = advanced, Margin = new Thickness(0, 16, 0, 8) }.Localize("Header"));
        form.Children.Add(new TextBlock { Text = "点击生成时，会将勾选文件的差异、分支和现有说明发送到所选服务商。生成结果可编辑，提交仍需手动操作。", TextWrapping = TextWrapping.Wrap, FontSize = 12 }.Localize("Text"));
        save.Click += (_, _) =>
        {
            try
            {
                AiCommitService.BuildPrompt(context, language.SelectedIndex == 1 ? "zh-CN" : "en", prompt.Text);
                credentials.Save(AiProvider.OpenAI, openAiKey.Password);
                credentials.Save(AiProvider.DeepSeek, deepSeekKey.Password);
                settings.CommitAiProvider = provider.SelectedIndex == 1 ? AiProvider.DeepSeek : AiProvider.OpenAI;
                settings.CommitAiLanguage = language.SelectedIndex == 1 ? "zh-CN" : "en";
                settings.CommitAiPrompt = string.IsNullOrWhiteSpace(prompt.Text) ? AiCommitService.DefaultPrompt : prompt.Text.Trim();
                settings.OpenAiCommitModel = string.IsNullOrWhiteSpace(openAiModel.Text) ? AiCommitService.DefaultModel(AiProvider.OpenAI) : openAiModel.Text.Trim();
                settings.DeepSeekCommitModel = string.IsNullOrWhiteSpace(deepSeekModel.Text) ? AiCommitService.DefaultModel(AiProvider.DeepSeek) : deepSeekModel.Text.Trim();
                settings.Save(); DialogResult = true;
            }
            catch (InvalidOperationException error) { Dialogs.Error(this, error); }
        };
        Closed += (_, _) => { openAiKey.Clear(); deepSeekKey.Clear(); };
        Content = panel;
    }
}
