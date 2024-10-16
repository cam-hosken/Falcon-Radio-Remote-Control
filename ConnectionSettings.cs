using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO.Ports;
using ExtensionMethods;

namespace PRC_138_Remote_Control
{
    public partial class frmConnectionSettings : Form
    {
        public event EventHandler<RequestSerialPortRefreshEventArgs> RequestSerialPortRefresh;        

        public frmConnectionSettings()
        {
            InitializeComponent();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void frmConnectionSettings_Load(object sender, EventArgs e)
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            RefreshPorts();          
            
            int storedPortIndex = cmbPort.FindStringExact(Properties.Settings.Default.Port);
            
            if (storedPortIndex >= 0)       //The serial port we are looking for is in the list.
            {
                cmbPort.SelectedIndex = storedPortIndex;        //Set the selected index to the stored port.
            }
            else
            {
                //if ()
            }          

            
            cmbBaudRate.DataSource = EnumEx.GetDescriptions(typeof(SerialPortController.BaudRatesAvailable));
            cmbBaudRate.SelectedIndex = cmbBaudRate.FindStringExact(Properties.Settings.Default.Baudrate.ToString());
            
            cmbDataBits.DataSource = EnumEx.GetDescriptions(typeof(SerialPortController.DataBitsAvailable));
            cmbDataBits.SelectedIndex = cmbDataBits.FindStringExact(Properties.Settings.Default.Databits.ToString());

            cmbParity.DataSource = Enum.GetNames(typeof(Parity));
            cmbParity.SelectedIndex = Properties.Settings.Default.Parity;

            cmbStopBits.DataSource = Enum.GetNames(typeof(StopBits));
            cmbStopBits.SelectedIndex = Properties.Settings.Default.Stopbits;

            cmbFlowControl.DataSource = Enum.GetNames(typeof(Handshake));
            cmbFlowControl.SelectedIndex = Properties.Settings.Default.Flowcontrol;
            
            chkEcho.Checked = Properties.Settings.Default.isReEnableSerialPortEcho;
            
        }

        protected virtual void OnRequestSerialPortRefresh(RequestSerialPortRefreshEventArgs e)
        {
            EventHandler<RequestSerialPortRefreshEventArgs> handler = RequestSerialPortRefresh;
            handler?.Invoke(this, e);
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            SaveSettings();
            this.Hide();
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.Port = cmbPort.SelectedItem.ToString();
            Properties.Settings.Default.Baudrate = Convert.ToInt32(cmbBaudRate.SelectedItem.ToString());
            Properties.Settings.Default.Databits = Convert.ToInt32(cmbDataBits.SelectedItem.ToString());
            Properties.Settings.Default.Parity = (int)Enum.Parse(typeof(Parity), cmbParity.SelectedItem.ToString());
            Properties.Settings.Default.Stopbits = (int)Enum.Parse(typeof(StopBits), cmbStopBits.SelectedItem.ToString());
            Properties.Settings.Default.Flowcontrol = (int)Enum.Parse(typeof(Handshake), cmbFlowControl.SelectedItem.ToString());

            Properties.Settings.Default.isReEnableSerialPortEcho = chkEcho.Checked;

            Properties.Settings.Default.Save();
        }

        private void btnRefreshPorts_Click(object sender, EventArgs e)
        {
            RefreshPorts();
        }

        private void RefreshPorts()
        {
            RequestSerialPortRefreshEventArgs args = new RequestSerialPortRefreshEventArgs();

            OnRequestSerialPortRefresh(args);

            cmbPort.DataSource = args.PortList.ToArray();
        }

        private void chkEcho_CheckStateChanged(object sender, EventArgs e)
        {

        }
    }
    public class RequestSerialPortRefreshEventArgs : EventArgs
    {
        public List<string> PortList { get; set; }

    }
}
