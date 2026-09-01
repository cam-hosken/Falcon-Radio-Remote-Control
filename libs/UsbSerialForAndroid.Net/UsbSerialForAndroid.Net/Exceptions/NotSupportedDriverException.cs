using Android.Hardware.Usb;
using System;

namespace UsbSerialForAndroid.Net.Exceptions
{
    /// <summary>
    /// Not supported driver exception
    /// </summary>
    public class NotSupportedDriverException : Exception
    {
        public NotSupportedDriverException(UsbDevice usbDevice)
            : base($"Driver not supported,VendorId={usbDevice.VendorId},ProductId={usbDevice.ProductId},DeviceId={usbDevice.DeviceId}")
        {

        }
    }
}
