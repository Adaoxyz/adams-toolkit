using System.Diagnostics;
using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// Limpeza automática semanal via Agendador de Tarefas do Windows (schtasks,
/// tarefa por-utilizador, sem admin): domingo 12:00 corre o exe com --cleanup,
/// que limpa headless e sai sem abrir janela. Agendador em vez de timer na app
/// de propósito — na bandeja a app desliga tudo e não pode ter trabalho
/// periódico próprio. Preferência local em cleanup-sched.txt; quando ligada,
/// a tarefa é re-escrita a cada arranque para o caminho acompanhar o exe.
/// </summary>
public static class ScheduledCleanup
{
    private const string TaskName = "AdamsToolkit Limpeza";

    private static string PrefPath => Path.Combine(ConfigService.DataDir, "cleanup-sched.txt");

    // wrp 2026-08-26: limpeza DESATIVADA de vez (jogador banido numa verificação de PC — Prefetch/WER
    // apagados lêem-se como limpeza de rasto). IsEnabled fixo em false → Sync() no boot apaga a
    // tarefa semanal em todos os PCs que a tinham; RunHeadless não faz nada.
    public static bool IsEnabled => false;

    public static bool SetEnabled(bool enabled)
    {
        try { File.WriteAllText(PrefPath, enabled ? "on" : "off"); } catch { }
        return Sync();
    }

    /// <summary>
    /// Aplica a preferência ao Agendador. Chamado no boot e a cada mudança.
    /// Como a limpeza está desativada de vez, isto só APAGA a tarefa semanal
    /// antiga em qualquer PC que ainda a tenha de versões antigas (self-heal).
    /// Não volta a criar tarefa nenhuma — daí não haver aqui o ramo /Create.
    /// </summary>
    public static bool Sync()
    {
        try
        {
            return Run($"/Delete /TN \"{TaskName}\" /F") is 0 or 1; // 1 = não existia
        }
        catch { return false; }
    }

    private static int Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return -1;
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    /// <summary>
    /// Handler do arg "--cleanup" — no-op. A limpeza está desativada; mantido só
    /// para uma tarefa agendada antiga (de versões antigas) sair calada sem fazer
    /// nada até o Sync() do próximo arranque a apagar.
    /// </summary>
    public static void RunHeadless() { /* desativado: não faz nada */ }
}
