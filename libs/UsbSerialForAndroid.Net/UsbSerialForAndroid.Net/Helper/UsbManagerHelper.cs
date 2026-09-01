using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using System;
using System.Collections.Generic;
using System.Linq;
using UsbSerialForAndroid.Net.Drivers;

namespace UsbSerialForAndroid.Net.Helper
{
    public static class UsbManagerHelper
    {
        private static readonly UsbManager usbManager = UsbDriverBase.UsbManager;
        /// <summary>
        /// Get the USB device by device id
        /// </summary>
        /// <param name="deviceId"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public static UsbDevice GetUsbDevice(int deviceId)
        {
            return usbManager.DeviceList?
                .Select(c => c.Value)
                .FirstOrDefault(c => c.DeviceId == deviceId)
                ?? throw new Exception($"No usb device with id found `{deviceId}`"); ;
        }
        /// <summary>
        /// Get the USB device by vendor id and product id
        /// </summary>
        /// <param name="vendorId"></param>
        /// <param name="productId"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public static UsbDevice GetUsbDevice(int vendorId, int productId)
        {
            return usbManager.DeviceList?
                .Select(c => c.Value)
                .FirstOrDefault(c => c.VendorId == vendorId && c.ProductId == productId)
                ?? throw new Exception($"The corresponding device could not be found VendorId={vendorId} ProductId={productId}");
        }
        /// <summary>
        /// Check whether the USB device has permission
        /// </summary>
        /// <param name="usbDevice"></param>
        /// <returns></returns>
        public static bool HasPermission(UsbDevice usbDevice)
        {
            return usbManager.HasPermission(usbDevice);
        }
        /// <summary>
        /// Get all USB devices
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<UsbDevice> GetAllUsbDevices()
        {
            return usbManager.DeviceList?.Select(c => c.Value) ?? Array.Empty<UsbDevice>();
        }
        /// <summary>
        /// Get all USB devices with permission
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<UsbDevice> GetPermissionedUsbDevices()
        {
            return GetAllUsbDevices().Where(HasPermission);
        }
        /// <summary>
        /// Request permission for USB device
        /// </summary>
        /// <param name="usbDevice"></param>
        public static void RequestPermission(UsbDevice usbDevice)
        {
            var intent = new Intent(UsbManager.ActionUsbDeviceAttached);
            var pendingIntent = PendingIntent.GetBroadcast(Application.Context, 0, intent, PendingIntentFlags.Immutable);
            usbManager.RequestPermission(usbDevice, pendingIntent);
        }
    }
}
