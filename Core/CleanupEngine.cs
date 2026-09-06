namespace AdamsToolkit.Core;

/// <summary>
/// Motor de limpeza de temporários — DESATIVADO E ESVAZIADO (wrp 2026-09-03).
///
/// A limpeza foi desligada em 2026-08-26 (v1.67.0) depois de um jogador levar ban
/// numa verificação de PC: temp/Reciclagem vazios foram lidos como "limpeza de
/// rasto". O código continuava compilado (só a UI desligada), mas isso deixava no
/// binário as strings `SHEmptyRecycleBin` e os caminhos %TEMP%/WER — que um
/// screenshare/anti-cheat a fazer scan ESTÁTICO ao exe pode apitar como
/// ferramenta de limpeza (falso-positivo → risco de ban para todos).
///
/// Por isso o motor foi reduzido a stubs inertes: sem P/Invoke ao shell32, sem
/// caminhos de temporários, sem apagar nada. A API pública fica só para os
/// chamadores compilarem; nada corre. NÃO reintroduzir a lógica de apagar —
/// se algum dia voltar, só temp >7 dias e NUNCA a Reciclagem (ver [[toolkit-cleanup]]).
/// </summary>
public static class CleanupEngine
{
    public sealed record Result(long Bytes, int Files, int Skipped);

    /// <summary>Sem alvos: a limpeza está desativada.</summary>
    public static IReadOnlyList<(string Name, string Path)> Targets()
        => System.Array.Empty<(string, string)>();

    /// <summary>No-op: não apaga nada.</summary>
    public static Result CleanAll(System.Action<string>? progress = null) => new(0, 0, 0);

    /// <summary>No-op: não mexe na Reciclagem.</summary>
    public static bool EmptyRecycleBin() => false;
}
