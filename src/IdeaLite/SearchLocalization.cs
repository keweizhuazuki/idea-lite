using System.ComponentModel;
using System.Windows;

namespace IdeaLite;

/// <summary>
/// 编辑器内查找栏的双语文案，沿用同一个查找面板和查询状态。
/// </summary>
public sealed class SearchLocalization : ICSharpCode.AvalonEdit.Search.Localization, INotifyPropertyChanged
{
    public SearchLocalization() => PropertyChangedEventManager.AddHandler(UiText.Current, LanguageChanged, nameof(UiText.Language));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, new(null));
    public override string MatchCaseText => UiText.Current.Translate("区分大小写");
    public override string MatchWholeWordsText => UiText.Current.Translate("全词匹配");
    public override string UseRegexText => UiText.Current.Translate("正则表达式");
    public override string FindNextText => UiText.Current.Translate("查找下一个");
    public override string FindPreviousText => UiText.Current.Translate("查找上一个");
    public override string ErrorText => UiText.Current.Translate("错误：");
    public override string NoMatchesFoundText => UiText.Current.Translate("没有匹配的文本。");
}
