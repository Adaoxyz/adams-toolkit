using System.Management;

namespace AdamsToolkit.Core;

/// <summary>
/// Inventário dos drivers instalados no PC (WMI <c>Win32_PnPSignedDriver</c>) —
/// a vista "estilo Driver Booster" da aba Atualizações: que hardware existe, que
/// versão/data de driver tem e há quanto tempo.
///
/// REGRA (zero falsos positivos): nada aqui diz "desatualizado" por conta própria.
/// A etiqueta de atualização só aparece quando uma fonte oficial confirma —
/// Windows Update/Microsoft Update ([[DriverUpdateService]]) ou a API da NVIDIA
/// ([[NvidiaDriverCheck]]). A idade do driver é informativa, não é um alerta.
/// </summary>
public static class DeviceDriverInventory
{
    public sealed class DeviceDriver
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public DateTime? Date { get; init; }
        public string Provider { get; init; } = "";
        public string DeviceClass { get; init; } = "";
        public string Category { get; init; } = "";
        public string Icon { get; init; } = "";

        /// <summary>Confirmado por fonte oficial (WU/MU ou NVIDIA), nunca inferido da data.</summary>
        public bool UpdateAvailable { get; set; }
        public string UpdateNote { get; set; } = "";

        /// <summary>UpdateID do Windows Update quando é instalável pela app.</summary>
        public string WuUpdateId { get; set; } = "";

        /// <summary>Download oficial do fabricante quando não passa pelo WU (ex.: NVIDIA).</summary>
        public string VendorUrl { get; set; } = "";

        /// <summary>Driver genérico da própria Microsoft (não é do fabricante).</summary>
        public bool IsMicrosoft =>
            Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

        public int AgeYears => Date is { } d ? (int)((DateTime.Now - d).TotalDays / 365) : -1;
    }

    public sealed class Result
    {
        public bool Ok { get; init; }
        public List<DeviceDriver> Drivers { get; init; } = new();
        public DateTime When { get; init; } = DateTime.Now;
    }

    public static Result? Last { get; private set; }

    // classes PnP que interessam a um jogador; o resto (impressoras virtuais,
    // dispositivos de software, etc.) é ruído
    private static readonly Dictionary<string, (string cat, string icon, int order)> Classes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DISPLAY"]      = ("Placa gráfica", "🎮", 0),
            ["MEDIA"]        = ("Áudio", "🔊", 1),
            ["NET"]          = ("Rede", "🌐", 2),
            ["BLUETOOTH"]    = ("Bluetooth", "📶", 3),
            ["SYSTEM"]       = ("Chipset / Sistema", "🧩", 4),
            ["HDC"]          = ("Armazenamento", "💾", 5),
            ["SCSIADAPTER"]  = ("Armazenamento", "💾", 5),
            ["DISKDRIVE"]    = ("Armazenamento", "💾", 5),
            ["USB"]          = ("USB", "🔌", 6),
            ["MONITOR"]      = ("Monitor", "🖥", 7),
            ["HIDCLASS"]     = ("Periféricos", "🖱", 8),
            ["KEYBOARD"]     = ("Periféricos", "🖱", 8),
            ["MOUSE"]        = ("Periféricos", "🖱", 8),
            ["IMAGE"]        = ("Câmara / Imagem", "📷", 9),
            ["CAMERA"]       = ("Câmara / Imagem", "📷", 9),
        };

    public static int CategoryOrder(string category) =>
        Classes.Values.FirstOrDefault(v => v.cat == category).order;

    public static async Task<Result> ScanAsync()
    {
        var r = await Task.Run(Scan);
        Last = r;
        return r;
    }

    private static Result Scan()
    {
        var list = new List<DeviceDriver>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, DriverVersion, DriverDate, DriverProviderName, DeviceClass " +
                "FROM Win32_PnPSignedDriver");
            foreach (var mo in searcher.Get())
            {
                var name = (mo["DeviceName"] as string ?? "").Trim();
                var cls = (mo["DeviceClass"] as string ?? "").Trim();
                if (name.Length == 0 || !Classes.TryGetValue(cls, out var meta)) continue;

                list.Add(new DeviceDriver
                {
                    Name = name,
                    Version = (mo["DriverVersion"] as string ?? "").Trim(),
                    Date = ParseWmiDate(mo["DriverDate"] as string),
                    Provider = (mo["DriverProviderName"] as string ?? "").Trim(),
                    DeviceClass = cls,
                    Category = meta.cat,
                    Icon = meta.icon,
                });
            }
        }
        catch
        {
            return new Result { Ok = false };
        }

        // o mesmo driver aparece repetido (hubs USB, núcleos, etc.)
        var deduped = list
            .GroupBy(d => $"{d.Name.ToLowerInvariant()}|{d.Version}")
            .Select(g => g.First())
            .OrderBy(d => CategoryOrder(d.Category))
            .ThenBy(d => d.Date ?? DateTime.MaxValue)
            .ToList();

        return new Result { Ok = true, Drivers = deduped };
    }

    private static DateTime? ParseWmiDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); } catch { return null; }
    }

    // ---- cruzamento com as fontes oficiais ----

    /// <summary>
    /// Marca os dispositivos para os quais o Windows Update/Microsoft Update trouxe
    /// mesmo um driver novo. Sem correspondência → fica sem etiqueta (nunca inventa).
    /// </summary>
    public static void MarkFromWindowsUpdate(
        IEnumerable<DeviceDriver> drivers, IEnumerable<DriverUpdateService.DriverUpdate> updates)
    {
        var ups = updates.ToList();
        foreach (var d in drivers)
        {
            var dn = Norm(d.Name);
            if (dn.Length < 6) continue;
            var hit = ups.FirstOrDefault(u =>
            {
                var model = Norm(u.Model);
                var title = Norm(u.Title);
                if (model.Length >= 6 && (dn.Contains(model) || model.Contains(dn))) return true;
                return title.Length >= 6 && title.Contains(dn);
            });
            if (hit == null) continue;
            d.UpdateAvailable = true;
            d.WuUpdateId = hit.Id;
            d.UpdateNote = "Windows Update tem um driver novo para este dispositivo";
        }
    }

    /// <summary>Marca a GPU NVIDIA quando a API oficial da NVIDIA anuncia versão mais recente.</summary>
    public static void MarkNvidia(IEnumerable<DeviceDriver> drivers, string latestVersion, string downloadUrl)
    {
        foreach (var d in drivers.Where(x =>
                     x.DeviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                     x.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            d.UpdateAvailable = true;
            d.VendorUrl = downloadUrl;
            d.UpdateNote = $"NVIDIA {latestVersion} disponível no site oficial";
        }
    }

    private static string Norm(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
