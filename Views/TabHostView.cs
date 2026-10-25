using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AdamsToolkit.Core;

namespace AdamsToolkit.Views;

/// <summary>
/// Página com separadores no topo (pílulas): junta várias views debaixo de 1 item da
/// barra lateral. Cada separador cria a sua view só na 1ª vez que é aberto.
/// </summary>
public class TabHostView : UserControl
{
    public record Tab(string Key, string Label, Func<UserControl> Create);

    private readonly List<Tab> _tabs;
    private readonly Dictionary<string, UserControl> _views = new();
    private readonly Dictionary<string, RadioButton> _buttons = new();
    private readonly ContentControl _host = new();
    private static int _groupSeq;

    /// <summary>Separador mudou (rótulo) — a janela atualiza o breadcrumb.</summary>
    public event Action<string>? TabChanged;
    public string CurrentKey { get; private set; } = "";
    public string CurrentLabel => _tabs.FirstOrDefault(t => t.Key == CurrentKey)?.Label ?? "";

    public TabHostView(params Tab[] tabs)
    {
        _tabs = tabs.ToList();
        var group = "tabs" + (++_groupSeq);
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var t in _tabs)
        {
            var rb = new RadioButton { Content = t.Label, GroupName = group, Style = (Style)FindResource("SegBtn") };
            rb.Checked += (_, _) => Show(t.Key);
            _buttons[t.Key] = rb;
            bar.Children.Add(rb);
        }
        var pill = new Border
        {
            CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14), Child = bar,
        };
        pill.SetResourceReference(Border.BackgroundProperty, "BrSurface");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_host, 1);
        root.Children.Add(pill);
        root.Children.Add(_host);
        Content = root;

        if (_tabs.Count > 0) Select(_tabs[0].Key);
    }

    /// <summary>Abre um separador pela chave (usado pela navegação/ações rápidas).</summary>
    public void Select(string key)
    {
        if (!_buttons.TryGetValue(key, out var rb)) return;
        if (rb.IsChecked == true) Show(key); else rb.IsChecked = true;
    }

    public T? Get<T>(string key) where T : UserControl => _views.TryGetValue(key, out var v) ? v as T : null;

    private void Show(string key)
    {
        var tab = _tabs.First(t => t.Key == key);
        if (!_views.TryGetValue(key, out var view)) _views[key] = view = tab.Create();
        CurrentKey = key;
        view.RenderTransform = new TranslateTransform();
        _host.Content = view;
        if (TryFindResource("ViewFadeIn") is Storyboard sb) sb.Begin(view);
        Lang.Apply(view); // separador em cache pode ter sido visto noutro idioma
        TabChanged?.Invoke(tab.Label);
    }
}
