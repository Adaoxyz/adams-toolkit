using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace AdamsToolkit.Core;

/// <summary>
/// Idiomas: Português (o texto original do código) e English. Troca AO VIVO, sem reiniciar.
///
/// Tradução por TEXTO, estilo gettext: a chave é a própria frase em PT e o dicionário
/// (Lang/en.json, embutido) diz o que mostrar. Em vez de mexer nas ~1200 frases
/// espalhadas pela app, cada TextBlock é traduzido ao aparecer e sempre que o código
/// lhe muda o texto (SetCurrentValue — os bindings continuam vivos). O texto ORIGINAL
/// fica guardado no próprio elemento (LangSource), por isso trocar de idioma é só
/// voltar a traduzir o que está aberto a partir do original. Frases com partes
/// variáveis ("A aplicar 3/9 — …") entram como modelo com {0}, {1}….
///
/// Peso: em Português e sem nunca ter trocado, o gancho nem é instalado (custo zero).
/// </summary>
public static class Lang
{
    public static readonly (string code, string label)[] Available =
    {
        // PT-BR e Español desativados a pedido do dono (10-24) — só PT e EN
        ("pt-PT", "Português"), ("en", "English"),
    };

    private static string PrefPath => Path.Combine(ConfigService.DataDir, "lang.txt");

    public static string Current { get; private set; } = LoadPref();

    private static string LoadPref()
    {
        try
        {
            var c = File.ReadAllText(PrefPath).Trim();
            if (Available.Any(a => a.code == c)) return c;
        }
        catch { }
        return "pt-PT";
    }

    public static bool IsSource => Current == "pt-PT";

    /// <summary>Disparado depois de trocar de idioma (UI thread) — para o que não é TextBlock (bandeja…).</summary>
    public static event Action? Changed;

    /// <summary>Troca de idioma na hora: grava, carrega o dicionário e retraduz as janelas abertas.</summary>
    public static void SwitchTo(string code)
    {
        if (code == Current || Available.All(a => a.code != code)) return;
        try { File.WriteAllText(PrefPath, code); }
        catch (Exception ex) { Log.Swallow("gravar idioma", ex); }
        Current = code;
        LoadDictionary();
        Hook();
        foreach (Window w in Application.Current.Windows) Apply(w);
        Changed?.Invoke();
        Log.Info($"idioma → {code}", "idioma");
    }

    // ---------- dicionário ----------

    private static readonly Dictionary<string, string> Exact = new();
    private record Pattern(string Prefix, Regex Rx, string Target);
    private static readonly List<Pattern> Patterns = new();
    private static readonly Regex Hole = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static void LoadDictionary()
    {
        Exact.Clear();
        Patterns.Clear();
        if (IsSource) return;
        try
        {
            var res = Application.GetResourceStream(new Uri($"Lang/{Current}.json", UriKind.Relative));
            if (res == null) return;
            using var s = res.Stream;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            foreach (var (src, dst) in map)
            {
                if (string.IsNullOrWhiteSpace(dst)) continue;
                if (!Hole.IsMatch(src)) { Exact[src] = dst; continue; }
                // modelo: o texto fixo é literal, cada {n} apanha o que vier
                var rx = "^" + Hole.Replace(Regex.Escape(src).Replace(@"\{", "{"), m => $"(?<g{m.Groups[1].Value}>.+?)") + "$";
                Patterns.Add(new Pattern(src[..src.IndexOf('{')], new Regex(rx, RegexOptions.Singleline | RegexOptions.Compiled), dst));
            }
            // modelos com mais texto fixo primeiro (mais específicos)
            Patterns.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
        }
        catch (Exception ex) { Log.Error($"carregar idioma {Current} falhou — fica em português", ex); }
    }

    /// <summary>Tradução de uma frase PT (devolve a original se não houver).</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsSource) return text ?? "";
        if (!text.Any(char.IsLetter)) return text;

        var core = text.Trim();
        if (core.Length == 0) return text;
        string? hit = null;
        if (Exact.TryGetValue(core, out var e)) hit = e;
        else
        {
            foreach (var p in Patterns)
            {
                if (!core.StartsWith(p.Prefix, StringComparison.Ordinal)) continue;
                var m = p.Rx.Match(core);
                if (!m.Success) continue;
                hit = Hole.Replace(p.Target, h =>
                {
                    var g = m.Groups["g" + h.Groups[1].Value];
                    return g.Success ? T(g.Value) : h.Value;
                });
                break;
            }
        }
        if (hit == null) return text;
        // preserva espaços à volta (usados para alinhar ícones e texto)
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        return text[..lead] + hit + text[(text.Length - trail)..];
    }

    // ---------- ligação à interface ----------
    //
    // Porque não o Loaded: um class handler de Loaded NÃO dispara na maior parte dos
    // elementos — o WPF só faz broadcast do Loaded a quem tem handler de instância. O
    // SizeChanged dispara sempre no 1º layout de cada elemento. Mudanças de texto
    // seguem-se com um binding do TextBlock a ele próprio (Text → LangWatch): vive e
    // morre com o elemento, sem fugas de memória.

    private static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "LangWatch", typeof(string), typeof(Lang), new PropertyMetadata(null, OnWatchChanged));
    // texto original (PT) e o último texto que nós escrevemos — distingue "o código mudou
    // o texto" (novo original) de "fomos nós que traduzimos"
    private static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "LangSource", typeof(string), typeof(Lang));
    private static readonly DependencyProperty OutProperty = DependencyProperty.RegisterAttached(
        "LangOut", typeof(string), typeof(Lang));

    private static bool _hooked;

    /// <summary>Chamado no arranque. Em Português não instala nada (só ao trocar).</summary>
    public static void Install()
    {
        if (IsSource) return;
        LoadDictionary();
        Hook();
        Log.Info($"idioma {Current}: {Exact.Count} frases + {Patterns.Count} modelos", "idioma");
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler(OnSizeChanged), handledEventsToo: true);
    }

    private static readonly ConditionalWeakTable<Window, string> TitleSource = new();

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock tb) Attach(tb);
        else if (sender is Window w && !TitleSource.TryGetValue(w, out _)) ApplyTitle(w);
    }

    private static void Attach(TextBlock tb)
    {
        if (tb.ReadLocalValue(WatchProperty) != DependencyProperty.UnsetValue) return; // já ligado
        Translate(tb, retranslate: false);
        BindingOperations.SetBinding(tb, WatchProperty, new Binding(nameof(TextBlock.Text)) { Source = tb, Mode = BindingMode.OneWay });
    }

    private static void ApplyTitle(Window w)
    {
        if (!TitleSource.TryGetValue(w, out var src)) { src = w.Title; TitleSource.Add(w, src); }
        w.Title = T(src);
    }

    /// <summary>
    /// Aplica o idioma atual a uma árvore (janela inteira ou uma view que volta a ser
    /// mostrada — views em cache não voltam a disparar SizeChanged se o tamanho for igual).
    /// </summary>
    public static void Apply(DependencyObject root)
    {
        if (!_hooked) return;
        if (root is Window w) ApplyTitle(w);
        Walk(root);
    }

    private static void Walk(DependencyObject d)
    {
        if (d is TextBlock tb)
        {
            if (tb.ReadLocalValue(WatchProperty) == DependencyProperty.UnsetValue) Attach(tb);
            else Translate(tb, retranslate: true);
        }
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
    }

    private static bool _inside;

    private static void OnWatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (_inside || d is not TextBlock tb) return;
        Translate(tb, retranslate: false);
    }

    /// <summary>
    /// retranslate=false: o texto atual pode ser um original novo (o código mudou-o) —
    /// guarda-o. retranslate=true: trocou o idioma — traduz de novo a partir do original.
    /// </summary>
    private static void Translate(TextBlock tb, bool retranslate)
    {
        _inside = true;
        try
        {
            // TextBlock com vários Runs (texto formatado): cada pedaço tem o seu original
            if (tb.Inlines.Count > 1)
            {
                foreach (var run in Runs(tb.Inlines)) Retext(run, Run.TextProperty, run.Text, retranslate);
                return;
            }
            Retext(tb, TextBlock.TextProperty, tb.Text, retranslate);
        }
        catch (Exception ex) { Log.Swallow("traduzir texto", ex); }
        finally { _inside = false; }
    }

    private static void Retext(DependencyObject o, DependencyProperty prop, string current, bool retranslate)
    {
        var src = o.GetValue(SourceProperty) as string;
        var last = o.GetValue(OutProperty) as string;
        if (src == null || (!retranslate && current != last))
        {
            src = current; // original novo (1ª vez ou o código mudou o texto)
            o.SetValue(SourceProperty, src);
        }
        var x = T(src);
        o.SetValue(OutProperty, x);
        if (x != current) o.SetCurrentValue(prop, x);
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Run r) yield return r;
            else if (i is Span s) foreach (var x in Runs(s.Inlines)) yield return x;
        }
    }
}
