using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdamsToolkit.Core;

/// <summary>App da Microsoft Store / Appx (Xbox, Cortana, Clipchamp, Teams pessoal…).</summary>
public class AppxPackage
{
    public string Name { get; set; } = "";              // ex.: Microsoft.XboxApp
    public string DisplayName { get; set; } = "";       // nome legível (manifest) ou Name limpo
    public string PackageFullName { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";         // "CN=Microsoft Corporation, …" → só o CN
    public string InstallLocation { get; set; } = "";
    public bool NonRemovable { get; set; }              // Windows recusa remover (Store, Edge, Settings…)
    public string SignatureKind { get; set; } = "";     // Store | System | Developer | None
    public string LogoPath { get; set; } = "";          // png absoluto quando encontrado
    public long SizeBytes { get; set; }
    public bool IsSystem => SignatureKind.Equals("System", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Desinstalador de apps do Windows (Store/Appx). Tudo via PowerShell
/// (<c>Get-AppxPackage</c> / <c>Remove-AppxPackage</c>) para o utilizador atual — sem admin.
/// Apps NonRemovable (Store, Edge, Definições, Explorador) aparecem mas bloqueadas:
/// o Windows recusa e podem partir o sistema; não se tenta contornar.
/// </summary>
public static class AppxUninstaller
{
    // enums/Version saem como número/objeto no ConvertTo-Json do PS 5.1 → forçar strings
    private const string ListScript = @"
$ErrorActionPreference='SilentlyContinue'
Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } | ForEach-Object {
  [pscustomobject]@{
    Name=[string]$_.Name; PackageFullName=[string]$_.PackageFullName; PackageFamilyName=[string]$_.PackageFamilyName
    Version=[string]$_.Version; Publisher=[string]$_.Publisher; InstallLocation=[string]$_.InstallLocation
    NonRemovable=[bool]$_.NonRemovable; SignatureKind=[string]$_.SignatureKind
  }
} | ConvertTo-Json -Compress
";

    // pacotes que nunca devem ser removidos mesmo que o Windows deixe (mata Start/Definições/Store)
    private static readonly string[] Protected =
    {
        "Microsoft.WindowsStore", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.Windows.Search", "Microsoft.UI.Xaml", "Microsoft.VCLibs",
        "Microsoft.NET.Native", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.SecHealthUI",
        "MicrosoftWindows.Client", "Microsoft.Windows.CloudExperienceHost", "Microsoft.LockApp", "Microsoft.Win32WebViewHost",
        "Microsoft.WindowsAppRuntime", "Microsoft.Windows.ContentDeliveryManager", "Microsoft.WindowsTerminal",
    };

    public static bool IsProtected(AppxPackage p) =>
        p.NonRemovable || Protected.Any(x => p.Name.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    public static async Task<List<AppxPackage>> EnumerateAsync()
    {
        var json = await RunPsAsync(ListScript, TimeSpan.FromSeconds(90));
        var list = new List<AppxPackage>();
        Log($"enumerate: {json.Length} bytes; head={json[..Math.Min(300, json.Length)]}");
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new() { root };
            foreach (var e in items)
            {
                var p = new AppxPackage
                {
                    Name = Str(e, "Name"),
                    PackageFullName = Str(e, "PackageFullName"),
                    PackageFamilyName = Str(e, "PackageFamilyName"),
                    Version = Str(e, "Version"),
                    Publisher = CleanPublisher(Str(e, "Publisher")),
                    InstallLocation = Str(e, "InstallLocation"),
                    SignatureKind = e.TryGetProperty("SignatureKind", out var sk) && sk.ValueKind == JsonValueKind.Number
                        ? (sk.GetInt32() switch { 1 => "Developer", 2 => "Enterprise", 3 => "Store", 4 => "System", _ => "None" })
                        : Str(e, "SignatureKind"),
                    NonRemovable = e.TryGetProperty("NonRemovable", out var nr) && nr.ValueKind == JsonValueKind.True,
                };
                if (p.Name.Length == 0) continue;
                // só o que o utilizador pode mesmo remover: apps da Store/dev. Componentes
                // internos assinados "System" (OOBE, Search, PinningConfirmationDialog, hosts
                // do shell…) e protegidas ficam de fora — não são "apps" e o Windows recusa.
                if (IsProtected(p) || p.IsSystem) continue;
                if (p.SignatureKind.Equals("Developer", StringComparison.OrdinalIgnoreCase) == false &&
                    p.SignatureKind.Equals("Store", StringComparison.OrdinalIgnoreCase) == false &&
                    p.SignatureKind.Equals("Enterprise", StringComparison.OrdinalIgnoreCase) == false) continue;
                ReadManifest(p);
                list.Add(p);
            }
        }
        catch { }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        Log($"enumerate: {list.Count} removíveis");
        return list;
    }

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdamsToolkit", "appx.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    /// <summary>
    /// Remove o pacote. 1º tenta para o utilizador atual (sem admin); se o Windows recusar
    /// (pacote provisionado / all-users / 0x80073CFA), pede UAC e remove para todos os
    /// utilizadores + desprovisiona. Devolve null = OK, senão mensagem de erro.
    /// </summary>
    private static string Q(string v) => v.Replace("'", "''"); // literal PowerShell single-quoted

    public static async Task<string?> RemoveAsync(AppxPackage p)
    {
        if (IsProtected(p)) return "Esta app faz parte do Windows e não pode ser removida.";
        var script = $"$ErrorActionPreference='Stop'\ntry {{ Remove-AppxPackage -Package '{Q(p.PackageFullName)}' ; 'OK' }} catch {{ 'ERR: ' + $_.Exception.Message }}";
        var outp = (await RunPsAsync(script, TimeSpan.FromMinutes(3))).Trim();
        Log($"remove {p.PackageFullName} (user) → {outp}");
        if (outp.StartsWith("OK") && !await ExistsAsync(p)) return null;

        // ainda existe ou falhou → tentar elevado (todos os utilizadores + provisioning)
        var err = outp.StartsWith("ERR: ") ? outp[5..].Split('\n')[0].Trim() : "";
        var elevated = await RemoveElevatedAsync(p);
        Log($"remove {p.PackageFullName} (admin) → {elevated ?? "OK"}");
        if (elevated == null && !await ExistsAsync(p)) return null;
        return elevated ?? (err.Length > 0 ? err : "O Windows não removeu o pacote.");
    }

    /// <summary>PowerShell elevado (UAC). Sem stdout — resultado vai para um ficheiro temp.</summary>
    private static async Task<string?> RemoveElevatedAsync(AppxPackage p)
    {
        var result = Path.Combine(Path.GetTempPath(), $"adams-appx-r-{Guid.NewGuid():N}.txt");
        var script = Path.Combine(Path.GetTempPath(), $"adams-appx-e-{Guid.NewGuid():N}.ps1");
        var body = $@"$ErrorActionPreference='Continue'
$out=@()
try {{ Remove-AppxPackage -Package '{Q(p.PackageFullName)}' -AllUsers -ErrorAction Stop; $out+='alluser OK' }} catch {{ $out+='alluser ERR: '+$_.Exception.Message }}
try {{ Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq '{Q(p.Name)}' }} | Remove-AppxProvisionedPackage -Online -ErrorAction Stop | Out-Null; $out+='prov OK' }} catch {{ $out+='prov ERR: '+$_.Exception.Message }}
try {{ Remove-AppxPackage -Package '{Q(p.PackageFullName)}' -ErrorAction Stop; $out+='user OK' }} catch {{ $out+='user ERR: '+$_.Exception.Message }}
Set-Content -Path '{result}' -Value ($out -join ""`n"") -Encoding UTF8
";
        try
        {
            await File.WriteAllTextAsync(script, body, System.Text.Encoding.UTF8);
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{script}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process? proc;
            try { proc = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { return "Precisa de administrador (UAC cancelado)."; }
            if (proc == null) return "PowerShell indisponível.";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await proc.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return "Demorou demasiado."; }
            var txt = File.Exists(result) ? (await File.ReadAllTextAsync(result)).Trim() : "";
            Log($"elevated out: {txt.Replace('\n', '|')}");
            if (txt.Contains("alluser OK") || txt.Contains("user OK")) return null;
            var first = txt.Split('\n').FirstOrDefault(l => l.Contains("ERR: "));
            return first == null ? "Sem resposta do PowerShell elevado." : first[(first.IndexOf("ERR: ") + 5)..].Trim();
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(script); File.Delete(result); } catch { } }
    }

    /// <summary>Confirma se o pacote ainda existe (depois de remover).</summary>
    public static async Task<bool> ExistsAsync(AppxPackage p)
    {
        var outp = await RunPsAsync($"$ErrorActionPreference='SilentlyContinue'\nif (Get-AppxPackage -Name '{Q(p.Name)}') {{ 'YES' }} else {{ 'NO' }}", TimeSpan.FromSeconds(30));
        return outp.Trim().StartsWith("YES");
    }

    // ---- helpers ----

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string CleanPublisher(string s)
    {
        var m = Regex.Match(s, @"CN=([^,]+)");
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : s;
    }

    /// <summary>Nome legível + logo a partir do AppxManifest.xml (DisplayName ms-resource: → fallback ao Name).</summary>
    private static void ReadManifest(AppxPackage p)
    {
        p.DisplayName = FriendlyFromName(p.Name);
        try
        {
            var manifest = Path.Combine(p.InstallLocation, "AppxManifest.xml");
            if (p.InstallLocation.Length == 0 || !File.Exists(manifest)) return;
            var doc = XDocument.Load(manifest);
            var props = doc.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "Properties");
            var dn = props?.Elements().FirstOrDefault(x => x.Name.LocalName == "DisplayName")?.Value?.Trim() ?? "";
            if (dn.Length > 0 && !dn.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) p.DisplayName = dn;
            var logo = props?.Elements().FirstOrDefault(x => x.Name.LocalName == "Logo")?.Value?.Trim() ?? "";
            if (logo.Length > 0) p.LogoPath = ResolveLogo(p.InstallLocation, logo);
        }
        catch { }
    }

    private static string FriendlyFromName(string name)
    {
        var last = name.Split('.').Last();
        last = Regex.Replace(last, "([a-z])([A-Z])", "$1 $2");
        return last;
    }

    /// <summary>Assets/StoreLogo.png raramente existe tal e qual — o Windows guarda variantes .scale-100/.targetsize-48.</summary>
    private static string ResolveLogo(string root, string rel)
    {
        try
        {
            var full = Path.Combine(root, rel.Replace('/', '\\'));
            if (File.Exists(full)) return full;
            var dir = Path.GetDirectoryName(full) ?? root;
            var stem = Path.GetFileNameWithoutExtension(full);
            if (!Directory.Exists(dir)) return "";
            var candidates = Directory.GetFiles(dir, stem + "*.png");
            if (candidates.Length == 0) return "";
            // preferir scale-100/125/150, depois qualquer
            return candidates.OrderBy(f =>
            {
                var n = Path.GetFileName(f);
                if (n.Contains("scale-100")) return 0;
                if (n.Contains("scale-125")) return 1;
                if (n.Contains("scale-150")) return 2;
                if (n.Contains("targetsize-48")) return 3;
                return 9;
            }).First();
        }
        catch { return ""; }
    }

    public static System.Windows.Media.Imaging.BitmapSource? LoadLogo(AppxPackage p)
    {
        if (p.LogoPath.Length == 0 || !File.Exists(p.LogoPath)) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48;
            bmp.UriSource = new Uri(p.LogoPath);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static async Task<string> RunPsAsync(string script, TimeSpan timeout)
    {
        var path = Path.Combine(Path.GetTempPath(), $"adams-appx-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(path, script, System.Text.Encoding.UTF8);
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{path}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return ""; }
            return await stdout;
        }
        catch { return ""; }
        finally { try { File.Delete(path); } catch { } }
    }
}
