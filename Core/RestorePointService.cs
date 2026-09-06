using System.Diagnostics;
using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// Ponto de restauro do sistema a pedido (Checkpoint-Computer). Precisa de
/// admin, por isso corre num PowerShell elevado (UAC) — a app é asInvoker.
/// O Windows limita 1 ponto por 24h; contornamos só durante a criação
/// (SystemRestorePointCreationFrequency=0) e repomos o valor no fim.
/// O resultado volta por ficheiro temporário, como no DriverUpdateService.
/// </summary>
public static class RestorePointService
{
    private const string Script = @"
$ErrorActionPreference = 'Stop'
try {
  Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue
  $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
  $old = (Get-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
  Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
  try {
    Checkpoint-Computer -Description '__DESC__' -RestorePointType MODIFY_SETTINGS
  } finally {
    if ($null -ne $old) { Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value $old -Type DWord }
    else { Remove-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
  }
  Set-Content -Path '__OUT__' -Value 'ok' -Encoding UTF8
} catch {
  Set-Content -Path '__OUT__' -Value ""err|$($_.Exception.Message)"" -Encoding UTF8
  exit 1
}
";

    /// <summary>Cria um ponto de restauro (elevado). Devolve (ok, mensagem).</summary>
    public static async Task<(bool ok, string message)> CreateAsync(string description)
    {
        var tag = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"adams-restore-{tag}.ps1");
        var outPath = Path.Combine(Path.GetTempPath(), $"adams-restore-{tag}.txt");
        try
        {
            var desc = description.Replace("'", "").Replace("\r", " ").Replace("\n", " ");
            await File.WriteAllTextAsync(scriptPath,
                Script.Replace("__DESC__", desc).Replace("__OUT__", outPath),
                System.Text.Encoding.UTF8);

            Process? p;
            try
            {
                p = Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (false, "Permissão de administrador recusada.");
            }
            if (p == null) return (false, "Não foi possível iniciar o PowerShell.");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "Demorou demasiado tempo.");
            }

            if (!File.Exists(outPath))
                return (false, "Sem resposta do Windows — o Restauro do Sistema pode estar desativado.");

            var text = (await File.ReadAllTextAsync(outPath)).Trim();
            if (text == "ok") return (true, "Ponto de restauro criado.");
            var err = text.StartsWith("err|") ? text[4..] : text;
            err = err.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return (false, err.Length > 140 ? err[..140] + "…" : err);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }
}
