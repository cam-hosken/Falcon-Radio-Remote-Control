using System;

namespace UsbSerialForAndroid.Net.Exceptions
{
    /// <summary>
    /// Control transfer exception
    /// </summary>
    public class ControlTransferException : Exception
    {
        public int RequestType { get; }
        public int Request { get; }
        public int Value { get; }
        public int Index { get; }
        public byte[]? Buffer { get; }
        public int Offset { get; }
        public int Length { get; }
        public int Timeout { get; }
        public int Result { get; }
        /// <summary>
        /// Control transfer exception
        /// </summary>
        /// <param name="message"></param>
        /// <param name="result"></param>
        /// <param name="requestType"></param>
        /// <param name="request"></param>
        /// <param name="value"></param>
        /// <param name="index"></param>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        /// <param name="timeout"></param>
        public ControlTransferException(string message, int result, int requestType, int request, int value, int index, byte[]? buffer, int length, int timeout)
            : base(GetMessage(message, result, requestType, request, value, index, buffer, length, timeout))
        {
            Result = result;
            RequestType = requestType;
            Request = request;
            Value = value;
            Index = index;
            Buffer = buffer;
            Length = length;
            Timeout = timeout;
        }
        /// <summary>
        /// Control transfer exception
        /// </summary>
        /// <param name="message"></param>
        /// <param name="result"></param>
        /// <param name="requestType"></param>
        /// <param name="request"></param>
        /// <param name="value"></param>
        /// <param name="index"></param>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="timeout"></param>
        public ControlTransferException(string message, int result, int requestType, int request, int value, int index, byte[]? buffer, int offset, int length, int timeout)
            : base(GetMessage(message, result, requestType, request, value, index, buffer, offset, length, timeout))
        {
            Result = result;
            RequestType = requestType;
            Request = request;
            Value = value;
            Index = index;
            Buffer = buffer;
            Offset = offset;
            Length = length;
            Timeout = timeout;
        }
        private static string GetMessage(string message, int result, int requestType, int request, int value, int index, byte[]? buffer, int offset, int length, int timeout)
            => $"{message},Result:{result},RequestType:{requestType},Request:{request},Value:{value},Index:{index},Buffer:{BitConverter.ToString(buffer ?? Array.Empty<byte>())},Offset:{offset},Length:{length},Timeout:{timeout}";
        private static string GetMessage(string message, int result, int requestType, int request, int value, int index, byte[]? buffer, int length, int timeout)
            => $"{message},Result:{result},RequestType:{requestType},Request:{request},Value:{value},Index:{index},Buffer:{BitConverter.ToString(buffer ?? Array.Empty<byte>())},Length:{length},Timeout:{timeout}";
    }
}
