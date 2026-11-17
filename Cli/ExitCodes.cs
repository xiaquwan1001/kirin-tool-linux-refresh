namespace Kirin_Tool.Cli;

/// <summary>Stable CLI exit codes (documented in docs/cli-mcp.md).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int NoDevice = 2;
    public const int DeviceRejected = 3;
    public const int Gated = 4;
    public const int FileOrVerify = 5;

    /// <summary>
    /// D-07 / D-08: the command ran and told the truth, but the truth is "this environment is not
    /// healthy" — a non-Huawei fastboot binary (audit P11/P13) or a device that connects and answers
    /// nothing (audit F17/F18). Distinct from 5 (nothing usable found) and from 2/3 (device state).
    /// </summary>
    public const int Degraded = 6;
}
