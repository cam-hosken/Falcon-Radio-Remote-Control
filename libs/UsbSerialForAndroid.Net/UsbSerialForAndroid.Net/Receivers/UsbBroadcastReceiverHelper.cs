using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using System;

namespace UsbSerialForAndroid.Net.Receivers
{
    internal static class UsbBroadcastReceiverHelper
    {
        private static UsbBroadcastReceiver? usbBroadcastReceiver;
        /// <summary>
        /// Register USB broadcast receiver
        /// </summary>
        /// <param name="isShowToast">true=show toast</param>
        /// <param name="attached">USB insert callback</param>
        /// <param name="detached">USB pull out callback</param>
        /// <param name="errorCallback">Internal error callback</param>
        public static void RegisterUsbBroadcastReceiver(bool isShowToast = true,
            Action<UsbDevice>? attached = default, Action<UsbDevice>? detached = default,
            Action<Exception>? errorCallback = default)
        {
            var intentFilter = new IntentFilter();
            intentFilter.AddAction(UsbManager.ActionUsbDeviceAttached);
            intentFilter.AddAction(UsbManager.ActionUsbDeviceDetached);
            usbBroadcastReceiver = new UsbBroadcastReceiver
            {
                UsbDeviceAttached = attached,
                UsbDeviceDetached = detached,
                ErrorCallback = errorCallback,
                IsShowToast = isShowToast
            };
            Application.Context.RegisterReceiver(usbBroadcastReceiver, intentFilter);
        }
        /// <summary>
        /// Unregister USB broadcast receiver
        /// </summary>
        public static void UnRegisterUsbBroadcastReceiver()
        {
            Application.Context.UnregisterReceiver(usbBroadcastReceiver);
        }
    }
}
