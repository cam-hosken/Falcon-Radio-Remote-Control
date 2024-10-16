using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO.Ports;
using ExtensionMethods;

namespace PRC_138_Remote_Control
{
    public partial class frmMain : Form
    {
        public static FalconRadio Radio = new FalconRadio();

        frmConnectionSettings ConnectionSettings = new frmConnectionSettings();
        //frmConfiguration Configuration = new frmConfiguration();

        delegate void UpdateUI(RadioStateChangedEventArgs e);
        delegate void RefreshPorts();

        private System.Threading.Timer ConnectionInitializationTimer = null;
        private System.Threading.Timer KeyboardRateLimiterTimer = null;
        private System.Threading.Timer MouseRateLimiterTimer = null;
        private System.Threading.Timer PortClosingTimeoutTimer = null;

        private bool isParameterInitializaionComplete = false;        
        private const int ConnectionOpenDelay = 1500;

        private bool isSplitFrequency = false;
        
        private const int KeyboardRateLimit = 125;
        private const int MouseRateLimit = 100;
        
        private bool isKeyboardInhibit= true;
        private bool isMouseInhibit = true;

        private bool isPortClosing = false;
        private const int PortClosingTimeout = 500;

        private bool isHotkeyIncrement = false;

        private bool isExitArmed = false;

        public frmMain()
        {
            InitializeComponent();
            Radio.RadioStateChanged += Radio_RadioStateChanged;
            ConnectionSettings.RequestSerialPortRefresh += ConnectionSettings_RequestSerialPortRefresh;
            
            ConnectionInitializationTimer = new System.Threading.Timer(new TimerCallback(ConnectionTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);
            KeyboardRateLimiterTimer = new System.Threading.Timer(new TimerCallback(KeyboardRateLimitTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);
            MouseRateLimiterTimer = new System.Threading.Timer(new TimerCallback(MouseRateLimitTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);
            PortClosingTimeoutTimer = new System.Threading.Timer(new TimerCallback(PortClosingTimeoutTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);

            nudChannel.UpButton(); //This is required to fix some initialization bug in the control. Enter key does not commit the value until after the first time one of the arrows is pressed.

        }

        private void PortClosingTimeoutTimerTick(object state)
        {
            PortClosingTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);     //Timer is a one-shot.

            isPortClosing = false;                                                  //Reset the flag.

            Radio.CloseRemotePort();
        }

        private void MouseRateLimitTimerTick(object state)
        {
            isMouseInhibit = false;
        }

        private void KeyboardRateLimitTimerTick(object state)
        {
            isKeyboardInhibit = false;
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //Instantiate connections settings form.

            ConnectionSettings.ShowDialog(); //Display form in modal window.
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            frmAbout About = new frmAbout(); //Instantiate connections settings form.

            About.ShowDialog(); //Display form in modal window.
        }
        private void ConnectionTimerTick(object state)
        {
            ConnectionInitializationTimer.Change(Timeout.Infinite, Timeout.Infinite);
            
            isParameterInitializaionComplete = true;

            RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
            args.PropertyChanged = FalconRadio.Parameters.ConnectionOpen;

            Invoke(new UpdateUI(UpdateDisplayedValues), args);

        }
        private void radioConfigurationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //frmConfiguration Configuration = new frmConfiguration();

            frmConfiguration Configuration = Application.OpenForms.OfType<frmConfiguration>().FirstOrDefault();

            if (Configuration == null)
            {
                Configuration = new frmConfiguration();
            }

            Configuration.Show();    
        }

        private void ConnectionSettings_RequestSerialPortRefresh(object sender, RequestSerialPortRefreshEventArgs e)
        {
            RefreshPortList(ref e);
        }

        private void RefreshPortList(ref RequestSerialPortRefreshEventArgs e)
        {
            e.PortList = Radio.RefreshPortList();
        }

        private void Radio_RadioStateChanged(object sender, RadioStateChangedEventArgs e)
        {
            Invoke(new UpdateUI(UpdateDisplayedValues), e);
        }

        private void UpdateDisplayedValues(RadioStateChangedEventArgs e)
        {
            if (!isParameterInitializaionComplete) { ConnectionInitializationTimer.Change(ConnectionOpenDelay, 0); }    //If the connection has just been opened wait until the last parameter comes in to enable controls.

            switch (e.PropertyChanged)
            {
                case FalconRadio.Parameters.ConnectionOpen:

                    if (Radio.isConnectionOpen)
                    {
                        if (!isParameterInitializaionComplete)
                        {
                            lblStatus.Text = Radio.RemotePort + ": Downloading Radio Configuration...";
                            connectToolStripMenuItem1.Enabled = false;
                            disconnectToolStripMenuItem1.Enabled = true;
                            settingsToolStripMenuItem.Enabled = false;

                            ConnectionInitializationTimer.Change(ConnectionOpenDelay, Timeout.Infinite);
                        }
                        else
                        {
                                radioConfigurationToolStripMenuItem.Enabled = true;

                                lblStatus.Text = Radio.RemotePort + ": Port Open";
                                gbControl.Enabled = true;
                        }
                    }
                    else
                    {
                        lblStatus.Text = "Standby";
                        connectToolStripMenuItem1.Enabled = true;
                        disconnectToolStripMenuItem1.Enabled = false;
                        settingsToolStripMenuItem.Enabled = true;
                        radioConfigurationToolStripMenuItem.Enabled = false;
                        gbControl.Enabled = false;

                        isParameterInitializaionComplete = false;

                        if (isExitArmed)
                        {
                            this.Close();
                        }
                    }

                    break;

                case FalconRadio.Parameters.OperatingMode:

                    switch (Radio.operatingMode)
                    {
                        case FalconRadio.OperatingModes.SSB:

                            rdoSSB.Checked = true;
                            rdoHOP.Checked = false;
                            rdoALE.Checked = false;

                            gbSSB.Show();

                            if (Radio.operatingChannel == 0) { gbFrequency.Enabled = true; }

                            if (isParameterInitializaionComplete) { gbControl.Enabled = true; }                           

                            break;

                        case FalconRadio.OperatingModes.HOP:

                            rdoSSB.Checked = false;
                            rdoHOP.Checked = true;
                            rdoALE.Checked = false;

                            gbHOP.Show();

                            gbFrequency.Enabled = false;
                            if (isParameterInitializaionComplete) { gbControl.Enabled = true; }

                            break;

                        case FalconRadio.OperatingModes.ALE:

                            rdoSSB.Checked = false;
                            rdoHOP.Checked = false;
                            rdoALE.Checked = true;

                            gbALE.Show();

                            gbFrequency.Enabled = false;
                            if (isParameterInitializaionComplete) { gbControl.Enabled = true; }

                            break;

                        default:

                            break;
                    }

                    break;

                case FalconRadio.Parameters.PowerLevel:

                    switch (Radio.txPowerLevel)
                    {
                        case FalconRadio.PowerLevels.High:

                            rdoPowerHigh.Checked = true;

                            break;

                        case FalconRadio.PowerLevels.Medium:

                            rdoPowerMedium.Checked = true;

                            break;

                        case FalconRadio.PowerLevels.Low:

                            rdoPowerLow.Checked = true;

                            break;

                        default:

                            break;
                    }

                    rdoPowerHigh.Enabled = true;
                    rdoPowerMedium.Enabled = true;
                    rdoPowerLow.Enabled = true;

                    break;

                case FalconRadio.Parameters.Keyline:

                    if (Radio.keyLine == FalconRadio.KeylineStates.Off)
                    {
                        rdoTx.Checked = false;
                        rdoRx.Checked = true;
                    }
                    else if (Radio.keyLine == FalconRadio.KeylineStates.Mic || Radio.keyLine == FalconRadio.KeylineStates.Aux)
                    {
                        rdoTx.Checked = true;
                        rdoRx.Checked = false;
                    }

                    break;

                case FalconRadio.Parameters.RxFrequency:

                    txtRxFrequency.Text = Radio.rxFrequency;

                    break;

                case FalconRadio.Parameters.TxFrequency:

                    txtTxFrequency.Text = Radio.txFrequency;

                    ParseSplit();

                    break;

                case FalconRadio.Parameters.Tuning:

                    rdoTuning.Checked = Radio.isTuning;

                    break;

                case FalconRadio.Parameters.TuneComplete:

                    rdoTuneComplete.Checked = Radio.isTuneComplete;

                    break;

                case FalconRadio.Parameters.TuneFail:

                    rdoTuneFail.Checked = Radio.isTuneFail;

                    break;

                case FalconRadio.Parameters.TuneMarginal:

                    chkTuneMarginal.Checked = Radio.isTuneMarginal;

                    break;

                case FalconRadio.Parameters.OperatingChannel:

                    if (Radio.operatingChannel >= 0 && Radio.operatingChannel <= 99)     //Must catch this because the initial value = -1
                    {
                        nudChannel.Value = Radio.operatingChannel;
                    }

                    nudChannel.Enabled = true;

                    //ParseSquelch();

                    if (Radio.operatingChannel == 0)
                    {
                        gbFrequency.Enabled = true;
                        cmbModulation.Enabled = true;
                        //cmbBandwidth.Enabled = true;
                        //cmbAGC.Enabled = true;
                    }
                    else
                    {
                        gbFrequency.Enabled = false;
                        cmbModulation.Enabled = false;
                        //cmbBandwidth.Enabled = false;
                        //cmbAGC.Enabled = false;


                    }

                    break;

                case FalconRadio.Parameters.DigitalVoice:

                    if (Radio.digitalVoice == FalconRadio.OnOffState.On) { chkDigitalVoice.Checked = true; } else { chkDigitalVoice.Checked = false; }

                    ParseSquelch();

                    break;

                case FalconRadio.Parameters.FMTxTone:

                    if (Radio.fMTxTone == FalconRadio.OnOffState.On) { chkTransmitTone.Checked = true; } else { chkTransmitTone.Checked = false; }

                    break;

                case FalconRadio.Parameters.VoiceCompression:

                    if (Radio.voiceCompression == FalconRadio.OnOffState.On) { chkCompression.Checked = true; } else { chkCompression.Checked = false; }

                    break;

                case FalconRadio.Parameters.AnalogSquelch:

                    ParseSquelch();

                    break;

                case FalconRadio.Parameters.DigitalSquelch:

                    ParseSquelch();

                    break;

                case FalconRadio.Parameters.FMSquelch:

                    ParseSquelch();

                    break;

                case FalconRadio.Parameters.ModulationMode:

                    cmbModulation.SelectedIndex = (int)Radio.modulationMode;

                    switch (Radio.modulationMode)
                    {
                        case FalconRadio.ModulationModes.USB:

                            cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.SSBBandwidths));
                            lblBandwidth.Text = "Bandwidth";

                            chkCompression.Enabled = true;
                            cmbSquelchLevel.Enabled = true;
                            cmbFMSquelchType.Enabled = false;
                            chkTransmitTone.Enabled = false;

                            chkDigitalVoice.Enabled = true;
                            chkCWOffset.Enabled = false;
                            nudBFO.Enabled = false;

                            cmbModem.Enabled = true;

                            break;

                        case FalconRadio.ModulationModes.LSB:

                            cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.SSBBandwidths));
                            lblBandwidth.Text = "Bandwidth";

                            chkCompression.Enabled = true;
                            cmbSquelchLevel.Enabled = true;
                            cmbFMSquelchType.Enabled = false;
                            chkTransmitTone.Enabled = false;

                            chkDigitalVoice.Enabled = true;
                            chkCWOffset.Enabled = false;
                            nudBFO.Enabled = false;

                            cmbModem.Enabled = true;

                            break;

                        case FalconRadio.ModulationModes.AME:

                            cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.AMEBandwidths));
                            lblBandwidth.Text = "Bandwidth";

                            chkCompression.Enabled = true;
                            cmbSquelchLevel.Enabled = false;
                            cmbFMSquelchType.Enabled = false;
                            chkTransmitTone.Enabled = false;

                            chkDigitalVoice.Enabled = false;
                            chkCWOffset.Enabled = false;
                            nudBFO.Enabled = false;

                            cmbModem.Enabled = false;

                            break;

                        case FalconRadio.ModulationModes.CW:

                            cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.CWBandwidths));
                            lblBandwidth.Text = "Bandwidth";

                            chkCompression.Enabled = false;
                            cmbSquelchLevel.Enabled = false;
                            cmbFMSquelchType.Enabled = false;
                            chkTransmitTone.Enabled = false;

                            chkDigitalVoice.Enabled = false;
                            chkCWOffset.Enabled = true;
                            nudBFO.Enabled = true;

                            cmbModem.Enabled = false;

                            break;

                        case FalconRadio.ModulationModes.FM:

                            cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.FMDeviations));
                            lblBandwidth.Text = "FM Deviation";

                            chkCompression.Enabled = false;
                            cmbSquelchLevel.Enabled = false;
                            cmbFMSquelchType.Enabled = true;
                            chkTransmitTone.Enabled = true;

                            chkDigitalVoice.Enabled = false;
                            chkCWOffset.Enabled = false;
                            nudBFO.Enabled = false;

                            cmbModem.Enabled = false;

                            break;


                        default:

                            break;
                    }

                    ParseBandwidth();            //After loading the combobox, figure out which value to set.
                    ParseSquelch();              //Also check that the squelch checkbox is set appropriately.   
                    ParseAGCSpeed();             //Refresh the AGC Speed.

                    break;

                case FalconRadio.Parameters.Bandwidth:

                    ParseBandwidth();

                    break;

                case FalconRadio.Parameters.FMDeviation:

                    ParseBandwidth();

                    break;

                case FalconRadio.Parameters.AGCSpeed:

                    ParseAGCSpeed();

                    break;

                case FalconRadio.Parameters.SquelchLevel:

                    cmbSquelchLevel.SelectedIndex = (int)Radio.squelchLevel;

                    break;

                case FalconRadio.Parameters.FMSquelchType:

                    cmbFMSquelchType.SelectedIndex = (int)Radio.fMSquelchType;

                    break;

                case FalconRadio.Parameters.AnalogVoiceSecurity:

                    //if (Radio.isAVSInstalled)
                    //{                        
                    //    cmbAnalogVoiceSecurity.SelectedIndex = (int)Radio.modulationMode;
                    //}

                    break;

                case FalconRadio.Parameters.Encryption:

                    //if (Radio.isEncryptionInstalled)
                    //{
                    //    cmbEncryption.SelectedIndex = (int)Radio.modulationMode;
                    //}

                    break;

                case FalconRadio.Parameters.CWOffset:

                    if (Radio.cWOffset == FalconRadio.CWOffsets.OneThousand) { chkCWOffset.Checked = true; } else { chkCWOffset.Checked = false; }

                    break;

                case FalconRadio.Parameters.BFOOffset:

                    nudBFO.Value = Convert.ToInt32(Radio.bFOOffset.ToDescription());

                    if (Radio.modulationMode == FalconRadio.ModulationModes.CW)
                    {
                        nudBFO.Enabled = true;
                    }

                    break;

                case FalconRadio.Parameters.Retransmit:

                    if (Radio.reTransmit == FalconRadio.EnableState.Enabled) { chkRetrans.Checked = true; } else { chkRetrans.Checked = false; }

                    break;

                case FalconRadio.Parameters.FrequencyStep:

                    lblStepSize.Text = Radio.frequencyStep.ToDescription().Insert(2,".");

                    break;

                case FalconRadio.Parameters.ChannelRXOnly:

                    if (Radio.channelRxOnly == FalconRadio.YesNoState.Yes) { chkRxOnly.Checked = true; } else { chkRxOnly.Checked = false; }

                    break;

                case FalconRadio.Parameters.ModemSetting:

                    try
                    {
                        cmbModem.SelectedIndex = (int)Radio.modemSetting;
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        //If the index is out of range don't set the combobox selection.
                    }
                    finally
                    {                        

                    }

                    break;

                case FalconRadio.Parameters.ModemPreset:

                    cmbModem.Items.Clear();

                    cmbModem.Items.Add("Off");

                    foreach (FalconRadio.ModemPreset preset in Radio.modemPresets)
                    {
                        cmbModem.Items.Add(preset.PresetNumber + " - " + preset.PresetName);
                    }

                    //Once the combobox is loaded the UI must be refreshed.
                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = FalconRadio.Parameters.ModemSetting;

                    UpdateDisplayedValues(args);

                    break;

                case FalconRadio.Parameters.PortRemoteEcho:

                    if (Radio.portRemoteEcho == FalconRadio.OnOffState.On)
                    {
                        if (isPortClosing)
                        {
                            PortClosingTimeoutTimer.Change(0, Timeout.Infinite);        //Fire the timer now.
                        }
                    }

                    break;
                
                case FalconRadio.Parameters.BacklightFunction:
                case FalconRadio.Parameters.BacklightIntensity:
                case FalconRadio.Parameters.ContrastIntensity:
                case FalconRadio.Parameters.RxPreamp:
                case FalconRadio.Parameters.InternalCoupler:
                case FalconRadio.Parameters.OneKWPA:
                case FalconRadio.Parameters.PrePostFilter:
                case FalconRadio.Parameters.PrePostRxAntenna:
                case FalconRadio.Parameters.PrePostScanRate:
                case FalconRadio.Parameters.AntennaMode:
                case FalconRadio.Parameters.RWAS:
                case FalconRadio.Parameters.ALEState:
                case FalconRadio.Parameters.ListeningSilence:
                case FalconRadio.Parameters.LinkToAnyCalls:
                case FalconRadio.Parameters.LinkToAllCalls:
                case FalconRadio.Parameters.ListenBeforeTx:
                case FalconRadio.Parameters.KeyToCall:
                case FalconRadio.Parameters.AMDDisplay:
                case FalconRadio.Parameters.LinkTimeoutTime:
                case FalconRadio.Parameters.MaxChannelsToScan:
                case FalconRadio.Parameters.TuneTime:

                default:

                    break;
            }
        }

        private void ParseSquelch()
        {
            switch (Radio.modulationMode)
            {                
                case FalconRadio.ModulationModes.USB:

                    chkSquelch.Enabled = true;

                    if (Radio.digitalVoice == FalconRadio.OnOffState.On)
                    {
                        if (Radio.digitalSquelch == FalconRadio.OnOffState.On)
                        {
                            chkSquelch.Checked = true;
                        }
                        else
                        {
                            chkSquelch.Checked = false;
                        }
                    }
                    else
                    {
                        if (Radio.analogSquelch == FalconRadio.OnOffState.On)
                        {
                            chkSquelch.Checked = true;
                        }
                        else
                        {
                            chkSquelch.Checked = false;
                        }
                    }

                    break;

                case FalconRadio.ModulationModes.LSB:

                    chkSquelch.Enabled = true;

                    if (Radio.digitalVoice == FalconRadio.OnOffState.On)
                    {
                        if (Radio.digitalSquelch == FalconRadio.OnOffState.On)
                        {
                            chkSquelch.Checked = true;
                        }
                        else
                        {
                            chkSquelch.Checked = false;
                        }
                    }
                    else
                    {
                        if (Radio.analogSquelch == FalconRadio.OnOffState.On)
                        {
                            chkSquelch.Checked = true;
                        }
                        else
                        {
                            chkSquelch.Checked = false;
                        }
                    }

                    break;

                case FalconRadio.ModulationModes.AME:

                    chkSquelch.Enabled = false;
                    chkSquelch.Checked = false;

                    break;

                case FalconRadio.ModulationModes.CW:

                    chkSquelch.Enabled = false;
                    chkSquelch.Checked = false;

                    break;

                case FalconRadio.ModulationModes.FM:

                    chkSquelch.Enabled = true;

                    if (Radio.fMSquelch == FalconRadio.OnOffState.On)
                    {
                        chkSquelch.Checked = true;
                    }
                    else
                    {
                        chkSquelch.Checked = false;
                    }

                    break;


                default: 
                    
                    break;
            }            
           
        }

        private void ParseBandwidth()
        {
            if (Radio.modulationMode == FalconRadio.ModulationModes.FM)
            {
                cmbBandwidth.SelectedIndex = cmbBandwidth.FindStringExact(Radio.fMDeviation.ToDescription());
            }
            else
            {
                cmbBandwidth.SelectedIndex = cmbBandwidth.FindStringExact(Radio.bandWidth.ToDescription());
            }
        }
        private void ParseAGCSpeed()
        {
            cmbAGC.SelectedIndex = (int)Radio.aGCSpeed;
        }


        private void connectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Connect();
        }

        private void Connect()
        {
            Radio.OpenRemotePort();
        }

        private void disconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Disconnect();       
        }

        private void Disconnect()
        {
            isPortClosing = true;

            if (Properties.Settings.Default.isReEnableSerialPortEcho)       //If echo is to be turned back on send the command and set the timeout timer.
            {
                Radio.SerialPortEcho(FalconRadio.OnOffState.On);

                PortClosingTimeoutTimer.Change(PortClosingTimeout, Timeout.Infinite);
            }
            else
            {
                PortClosingTimeoutTimer.Change(0, Timeout.Infinite);        //else just fire the timer now.
            }         
        }

        private void rdoSSB_Click(object sender, EventArgs e)
        {
            gbControl.Enabled = false;

            Radio.SSB();            
        }

        private void rdoHOP_Click(object sender, EventArgs e)
        {
            gbControl.Enabled = false;

            Radio.HOP();            
        }

        private void rdoALE_Click(object sender, EventArgs e)
        {
            gbControl.Enabled = false;

            Radio.ALE();            
        }
        private void rdoPowerHigh_Click(object sender, EventArgs e)
        {
            ParsePowerLevel(sender, e);          
        }

        private void rdoPowerMedium_Click(object sender, EventArgs e)
        {
            ParsePowerLevel(sender, e);
        }

        private void rdoPowerLow_Click(object sender, EventArgs e)
        {
            ParsePowerLevel(sender, e);
        }

        private void ParsePowerLevel(object sender, EventArgs e)
        {
            rdoPowerHigh.Enabled = false;
            rdoPowerMedium.Enabled = false;
            rdoPowerLow.Enabled = false;

            switch (((System.Windows.Forms.RadioButton)sender).Text)                
            {
                case "High":
                    Radio.TxPowerLevel(FalconRadio.PowerLevels.High);
                    break;

                case "Med":
                    Radio.TxPowerLevel(FalconRadio.PowerLevels.Medium);
                    break;

                case "Low":
                    Radio.TxPowerLevel(FalconRadio.PowerLevels.Low);
                    break;

                default:
                    break;
            }
        }

        private void btnFreqUp_Click(object sender, EventArgs e)
        {
            //lblStepSize.Focus();

            Radio.IncrementFrequency();           
        }
        private void btnFreqDown_Click(object sender, EventArgs e)
        {
            //lblStepSize.Focus();

            Radio.DecrementFrequency();         
        }

        private void btnStepUp_Click(object sender, EventArgs e)
        {
            //lblStepSize.Focus();
            
            Radio.FrequencyStep(true);
        }

        private void btnStepDown_Click(object sender, EventArgs e)
        {
            //lblStepSize.Focus();

            Radio.FrequencyStep(false);
        }

        private void btnRetune_Click(object sender, EventArgs e)
        {
            Radio.Retune();            
        }

        private void chkRxOnly_Click(object sender, EventArgs e)
        {
            if (Radio.channelRxOnly == FalconRadio.YesNoState.Yes)
            {
                DialogResult dialogResult = MessageBox.Show("Allow Channel Tx?", "Channel Rx Only", MessageBoxButtons.YesNo);

                if (dialogResult == DialogResult.Yes)
                {
                    Radio.RxOnly(FalconRadio.YesNoState.No);
                }
                else if (dialogResult == DialogResult.No)
                {
                    //do nothing
                }
                
            }
            else
            {
                Radio.RxOnly(FalconRadio.YesNoState.Yes);
            }
        }

        private void nudChannel_ValueChanged(object sender, EventArgs e)
        {
            NumericUpDownEx nud = sender as NumericUpDownEx;

            if (nud.ChangedType == NumericUpDownEx.ValueChangedType.Programmatic)  //If the value was set from the radio then do nothing. If the user set it, set the radio.
            {
                //do nothing
            }
            else
            {
                Radio.OperatingChannel(Convert.ToInt32(nud.Value));
                nudChannel.Enabled = false;
            }
            
        }
        private void nudChannel_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                

            }
        }

        private void txtRxFrequency_Leave(object sender, EventArgs e)
        {
            txtRxFrequency.Text = Radio.rxFrequency;
        }

        private void txtTxFrequency_Leave(object sender, EventArgs e)
        {
            txtTxFrequency.Text = Radio.txFrequency;
        }

        private void txtRxFrequency_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            { 

            case Keys.Enter:

                    if (chkSplit.Checked)
                    {
                        Radio.RxFrequency(txtRxFrequency.Text.Replace('_', '0'));     //Send jut the Rx freq.
                    }
                    else
                    {
                        Radio.Frequency(txtRxFrequency.Text.Replace('_', '0'));       //Send em both.
                    }

                break;

            case Keys.Escape:
                txtRxFrequency.Text = Radio.rxFrequency;
                    break;               

                default:                   

                break;
            }

        }

        private void txtTxFrequency_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {

                case Keys.Enter:

                    Radio.TxFrequency(txtTxFrequency.Text.Replace('_', '0'));

                    break;

                case Keys.Escape:
                    txtTxFrequency.Text = Radio.txFrequency;
                    break;

                default:                   

                    break;
            }
        }

        private void rdoTx_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoTx.BackColor = Color.Red;
            }
            else
            {
                rdoTx.BackColor = default(Color);
            }
        }

        private void rdoTuneFail_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoTuneFail.BackColor = Color.Red;
            }
            else
            {
                rdoTuneFail.BackColor = default(Color);
            }
        }

        private void rdoTuning_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoTuning.BackColor = Color.Yellow;
            }
            else
            {
                rdoTuning.BackColor = default(Color);
            }
        }

        private void chkTuneMarginal_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.CheckBox)sender).Checked)
            {
                chkTuneMarginal.BackColor = Color.Yellow;
            }
            else
            {
                chkTuneMarginal.BackColor = default(Color);
            }
        }

        private void rdoPowerLow_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoPowerLow.BackColor = Color.Green;
                
                rdoPowerHigh.Checked = false;
                rdoPowerMedium.Checked = false;                
            }
            else
            {
                rdoPowerLow.BackColor = default(Color);
            }
        }

        private void rdoPowerMedium_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoPowerMedium.BackColor = Color.Yellow;

                rdoPowerHigh.Checked = false;
                rdoPowerLow.Checked = false;
            }
            else
            {
                rdoPowerMedium.BackColor = default(Color);
            }
        }

        private void rdoPowerHigh_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoPowerHigh.BackColor = Color.Red;

                rdoPowerMedium.Checked = false;
                rdoPowerLow.Checked = false;
            }
            else
            {
                rdoPowerHigh.BackColor = default(Color);
            }
        }

        private void rdoTuneComplete_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoTuneComplete.BackColor = Color.Green;
            }
            else
            {
                rdoTuneComplete.BackColor = default(Color);
            }
        }

        private void rdoRx_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked == true)
            {
                rdoRx.BackColor = Color.Green;
            }
            else
            {
                rdoRx.BackColor = default(Color);
            }
        }

        private void gbControl_SizeChanged(object sender, EventArgs e)
        {
            this.Width = ((System.Windows.Forms.GroupBox)sender).Width + 40;
        }

        private void gbSSB_VisibleChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.GroupBox)sender).Visible)
            {
                gbHOP.Visible = false;
                gbALE.Visible = false;

                gbControl.Width = gbSSB.Location.X + gbSSB.Width + 6;
            }
        }

        private void gbHOP_VisibleChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.GroupBox)sender).Visible)
            {
                gbSSB.Visible = false;
                gbALE.Visible = false;

                gbControl.Width = gbHOP.Location.X + gbHOP.Width + 6;
            }
        }

        private void gbALE_VisibleChanged(object sender, EventArgs e)
        {
            
            if (((System.Windows.Forms.GroupBox)sender).Visible)
            {
                gbSSB.Visible = false;
                gbHOP.Visible = false;

                gbControl.Width = gbALE.Location.X + gbALE.Width + 6;
            }
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            gbSSB.Visible = true;
            //gbALE.Visible = true;
            PopulateComboboxes();
        }

        private void PopulateComboboxes()
        {
            cmbModulation.DataSource = Enum.GetNames(typeof(FalconRadio.ModulationModes));
            cmbAGC.DataSource = Enum.GetNames(typeof(FalconRadio.AGCSpeeds));         
            cmbSquelchLevel.DataSource = Enum.GetNames(typeof(FalconRadio.SquelchLevels));
            cmbFMSquelchType.DataSource = Enum.GetNames(typeof(FalconRadio.FMSquelchTypes));
            //cmbBandwidth.DataSource = EnumEx.GetDescriptions(typeof(FalconRadio.Bandwidths));
            
            //if (Radio.isEncryptionInstalled)
            //{
            //    cmbEncryption.DataSource = Enum.GetNames(typeof(FalconRadio.OnOffState));
            //}
            //else
            //{
            //    cmbEncryption.Items.Add("NOT INSTALLED");
            //}
            
            //if (Radio.isAVSInstalled)
            //{
            //    cmbAnalogVoiceSecurity.DataSource = Enum.GetNames(typeof(FalconRadio.OnOffState));
            //}
            //else
            //{
            cmbAnalogVoiceSecurity.Items.Add("NOT INSTALLED");
            cmbAnalogVoiceSecurity.SelectedIndex = 0;
            //}
        }

        private void ParseSplit()
        {
            if (Radio.txFrequency == Radio.rxFrequency)
            {
                chkSplit.Checked = false;
            }
            else
            {
                chkSplit.Checked = true;
            }
        }
        private void chkSplit_CheckedChanged(object sender, EventArgs e)
        {
            isSplitFrequency = ((System.Windows.Forms.CheckBox)sender).Checked;

            if (((System.Windows.Forms.CheckBox)sender).Checked)
            {
                txtTxFrequency.Enabled = true;
            }
            else
            {
                txtTxFrequency.Enabled = false;
                if (!string.Equals(Radio.rxFrequency, Radio.txFrequency))
                {
                    Radio.Frequency(txtRxFrequency.Text.Replace('_', '0'));       //Set the Rx and Tx freqs the same.
                }
            }
        }

        private void rdoSSB_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoHOP.Checked = false;
                rdoALE.Checked = false;
            }

        }

        private void rdoHOP_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoSSB.Checked = false;
                rdoALE.Checked = false;
            }
        }

        private void rdoALE_CheckedChanged(object sender, EventArgs e)
        {
            if (((System.Windows.Forms.RadioButton)sender).Checked)
            {
                rdoSSB.Checked = false;
                rdoHOP.Checked = false;
            }
        }

        private void chkSquelch_Click(object sender, EventArgs e)
        {            
            if (Radio.modulationMode == FalconRadio.ModulationModes.FM)
            {
                if (Radio.fMSquelch == FalconRadio.OnOffState.On)
                {
                    Radio.FMSquelch(FalconRadio.OnOffState.Off);
                }
                else
                {
                    Radio.FMSquelch(FalconRadio.OnOffState.On);
                }
            }
            else if (Radio.modulationMode == FalconRadio.ModulationModes.USB)
            {
                if (Radio.digitalVoice == FalconRadio.OnOffState.On)
                {
                    if (Radio.digitalSquelch == FalconRadio.OnOffState.On)
                    {
                        Radio.DigitalSquelch(FalconRadio.OnOffState.Off);
                    }
                    else
                    {
                        Radio.DigitalSquelch(FalconRadio.OnOffState.On);
                    }
                }
                else
                {
                    if (Radio.analogSquelch == FalconRadio.OnOffState.On)
                    {
                        Radio.AnalogSquelch(FalconRadio.OnOffState.Off);
                    }
                    else
                    {
                        Radio.AnalogSquelch(FalconRadio.OnOffState.On);
                    }
                }
            }
            else if (Radio.modulationMode == FalconRadio.ModulationModes.LSB)
            {
                if (Radio.digitalVoice == FalconRadio.OnOffState.On)
                {
                    if (Radio.digitalSquelch == FalconRadio.OnOffState.On)
                    {
                        Radio.DigitalSquelch(FalconRadio.OnOffState.Off);
                    }
                    else
                    {
                        Radio.DigitalSquelch(FalconRadio.OnOffState.On);
                    }
                }
                else
                {
                    if (Radio.analogSquelch == FalconRadio.OnOffState.On)
                    {
                        Radio.AnalogSquelch(FalconRadio.OnOffState.Off);
                    }
                    else
                    {
                        Radio.AnalogSquelch(FalconRadio.OnOffState.On);
                    }
                }
            }


        }

        private void chkDigitalVoice_Click(object sender, EventArgs e)
        {
            if (Radio.digitalVoice == FalconRadio.OnOffState.On)
            {
                Radio.DigitalVoice(FalconRadio.OnOffState.Off);
            }
            else
            {
                Radio.DigitalVoice(FalconRadio.OnOffState.On);
            }

            ParseSquelch();
        }

        private void chkTransmitTone_Click(object sender, EventArgs e)
        {
            if (Radio.fMTxTone == FalconRadio.OnOffState.On)
            {
                Radio.FMTxTone(FalconRadio.OnOffState.Off);
            }
            else
            {
                Radio.FMTxTone(FalconRadio.OnOffState.On);
            }
        }

        private void chkCompression_Click(object sender, EventArgs e)
        {
            if (Radio.voiceCompression == FalconRadio.OnOffState.On)
            {
                Radio.VoiceCompression(FalconRadio.OnOffState.Off);
            }
            else
            {
                Radio.VoiceCompression(FalconRadio.OnOffState.On);
            }
        }

        private void cmbModulation_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;
            
            cmbModulation.SelectedIndex = (int)Radio.modulationMode;

            Radio.ModulationMode((FalconRadio.ModulationModes)selectedindex);
        }

        private void cmbBandwidth_SelectionChangeCommitted(object sender, EventArgs e)
        {
            string selectedvalue = ((System.Windows.Forms.ComboBox)sender).SelectedValue.ToString();

            if (Radio.modulationMode == FalconRadio.ModulationModes.FM)
            {
                cmbBandwidth.SelectedIndex = cmbBandwidth.FindStringExact(Radio.fMDeviation.ToDescription());
            }
            else
            {
                cmbBandwidth.SelectedIndex = cmbBandwidth.FindStringExact(Radio.bandWidth.ToDescription());
            }            
            
            if (Radio.modulationMode == FalconRadio.ModulationModes.FM)
            {
                Radio.FMDeviation((FalconRadio.FMDeviations)EnumEx.GetValueFromDescription<FalconRadio.FMDeviations>(selectedvalue));
            }
            else
            {
                Radio.Bandwidth((FalconRadio.Bandwidths)EnumEx.GetValueFromDescription<FalconRadio.Bandwidths>(selectedvalue));
            }
            
        }

        private void cmbAGC_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbAGC.SelectedIndex = (int)Radio.aGCSpeed;

            Radio.AGCSpeed((FalconRadio.AGCSpeeds)selectedindex);
        }

        private void cmbSquelchLevel_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbSquelchLevel.SelectedIndex = (int)Radio.squelchLevel;

            Radio.SquelchLevel((FalconRadio.SquelchLevels)selectedindex);
        }

        private void cmbSquelchType_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbFMSquelchType.SelectedIndex = (int)Radio.fMSquelchType;

            Radio.FMSquelchType((FalconRadio.FMSquelchTypes)selectedindex);
        }

        private void cmbModem_SelectionChangeCommitted(object sender, EventArgs e)
        {
            int selectedindex = ((System.Windows.Forms.ComboBox)sender).SelectedIndex;

            cmbModem.SelectedIndex = (int)Radio.modemSetting;

            Radio.Modem((FalconRadio.ModemPresets)selectedindex);
        }

        private void cmbAnalogVoiceSecurity_SelectionChangeCommitted(object sender, EventArgs e)
        {

        }

        private void chkRetrans_Click(object sender, EventArgs e)
        {
            if (Radio.reTransmit == FalconRadio.EnableState.Enabled)
            {
                Radio.Retransmit(FalconRadio.EnableState.Disabled);
            }
            else
            {
                Radio.Retransmit(FalconRadio.EnableState.Enabled);
            }
        }

        private void nudBFO_ValueChanged(object sender, EventArgs e)
        {
            NumericUpDownEx nud = sender as NumericUpDownEx;

            if (nud.ChangedType == NumericUpDownEx.ValueChangedType.Programmatic)  //If the value was set from the radio then do nothing. If the user set it, set the radio.
            {
                //do nothing
            }
            else
            {                
                Radio.BFOOffset(ParseNudBFO(nud.Value));
                nudBFO.Enabled = false;                
            }
        }
        private FalconRadio.BFOOffsets ParseNudBFO(decimal value)
        {
            if (value == 0)
            {
                return FalconRadio.BFOOffsets.Zero;
            }
            else
            {
                return (FalconRadio.BFOOffsets)(nudBFO.Value / 1000);
            }

        }
        private void chkCWOffset_Click(object sender, EventArgs e)
        {
            if (Radio.cWOffset == FalconRadio.CWOffsets.Zero)
            {
                Radio.CWOffset(FalconRadio.CWOffsets.OneThousand);
            }
            else
            {
                Radio.CWOffset(FalconRadio.CWOffsets.Zero);
            }
        }
        private void gbFrequency_MouseWheel(object sender, MouseEventArgs e)
        {
            if (isHotkeyIncrement)
            {
                if (!isMouseInhibit)
                {
                    if (e.Delta > 0)
                    {
                        Radio.IncrementFrequency();
                    }
                    else
                    {
                        Radio.DecrementFrequency();
                    }

                    isMouseInhibit = true;
                }
            }
        }

        private void ArrowKeysFrequencyChange(ref PreviewKeyDownEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:

                    if (!isKeyboardInhibit)
                    {
                        Radio.IncrementFrequency();

                        isKeyboardInhibit = true;
                    }

                    e.IsInputKey = true;

                    break;

                case Keys.Down:

                    if (!isKeyboardInhibit)
                    { 
                        Radio.DecrementFrequency();

                        isKeyboardInhibit = true;
                    }

                    e.IsInputKey = true;

                    break;

                case Keys.Left:

                    if (!isKeyboardInhibit)
                    {
                        Radio.FrequencyStep(true);

                        isKeyboardInhibit = true;
                    }

                    e.IsInputKey = true;

                    break;

                case Keys.Right:

                    if (!isKeyboardInhibit)
                    {
                        Radio.FrequencyStep(false);

                        isKeyboardInhibit = true;
                    }

                    e.IsInputKey = true;

                    break;


                default:

                    break;
            }
        }

        private void lblStepSize_MouseClick(object sender, MouseEventArgs e)
        {
            ((System.Windows.Forms.Label)sender).Focus();            

            SetHotKeyIncrement();
        }

        private void SetHotKeyIncrement()
        {
            isHotkeyIncrement = !isHotkeyIncrement;

            if (isHotkeyIncrement)
            {
                KeyboardRateLimiterTimer.Change(0, KeyboardRateLimit);
                MouseRateLimiterTimer.Change(0, MouseRateLimit);

                lblStepSize.Font = new Font(lblStepSize.Font, FontStyle.Bold);
            }
            else
            {
                KeyboardRateLimiterTimer.Change(Timeout.Infinite, Timeout.Infinite);
                MouseRateLimiterTimer.Change(Timeout.Infinite, Timeout.Infinite);

                lblStepSize.Font = new Font(lblStepSize.Font, FontStyle.Regular);
            }
        }

        private void lblStepSize_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void fileToolStripMenuItem_Click(object sender, EventArgs e)
        {


            if (!Radio.isConnectionOpen)
            {
                this.Close();
            }
            else
            {
                isExitArmed = true;
            }

            Disconnect();
        }

        private void btnStepDown_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void btnStepUp_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void btnFreqUp_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void btnFreqDown_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void lblStepSize_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void gbControl_EnabledChanged(object sender, EventArgs e)
        {
            if (isHotkeyIncrement)
            {
                SetHotKeyIncrement();
            }
        }

        private void gbFrequency_EnabledChanged(object sender, EventArgs e)
        {
            if (isHotkeyIncrement)
            {
                SetHotKeyIncrement();
            }
        }

        private void btnRetune_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            ArrowKeysFrequencyChange(ref e);
        }

        private void btnFreqUp_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void btnFreqDown_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void btnStepUp_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void btnStepDown_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void btnRetune_Leave(object sender, EventArgs e)
        {
            if (!btnFreqUp.Focused && !btnFreqDown.Focused && !btnStepUp.Focused && !btnStepDown.Focused && !btnRetune.Focused)
            {
                if (isHotkeyIncrement)
                {
                    SetHotKeyIncrement();
                }
            }
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            Disconnect();
        }
    }   
}
