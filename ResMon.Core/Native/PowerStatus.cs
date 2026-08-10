using System.Runtime.InteropServices;

namespace ResMon.Core.Native;

/// <summary>Der Energiezustand, wie ihn Windows selbst meldet.</summary>
public readonly record struct SystemPower(
    bool HasBattery,
    bool OnAcPower,
    bool Charging,
    double? ChargePercent,
    TimeSpan? Remaining)
{
    public static readonly SystemPower None = new(false, true, false, null, null);
}

/// <summary>
/// <c>GetSystemPowerStatus</c> aus kernel32. Diese Angaben kommen unmittelbar vom
/// Energiedienst des Systems und stehen auf jedem Gerät zur Verfügung — anders
/// als die Akkusensoren, die je nach Hardware fehlen können.
/// </summary>
public static class PowerStatus
{
    private const byte BATTERY_FLAG_CHARGING = 8;
    private const byte BATTERY_FLAG_NO_BATTERY = 128;
    private const byte UNKNOWN_PERCENT = 255;
    private const int UNKNOWN_TIME = -1;

    private const byte AC_OFFLINE = 0;

    public static SystemPower Read()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            return SystemPower.None;

        bool hasBattery = (status.BatteryFlag & BATTERY_FLAG_NO_BATTERY) == 0 && status.BatteryFlag != 255;
        if (!hasBattery)
            return SystemPower.None;

        return new SystemPower(
            HasBattery: true,
            OnAcPower: status.ACLineStatus != AC_OFFLINE,
            Charging: (status.BatteryFlag & BATTERY_FLAG_CHARGING) != 0,
            ChargePercent: status.BatteryLifePercent == UNKNOWN_PERCENT ? null : status.BatteryLifePercent,
            Remaining: status.BatteryLifeTime == UNKNOWN_TIME
                ? null
                : TimeSpan.FromSeconds(status.BatteryLifeTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
