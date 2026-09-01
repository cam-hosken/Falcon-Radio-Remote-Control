using Android.Hardware.Usb;
using System;
using UsbSerialForAndroid.Net.Drivers;
using UsbSerialForAndroid.Net.Exceptions;
using UsbSerialForAndroid.Net.Helper;
using UsbSerialForAndroid.Net.Receivers;

namespace UsbSerialForAndroid.Net
{
    /// <summary>
    /// USB driver factory
    /// </summary>
    public static class UsbDriverFactory
    {
        public static UsbDriverDictionary UsbDriverDict => _usbDriverDictInst.Value;
        static readonly Lazy<UsbDriverDictionary> _usbDriverDictInst = new(() => new());
        /// <summary>
        /// Create USB driver
        /// </summary>
        /// <param name="usbDevice">USB device</param>
        /// <returns>UsbDriver</returns>
        /// <exception cref="NotSupportedDriverException">not supported driver exception</exception>
        private static UsbDriverBase CreateUsbDriver(UsbDevice usbDevice)
        {
            return UsbDriverDict.CreateUsbDriver(usbDevice);
        }
        /// <summary>
        /// Create USB driver
        /// </summary>
        /// <param name="vendorId">Vendor Id</param>
        /// <param name="productId">Product Id</param>
        /// <returns>UsbDriver</returns>
        public static UsbDriverBase CreateUsbDriver(int vendorId, int productId)
        {
            var device = UsbManagerHelper.GetUsbDevice(vendorId, productId);
            return CreateUsbDriver(device);
        }
        /// <summary>
        /// Create USB driver
        /// </summary>
        /// <param name="deviceId">Device Id</param>
        /// <returns>UsbDriver</returns>
        public static UsbDriverBase CreateUsbDriver(int deviceId)
        {
            var device = UsbManagerHelper.GetUsbDevice(deviceId);
            return CreateUsbDriver(device);
        }
        /// <summary>
        /// Whether there is a support driver
        /// </summary>
        /// <param name="vendorId">Vendor Id</param>
        /// <param name="productId">Product Id</param>
        /// <returns></returns>
        public static bool HasSupportedDriver(int vendorId, int productId)
        {
            return UsbDriverDict.HasSupportedDriver(vendorId, productId);
        }
        /// <summary>
        /// Register a USB broadcast receiver
        /// </summary>
        /// <param name="isShowToast">true=show toast</param>
        /// <param name="attached">USB insert callback</param>
        /// <param name="detached">USB pull out callback</param>
        /// <param name="errorCallback">Internal error callback</param>
        public static void RegisterUsbBroadcastReceiver(bool isShowToast = true,
            Action<UsbDevice>? attached = default, Action<UsbDevice>? detached = default,
            Action<Exception>? errorCallback = default)
        {
            UsbBroadcastReceiverHelper.RegisterUsbBroadcastReceiver(isShowToast, attached, detached, errorCallback);
        }
        /// <summary>
        /// Unregister the USB broadcast receiver
        /// </summary>
        public static void UnRegisterUsbBroadcastReceiver()
        {
            UsbBroadcastReceiverHelper.UnRegisterUsbBroadcastReceiver();
        }
    }
}
