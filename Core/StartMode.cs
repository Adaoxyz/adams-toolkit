using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// (v1.71.3: já não se guarda preferência — pergunta sempre. Fica só p/ as constantes.)
/// O que abrir quando o jogador lança o exe à mão (v1.71): perguntar (2 cartões),
/// ir direto ao Toolkit ou direto ao Editor de Clips. Arranque com o Windows
/// (--startup) ignora isto — vai sempre para o Toolkit minimizado.
/// </summary>
public static class StartMode
{
    public const string Ask = "ask", Toolkit = "toolkit", Editor = "editor";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "start-mode.txt");

    public static string Get()
    {
        try
        {
            var v = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim().ToLowerInvariant() : Ask;
            return v is Toolkit or Editor ? v : Ask;
        }
        catch { return Ask; }
    }

    public static void Set(string mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, mode);
        }
        catch { }
    }
}
