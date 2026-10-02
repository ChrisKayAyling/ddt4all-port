using Ddt4All.Core.Abstractions;
using Ddt4All.Comms.Elm;

namespace Ddt4All.Comms.Usb;

/// <summary>Known USB adapters (VID/PID table from the Python usb_can.py).</summary>
public sealed record UsbDeviceInfo(int VendorId, int ProductId, string Description, string ProfileKey);

public static class UsbDeviceCatalog
{
    /// <summary>The "USB CAN" HID/vendor-request device handled natively (VID 16C0 / PID 05DF).</summary>
    public static UsbDeviceInfo NativeUsbCan { get; } = new(0x16C0, 0x05DF, "USB CAN adapter", "usbcan");

    public static IReadOnlyList<UsbDeviceInfo> All { get; } = new[]
    {
        NativeUsbCan,
        new UsbDeviceInfo(0x0403, 0x6001, "FTDI based ELS27/ELM327", "els27"),
        new UsbDeviceInfo(0x10C4, 0xEA60, "CP210x based ELS27/ELM327", "els27"),
        new UsbDeviceInfo(0x1A86, 0x7523, "CH340 based ELS27/ELM327", "els27"),
        new UsbDeviceInfo(0x067B, 0x2303, "PL2303 based ELS27/ELM327", "els27"),
        new UsbDeviceInfo(0x1209, 0x0001, "VGate USB CAN", "vgate"),
        new UsbDeviceInfo(0x04D8, 0x000A, "VGate iCar Pro USB", "vgate"),
        new UsbDeviceInfo(0x0403, 0x6015, "ObdLink USB CAN", "obdlink"),
        new UsbDeviceInfo(0x16D0, 0x0498, "ObdLink Direct USB", "obdlink"),
        new UsbDeviceInfo(0x1A86, 0x7584, "Vlinker USB CAN", "vlinker"),
        new UsbDeviceInfo(0x0403, 0x6010, "Vlinker FTDI USB", "vlinker"),
        new UsbDeviceInfo(0x0483, 0x5740, "DERLEK USB-DIAG2", "derlek_usb_diag2"),
        new UsbDeviceInfo(0x0483, 0x5741, "DERLEK USB-DIAG3", "derlek_usb_diag3"),
        new UsbDeviceInfo(0x108C, 0x0156, "Bosch MTS 6531", "bosch_mts"),
        new UsbDeviceInfo(0x108C, 0x0157, "Bosch MTS 6532", "bosch_mts"),
        new UsbDeviceInfo(0x108C, 0x0158, "Bosch MTS 6533", "bosch_mts"),
        new UsbDeviceInfo(0x108C, 0x0159, "Bosch MTS 6534", "bosch_mts"),
    };

    public static UsbDeviceInfo? Find(int vid, int pid) => All.FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid);
}

/// <summary>
/// USB control-transfer seam. A libusb based implementation can be supplied by the host application;
/// the library itself ships none (no native dependency).
/// </summary>
public interface IUsbControlTransfer : IAsyncDisposable
{
    /// <summary>Class request, device recipient, IN (HID GET_REPORT style).</summary>
    ValueTask<byte[]> ClassInAsync(byte request, ushort value, ushort index, int length, CancellationToken ct);
    /// <summary>Class request, device recipient, OUT (HID SET_REPORT style).</summary>
    ValueTask ClassOutAsync(byte request, ushort value, ushort index, ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>Vendor request IN.</summary>
    ValueTask<byte[]> VendorInAsync(byte request, ushort value, ushort index, int length, CancellationToken ct);
    /// <summary>Vendor request OUT.</summary>
    ValueTask VendorOutAsync(byte request, ushort value, ushort index, CancellationToken ct);
}

/// <summary>
/// Native USB CAN adapter (ISO-TP in firmware) — port of Python usbdevice/usb_can.py + obd_device.py.
/// The firmware segments/reassembles ISO-TP; the host writes the request and polls the RX length vendor request.
/// </summary>
public sealed class UsbCanTransport : IAddressableTransport
{
    // constants.py
    private const byte UsbrqHidGetReport = 0x01;
    private const byte CustomRqSetStatus = 0x01, CustomRqGetStatus = 0x02;
    private const byte VendorCanMode = 0x00, VendorCanTx = 0x01, VendorCanRx = 0x02, VendorCanRxSize = 0x03;
    private const byte CanMonitorMode = 0x00, CanIsotpMode = 0x01;

    private readonly IUsbControlTransfer _usb;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsbCanTransport(IUsbControlTransfer usb, TimeSpan? timeout = null)
    {
        _usb = usb; Timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public TimeSpan Timeout { get; set; }
    /// <summary>Poll interval while waiting for the firmware's RX buffer (the device has no interrupt endpoint for this).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(1);
    public EcuAddress? CurrentEcu { get; private set; }

    /// <summary>Selects ISO-TP mode (Python init_can).</summary>
    public async ValueTask InitCanAsync(CancellationToken ct = default) =>
        await _usb.VendorOutAsync(CustomRqSetStatus, CanIsotpMode, VendorCanMode, ct).ConfigureAwait(false);

    public async ValueTask SetMonitorModeAsync(CancellationToken ct = default) =>
        await _usb.VendorOutAsync(CustomRqSetStatus, CanMonitorMode, VendorCanMode, ct).ConfigureAwait(false);

    public async ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default)
    {
        if (ecu.Protocol != EcuProtocol.Can) throw new NotSupportedException("The USB CAN adapter only speaks CAN.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (CurrentEcu is null) await InitCanAsync(ct).ConfigureAwait(false);
            await _usb.VendorOutAsync(CustomRqSetStatus, unchecked((ushort)ecu.TxId), VendorCanTx, ct).ConfigureAwait(false);
            await _usb.VendorOutAsync(CustomRqSetStatus, unchecked((ushort)ecu.RxId), VendorCanRx, ct).ConfigureAwait(false);
            CurrentEcu = ecu;
        }
        finally { _gate.Release(); }
    }

    public ValueTask CloseProtocolAsync(CancellationToken ct = default) { CurrentEcu = null; return ValueTask.CompletedTask; }

    public async ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _usb.ClassOutAsync(UsbrqHidGetReport, 0, 0, request, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                while (true)
                {
                    var r = await _usb.VendorInAsync(CustomRqGetStatus, 0, VendorCanRxSize, 2, cts.Token).ConfigureAwait(false);
                    int len = r.Length >= 2 ? (r[0] << 8) | r[1] : 0;
                    if (len == 0xFFFF) throw new ProtocolException("USB CAN adapter reported a bad response.");
                    if (len > 0 && len != 0xFFFE)
                        return await _usb.ClassInAsync(UsbrqHidGetReport, 0, 0, len, ct).ConfigureAwait(false);
                    await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EcuTimeoutException("USB CAN adapter: no response.");
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _usb.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
