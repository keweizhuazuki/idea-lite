using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace IdeaLite;

/// <summary>
/// 界面文案及其语言绑定；源码、路径、协议标识和子进程输出不参与翻译。
/// </summary>
public sealed class UiText : INotifyPropertyChanged
{
    private sealed record Pattern(Regex Match, string Replacement);
    public static UiText Current { get; } = new();
    private readonly Dictionary<string, string> _english;
    private readonly Dictionary<string, string> _chinese;
    private readonly Lazy<Pattern[]> _toEnglish, _toChinese;
    private string _language = "en";
    internal static readonly TranslationConverter Converter = new();

    private UiText()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("IdeaLite.Strings.en.json") ?? throw new InvalidOperationException("Missing UI translations.");
        _english = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        _chinese = _english.GroupBy(pair => pair.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        _toEnglish = new(() => Patterns(_english)); _toChinese = new(() => Patterns(_chinese));
    }

    public string Language
    {
        get => _language;
        set
        {
            var next = value.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
            if (_language == next) return;
            _language = next;
            PropertyChanged?.Invoke(this, new(nameof(Language)));
            PropertyChanged?.Invoke(this, new(nameof(LanguageLabel)));
        }
    }
    public string LanguageLabel => Language == "en" ? "EN" : "中";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Format(FormattableString message) => string.Format(CultureInfo.CurrentCulture, Translate(message.Format), message.GetArguments());

    public string Translate(string? source)
    {
        if (source == null) return "";
        var catalog = Language == "en" ? _english : _chinese;
        if (catalog.TryGetValue(source, out var translated)) return translated;
        foreach (var pattern in (Language == "en" ? _toEnglish : _toChinese).Value)
        {
            var match = pattern.Match.Match(source);
            if (!match.Success) continue;
            // Captures are data (paths, IDs, counts), so substitute them without translating their contents.
            return Regex.Replace(pattern.Replacement, @"\{(\d+)\}", token => match.Groups["v" + token.Groups[1].Value].Value);
        }
        return source;
    }

    private static Pattern[] Patterns(Dictionary<string, string> catalog) => catalog.Where(pair => pair.Key is not ("{0}完成" or "{0} completed" or "{0}…") && Regex.IsMatch(pair.Key, @"\{\d+\}"))
        .OrderByDescending(pair => pair.Key.Length).Select(pair =>
        {
            var expression = "\\A"; int position = 0;
            foreach (Match token in Regex.Matches(pair.Key, @"\{(\d+)\}"))
            {
                expression += Regex.Escape(pair.Key[position..token.Index]) + "(?<v" + token.Groups[1].Value + ">[\\s\\S]*?)";
                position = token.Index + token.Length;
            }
            expression += Regex.Escape(pair.Key[position..]) + "\\z";
            return new Pattern(new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), pair.Value);
        }).ToArray();

    public static void Set(DependencyObject element, string property, object? source)
    {
        var descriptor = DependencyPropertyDescriptor.FromName(property, element.GetType(), element.GetType()) ?? throw new ArgumentException("Unknown UI property: " + property);
        BindingOperations.SetBinding(element, descriptor.DependencyProperty, new Binding(nameof(Language)) { Source = Current, Converter = Converter, ConverterParameter = source });
    }
    public static void Set(DependencyObject element, string property, Func<string> source) => Set(element, property, (object)source);

    public static object? Source(DependencyObject element, DependencyProperty property) => BindingOperations.GetBinding(element, property)?.ConverterParameter ?? element.GetValue(property);

    internal sealed class TranslationConverter : IValueConverter, IMultiValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => parameter switch
        {
            string text => Current.Translate(text),
            Func<string> text => text(),
            _ => parameter
        };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values[0] is string text ? Current.Translate(text) : DependencyProperty.UnsetValue;
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public static class UiLocalizationExtensions
{
    public static T Localize<T>(this T element, params string[] properties) where T : DependencyObject
    {
        foreach (var property in properties)
        {
            var descriptor = DependencyPropertyDescriptor.FromName(property, element.GetType(), element.GetType());
            if (descriptor != null && element.GetValue(descriptor.DependencyProperty) is string text) UiText.Set(element, property, text);
        }
        return element;
    }
}

public sealed class TrExtension(string text) : MarkupExtension
{
    [ConstructorArgument("text")]
    public string Text { get; set; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(UiText.Language))
    { Source = UiText.Current, Converter = UiText.Converter, ConverterParameter = Text }.ProvideValue(serviceProvider);
}

public sealed class TranslateBindingExtension(string path) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = UiText.Converter };
        binding.Bindings.Add(new Binding(path)); binding.Bindings.Add(new Binding(nameof(UiText.Language)) { Source = UiText.Current });
        return binding.ProvideValue(serviceProvider);
    }
}
