using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdamsToolkit.Core;

/// <summary>
/// Verificação e instalação de atualizações de DRIVERS através do Windows Update
/// Agent (COM, presente em todos os Win10/11) — só drivers assinados do catálogo
/// oficial Microsoft, nunca de terceiros. A pesquisa corre sem admin; a instalação
/// lança um PowerShell elevado (UAC) à parte, porque a app corre asInvoker.
/// Se a pesquisa falhar ou não encontrar nada, o resultado é neutro — a UI nunca
/// propõe atualizar sem uma atualização real encontrada.
/// </summary>
public static class DriverUpdateService
{
    public class DriverUpdate
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("maker")] public string Manufacturer { get; set; } = "";
        [JsonPropertyName("prov")] public string Provider { get; set; } = "";
        [JsonPropertyName("cls")] public string DriverClass { get; set; } = "";
        [JsonPropertyName("date")] public string Date { get; set; } = "";
        [JsonPropertyName("mb")] public double SizeMb { get; set; }
    }

    private class ScanPayload
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("updates")] public List<DriverUpdate>? Updates { get; set; }
    }

    private class InstallPayload
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("reboot")] public bool Reboot { get; set; }
        [JsonPropertyName("done")] public int Done { get; set; }
        [JsonPropertyName("failed")] public int Failed { get; set; }
    }

    public class ScanResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public List<DriverUpdate> Updates { get; init; } = new();
        public DateTime When { get; init; } = DateTime.Now;
    }

    /// <summary>Último resultado de pesquisa (cache em memória para badge/UI).</summary>
    public static ScanResult? Last { get; private set; }

    /// <summary>Disparado no fim de cada pesquisa (thread de background).</summary>
    public static event Action<ScanResult>? ScanFinished;

    private static readonly object _lock = new();
    private static Task<ScanResult>? _running;

    public static bool IsScanning { get { lock (_lock) return _running != null; } }

    /// <summary>
    /// Pesquisa single-flight: chamadas concorrentes partilham a mesma tarefa
    /// (ex.: badge no arranque + abertura da aba).
    /// </summary>
    public static Task<ScanResult> ScanAsync()
    {
        lock (_lock)
        {
            if (_running != null) return _running;
            _running = Task.Run(RunScan);
            _running.ContinueWith(t =>
            {
                lock (_lock) _running = null;
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    Last = t.Result;
                    try { ScanFinished?.Invoke(t.Result); } catch { }
                }
            });
            return _running;
        }
    }

    // ---- pesquisa (sem admin) ----

    // Drivers de hardware vivem no serviço "Microsoft Update" (MU), não no serviço
    // base "Windows Update" — pesquisar só com ServerSelection=2 devolve quase
    // sempre 0 drivers. Registamos o MU (id fixo da Microsoft) quando possível e
    // pesquisamos nele; sem registo possível (sem admin e MU nunca registado),
    // caímos no comportamento antigo em vez de falhar.
    private const string SelectServerSnippet = @"
  $muId = '7971f918-a847-4430-bd80-ff96be7fc95f'
  $useMu = $false
  try {
    $sm = New-Object -ComObject Microsoft.Update.ServiceManager
    $reg = @($sm.Services) | Where-Object { $_.ServiceID -eq $muId }
    if (-not $reg) { $null = $sm.AddService2($muId, 3, '') }  # 3 = registo pendente+online, sem mexer no Automatic Updates
    $useMu = $true
  } catch {}
  if ($useMu) {
    try { $searcher.ServerSelection = 3; $searcher.ServiceID = $muId } catch { $useMu = $false }
  }
  if (-not $useMu) { try { $searcher.ServerSelection = 2 } catch {} }
";

    private const string ScanScript = @"
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
# o PS embrulha erros COM num MethodInvocationException (HResult genérico
# 0x80131500) — o código real do Windows Update está na InnerException
function HrOf($e) {
  $h = 0
  try { if ($e.Exception.InnerException) { $h = $e.Exception.InnerException.HResult } } catch {}
  if (-not $h) { try { $h = $e.Exception.HResult } catch {} }
  return $h
}
try {
  $q = ""IsInstalled=0 and Type='Driver' and IsHidden=0""
  $session  = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
__SERVER__
  $result = $null; $hr = 0; $err = ''
  try { $result = $searcher.Search($q) }
  catch { $hr = HrOf $_; $err = ""$($_.Exception.Message)"" }
  # O datastore do Microsoft Update pode estar em mau estado (família 0x80248xxx)
  # → 2ª tentativa no serviço base antes de dar erro ao jogador.
  if ($null -eq $result -and $useMu) {
    try {
      $s2 = $session.CreateUpdateSearcher()
      $s2.ServerSelection = 2
      $result = $s2.Search($q)
      $hr = 0; $err = ''
    } catch { if ($hr -eq 0) { $hr = HrOf $_; $err = ""$($_.Exception.Message)"" } }
  }
  if ($null -eq $result) {
    Write-Output ('##JSON##' + (@{ ok = $false; error = $err; code = ('0x{0:X8}' -f $hr) } | ConvertTo-Json -Compress))
    exit 0
  }
  $list = New-Object System.Collections.ArrayList
  foreach ($u in $result.Updates) {
    try {
      $null = $list.Add(@{
        id    = ""$($u.Identity.UpdateID)""
        title = ""$($u.Title)""
        model = ""$($u.DriverModel)""
        maker = ""$($u.DriverManufacturer)""
        prov  = ""$($u.DriverProvider)""
        cls   = ""$($u.DriverClass)""
        date  = if ($u.DriverVerDate) { $u.DriverVerDate.ToString('yyyy-MM-dd') } else { '' }
        mb    = [math]::Round($u.MaxDownloadSize / 1MB, 1)
      })
    } catch {}
  }
  Write-Output ('##JSON##' + (@{ ok = $true; updates = @($list) } | ConvertTo-Json -Depth 4 -Compress))
} catch {
  Write-Output ('##JSON##' + (@{ ok = $false; error = ""$($_.Exception.Message)""; code = ('0x{0:X8}' -f (HrOf $_)) } | ConvertTo-Json -Compress))
}
";

    private static async Task<ScanResult> RunScan()
    {
        string? scriptPath = null;
        try
        {
            scriptPath = Path.Combine(Path.GetTempPath(), $"adams-wuscan-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(scriptPath,
                ScanScript.Replace("__SERVER__", SelectServerSnippet), System.Text.Encoding.UTF8);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p == null)
                return new ScanResult { Ok = false, Error = "PowerShell indisponível." };

            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();

            // 1ª pesquisa WU pode ser lenta; 5 min de teto
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ScanResult { Ok = false, Error = "A verificação demorou demasiado tempo." };
            }

            var outText = await stdout;
            var idx = outText.LastIndexOf("##JSON##", StringComparison.Ordinal);
            if (idx < 0)
                return new ScanResult { Ok = false, Error = "Resposta inesperada do Windows Update." };

            var json = outText[(idx + 8)..].Trim();
            var payload = JsonSerializer.Deserialize<ScanPayload>(json);
            if (payload == null)
                return new ScanResult { Ok = false, Error = "Resposta inesperada do Windows Update." };

            if (!payload.Ok)
                return new ScanResult { Ok = false, Error = FriendlyError(payload.Code, payload.Error) };

            return new ScanResult { Ok = true, Updates = payload.Updates ?? new() };
        }
        catch (Exception e)
        {
            return new ScanResult { Ok = false, Error = Shorten(e.Message) };
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    // ---- instalação (PowerShell elevado, UAC) ----

    // Um driver de cada vez (download → install) para o progresso mexer e um
    // driver estragado não deitar o lote todo abaixo. Tudo fica em __LOG__
    // (%AppData%\AdamsToolkit\logs) — é a única janela para o que se passou no
    // PowerShell elevado, que corre escondido e sem stdout.
    private const string InstallScript = @"
$ErrorActionPreference = 'Stop'
function Log($m) { try { Add-Content -Path '__LOG__' -Value (""{0:HH:mm:ss} {1}"" -f (Get-Date), $m) -Encoding UTF8 } catch {} }
function Prog($pct, $msg) { try { ""$pct|$msg"" | Set-Content -Path '__PROG__' -Encoding UTF8 } catch {}; Log ""[$pct%] $msg"" }
function Out-Result($obj) { $obj | ConvertTo-Json -Compress | Set-Content -Path '__OUT__' -Encoding UTF8 }
try {
  $ids = @(__IDS__)
  Log ""início; $($ids.Count) id(s)""
  Prog 2 'A criar ponto de restauro do sistema…'
  # best-effort: falhar aqui nunca trava a instalação (System Restore pode estar off)
  try {
    Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
    $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $old = (Get-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
    Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
    Checkpoint-Computer -Description 'Adams Toolkit — antes de atualizar drivers' -RestorePointType MODIFY_SETTINGS -ErrorAction SilentlyContinue
    if ($null -ne $old) { Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value $old -Type DWord }
    else { Remove-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
    Log 'ponto de restauro: ok (ou já existia recente)'
  } catch { Log ""ponto de restauro falhou: $($_.Exception.Message)"" }
  Prog 5 'A confirmar atualizações no Windows Update…'
  $session  = New-Object -ComObject Microsoft.Update.Session
  $searcher = $session.CreateUpdateSearcher()
__SERVER__
  Log ""serviço: $(if ($useMu) { 'Microsoft Update' } else { 'Windows Update (fallback)' })""
  $q = ""IsInstalled=0 and Type='Driver' and IsHidden=0""
  $result = $null
  try { $result = $searcher.Search($q) }
  catch {
    Log ""pesquisa falhou (hr=0x$('{0:X8}' -f $_.Exception.HResult)): $($_.Exception.Message)""
    if (-not $useMu) { throw }
    # datastore do MU em mau estado → repetir no serviço base
    $s2 = $session.CreateUpdateSearcher(); $s2.ServerSelection = 2
    $result = $s2.Search($q)
    Log 'pesquisa repetida no serviço base: ok'
  }
  Log ""pesquisa devolveu $($result.Updates.Count) driver(s)""
  $targets = @()
  foreach ($u in $result.Updates) {
    if ($ids -contains $u.Identity.UpdateID) {
      try { if (-not $u.EulaAccepted) { $u.AcceptEula() } } catch {}
      $targets += $u
    }
  }
  if ($targets.Count -eq 0) { throw 'As atualizações já não estão disponíveis no Windows Update.' }
  $n = $targets.Count; $done = 0; $failed = 0; $reboot = $false; $lastErr = ''
  for ($i = 0; $i -lt $n; $i++) {
    $u = $targets[$i]
    $title = $u.Title; if ($title.Length -gt 60) { $title = $title.Substring(0, 60) + '…' }
    $base = 10 + [int](85 * $i / $n)
    try {
      $coll = New-Object -ComObject Microsoft.Update.UpdateColl
      $null = $coll.Add($u)
      Prog $base ""($($i+1)/$n) A transferir: $title""
      Log ""download: $($u.Title) ($([math]::Round($u.MaxDownloadSize/1MB,1)) MB)""
      $dl = $session.CreateUpdateDownloader()
      $dl.Updates = $coll
      $dres = $dl.Download()
      Log ""download resultado: $($dres.ResultCode) hr=0x$('{0:X8}' -f $dres.HResult)""
      if ($dres.ResultCode -ne 2 -and $dres.ResultCode -ne 3) { throw ""transferência falhou (código $($dres.ResultCode))"" }
      Prog ($base + [int](42 / $n)) ""($($i+1)/$n) A instalar: $title""
      $inst = $session.CreateUpdateInstaller()
      $inst.Updates = $coll
      $ires = $inst.Install()
      Log ""install resultado: $($ires.ResultCode) hr=0x$('{0:X8}' -f $ires.HResult) reboot=$($ires.RebootRequired)""
      if ($ires.ResultCode -ne 2 -and $ires.ResultCode -ne 3) { throw ""instalação falhou (código $($ires.ResultCode))"" }
      if ($ires.RebootRequired) { $reboot = $true }
      $done++
    } catch {
      $failed++; $lastErr = $_.Exception.Message
      Log ""ERRO em '$($u.Title)': $($_.Exception.Message)""
    }
  }
  Prog 100 'Concluído'
  Log ""fim: $done ok, $failed falharam""
  if ($done -eq 0) { throw ""Nenhum driver instalado — último erro: $lastErr"" }
  Out-Result @{ ok = $true; reboot = [bool]$reboot; done = $done; failed = $failed }
} catch {
  Log ""FATAL: $($_.Exception.Message)`n$($_.ScriptStackTrace)""
  Out-Result @{ ok = $false; error = ""$($_.Exception.Message)"" }
  exit 1
}
";

    /// <summary>
    /// Instala (elevado) as atualizações indicadas por UpdateID.
    /// Devolve (sucesso, mensagem, precisaReiniciar).
    /// </summary>
    public static async Task<(bool ok, string message, bool reboot)> InstallAsync(
        IEnumerable<string> updateIds, IProgress<(double pct, string label)> progress)
    {
        var ids = updateIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Contains('\''))
            .Distinct().ToList();
        if (ids.Count == 0) return (false, "Nada selecionado.", false);

        var tag = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"adams-wuinstall-{tag}.ps1");
        var progPath = Path.Combine(Path.GetTempPath(), $"adams-wuprog-{tag}.txt");
        var outPath = Path.Combine(Path.GetTempPath(), $"adams-wuout-{tag}.json");
        var logPath = InstallLogPath(); // fica no disco — é o diagnóstico quando algo corre mal

        try
        {
            var script = InstallScript
                .Replace("__SERVER__", SelectServerSnippet)
                .Replace("__IDS__", string.Join(",", ids.Select(i => $"'{i}'")))
                .Replace("__PROG__", progPath)
                .Replace("__LOG__", logPath)
                .Replace("__OUT__", outPath);
            await File.WriteAllTextAsync(scriptPath, script, System.Text.Encoding.UTF8);

            Process? p;
            try
            {
                // runas exige UseShellExecute → sem redirect; resultado vai por ficheiros
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
                return (false, "Permissão de administrador recusada — instalação cancelada.", false);
            }
            if (p == null) return (false, "Não foi possível iniciar a instalação.", false);

            progress.Report((-1, "A pedir permissão de administrador…"));

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            while (!p.HasExited && DateTime.UtcNow < deadline)
            {
                ReportFromFile(progPath, progress);
                await Task.Delay(600);
            }
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "A instalação demorou demasiado tempo.", false);
            }
            ReportFromFile(progPath, progress);

            if (!File.Exists(outPath))
                return (false, "A instalação terminou sem resultado — verifica o Windows Update.", false);

            var payload = JsonSerializer.Deserialize<InstallPayload>(
                await File.ReadAllTextAsync(outPath));
            if (payload == null)
                return (false, "Resultado ilegível da instalação.", false);
            if (!payload.Ok)
                return (false, Shorten(payload.Error) ?? "A instalação falhou.", false);

            var msg = payload.Failed > 0
                ? $"{payload.Done} driver(s) instalados, {payload.Failed} falharam."
                : "Drivers instalados com sucesso.";
            if (payload.Reboot) msg += " Reinicia o PC para concluir.";
            return (true, msg, payload.Reboot);
        }
        catch (Exception e)
        {
            return (false, Shorten(e.Message) ?? "Erro na instalação.", false);
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(progPath);
            TryDelete(outPath);
        }
    }

    private static void ReportFromFile(string path, IProgress<(double, string)> progress)
    {
        try
        {
            if (!File.Exists(path)) return;
            var parts = File.ReadAllText(path).Trim().Split('|', 2);
            if (parts.Length == 2 && double.TryParse(parts[0],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                progress.Report((pct, parts[1]));
        }
        catch { }
    }

    /// <summary>Pasta de registos (%AppData%\AdamsToolkit\logs) — diagnóstico de instalações.</summary>
    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(ConfigService.DataDir, "logs");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    private static string InstallLogPath() =>
        Path.Combine(LogsDir, $"driver-install-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    /// <summary>
    /// "Excepção de HRESULT: 0x8024802A" não diz nada a um jogador. Traduz os
    /// códigos do Windows Update para linguagem normal; desconhecidos ficam com
    /// mensagem genérica + código (para suporte).
    /// </summary>
    private static string? FriendlyError(string? code, string? raw)
    {
        var hex = (code ?? "").Trim();
        // 0x80131500 = wrapper do PowerShell; o código real vem no texto ("HRESULT: 0x8024802A")
        if (hex.Length == 0 || hex == "0x00000000" || hex == "0x80131500")
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw ?? "", @"0x[0-9A-Fa-f]{8}");
            hex = m.Success ? m.Value : "";
        }
        if (hex.Length == 0) return Shorten(raw);

        var known = hex.ToUpperInvariant() switch
        {
            // família 0x80248xxx = base de dados local do Windows Update
            var h when h.StartsWith("0X80248") =>
                "A base de dados do Windows Update está ocupada ou danificada. Reinicia o PC e tenta de novo",
            "0X8024402C" or "0X80072EE2" or "0X80072EFD" or "0X80072EFE" =>
                "O Windows Update não conseguiu ligar-se aos servidores da Microsoft (rede/proxy)",
            "0X80244022" or "0X8024402F" =>
                "Os servidores da Microsoft estão ocupados",
            "0X8024001E" or "0X8024000B" =>
                "O serviço Windows Update foi interrompido a meio da verificação",
            "0X80070005" =>
                "O Windows bloqueou o acesso ao Windows Update (permissões)",
            "0X8024500C" or "0X80240438" =>
                "A verificação de drivers está bloqueada por política do Windows Update",
            _ => null,
        };

        return known != null ? $"{known} ({hex})" : Shorten($"O Windows Update não respondeu ({hex}).");
    }

    private static string? Shorten(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 140 ? s : s[..140] + "…";
    }

    private static void TryDelete(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }
}
