namespace Ddt4All.Core.Ecu;

/// <summary>A fault-code device (DTC definition).</summary>
public sealed class EcuDevice : INamed
{
    /// <summary>Device name.</summary>
    public string Name { get; set; } = "";
    /// <summary>DTC number.</summary>
    public int Dtc { get; set; }
    /// <summary>DTC type.</summary>
    public int DtcType { get; set; }
    /// <summary>Device data name to failure flag map.</summary>
    public Dictionary<string, string> DeviceData { get; } = new();
}

/// <summary>Auto-identification block of an ECU definition (diag version/supplier/soft/version).</summary>
public readonly record struct AutoIdent(string DiagVersion, string Supplier, string Soft, string Version);
