using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Ports;
using System.ComponentModel;
using ExtensionMethods;
using System.Windows.Forms;

namespace PRC_138_Remote_Control
{
    public class SerialPortController
    {        
        public enum BaudRatesAvailable
        {
            [Description("75")] SeventyFive,
            [Description("150")] OneFifty,
            [Description("300")] ThreeHundred,
            [Description("600")] SixHundred,
            [Description("1200")] TwelveHundred,
            [Description("2400")] TwentyFourHundred,
            [Description("4800")] FortyEightHundred,
            [Description("9600")] NinetySixHundred

        }
        public enum DataBitsAvailable
        {
            [Description("7")] Seven,
            [Description("8")] Eight
        }

        private const int readtimeout = 500;
        private const int writetimeout = 500;

        private const byte CR = 0x0D;
        private const byte LF = 0x0A;
        private const byte Space = 0x20;
        private const byte GreaterThan = 0x3E;

        private SerialPort serialPort;

        private ConcurrentQueue<byte[]> CommandQueue = new ConcurrentQueue<byte[]>();
        private Queue<byte[]> RawRxQueue = new Queue<byte[]>();
        private Queue<byte[]> RawRemainderQueue = new Queue<byte[]>();
        public Queue<string> ResponseQueue = new Queue<string>();

        public bool isOpen { get; internal set; }
                

        public string comPort { get; set; }

        public SerialPortController()
        {
            
        }

        public List<string> RefreshPortList()
        {
            List<string> PortsAvailable = new List<string>(); //Clear out the list of ports

            int index = 0;

            foreach (string s in SerialPort.GetPortNames())
            {
                PortsAvailable.Add(s);

                index++;
            }

            if (index <= 0)
            {
                PortsAvailable.Add("NONE FOUND");
            }

            return PortsAvailable;
        }
        public bool OpenSerialPort()
        {
            serialPort = new SerialPort();

            //serialPort.PortName = PortSettings.Port;
            //serialPort.BaudRate = PortSettings.Baudrate;
            //serialPort.Parity = PortSettings.Parity;
            //serialPort.DataBits = PortSettings.Databits;
            //serialPort.StopBits = PortSettings.Stopbits;
            //serialPort.Handshake = PortSettings.Flowcontrol;

            if (Properties.Settings.Default.Port.Contains("COM"))
            {
                comPort = Properties.Settings.Default.Port;

                serialPort.PortName = Properties.Settings.Default.Port;
                serialPort.BaudRate = Properties.Settings.Default.Baudrate;
                serialPort.Parity = (Parity)Properties.Settings.Default.Parity;
                serialPort.DataBits = Properties.Settings.Default.Databits;
                serialPort.StopBits = (StopBits)Properties.Settings.Default.Stopbits;
                serialPort.Handshake = (Handshake)Properties.Settings.Default.Flowcontrol;

                serialPort.ReadTimeout = readtimeout;
                serialPort.WriteTimeout = writetimeout;

                try
                {
                    serialPort.Open();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
            else
            {
                MessageBox.Show("No Serial Port Selected.");
            }

            isOpen = serialPort.IsOpen;
            return isOpen;

        }

        public bool CloseSerialPort()
        {
            try
            {
                serialPort.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                if (!serialPort.IsOpen)
                {
                    while (!CommandQueue.IsEmpty)
                    {
                         CommandQueue.TryDequeue(out byte[] command);
                    }
                }
            }

            isOpen = serialPort.IsOpen;
            return isOpen;
        }

        private void SendCommand(byte[] TxData)
        {
            if (TxData.Length > 0 && serialPort.IsOpen)        //make sure there is something there
            {
                serialPort.Write(TxData, 0, TxData.Length);
            }
        }

        public void ReadSerialPort()
        {
            isOpen = serialPort.IsOpen;

            if (isOpen)
            {
                byte[] RxData = new byte[serialPort.BytesToRead];

                try
                {
                    serialPort.Read(RxData, 0, RxData.Length);
                }
                catch (TimeoutException)
                {
                    RxData = null;
                }

                if (RxData.Length > 0)
                {
                    RawRxQueue.Enqueue(RxData);
                }
            }
        }

        public void ProcessRawRxQueue()
        {
            byte[] searchField = new byte[0];
            bool isMatch = false;

            if (RawRxQueue.Count > 0)                   //If there's stuff in the queue then process it.
            {
                while (RawRemainderQueue.Count > 0)     //Pull whatever's left over from the last bit of rx data and put it in the search field.
                {
                    searchField = ByteArrayTools.Combine(searchField, RawRemainderQueue.Dequeue());
                }                

                searchField = ByteArrayTools.Combine(searchField, RawRxQueue.Dequeue());        //Dequeue the next bit of new rx data and append it to the search field.

                for (int index = 0; index < searchField.Length; index++)
                {

                    switch (searchField[index])
                    {
                        case CR:            //Found the end of a response line. Add the data to the left to the queue for parsing.

                            isMatch = true;

                            if (index > 0)      //If the CR is the first char in the data then something is wrong. (FlushPort sent on connect?) so don't post the command.
                            {
                                ResponseQueue.Enqueue(System.Text.Encoding.UTF8.GetString(ByteArrayTools.TrimEnd(searchField, index))); //Don't take the CR
                            }

                            break;


                        case LF:            //Get rid of all the LF's.

                            isMatch = true;

                            if (index > 0)          //Stupid fucking radio sends an LF as the first character after the RXOnly message rather than a CR like every other message.
                            {
                                ResponseQueue.Enqueue(System.Text.Encoding.UTF8.GetString(ByteArrayTools.TrimEnd(searchField, index))); //Don't take the LF
                            }

                            break;

                        case GreaterThan:   //Found the end of a mode change line. Add the data to the left to the queue for parsing.

                            isMatch = true;

                            if (index > 0)      //If the > is the first line in the raw data then something is wrong. Serial data corrupt?
                            {
                                ResponseQueue.Enqueue(System.Text.Encoding.UTF8.GetString(ByteArrayTools.TrimEnd(searchField, index + 1))); //Do take the greater than
                            }

                            break;
                    }

                    if (isMatch)
                    {
                        isMatch = false;

                        if (index < searchField.Length - 1)                 //If this is the last byte then do nothing. Otherwise trim the search field and reset the index.
                        {
                            searchField = ByteArrayTools.TrimBeginning(searchField, index + 1);
                            index = -1;
                        }
                    }
                    else
                    {
                        if (index == searchField.Length - 1)        //There is a partial command left over. Put the data back in the RawRemainderQueue to be combined with the next data from the serial port.
                        {
                            RawRemainderQueue.Enqueue(searchField);

                            //break;
                        }
                    }
                }
            }
        }

        public void QueueCommand(byte[] command)
        {
            if (command.Length > 0)
            {
                CommandQueue.Enqueue(command);
            }
        }

        public void ProcessCommandQueue()
        {
            if (CommandQueue.Count() > 0)           //Try to dequeue a command. If the queue is locked try again until the queue is empty.
            {
                CommandQueue.TryDequeue(out byte[] command);

                SendCommand(command);
            }
        }


    }

}
