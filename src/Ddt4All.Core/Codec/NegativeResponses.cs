namespace Ddt4All.Core.Codec;

/// <summary>Negative response code (NRC) descriptions, identical to the Python <c>negrsp</c> table.</summary>
public static class NegativeResponses
{
    /// <summary>Service id marking a negative response (0x7F).</summary>
    public const byte NegativeResponseSid = 0x7F;

    /// <summary>NRC 0x78: request correctly received, response pending.</summary>
    public const byte ResponsePending = 0x78;

    private static readonly Dictionary<byte, string> Table = new()
    {
        [0x10] = "NR: General Reject",
        [0x11] = "NR: Service Not Supported",
        [0x12] = "NR: SubFunction Not Supported",
        [0x13] = "NR: Incorrect Message Length Or Invalid Format",
        [0x21] = "NR: Busy Repeat Request",
        [0x22] = "NR: Conditions Not Correct Or Request Sequence Error",
        [0x23] = "NR: Routine Not Complete",
        [0x24] = "NR: Request Sequence Error",
        [0x31] = "NR: Request Out Of Range",
        [0x33] = "NR: Security Access Denied- Security Access Requested  ",
        [0x35] = "NR: Invalid Key",
        [0x36] = "NR: Exceed Number Of Attempts",
        [0x37] = "NR: Required Time Delay Not Expired",
        [0x40] = "NR: Download not accepted",
        [0x41] = "NR: Improper download type",
        [0x42] = "NR: Can not download to specified address",
        [0x43] = "NR: Can not download number of bytes requested",
        [0x50] = "NR: Upload not accepted",
        [0x51] = "NR: Improper upload type",
        [0x52] = "NR: Can not upload from specified address",
        [0x53] = "NR: Can not upload number of bytes requested",
        [0x70] = "NR: Upload Download NotAccepted",
        [0x71] = "NR: Transfer Data Suspended",
        [0x72] = "NR: General Programming Failure",
        [0x73] = "NR: Wrong Block Sequence Counter",
        [0x74] = "NR: Illegal Address In Block Transfer",
        [0x75] = "NR: Illegal Byte Count In Block Transfer",
        [0x76] = "NR: Illegal Block Transfer Type",
        [0x77] = "NR: Block Transfer Data Checksum Error",
        [0x78] = "NR: Request Correctly Received-Response Pending",
        [0x79] = "NR: Incorrect ByteCount During Block Transfer",
        [0x7E] = "NR: SubFunction Not Supported In Active Session",
        [0x7F] = "NR: Service Not Supported In Active Session",
        [0x80] = "NR: Service Not Supported In Active Diagnostic Mode",
        [0x81] = "NR: Rpm Too High",
        [0x82] = "NR: Rpm Too Low",
        [0x83] = "NR: Engine Is Running",
        [0x84] = "NR: Engine Is Not Running",
        [0x85] = "NR: Engine RunTime TooLow",
        [0x86] = "NR: Temperature Too High",
        [0x87] = "NR: Temperature Too Low",
        [0x88] = "NR: Vehicle Speed Too High",
        [0x89] = "NR: Vehicle Speed Too Low",
        [0x8A] = "NR: Throttle/Pedal Too High",
        [0x8B] = "NR: Throttle/Pedal Too Low",
        [0x8C] = "NR: Transmission Range In Neutral",
        [0x8D] = "NR: Transmission Range In Gear",
        [0x8F] = "NR: Brake Switch(es)NotClosed (brake pedal not pressed or not applied)",
        [0x90] = "NR: Shifter Lever Not In Park ",
        [0x91] = "NR: Torque Converter Clutch Locked",
        [0x92] = "NR: Voltage Too High",
        [0x93] = "NR: Voltage Too Low",
    };

    /// <summary>Returns the description for an NRC, or "Unregistered error" (as the Python reference does).</summary>
    public static string Describe(byte nrc) => Table.TryGetValue(nrc, out var s) ? s : "Unregistered error";

    /// <summary>True if the NRC is in the known table.</summary>
    public static bool IsKnown(byte nrc) => Table.ContainsKey(nrc);
}
