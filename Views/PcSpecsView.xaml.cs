using System.IO;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AdamsToolkit.Views;

public record SpecRow(string Label, string Value);
public record SpecSection(string Title, List<SpecRow> Items);

/// <summary>
/// "O meu PC": specs completas lidas por WMI/registry. Leitura corre uma vez em
/// background (a view é cacheada pelo MainWindow); cada secção falha isolada —
/// hardware exótico nunca deixa a página em branco.
/// </summary>
public partial class PcSpecsView : UserControl
{
    private List<SpecSection> _sections = new();

    public PcSpecsView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_sections.Count > 0) return;
            _sections = await Task.Run(ReadAll);
            LoadingText.Visibility = Visibility.Collapsed;
            SectionsList.ItemsSource = _sections;
        };
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine($"=== Specs (Adams Toolkit v{Core.SelfUpdater.CurrentVersion}) ===");
        foreach (var s in _sections)
        {
            sb.AppendLine();
            sb.AppendLine($"[{s.Title}]");
            foreach (var r in s.Items) sb.AppendLine($"{r.Label}: {r.Value}");
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            CopyBtn.Content = "✓  Copiado";
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);
                CopyBtn.Content = "📋  Copiar specs";
            });
        }
        catch { }
    }

    // ---------- leitura ----------

    private static List<SpecSection> ReadAll()
    {
        var list = new List<SpecSection>();
        void Add(string title, string icon, Func<List<SpecRow>> read)
        {
            try
            {
                var rows = read();
                if (rows.Count > 0) list.Add(new SpecSection($"{icon} {title}", rows));
            }
            catch { }
        }

        Add("PROCESSADOR", "💻", ReadCpu);
        Add("PLACA GRÁFICA", "🎮", ReadGpu);
        Add("MEMÓRIA RAM", "🧠", ReadRam);
        Add("MOTHERBOARD", "🔧", ReadBoard);
        Add("ARMAZENAMENTO", "💾", ReadDisks);
        Add("SISTEMA", "🖥️", ReadOs);
        Add("REDE", "🌐", ReadNetwork);
        Add("ECRÃ", "🖼️", ReadDisplays);
        return list;
    }

    private static IEnumerable<ManagementObject> Query(string wql, string? scope = null)
    {
        using var s = scope == null
            ? new ManagementObjectSearcher(wql)
            : new ManagementObjectSearcher(scope, wql);
        foreach (ManagementObject o in s.Get()) yield return o;
    }

    private static string Gb(double bytes) => $"{bytes / 1024 / 1024 / 1024:0.#} GB";

    private static List<SpecRow> ReadCpu()
    {
        var rows = new List<SpecRow>();
        foreach (var o in Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor"))
        {
            rows.Add(new("Modelo", o["Name"]?.ToString()?.Trim() ?? "—"));
            rows.Add(new("Núcleos / threads", $"{o["NumberOfCores"]} núcleos / {o["NumberOfLogicalProcessors"]} threads"));
            if (o["MaxClockSpeed"] is uint mhz && mhz > 0)
                rows.Add(new("Frequência base", $"{mhz / 1000.0:0.0#} GHz"));
        }
        return rows;
    }

    private static List<SpecRow> ReadGpu()
    {
        var rows = new List<SpecRow>();
        var n = 0;
        foreach (var o in Query("SELECT Name,AdapterRAM,DriverVersion,DriverDate FROM Win32_VideoController"))
        {
            var name = o["Name"]?.ToString() ?? "—";
            n++;
            var prefix = n > 1 ? $"GPU {n}" : "GPU";
            rows.Add(new(prefix, name));

            // AdapterRAM é uint32 (máx 4 GB) — a VRAM real vem do registry (qwMemorySize)
            var vram = VramFromRegistry(name);
            if (vram == null && o["AdapterRAM"] is uint ram && ram > 0) vram = Gb(ram);
            if (vram != null) rows.Add(new($"{prefix} · VRAM", vram));

            var drv = o["DriverVersion"]?.ToString();
            if (o["DriverDate"]?.ToString() is string dd && dd.Length >= 8)
            {
                try { drv += $" ({ManagementDateTimeConverter.ToDateTime(dd):dd/MM/yyyy})"; }
                catch { }
            }
            if (drv != null) rows.Add(new($"{prefix} · driver", drv));
        }
        return rows;
    }

    private static string? VramFromRegistry(string gpuName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.StartsWith('0')) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc")?.ToString() != gpuName) continue;
                if (k.GetValue("HardwareInformation.qwMemorySize") is long q && q > 0)
                    return Gb(q);
            }
        }
        catch { }
        return null;
    }

    private static List<SpecRow> ReadRam()
    {
        var rows = new List<SpecRow>();
        ulong total = 0;
        var sticks = new List<string>();
        foreach (var o in Query("SELECT Capacity,ConfiguredClockSpeed,Speed,Manufacturer,SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            var cap = o["Capacity"] is ulong c ? c : 0;
            total += cap;
            var speed = o["ConfiguredClockSpeed"] is uint cs && cs > 0 ? cs
                      : o["Speed"] is uint sp ? sp : 0;
            var type = o["SMBIOSMemoryType"] switch
            {
                uint t when t == 20 => "DDR", uint t when t == 21 => "DDR2",
                uint t when t == 24 => "DDR3", uint t when t == 26 => "DDR4",
                uint t when t == 34 => "DDR5", _ => "",
            };
            var mfg = o["Manufacturer"]?.ToString()?.Trim();
            var desc = $"{Gb(cap)} {type} {(speed > 0 ? $"{speed} MHz" : "")}".Trim();
            if (!string.IsNullOrEmpty(mfg) && mfg != "Unknown") desc += $" — {mfg}";
            sticks.Add(desc);
        }
        // Nalgumas BIOS o SMBIOS não lista todos os módulos (Win32_PhysicalMemory
        // incompleto — visto num cliente com 16 GB reais e só 1×8 GB reportado).
        // Cruzar com o total que o Windows vê (mesma API do Gestor de Tarefas):
        // GlobalMemoryStatusEx desconta o reservado p/ hardware, daí a folga de 1.5 GB.
        var wmiGb = total / 1073741824.0;
        var osGb = Core.SystemMonitor.GetRam().totalGb;
        var smbiosIncomplete = osGb > 0 && osGb - wmiGb > 1.5;
        if (smbiosIncomplete)
        {
            rows.Add(new("Total", $"{Math.Round(osGb)} GB"));
            rows.Add(new("Nota", $"A BIOS só reporta {Gb(total)} em módulos — total corrigido pelo Windows."));
        }
        else if (total > 0) rows.Add(new("Total", Gb(total)));
        for (var i = 0; i < sticks.Count; i++) rows.Add(new($"Módulo {i + 1}", sticks[i]));
        // com SMBIOS incompleto a contagem de módulos não é fiável — sem nota de single channel
        if (sticks.Count == 1 && !smbiosIncomplete)
            rows.Add(new("Nota", "1 módulo = single channel. Um 2º módulo igual pode dar +10-20% FPS."));
        return rows;
    }

    private static List<SpecRow> ReadBoard()
    {
        var rows = new List<SpecRow>();
        foreach (var o in Query("SELECT Manufacturer,Product FROM Win32_BaseBoard"))
            rows.Add(new("Modelo", $"{o["Manufacturer"]} {o["Product"]}".Trim()));
        foreach (var o in Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            rows.Add(new("BIOS", o["SMBIOSBIOSVersion"]?.ToString() ?? "—"));
        return rows;
    }

    private static List<SpecRow> ReadDisks()
    {
        var rows = new List<SpecRow>();
        try
        {
            var n = 0;
            foreach (var o in Query("SELECT FriendlyName,MediaType,Size FROM MSFT_PhysicalDisk",
                                    @"root\microsoft\windows\storage"))
            {
                n++;
                var type = o["MediaType"] switch
                {
                    ushort t when t == 4 => "SSD", ushort t when t == 3 => "HDD",
                    ushort t when t == 5 => "SCM", _ => "?",
                };
                var size = o["Size"] is ulong s ? Gb(s) : "—";
                rows.Add(new($"Disco {n}", $"{o["FriendlyName"]} — {size} ({type})"));
            }
        }
        catch { }
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            rows.Add(new($"Volume {d.Name}", $"{Gb(d.TotalFreeSpace)} livres de {Gb(d.TotalSize)}"));
        }
        return rows;
    }

    private static List<SpecRow> ReadOs()
    {
        var rows = new List<SpecRow>();
        foreach (var o in Query("SELECT Caption,Version,BuildNumber,InstallDate FROM Win32_OperatingSystem"))
        {
            rows.Add(new("Windows", $"{o["Caption"]?.ToString()?.Trim()} (build {o["BuildNumber"]})"));
            if (o["InstallDate"]?.ToString() is string inst && inst.Length >= 8)
            {
                try { rows.Add(new("Instalado em", ManagementDateTimeConverter.ToDateTime(inst).ToString("dd/MM/yyyy"))); }
                catch { }
            }
        }
        rows.Add(new("Arquitetura", Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
        return rows;
    }

    private static List<SpecRow> ReadNetwork()
    {
        var rows = new List<SpecRow>();
        foreach (var o in Query("SELECT Name,Speed FROM Win32_NetworkAdapter WHERE NetConnectionStatus=2 AND PhysicalAdapter=TRUE"))
        {
            var speed = "";
            if (o["Speed"] is ulong bps && bps > 0)
                speed = bps >= 1_000_000_000 ? $" — link {bps / 1_000_000_000.0:0.#} Gbps" : $" — link {bps / 1_000_000} Mbps";
            rows.Add(new("Adaptador ativo", $"{o["Name"]}{speed}"));
        }
        return rows;
    }

    private static List<SpecRow> ReadDisplays()
    {
        var rows = new List<SpecRow>();
        var n = 0;
        foreach (var o in Query("SELECT CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate FROM Win32_VideoController"))
        {
            if (o["CurrentHorizontalResolution"] is not uint w || w == 0) continue;
            n++;
            var hz = o["CurrentRefreshRate"] is uint r && r > 1 ? $" @ {r} Hz" : "";
            rows.Add(new(n > 1 ? $"Resolução (GPU {n})" : "Resolução",
                $"{w} × {o["CurrentVerticalResolution"]}{hz}"));
        }
        return rows;
    }
}
