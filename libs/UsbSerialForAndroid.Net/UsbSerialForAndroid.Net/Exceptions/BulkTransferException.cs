using Android.Hardware.Usb;
using System;

namespace UsbSerialForAndroid.Net.Exceptions
{
    /// <summary>
    /// Bulk transfer exception
    /// </summary>
    public class BulkTransferException : Exception
    {
        /// <summary>
        /// Bulk transfer exception
        /// </summary>
        public UsbEndpoint? Endpoint { get; }
        public byte[]? Buffer { get; }
        public int Offset { get; }
        public int Length { get; }
        public int Timeout { get; }
        public int Result { get; }
        /// <summary>
        /// Bulk transfer exception
        /// </summary>
        /// <param name="message"></param>
        /// <param name="result"></param>
        /// <param name="endpoint"></param>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="timeout"></param>
        public BulkTransferException(string message, int result, UsbEndpoint? endpoint, byte[]? buffer, int offset, int length, int timeout)
            : base(GetMessage(message, result, endpoint, buffer, offset, length, timeout))
        {
            Result = result;
            Endpoint = endpoint;
            Buffer = buffer;
            Offset = offset;
            Length = length;
            Timeout = timeout;
        }
        public BulkTransferException(string message, int result, UsbEndpoint? endpoint, byte[]? buffer, int length, int timeout)
            : base(GetMessage(message, result, endpoint, buffer, length, timeout))
        {
            Result = result;
            Endpoint = endpoint;
            Buffer = buffer;
            Length = length;
            Timeout = timeout;
        }
        private static string GetMessage(string message, int result, UsbEndpoint? endpoint, byte[]? buffer, int offset, int length, int timeout)
            => $"{message},Result:{result},Address:{endpoint?.Address},Direction:{endpoint?.Direction},EndpointNumber:{endpoint?.EndpointNumber},Type:{endpoint?.Type},MaxPacketSize:{endpoint?.MaxPacketSize},Buffer:{BitConverter.ToString(buffer ?? Array.Empty<byte>())},Offset:{offset},Length:{length},Timeout:{timeout}";
        private static string GetMessage(string message, int result, UsbEndpoint? endpoint, byte[]? buffer, int length, int timeout)
             => $"{message},Result:{result},Address:{endpoint?.Address},Direction:{endpoint?.Direction},EndpointNumber:{endpoint?.EndpointNumber},Type:{endpoint?.Type},MaxPacketSize:{endpoint?.MaxPacketSize},Buffer:{BitConverter.ToString(buffer ?? Array.Empty<byte>())},Length:{length},Timeout:{timeout}";
    }
}
