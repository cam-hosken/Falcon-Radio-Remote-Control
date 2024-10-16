using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExtensionMethods;
using System.Threading;

namespace PRC_138_Remote_Control
{
    public partial class frmConfiguration : Form
    {
        delegate void UpdateConsole(MessageReceivedEventArgs e);
        delegate void UpdateUI(RadioStateChangedEventArgs e);
        
        private System.Threading.Timer ParameterInitializationTimer = null;

        private bool isParameterInitializaionComplete = false;
        private int ParameterDownloadDelay = 1500;

        public frmConfiguration()
        {
            InitializeComponent();
            frmMain.Radio.MessageReceived += Radio_MessageReceived;
            frmMain.Radio.RadioStateChanged += Radio_RadioStateChanged;

            ParameterInitializationTimer = new System.Threading.Timer(new TimerCallback(ParameterTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);

            PopulateComboBoxes();            
        }

        private void ParameterTimerTick(object state)
        {
            ParameterInitializationTimer.Change(Timeout.Infinite, Timeout.Infinite);

            isParameterInitializaionComplete = true;

            RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
            args.PropertyChanged = FalconRadio.Parameters.ConnectionOpen;

            Invoke(new UpdateUI(UpdateDisplayedValues), args);
        }

        private void PopulateComboBoxes()
        {
            cmbAntennaMode.DataSource = Enum.GetNames(typeof(FalconRadio.AntennaModes));
            cmbBacklightFunction.DataSource = Enum.GetNames(typeof(FalconRadio.BacklightFunctions));
            cmbBacklightIntensity.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.Intensities));
            cmbContrast.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.Intensities));
            cmbPrePostScanRate.DataSource = Enum.GetNames(typeof(FalconRadio.PrePostScanRates));
        }
        private void InitializeSettings()
        {
            ParameterInitializationTimer.Change(ParameterDownloadDelay, Timeout.Infinite);

            RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();

            args.PropertyChanged = FalconRadio.Parameters.ConnectionOpen;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.BacklightFunction();
            args.PropertyChanged = FalconRadio.Parameters.BacklightFunction;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.BacklightIntensity();
            args.PropertyChanged = FalconRadio.Parameters.BacklightIntensity;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.ContrastIntensity();
            args.PropertyChanged = FalconRadio.Parameters.ContrastIntensity;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.RxPreamp();
            args.PropertyChanged = FalconRadio.Parameters.RxPreamp;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.InternalCoupler();
            args.PropertyChanged = FalconRadio.Parameters.InternalCoupler;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.OneKWPA();
            args.PropertyChanged = FalconRadio.Parameters.OneKWPA;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.PrePostFilter();
            args.PropertyChanged = FalconRadio.Parameters.PrePostFilter;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.PrePostRxAntenna();
            args.PropertyChanged = FalconRadio.Parameters.PrePostRxAntenna;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.PrePostScanRate();
            args.PropertyChanged = FalconRadio.Parameters.PrePostScanRate;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);

            frmMain.Radio.AntennaMode();
            args.PropertyChanged = FalconRadio.Parameters.AntennaMode;
            Invoke(new UpdateUI(UpdateDisplayedValues), args);
        }
        private void Radio_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            if (this.Visible)
            {
                try
                {
                    Invoke(new UpdateConsole(AddMessageToConsole), e);
                }
                catch
                {

                }
            }
        }
        private void Radio_RadioStateChanged(object sender, RadioStateChangedEventArgs e)
        {
            if (this.Visible)
            {
                try
                {
                    Invoke(new UpdateUI(UpdateDisplayedValues), e);
                }
                catch
                {

                }
            }
        }

        private void UpdateDisplayedValues(RadioStateChangedEventArgs e)
        {
            if (!isParameterInitializaionComplete) { ParameterInitializationTimer.Change(ParameterDownloadDelay, 0); }

            switch (e.PropertyChanged)
            {
                case FalconRadio.Parameters.ConnectionOpen:

                    if (frmMain.Radio.isConnectionOpen)
                    {
                        if (!isParameterInitializaionComplete)
                        {
                            lblStatus.Text = frmMain.Radio.RemotePort + ": Downloading Radio Configuration...";

                            ParameterInitializationTimer.Change(ParameterDownloadDelay, Timeout.Infinite);
                        }
                        else
                        {
                            lblStatus.Text = frmMain.Radio.RemotePort + ": Port Open";
                            tabModes.Enabled = true;
                        }
                    }
                    else
                    {
                        this.Close();
                    }

                    break;

                case FalconRadio.Parameters.BacklightFunction:

                    cmbBacklightFunction.SelectedIndex = cmbBacklightFunction.FindStringExact(frmMain.Radio.backlightFunction.ToDescription());

                    break;

                case FalconRadio.Parameters.BacklightIntensity:

                    cmbBacklightIntensity.SelectedIndex = cmbBacklightIntensity.FindStringExact(frmMain.Radio.backlightIntensity.ToDescription());

                    break;

                case FalconRadio.Parameters.ContrastIntensity:

                    cmbContrast.SelectedIndex = cmbContrast.FindStringExact(frmMain.Radio.contrastIntensity.ToDescription());

                    break;

                case FalconRadio.Parameters.RxPreamp:

                    if (frmMain.Radio.rxPreamp == FalconRadio.BypassState.Enabled) { chkRxPreamp.Checked = true; } else { chkRxPreamp.Checked = false; }

                    break;

                case FalconRadio.Parameters.InternalCoupler:

                    if (frmMain.Radio.internalCoupler == FalconRadio.BypassState.Enabled) { chkInternalCoupler.Checked = true; } else { chkInternalCoupler.Checked = false; }

                    break;

                case FalconRadio.Parameters.OneKWPA:

                    if (frmMain.Radio.onekWPA == FalconRadio.YesNoState.Yes) { chk1kWPA.Checked = true; } else { chk1kWPA.Checked = false; }

                    break;

                case FalconRadio.Parameters.PrePostFilter:

                    if (frmMain.Radio.prepostFilter == FalconRadio.PrePostEnableState.Enabled) { chkPrePostFilter.Checked = true; } else { chkPrePostFilter.Checked = false; }

                    break;

                case FalconRadio.Parameters.PrePostRxAntenna:

                    if (frmMain.Radio.prepostRxAntenna == FalconRadio.PrePostEnableState.Enabled) { chkRxAntenna.Checked = true; } else { chkRxAntenna.Checked = false; }

                    break;

                case FalconRadio.Parameters.PrePostScanRate:

                    cmbPrePostScanRate.SelectedIndex = cmbPrePostScanRate.FindStringExact(frmMain.Radio.prepostScanRate.ToDescription());

                    break;

                case FalconRadio.Parameters.AntennaMode:

                    cmbAntennaMode.SelectedIndex = cmbAntennaMode.FindStringExact(frmMain.Radio.antennaMode.ToDescription());

                    break;

                case FalconRadio.Parameters.RWAS:

                    if (frmMain.Radio.rWAS == FalconRadio.EnableState.Enabled) { chkRWAS.Checked = true; } else { chkRWAS.Checked = false; }

                    break;

                default:
                    break;
            }
        }

        private void AddMessageToConsole(MessageReceivedEventArgs e)
        {
            txtConsole.AppendText(e.message);
            txtConsole.AppendText(Environment.NewLine);

            if (txtConsole.Lines.Count() > 500)
            {
                txtConsole.Lines = txtConsole.Lines.Skip(200).ToArray();
                txtConsole.Select(txtConsole.Text.Length, 0);
                txtConsole.ScrollToCaret();
            }
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            chkEnableConsole.Checked = false;

            this.Hide();
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            SendCommand(txtSend.Text);

            txtSend.Focus();
        }

        private void txtSend_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:

                    SendCommand(((System.Windows.Forms.TextBox)sender).Text);

                    break;

                default:
                    break;
            }
        }

        private void SendCommand(string command)
        {
            if (txtSend.Text.Length > 0)
            {
                frmMain.Radio.RawCommand(command);
                txtSend.Clear();
            }
        }

        private void chkEnableConsole_CheckedChanged(object sender, EventArgs e)
        {
            frmMain.Radio.isConsoleEnabled = chkEnableConsole.Checked;
            txtSend.Enabled = chkEnableConsole.Checked;
            btnSend.Enabled = chkEnableConsole.Checked;

            if(!chkEnableConsole.Checked)
            {
                
            }
        }

        private void btnDateTime_Click(object sender, EventArgs e)
        {            
            frmMain.Radio.Time(DateTime.Now.TimeOfDay.ToString().Substring(0, 8));
            frmMain.Radio.Date(DateTime.Now.Month.ToString() + "/" + DateTime.Now.Day.ToString() + "/" + DateTime.Now.Year.ToString().Substring(2));
            frmMain.Radio.Day(DateTime.Now.DayOfWeek.ToString());            
        }

        private void frmConfiguration_VisibleChanged(object sender, EventArgs e)
        {
            if (this.Visible)
            {
                InitializeSettings();
            }
        }

        private void frmConfiguration_FormClosed(object sender, FormClosedEventArgs e)
        {

        }

        private void chkRxPreamp_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.rxPreamp == FalconRadio.BypassState.Enabled)
            {
                frmMain.Radio.RxPreamp(FalconRadio.BypassState.Bypassed);
            }
            else
            {
                frmMain.Radio.RxPreamp(FalconRadio.BypassState.Enabled);
            }
        }

        private void chkInternalCoupler_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.internalCoupler == FalconRadio.BypassState.Enabled)
            {
                frmMain.Radio.InternalCoupler(FalconRadio.BypassState.Bypassed);
            }
            else
            {
                frmMain.Radio.InternalCoupler(FalconRadio.BypassState.Enabled);
            }   
        }

        private void chk1kWPA_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.onekWPA == FalconRadio.YesNoState.Yes)
            {
                frmMain.Radio.OneKWPA(FalconRadio.YesNoState.No);
            }
            else
            {
                frmMain.Radio.OneKWPA(FalconRadio.YesNoState.Yes);
            }
        }

        private void cmbAntennaMode_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbAntennaMode.SelectedIndex = (int)frmMain.Radio.antennaMode;

            frmMain.Radio.AntennaMode((FalconRadio.AntennaModes)selectedindex);
        }

        private void chkPrePostFilter_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.prepostFilter == FalconRadio.PrePostEnableState.Enabled)
            {
                frmMain.Radio.PrePostFilter(FalconRadio.PrePostEnableState.Disabled);
            }
            else
            {
                frmMain.Radio.PrePostFilter(FalconRadio.PrePostEnableState.Enabled);
            }
        }

        private void chkRxAntenna_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.prepostRxAntenna == FalconRadio.PrePostEnableState.Enabled)
            {
                frmMain.Radio.PrePostRxAntenna(FalconRadio.PrePostEnableState.Disabled);
            }
            else
            {
                frmMain.Radio.PrePostRxAntenna(FalconRadio.PrePostEnableState.Enabled);
            }
        }

        private void cmbPrePostScanRate_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbPrePostScanRate.SelectedIndex = (int)frmMain.Radio.prepostScanRate;

            frmMain.Radio.PrePostScanRate((FalconRadio.PrePostScanRates)selectedindex);
        }

        private void cmbBacklightFunction_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbBacklightFunction.SelectedIndex = (int)frmMain.Radio.backlightFunction;

            frmMain.Radio.BacklightFunction((FalconRadio.BacklightFunctions)selectedindex);
        }

        private void cmbContrast_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbContrast.SelectedIndex = (int)frmMain.Radio.contrastIntensity;

            frmMain.Radio.ContrastIntensity((FalconRadio.Intensities)selectedindex);
        }

        private void cmbBacklightIntensity_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbBacklightIntensity.SelectedIndex = (int)frmMain.Radio.backlightIntensity;

            frmMain.Radio.BacklightIntensity((FalconRadio.Intensities)selectedindex);
        }

        private void chkRWAS_Click(object sender, EventArgs e)
        {
            if (frmMain.Radio.rWAS == FalconRadio.EnableState.Enabled)
            {
                frmMain.Radio.RWAS(FalconRadio.EnableState.Disabled);
            }
            else
            {
                frmMain.Radio.RWAS(FalconRadio.EnableState.Enabled);
            }
        }
    }
}
