using System;
using System.IO;

namespace AdamsToolkit.Core;

/// <summary>
/// Leitura de temperaturas SEM driver de kernel.
///
/// A LibreHardwareMonitor foi REMOVIDA (2026-08-30): a DLL trazia o driver
/// WinRing0 embutido como recurso (WinRing0.gz / WinRing0x64.gz) e o Windows
/// Defender marcava o exe como "VulnerableDriver:WinNT/Winring0" (Grave) no
/// scan estático — mesmo com IsCpuEnabled/IsMotherboardEnabled já a false desde
/// a v1.68.1, porque os bytes do driver continuavam DENTRO do binário
/// single-file. Sem a lib não há bytes de WinRing0 em lado nenhum → sem alerta.
///
/// Consequência: as temperaturas passam a vir só das fontes nativas do
/// SystemMonitor — GPU NVIDIA via nvidia-smi, CPU via zona térmica ACPI (WMI).
/// GPU AMD/Intel e CPUs sem ACPI ficam N/D (o card esconde-se sozinho). Voltar
/// a ter CPU/AMD real exige uma LHM com PawnIO (driver assinado, sem CVE) — não
/// reintroduzir a 0.9.4 com WinRing0.
/// </summary>
public static class HardwareMonitor
{
    /// <summary>Sem lib: as temperaturas vêm do SystemMonitor (nvidia-smi / WMI).</summary>
    public static (double cpu, double gpu) ReadTemps() => (-1, -1);

    /// <summary>Mantido por compat: limpa qualquer ".sys" órfão de versões antigas.</summary>
    public static void Shutdown() => CleanupStrayDriver();

    private static void CleanupStrayDriver()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var sys = Path.ChangeExtension(exe, ".sys");
            if (!File.Exists(sys)) return;
            try { File.SetAttributes(sys, FileAttributes.Normal); } catch { } // Hidden/System bloqueia o Delete
            File.Delete(sys); // falha silenciosa se ainda estiver em uso
        }
        catch { }
    }
}
