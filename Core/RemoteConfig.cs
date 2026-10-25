using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdamsToolkit.Core;

// ===== Modelos da configuração remota (editável sem atualizar a app) =====

public class ToolkitConfig
{
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; } = 1;
    // Backend de verificação (bot Adao.exe): a app pede código + valida a presença no Discord aqui.
    [JsonPropertyName("authApiBase")] public string AuthApiBase { get; set; } = Endpoints.Primary;
    [JsonPropertyName("discordInvite")] public string DiscordInvite { get; set; } = "https://discord.gg/2VzE6PZ7J8";
    // Pack ReShade do ATL PVP (zip com dxgi.dll + ReShade.ini + reshade-shaders)
    [JsonPropertyName("reshadeZipUrl")] public string ReshadeZipUrl { get; set; } =
        Endpoints.Url("/files/reshade-atl.zip");
    [JsonPropertyName("communityStatus")] public string CommunityStatus { get; set; } = "Online";
    [JsonPropertyName("news")] public List<NewsItem> News { get; set; } = new();
    [JsonPropertyName("apps")] public List<AppEntry> Apps { get; set; } = new();
    [JsonPropertyName("fivemLinks")] public List<LinkItem> FiveMLinks { get; set; } = new();
    [JsonPropertyName("fivemGuides")] public List<LinkItem> FiveMGuides { get; set; } = new();
    [JsonPropertyName("resourceHub")] public List<ResourceCategory> ResourceHub { get; set; } = new();
}

public class NewsItem
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
}

public class AppEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "📦"; // emoji — sem assets externos
    // Secção no Install Center (ex: Launchers, Windows, FiveM); ordem = 1ª aparição na lista
    [JsonPropertyName("category")] public string Category { get; set; } = "Outros";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "latest";
    [JsonPropertyName("website")] public string Website { get; set; } = "";
    // ID no Windows Package Manager (winget) — instalação silenciosa 1-clique.
    // Vazio = usa downloadUrl/website. Winget aponta para os instaladores oficiais.
    [JsonPropertyName("wingetId")] public string WingetId { get; set; } = "";
    // Link OFICIAL de download (fallback sem winget). Se vazio, o botão abre o website.
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    // Deteção de instalação
    [JsonPropertyName("detectPaths")] public List<string> DetectPaths { get; set; } = new();
    [JsonPropertyName("detectRegistryNames")] public List<string> DetectRegistryNames { get; set; } = new();
}

public class LinkItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "🔗";
}

public class ResourceCategory
{
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("items")] public List<LinkItem> Items { get; set; } = new();
}

// ===== Loader: URL remota -> cache local -> default embebido =====

public static class ConfigService
{
    // URL da configuração remota (JSON). Alterar aqui ou via ficheiro
    // %AppData%\AdamsToolkit\config-url.txt (uma linha com a URL).
    public const string ConfigPath = "/files/config.json";
    public static string DefaultConfigUrl => Endpoints.Url(ConfigPath);

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "AdamsToolkit");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string CachePath => Path.Combine(DataDir, "config-cache.json");

    public static ToolkitConfig Current { get; private set; } = new();
    public static bool LoadedFromRemote { get; private set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>URL forçado à mão em %AppData%\AdamsToolkit\config-url.txt (escape de emergência).</summary>
    private static string? GetOverrideUrl()
    {
        try
        {
            var overrideFile = Path.Combine(DataDir, "config-url.txt");
            if (File.Exists(overrideFile))
            {
                var url = File.ReadAllText(overrideFile).Trim();
                if (url.StartsWith("http")) return url;
            }
        }
        catch (Exception ex) { Log.Swallow("ler config-url.txt", ex); }
        return null;
    }

    /// <summary>Override (se existir) + todos os hosts conhecidos, por ordem.</summary>
    private static IEnumerable<string> ConfigUrls()
    {
        var ov = GetOverrideUrl();
        if (ov != null) yield return ov;
        foreach (var u in Endpoints.All(ConfigPath)) yield return u;
    }

    public static async Task LoadAsync()
    {
        var json = await Endpoints.FirstOkAsync(ConfigUrls(), u => Http.GetStringAsync(u));
        if (json != null)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<ToolkitConfig>(json);
                if (cfg != null)
                {
                    Current = cfg;
                    LoadedFromRemote = true;
                    File.WriteAllText(CachePath, json);
                    return;
                }
            }
            catch (Exception ex) { Log.Error("config remota ilegível — a usar cache", ex); }
        }

        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<ToolkitConfig>(File.ReadAllText(CachePath));
                if (cached != null) { Current = cached; return; }
            }
        }
        catch (Exception ex) { Log.Swallow("ler cache da config", ex); }

        Current = BuiltInDefault();
    }

    // Catálogo por omissão — todos os links são páginas/downloads OFICIAIS dos fabricantes.
    private static ToolkitConfig BuiltInDefault() => new()
    {
        CommunityStatus = "Online",
        News = new()
        {
            new NewsItem { Date = "2026-07-14", Title = "Bem-vindo ao Adams Toolkit",
                Body = "Instala tudo o que precisas para jogar FiveM num só sítio. Configuração remota ainda não ligada — a usar catálogo local." }
        },
        Apps = new()
        {
            new AppEntry { Id = "steam", Category = "Launchers", WingetId = "Valve.Steam", Name = "Steam", Icon = "🕹️",
                Description = "Loja e launcher de jogos da Valve.", Website = "https://store.steampowered.com",
                DownloadUrl = "https://cdn.fastly.steamstatic.com/client/installer/SteamSetup.exe",
                FileName = "SteamSetup.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Steam\\steam.exe" },
                DetectRegistryNames = new() { "Steam" } },
            new AppEntry { Id = "rockstar", Category = "Launchers", WingetId = "RockstarGames.Launcher", Name = "Rockstar Games Launcher", Icon = "⭐",
                Description = "Necessário para GTA V.", Website = "https://socialclub.rockstargames.com/rockstar-games-launcher",
                DownloadUrl = "https://gamedownloads.rockstargames.com/public/installer/Rockstar-Games-Launcher.exe",
                FileName = "Rockstar-Games-Launcher.exe",
                DetectPaths = new() { "%ProgramFiles%\\Rockstar Games\\Launcher\\Launcher.exe" },
                DetectRegistryNames = new() { "Rockstar Games Launcher" } },
            new AppEntry { Id = "epic", Category = "Launchers", WingetId = "EpicGames.EpicGamesLauncher", Name = "Epic Games Launcher", Icon = "🏪",
                Description = "Loja Epic (Fortnite, jogos grátis semanais).", Website = "https://store.epicgames.com",
                DownloadUrl = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi",
                FileName = "EpicGamesLauncherInstaller.msi",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Epic Games\\Launcher\\Portal\\Binaries\\Win64\\EpicGamesLauncher.exe" },
                DetectRegistryNames = new() { "Epic Games Launcher" } },
            new AppEntry { Id = "ea", Category = "Launchers", WingetId = "ElectronicArts.EADesktop", Name = "EA app", Icon = "🅴",
                Description = "Launcher da Electronic Arts (ex-Origin).", Website = "https://www.ea.com/ea-app",
                DownloadUrl = "https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller.exe",
                FileName = "EAappInstaller.exe",
                DetectPaths = new() { "%ProgramFiles%\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe" },
                DetectRegistryNames = new() { "EA app" } },
            new AppEntry { Id = "ubisoft", Category = "Launchers", WingetId = "Ubisoft.Connect", Name = "Ubisoft Connect", Icon = "🌀",
                Description = "Launcher da Ubisoft.", Website = "https://ubisoftconnect.com",
                DownloadUrl = "https://static3.cdn.ubi.com/orbit/launcher_installer/UbisoftConnectInstaller.exe",
                FileName = "UbisoftConnectInstaller.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe" },
                DetectRegistryNames = new() { "Ubisoft Connect" } },
            new AppEntry { Id = "battlenet", Category = "Launchers", WingetId = "Blizzard.BattleNet", Name = "Battle.net", Icon = "❄️",
                Description = "Launcher da Blizzard (WoW, Diablo, CoD).", Website = "https://www.blizzard.com/apps/battle.net",
                DownloadUrl = "https://downloader.battle.net/download/getInstaller?os=win&installer=Battle.net-Setup.exe",
                FileName = "Battle.net-Setup.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Battle.net\\Battle.net Launcher.exe" },
                DetectRegistryNames = new() { "Battle.net" } },
            new AppEntry { Id = "gog", Category = "Launchers", WingetId = "GOG.Galaxy", Name = "GOG Galaxy", Icon = "🌌",
                Description = "Launcher da GOG, junta todas as bibliotecas.", Website = "https://www.gog.com/galaxy",
                DownloadUrl = "https://webinstallers.gog-statics.com/download/GOG_Galaxy_2.0.exe",
                FileName = "GOG_Galaxy_2.0.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\GOG Galaxy\\GalaxyClient.exe" },
                DetectRegistryNames = new() { "GOG GALAXY" } },
            new AppEntry { Id = "discord", Category = "Comunicação", WingetId = "Discord.Discord", Name = "Discord", Icon = "💬",
                Description = "Comunicação por voz e texto.", Website = "https://discord.com",
                DownloadUrl = "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64",
                FileName = "DiscordSetup.exe",
                DetectPaths = new() { "%LOCALAPPDATA%\\Discord\\Update.exe" },
                DetectRegistryNames = new() { "Discord" } },
            new AppEntry { Id = "vencord", Category = "Comunicação", WingetId = "", Name = "Vencord", Icon = "🧩",
                Description = "Mod do Discord (plugins, temas). Requer Discord instalado.", Website = "https://vencord.dev",
                DownloadUrl = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstaller.exe",
                FileName = "VencordInstaller.exe",
                DetectPaths = new() { "%APPDATA%\\Vencord" },
                DetectRegistryNames = new() { "Vencord" } },
            // Sem WingetId de propósito: o instalador do TS3 em modo silencioso (winget)
            // instala o Overwolf embutido sem perguntar. Download direto → instalador
            // interativo, onde o jogador destica a checkbox do Overwolf.
            new AppEntry { Id = "teamspeak", Category = "Comunicação", Name = "TeamSpeak", Icon = "🎙️",
                Description = "Voz de baixa latência para gaming.", Website = "https://teamspeak.com",
                DownloadUrl = "https://files.teamspeak-services.com/releases/client/3.6.2/TeamSpeak3-Client-win64-3.6.2.exe",
                FileName = "TeamSpeak3-Client-win64.exe",
                DetectPaths = new() { "%ProgramFiles%\\TeamSpeak 3 Client\\ts3client_win64.exe" },
                DetectRegistryNames = new() { "TeamSpeak" } },
            new AppEntry { Id = "rustdesk", Category = "Acesso Remoto", WingetId = "RustDesk.RustDesk", Name = "RustDesk", Icon = "🖥️",
                Description = "Acesso remoto open-source, alternativa ao TeamViewer.", Website = "https://rustdesk.com",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\RustDesk\\rustdesk.exe" },
                DetectRegistryNames = new() { "RustDesk" } },
            new AppEntry { Id = "anydesk", Category = "Acesso Remoto", WingetId = "AnyDeskSoftwareGmbH.AnyDesk", Name = "AnyDesk", Icon = "🔴",
                Description = "Acesso remoto rápido e leve.", Website = "https://anydesk.com",
                DownloadUrl = "https://download.anydesk.com/AnyDesk.exe", FileName = "AnyDesk.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\AnyDesk\\AnyDesk.exe" },
                DetectRegistryNames = new() { "AnyDesk" } },
            new AppEntry { Id = "teamviewer", Category = "Acesso Remoto", WingetId = "TeamViewer.TeamViewer", Name = "TeamViewer", Icon = "🔵",
                Description = "Acesso remoto clássico.", Website = "https://www.teamviewer.com",
                DownloadUrl = "https://download.teamviewer.com/download/TeamViewer_Setup_x64.exe",
                FileName = "TeamViewer_Setup_x64.exe",
                DetectPaths = new() { "%ProgramFiles%\\TeamViewer\\TeamViewer.exe" },
                DetectRegistryNames = new() { "TeamViewer" } },
            new AppEntry { Id = "chrome", Category = "Browser", WingetId = "Google.Chrome", Name = "Google Chrome", Icon = "🌐",
                Description = "Browser da Google.", Website = "https://www.google.com/chrome/",
                DownloadUrl = "https://dl.google.com/chrome/install/latest/chrome_installer.exe",
                FileName = "chrome_installer.exe",
                DetectPaths = new() { "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe", "%ProgramFiles(x86)%\\Google\\Chrome\\Application\\chrome.exe" },
                DetectRegistryNames = new() { "Google Chrome" } },
            new AppEntry { Id = "firefox", Category = "Browser", WingetId = "Mozilla.Firefox", Name = "Mozilla Firefox", Icon = "🦊",
                Description = "Browser open-source da Mozilla.", Website = "https://www.mozilla.org/firefox/",
                DownloadUrl = "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=pt-PT",
                FileName = "FirefoxSetup.exe",
                DetectPaths = new() { "%ProgramFiles%\\Mozilla Firefox\\firefox.exe" },
                DetectRegistryNames = new() { "Mozilla Firefox" } },
            new AppEntry { Id = "edge", Category = "Browser", WingetId = "Microsoft.Edge", Name = "Microsoft Edge", Icon = "🧭",
                Description = "Browser da Microsoft (vem com o Windows).", Website = "https://www.microsoft.com/edge",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe" },
                DetectRegistryNames = new() { "Microsoft Edge" } },
            new AppEntry { Id = "opera", Category = "Browser", WingetId = "Opera.Opera", Name = "Opera", Icon = "🅾️",
                Description = "Browser com VPN grátis integrada.", Website = "https://www.opera.com",
                DownloadUrl = "https://net.geo.opera.com/opera/stable/windows",
                FileName = "OperaSetup.exe",
                DetectPaths = new() { "%LOCALAPPDATA%\\Programs\\Opera\\launcher.exe" },
                DetectRegistryNames = new() { "Opera Stable", "Opera 1" } },
            new AppEntry { Id = "operagx", Category = "Browser", WingetId = "Opera.OperaGX", Name = "Opera GX", Icon = "🎮",
                Description = "Browser gaming (limita CPU/RAM).", Website = "https://www.opera.com/gx",
                DownloadUrl = "https://net.geo.opera.com/opera_gx/stable/windows",
                FileName = "OperaGXSetup.exe",
                DetectPaths = new() { "%LOCALAPPDATA%\\Programs\\Opera GX\\launcher.exe" },
                DetectRegistryNames = new() { "Opera GX" } },
            new AppEntry { Id = "brave", Category = "Browser", WingetId = "Brave.Brave", Name = "Brave", Icon = "🦁",
                Description = "Browser focado em privacidade, bloqueia ads.", Website = "https://brave.com",
                DownloadUrl = "https://laptop-updates.brave.com/latest/winx64",
                FileName = "BraveSetup.exe",
                DetectPaths = new() { "%ProgramFiles%\\BraveSoftware\\Brave-Browser\\Application\\brave.exe" },
                DetectRegistryNames = new() { "Brave" } },
            new AppEntry { Id = "vivaldi", Category = "Browser", WingetId = "Vivaldi.Vivaldi", Name = "Vivaldi", Icon = "🎻",
                Description = "Browser ultra-personalizável.", Website = "https://vivaldi.com",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%LOCALAPPDATA%\\Vivaldi\\Application\\vivaldi.exe" },
                DetectRegistryNames = new() { "Vivaldi" } },
            new AppEntry { Id = "vlc", Category = "Media & Streaming", WingetId = "VideoLAN.VLC", Name = "VLC", Icon = "🎦",
                Description = "Leitor de vídeo/áudio universal, grátis.", Website = "https://www.videolan.org/vlc/",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\VideoLAN\\VLC\\vlc.exe" },
                DetectRegistryNames = new() { "VLC" } },
            new AppEntry { Id = "spotify", Category = "Media & Streaming", WingetId = "Spotify.Spotify", Name = "Spotify", Icon = "🎵",
                Description = "Streaming de música.", Website = "https://www.spotify.com",
                DownloadUrl = "https://download.scdn.co/SpotifySetup.exe", FileName = "SpotifySetup.exe",
                DetectPaths = new() { "%APPDATA%\\Spotify\\Spotify.exe" },
                DetectRegistryNames = new() { "Spotify" } },
            new AppEntry { Id = "obs", Category = "Media & Streaming", WingetId = "OBSProject.OBSStudio", Name = "OBS Studio", Icon = "🎥",
                Description = "Gravação e streaming.", Website = "https://obsproject.com",
                DownloadUrl = "https://obsproject.com/download", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\obs-studio\\bin\\64bit\\obs64.exe" },
                DetectRegistryNames = new() { "OBS Studio" } },
            new AppEntry { Id = "vcpp", Category = "Runtimes Essenciais", WingetId = "Microsoft.VCRedist.2015+.x64", Name = "Visual C++ Redistributable", Icon = "🧩",
                Description = "Runtime necessário por muitos jogos (x64).", Website = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
                DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe", FileName = "vc_redist.x64.exe",
                DetectRegistryNames = new() { "Microsoft Visual C++ 2015-2022 Redistributable (x64)" } },
            new AppEntry { Id = "directx", Category = "Runtimes Essenciais", WingetId = "Microsoft.DirectX", Name = "DirectX Runtime", Icon = "🎛️",
                Description = "DirectX End-User Runtime (junho 2010) — pedido por jogos antigos.", Website = "https://www.microsoft.com/download/details.aspx?id=35",
                DownloadUrl = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe",
                FileName = "dxwebsetup.exe",
                DetectRegistryNames = new() { "DirectX" } },
            new AppEntry { Id = "webview2", Category = "Runtimes Essenciais", WingetId = "Microsoft.EdgeWebView2Runtime", Name = "Edge WebView2 Runtime", Icon = "🧩",
                Description = "Runtime web usado por muitas apps (Discord, launchers, NUI).", Website = "https://developer.microsoft.com/microsoft-edge/webview2/",
                DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703", FileName = "MicrosoftEdgeWebView2Setup.exe",
                DetectRegistryNames = new() { "Microsoft Edge WebView2 Runtime" } },
            new AppEntry { Id = "dotnet8", Category = "Runtimes Essenciais", WingetId = "Microsoft.DotNet.DesktopRuntime.8", Name = ".NET Desktop Runtime 8", Icon = "🟣",
                Description = "Necessário por apps .NET 8 (incluindo o Adams Toolkit).", Website = "https://dotnet.microsoft.com/download/dotnet/8.0",
                DownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe", FileName = "windowsdesktop-runtime-8-win-x64.exe",
                DetectRegistryNames = new() { "Windows Desktop Runtime - 8" } },
            new AppEntry { Id = "dotnet9", Category = "Runtimes Essenciais", WingetId = "Microsoft.DotNet.DesktopRuntime.9", Name = ".NET Desktop Runtime 9", Icon = "🟣",
                Description = "Necessário por apps .NET 9.", Website = "https://dotnet.microsoft.com/download/dotnet/9.0",
                DownloadUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe", FileName = "windowsdesktop-runtime-9-win-x64.exe",
                DetectRegistryNames = new() { "Windows Desktop Runtime - 9" } },
            new AppEntry { Id = "dotnet10", Category = "Runtimes Essenciais", WingetId = "Microsoft.DotNet.DesktopRuntime.10", Name = ".NET Desktop Runtime 10", Icon = "🟣",
                Description = "Necessário por apps .NET 10.", Website = "https://dotnet.microsoft.com/download/dotnet/10.0",
                DownloadUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", FileName = "windowsdesktop-runtime-10-win-x64.exe",
                DetectRegistryNames = new() { "Windows Desktop Runtime - 10" } },
            new AppEntry { Id = "7zip", Category = "Utilitários", WingetId = "7zip.7zip", Name = "7-Zip", Icon = "🗜️",
                Description = "Compressão/extração de ficheiros, grátis.", Website = "https://www.7-zip.org",
                DownloadUrl = "https://www.7-zip.org/a/7z2409-x64.exe", FileName = "7z-x64.exe",
                DetectPaths = new() { "%ProgramFiles%\\7-Zip\\7z.exe" },
                DetectRegistryNames = new() { "7-Zip" } },
            new AppEntry { Id = "qbittorrent", Category = "Utilitários", WingetId = "qBittorrent.qBittorrent", Name = "qBittorrent", Icon = "🌊",
                Description = "Cliente torrent open-source, sem ads.", Website = "https://www.qbittorrent.org",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\qBittorrent\\qbittorrent.exe" },
                DetectRegistryNames = new() { "qBittorrent" } },
            new AppEntry { Id = "notepadpp", Category = "Utilitários", WingetId = "Notepad++.Notepad++", Name = "Notepad++", Icon = "📝",
                Description = "Editor de texto/código leve.", Website = "https://notepad-plus-plus.org",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\Notepad++\\notepad++.exe" },
                DetectRegistryNames = new() { "Notepad++" } },
            new AppEntry { Id = "sharex", Category = "Utilitários", WingetId = "ShareX.ShareX", Name = "ShareX", Icon = "📸",
                Description = "Capturas de ecrã e gravação avançadas.", Website = "https://getsharex.com",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\ShareX\\ShareX.exe" },
                DetectRegistryNames = new() { "ShareX" } },
            new AppEntry { Id = "lightshot", Category = "Utilitários", WingetId = "Skillbrains.Lightshot", Name = "Lightshot", Icon = "💡",
                Description = "Capturas de ecrã rápidas (print screen).", Website = "https://app.prntscr.com",
                DownloadUrl = "https://app.prntscr.com/build/setup-lightshot.exe", FileName = "setup-lightshot.exe",
                DetectPaths = new() { "%ProgramFiles(x86)%\\Skillbrains\\lightshot\\Lightshot.exe" },
                DetectRegistryNames = new() { "Lightshot" } },
            new AppEntry { Id = "powertoys", Category = "Utilitários", WingetId = "Microsoft.PowerToys", Name = "PowerToys", Icon = "🔧",
                Description = "Utilitários avançados da Microsoft para Windows.", Website = "https://learn.microsoft.com/windows/powertoys/",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\PowerToys\\PowerToys.exe", "%LOCALAPPDATA%\\PowerToys\\PowerToys.exe" },
                DetectRegistryNames = new() { "PowerToys" } },
            new AppEntry { Id = "gdrive", Category = "Utilitários", WingetId = "Google.GoogleDrive", Name = "Google Drive", Icon = "📁",
                Description = "Sincronização de ficheiros na cloud.", Website = "https://www.google.com/drive/download/",
                DownloadUrl = "https://dl.google.com/drive-file-stream/GoogleDriveSetup.exe", FileName = "GoogleDriveSetup.exe",
                DetectPaths = new() { "%ProgramFiles%\\Google\\Drive File Stream" },
                DetectRegistryNames = new() { "Google Drive" } },
            new AppEntry { Id = "bcu", Category = "Utilitários", WingetId = "Klocman.BulkCrapUninstaller", Name = "Bulk Crap Uninstaller", Icon = "🗑️",
                Description = "Desinstala programas em massa, remove restos.", Website = "https://www.bcuninstaller.com",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\BCUninstaller\\BCUninstaller.exe" },
                DetectRegistryNames = new() { "Bulk Crap Uninstaller", "BCUninstaller" } },
            new AppEntry { Id = "malwarebytes", Category = "Utilitários", WingetId = "Malwarebytes.Malwarebytes", Name = "Malwarebytes", Icon = "🛡️",
                Description = "Anti-malware para limpezas ao PC.", Website = "https://www.malwarebytes.com",
                DownloadUrl = "https://downloads.malwarebytes.com/file/mb-windows", FileName = "MBSetup.exe",
                DetectPaths = new() { "%ProgramFiles%\\Malwarebytes\\Anti-Malware\\mbam.exe" },
                DetectRegistryNames = new() { "Malwarebytes" } },
            new AppEntry { Id = "warp", Category = "Utilitários", WingetId = "Cloudflare.Warp", Name = "Cloudflare WARP", Icon = "☁️",
                Description = "DNS 1.1.1.1 + VPN grátis da Cloudflare.", Website = "https://one.one.one.one",
                DownloadUrl = "https://1111-releases.cloudflareclient.com/win/latest", FileName = "Cloudflare_WARP.msi",
                DetectPaths = new() { "%ProgramFiles%\\Cloudflare\\Cloudflare WARP\\Cloudflare WARP.exe" },
                DetectRegistryNames = new() { "Cloudflare WARP" } },
            new AppEntry { Id = "winrar", Category = "Utilitários", WingetId = "RARLab.WinRAR", Name = "WinRAR", Icon = "📚",
                Description = "Compressão/extração RAR/ZIP.", Website = "https://www.win-rar.com",
                DownloadUrl = "https://www.win-rar.com/download.html", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\WinRAR\\WinRAR.exe" },
                DetectRegistryNames = new() { "WinRAR" } },
            new AppEntry { Id = "hwinfo", Category = "Hardware & Diagnóstico", WingetId = "REALiX.HWiNFO", Name = "HWiNFO", Icon = "📊",
                Description = "Monitorização completa de hardware (temperaturas, sensores).", Website = "https://www.hwinfo.com",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\HWiNFO64\\HWiNFO64.exe" },
                DetectRegistryNames = new() { "HWiNFO" } },
            new AppEntry { Id = "cpuz", Category = "Hardware & Diagnóstico", WingetId = "CPUID.CPU-Z", Name = "CPU-Z", Icon = "🧠",
                Description = "Informação detalhada do CPU, RAM e motherboard.", Website = "https://www.cpuid.com/softwares/cpu-z.html",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\CPUID\\CPU-Z\\cpuz.exe" },
                DetectRegistryNames = new() { "CPU-Z" } },
            new AppEntry { Id = "gpuz", Category = "Hardware & Diagnóstico", WingetId = "TechPowerUp.GPU-Z", Name = "GPU-Z", Icon = "🎞️",
                Description = "Informação detalhada da placa gráfica.", Website = "https://www.techpowerup.com/gpuz/",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles(x86)%\\GPU-Z\\GPU-Z.exe" },
                DetectRegistryNames = new() { "GPU-Z" } },
            new AppEntry { Id = "occt", Category = "Hardware & Diagnóstico", WingetId = "OCBASE.OCCT", Name = "OCCT", Icon = "🔥",
                Description = "Testes de stress e estabilidade (CPU/GPU/RAM/PSU).", Website = "https://www.ocbase.com",
                DownloadUrl = "", FileName = "",
                DetectRegistryNames = new() { "OCCT" } },
            new AppEntry { Id = "amdchipset", Category = "Hardware & Diagnóstico", Name = "AMD Chipset Software", Icon = "🔴",
                Description = "Drivers de chipset para motherboards AMD. O site dá sempre a versão mais recente.",
                Website = "https://www.amd.com/en/support/download/drivers.html",
                DownloadUrl = "", FileName = "",
                DetectRegistryNames = new() { "AMD Chipset Software" } },
            new AppEntry { Id = "ryzenmaster", Category = "Hardware & Diagnóstico", WingetId = "AMD.RyzenMaster", Name = "AMD Ryzen Master", Icon = "⚡",
                Description = "Overclock e monitorização de CPUs Ryzen.", Website = "https://www.amd.com/en/products/software/ryzen-master.html",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\AMD\\RyzenMaster\\bin\\AMD Ryzen Master.exe" },
                DetectRegistryNames = new() { "AMD Ryzen Master" } },
            new AppEntry { Id = "nvidia", Category = "Drivers", Name = "NVIDIA App", Icon = "🟢",
                Description = "Drivers GeForce + otimização (substitui o GeForce Experience). O site dá sempre a versão mais recente.",
                Website = "https://www.nvidia.com/en-us/software/nvidia-app/",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\NVIDIA Corporation\\NVIDIA app\\CEF\\NVIDIA app.exe" },
                DetectRegistryNames = new() { "NVIDIA app", "NVIDIA App" } },
            new AppEntry { Id = "amd", Category = "Drivers", Name = "AMD Software Adrenalin", Icon = "🔴",
                Description = "Drivers Radeon + painel Adrenalin. O site deteta a tua GPU e dá o driver mais recente.",
                Website = "https://www.amd.com/en/support/download/drivers.html",
                DownloadUrl = "", FileName = "",
                DetectPaths = new() { "%ProgramFiles%\\AMD\\CNext\\CNext\\RadeonSoftware.exe" },
                DetectRegistryNames = new() { "AMD Software" } },
            new AppEntry { Id = "driverbooster", Category = "Drivers", WingetId = "IObit.DriverBooster", Name = "Driver Booster", Icon = "🚀",
                Description = "Atualiza drivers antigos automaticamente (IObit).",
                Website = "https://www.iobit.com/en/driver-booster.php",
                DownloadUrl = "https://cdn.iobit.com/dl/driver_booster_setup.exe",
                FileName = "driver_booster_setup.exe",
                DetectRegistryNames = new() { "Driver Booster" } },
            new AppEntry { Id = "fivem", Category = "FiveM", Name = "FiveM", Icon = "🎮",
                Description = "Cliente multiplayer GTA V (Cfx.re).", Website = "https://fivem.net",
                DownloadUrl = "https://runtime.fivem.net/client/FiveM.exe", FileName = "FiveM.exe",
                DetectPaths = new() { "%LOCALAPPDATA%\\FiveM\\FiveM.exe" },
                DetectRegistryNames = new() { "FiveM" } },
        },
        FiveMLinks = new()
        {
            new LinkItem { Name = "Cfx.re", Url = "https://cfx.re", Icon = "🌐", Description = "Site oficial Cfx.re" },
            new LinkItem { Name = "Fórum Cfx.re", Url = "https://forum.cfx.re", Icon = "💬", Description = "Fórum oficial da comunidade" },
            new LinkItem { Name = "Estado dos servidores Cfx.re", Url = "https://status.cfx.re", Icon = "📡", Description = "Status da plataforma" },
        },
        ResourceHub = new()
        {
            new ResourceCategory { Category = "Ferramentas", Items = new()
            {
                new LinkItem { Name = "CodeWalker", Url = "https://github.com/dexyfex/CodeWalker", Icon = "🗺️", Description = "Explorador/editor de mapas GTA V" },
                new LinkItem { Name = "OpenIV", Url = "https://openiv.com", Icon = "📂", Description = "Editor de ficheiros do jogo" },
            }},
            new ResourceCategory { Category = "Comunidades", Items = new()
            {
                new LinkItem { Name = "Cfx.re Discord", Url = "https://discord.gg/fivem", Icon = "💬", Description = "Discord oficial FiveM" },
            }},
        }
    };
}
