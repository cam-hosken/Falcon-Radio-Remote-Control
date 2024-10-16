using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExtensionMethods;
using System.ComponentModel;
using ExtensionMethods;
using System.Windows.Forms;

namespace PRC_138_Remote_Control
{
    public class FalconRadio
    {        
        public event EventHandler<RadioStateChangedEventArgs> RadioStateChanged;
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        SerialPortController RemoteSerialPortController = new SerialPortController(); //Instantiate the serial port controller for the remote port.
        //SerialPortController DataSerialPortController = new SerialPortController(); //Instantiate the serial port controller for the data port.

        //Commands and Variables
        public enum Commands
        {
            [Description("SS")] SSB,
            [Description("HO")] HOP,
            [Description("ALE")] ALE,
            [Description("POW")] PowerLevel,
            [Description("SH")] Show,
            [Description("DI")] Display,
            [Description("FR")] Frequency,
            [Description("TXF")] TxFrequency,
            [Description("RXF")] RxFrequency,
            [Description("RXON")] ChannelRXOnly,
            [Description("RAD")] ListeningSilence,
            [Description("INC")] IncrementFrequency,
            [Description("DEC")] DecrementFrequency,
            [Description("STEP")] FrequencyStep,
            [Description("RETU")] RetuneCoupler,
            [Description("CH")] OperatingChannel,
            [Description("MODE")] ModulationMode,
            [Description("BA")] Bandwidth,
            [Description("FMDE")] FMDeviation,
            [Description("AG")] AGCSpeed,
            [Description("DV")] DigitalVoice,
            [Description("SQ")] AnalogSquelch,
            [Description("SQ_L")] SquelchLevel,
            [Description("FMSQ")] FMSquelch,
            [Description("FMSQ_T")] FMSquelchType,
            [Description("FMTONE")] FMTxTone,
            [Description("DGT_S")] DigitalSquelch,
            [Description("CWOFF")] CWOffset,
            [Description("BF")] BFOOffset,
            [Description("COM")] VoiceCompression,
            [Description("ANTENNA")] AntennaMode,
            [Description("RWAS")] RWAS,
            [Description("RWAS_K")] RWASKey,
            [Description("UNKEY_M")] RWASUnkey,
            [Description("FORCE_W")] RWASForce,
            [Description("RETR")] Retransmit,
            [Description("PRE")] RxPreamp,
            [Description("INTCOUPLER")] InternalCoupler,
            [Description("KWAT")] OneKWPA,
            [Description("PREPOST FILTER")] PrePostFilter,
            [Description("PREPOST RXANTENNA")] PrePostRxAntenna,
            [Description("PREPOST SCAN")] PrePostScanRate,
            [Description("LIG")] BacklightFunction,
            [Description("CONT")] ContrastIntensity,
            [Description("INT")] BacklightIntensity,
            [Description("BAT ST")] BatteryState,
            [Description("TI")] Time,
            [Description("DAT")] Date,
            [Description("DAY")] Day,
            [Description("RF")] RFGain,
            [Description("K")] Keyline,
            [Description("TE 3")] ModuleFirmwareVersion,
            [Description("TE 4")] VSWRTest,
            [Description("AUD")] AudioLevel,
            [Description("PORT_R ECHO")] SerialPortEcho,
            [Description("AVS")] AnalogVoiceSecurity,
            [Description("ENCR")] Encryption,
            [Description("MODEM")] Modem,
            [Description("SCA")] Scan,
            [Description("ST")] Stop,
            [Description("SOU")] Sound,
            [Description("EXCH")] Exchange,
            [Description("CAL")] Call,
            [Description("RAN")] Rank,
            [Description("SE")] Send,
            [Description("ALL_C")] LinkToAllCalls,
            [Description("ANY_C")] LinkToAnyCalls,
            [Description("LSTN")] ListenBeforeTx,
            [Description("KEY_T")] KeyToCall,
            [Description("AMD_D")] AMDDisplay,
            [Description("SLFAD")] SelfAddress,
            [Description("NETAD")] NetAddress,
            [Description("INDAD")] IndividualAddress,
            [Description("DELAD")] DeleteAddress,
            [Description("TIME_OU")] LinkTimeoutTime,
            [Description("MAXCH")] MaxChannelsToScan,
            [Description("TUNE")] TuneTime,
            [Description("CHG")] DisplayChannelGroup,
            [Description("ADDC")] AddChannelToGroup,
            [Description("DELC")] DeleteChannelFromGroup

        }
        public enum Responses
        {
            [Description("SSB>")] SSB,
            [Description("HOP>")] HOP,
            [Description("ALE>")] ALE,
            [Description("POWER")] TxPowerLevel,
            [Description("TXFR")] TxFrequency,
            [Description("RXFR")] RxFrequency,
            [Description("RXONLY")] RxOnly,
            [Description("RAD")] RadioSlience,
            [Description("TUNING")] TuningCoupler,
            [Description("TUNE")] TuneComplete,
            [Description("CHAN")] OperatingChannel,
            [Description("BAND")] Bandwidth,
            [Description("FMDEV")] FMDeviation,
            [Description("MODE")] ModulationMode,
            [Description("DV")] DigitalVoice,
            [Description("SQUELCH")] AnalogSquelch,
            [Description("SQ_LEVEL")] SquelchLevel,
            [Description("FMSQUELCH")] FMSquelch,
            [Description("FMSQ_TYPE")] FMSquelchType,
            [Description("FMTONE")] FMTxTone,
            [Description("DGT_SQUELCH")] DigitalSquelch,
            [Description("CWOFFSET")] CWOffset,
            [Description("BFO")] BFOOffset,
            [Description("KEY")] Keyline,
            [Description("AGC")] AGCSpeed,
            [Description("MODE")] OperatingMode,
            [Description("RWAS")] RWAS,
            [Description("RETRANS")] Retransmission,
            [Description("MODEM")] Modem,
            [Description("NO")] NoModemPresets,
            [Description("AVS")] AnalogVoiceSecurity,
            [Description("ENCRYPT")] Encrypt,
            [Description("ENCRYPTION")] Encryption,
            [Description("ANTENNA")] AntennaMode,
            [Description("COMPRESS")] VoiceCompression,
            [Description("STEP")] FrequencyStep,
            [Description("RFG")] RFGain,
            [Description("TIME")] Time,
            [Description("DATE")] Date,
            [Description("DAY")] Day,
            [Description("LIGHT")] BacklightFunction,
            [Description("CONTRAST")] ContrastIntensity,
            [Description("INTENSITY")] BacklightIntensity,
            [Description("PREAMP")] RxPreamp,
            [Description("INTCOUPLER")] InternalCoupler,
            [Description("KWATT")] OneKWPA,
            [Description("PREPOST")] PrePost,
            [Description("PORT_REMOTE")] PortRemote,
            [Description("PORT_R")] PortRemoteCommandEcho,
            [Description("SCANNING")] Scanning,
            [Description("TERMINATING")] TerminatingLink,
            [Description("SOUNDING")] Sounding,
            [Description("EXCHANGE")] Exchange,
            [Description("SCAN")] ScanStopped,
            [Description("SIGNAL")] SignalReceived,
            [Description("RECEIVING")] Receiving,
            [Description("RXMSG")] ReceivedMessage,
            [Description("LINKED")] Linked,
            [Description("RAD_SIL")] ListeningSilence,
            [Description("ALL_CALL")] LinkToAllCalls,
            [Description("ANY_CALL")] LinkToAnyCalls,
            [Description("LSTN")] ListenBeforeTx,
            [Description("KEY_TO_CALL")] KeyToCall,
            [Description("AMD_DISPLAY")] AMDDisplay,
            [Description("INV")] InvalidNetAddress,
            [Description("INVALID")] InvalidIndividualAddress,
            [Description("SLFAD")] SelfAddress,
            [Description("NETAD")] NetAddress,
            [Description("INDAD")] IndividualAddress,
            [Description("TIME_OUT")] LinkTimeoutTime,
            [Description("MAXCH")] MaxChannelsToScan,
            [Description("TUNETIME")] TuneTime,
            [Description("CHGROUP")] ChannelGroup,
            [Description("**")] Error
            //In_Sync
            //Awaiting_Sync
            //Sending_Sync_Req
            //Sync_Req_Rcv
            //Sending_Sync_Rsp
            //No_Sync

            //Net 05, resp: No Hopset
            //Net 00, resp: Generating Hopset...
            /*
            HOP> 
            NET  00
            KEY OFF
            NETID    00  12345678
            Hoptype 00 NB
            Center 00  29000
            Hopnum 0061
            MODEM OFF
            ENCRYPT OFF
            POWER low
            No_Sync
            */


        }
        public enum Parameters
        {
            RadioType,
            ConnectionOpen,
            OperatingMode,
            PowerLevel,
            TxFrequency,
            RxFrequency,
            ChannelRXOnly,
            ListeningSilence,
            FrequencyStep,
            OperatingChannel,
            ModulationMode,
            Bandwidth,
            AGCSpeed,
            DigitalVoice,
            AnalogSquelch,
            SquelchLevel,
            FMSquelch,
            FMSquelchType,
            FMTxTone,
            DigitalSquelch,
            CWOffset,
            BFOOffset,
            FMDeviation,
            VoiceCompression,
            RWAS,
            RWASKey,
            RWASUnkey,
            RWASForce,
            Retransmit,
            RxPreamp,
            InternalCoupler,
            OneKWPA,
            PrePostFilter,
            PrePostRxAntenna,
            PrePostScanRate,
            BacklightFunction,
            ContrastIntensity,
            BacklightIntensity,
            BatteryState,
            Time,
            Date,
            Day,
            RFGain,
            Keyline,
            ModuleFirmwareVersion,
            AudioLevel,
            SerialPortEcho,
            Tuning,
            TuneComplete,
            TuneMarginal,
            TuneFail,
            Encryption,
            AnalogVoiceSecurity,
            ModemSetting,
            ModemPreset,
            AntennaMode,
            PortRemoteEcho,
            ALEState,
            LinkToAllCalls,
            LinkToAnyCalls,
            ListenBeforeTx,
            KeyToCall,
            AMDDisplay,
            LinkTimeoutTime,
            MaxChannelsToScan,
            TuneTime
        }
        public enum OperatingModes
        {
            [Description("SSB")] SSB,
            [Description("HOP")] HOP,
            [Description("ALE")] ALE
        }
        public enum PowerLevels
        {
            [Description("HI")] High,
            [Description("MED")] Medium,
            [Description("LOW")] Low
        }
        public enum OnOffState
        {
            [Description("ON")] On,
            [Description("OFF")] Off
        }
        public enum YesNoState
        {
            [Description("YES")] Yes,
            [Description("NO")] No
        }
        public enum EnableState
        {
            [Description("ENABLED")] Enabled,
            [Description("DISABLED")] Disabled
        }
        public enum BypassState
        {
            [Description("ENABLED")] Enabled,
            [Description("BYPASSED")] Bypassed
        }
        public enum ModulationModes
        {
            [Description("USB")] USB,
            [Description("LSB")] LSB,
            [Description("AME")] AME,
            [Description("CW")] CW,
            [Description("FM")] FM
        }
        public enum Bandwidths
        {
            [Description(".35")] PointThreeFive = 0,
            [Description(".68")] PointSixEight = 1,
            [Description("1.0")] OnePointZero = 2,
            [Description("1.5")] OnePointFive = 3,
            [Description("2.0")] TwoPointZero = 4,
            [Description("2.4")] TwoPointFour = 5,
            [Description("2.7")] TwoPointSeven = 6,
            [Description("3.0")] ThreePointZero = 7,
            [Description("4.0")] FourPointZero = 8,
            [Description("5.0")] FivePointZero = 9,
            [Description("6.0")] SixPointZero = 10,
            [Description("6.5")] SixPointFive = 11,
            [Description("8.0")] EightPointZero = 12,
            //[Description("NONE")] None = 13
        }
        public enum SSBBandwidths
        {
            [Description("2.0")] TwoPointZero = 4,
            [Description("2.4")] TwoPointFour = 5,
            [Description("2.7")] TwoPointSeven = 6,
            [Description("3.0")] ThreePointZero = 7,
            //[Description("NONE")] None = 12
        }
        public enum AMEBandwidths
        {
            [Description("3.0")] ThreePointZero = 7,
            [Description("4.0")] FourPointZero = 8,
            [Description("5.0")] FivePointZero = 9,
            [Description("6.0")] SixPointZero = 10,
            [Description("8.0")] EightPointZero = 12,
            //[Description("NONE")] None = 12
        }
        public enum CWBandwidths
        {
            [Description(".35")] PointThreeFive = 0,
            [Description(".68")] PointSixEight = 1,
            [Description("1.0")] OnePointZero = 2,
            [Description("1.5")] OnePointFive = 3,
            [Description("2.0")] TwoPointZero = 4,
            //[Description("NONE")] None = 12
        }
        public enum FMDeviations
        {
            [Description("5.0")] FivePointZero = 9,
            [Description("6.5")] SixPointFive = 11,
            [Description("8.0")] EightPointZero = 12,
        }
        public enum AGCSpeeds
        {
            [Description("OFF")] Off,
            [Description("SLOW")] Slow,
            [Description("MED")] Medium,
            [Description("FAST")] Fast,
            [Description("DATA")] Data
        }
        public enum CWOffsets
        {
            [Description("0000")] Zero,
            [Description("1000")] OneThousand 
        }
        public enum AntennaModes
        {
            [Description("AUTO")] Auto,
            [Description("TUNED")] Tuned,
            [Description("BNC")] BNC
        }
        public enum AudioLevels
        {
            [Description("0DBM")] ZeroDBM,
            [Description("-10DBM")] NegativeTenDBM
        }
        public enum SquelchLevels
        {
            [Description("HIGH")] High,
            [Description("MED")] Medium,
            [Description("LOW")] Low
        }
        public enum FMSquelchTypes
        {
            [Description("NOISE")] Noise,
            [Description("TONE")] Tone
        }
        public enum TuneStates
        {
            [Description("NOT TUNED")] NotTuned,
            [Description("COMPLETE")] Complete,
            [Description("MARGINAL")] Marginal,
            [Description("FAIL")] Fail
        }
        public enum KeylineStates
        {
            [Description("OFF")] Off,
            [Description("MIC")] Mic,
            [Description("AUX")] Aux
        }
        public enum CouplerStates
        {
            [Description("ENABLED")] Enabled,
            [Description("BYPASS")] Bypassed
        }
        public enum ComSecInstalledState
        {
            [Description("INSTALLED")] Installed,
            [Description("NOT INSTALLED")] NotInstalled,
        }
        public enum FrequencySteps
        {
            [Description("00000001")] OneHz,
            [Description("00000010")] TenHz,
            [Description("00000100")] OneHundredHz,
            [Description("00001000")] OneKHz,
            [Description("00010000")] TenKHz,
            [Description("00100000")] OneHundredKHz,
        }
        public enum ModemCommands 
        {
            [Description("OF")] Off,
            [Description("SH")] Show,
            [Description("PRE")] GetPresets
        }
        public enum ModemResponses
        {
            [Description("OFF")] Off,
            [Description("PRESET")] Preset            
        }
        public enum ModemPresets
        {
            [Description("OFF")] Off,
            [Description("0")] Zero,
            [Description("1")] One,
            [Description("2")] Two,
            [Description("3")] Three,
            [Description("4")] Four,
            [Description("5")] Five,
            [Description("6")] Six,
            [Description("7")] Seven,
            [Description("8")] Eight,
            [Description("9")] Nine
        }
        public enum BFOOffsets
        {
            [Description("-4000")] NegativeFourThousand = -4,
            [Description("-3000")] NegativeThreeThousand = -3,
            [Description("-2000")] NegativeTwoThousand = -2,
            [Description("-1000")] NegativeOneThousand = -1,
            [Description("+0000")] Zero = 0,
            [Description("+1000")] OneThousand = 1,
            [Description("+2000")] TwoThousand = 2,
            [Description("+3000")] ThreeThousand = 3,
            [Description("+4000")] FourThousand = 4
        }
        public enum BacklightFunctions
        {            
            [Description("OFF")] Off,
            [Description("MOMENTARY")] Momentary,
        }
        public enum Intensities
        {
            [Description("00")] Zero,
            [Description("01")] One,
            [Description("02")] Two,
            [Description("03")] Three,
            [Description("04")] Four,
            [Description("05")] Five,
            [Description("06")] Six,
            [Description("07")] Seven,
            [Description("08")] Eight
        }
        public enum PrePostScanRates
        {
            [Description("SLOW")] Slow,
            [Description("FAST")] Fast,
            [Description("BYPASS")] Bypass
        }
        public enum PrePostParameters
        {
            [Description("FILTER")] Filter,
            [Description("RXANTENNA")] RxAntenna,
            [Description("SCAN")] ScanRate
        }
        public enum PrePostEnableState
        {
            [Description("ENABLE")] Enabled,
            [Description("DISABLE")] Disabled
        }
        public enum PortParameters
        {
            [Description("ECHO")] Echo
        }
        public enum ALEStates
        {
            Scanning,
            Stopped,
            Linked,
            Calling,
            Sounding,
            Exchanging,
            ReceivingSignal,
            SignalReceived,
            TerminatingLink
        }

        public bool isPRC138 { get; internal set; }

        private bool isconnectionopen;
        public bool isConnectionOpen
        {
            get { return isconnectionopen; }
            internal set
            {
                if (value != isconnectionopen)
                {
                    isconnectionopen = value;

                    if (value == false)
                    {
                        modemPresets.Clear();       //if the radio is disconnected the modem presets must be cleared out.
                    }

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ConnectionOpen;

                    OnRadioStateChanged(args);                    
                }
            }
        }
        private OperatingModes operatingmode;
        public OperatingModes operatingMode
        {
            get { return operatingmode; }
            internal set
            {
                //if (value != operatingmode)
                //{
                    operatingmode = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.OperatingMode;

                    OnRadioStateChanged(args);
                //}
            }
        }
        private PowerLevels txpowerlevel;
        public PowerLevels txPowerLevel
        {
            get { return txpowerlevel; }
            internal set
            {
                if (value != txpowerlevel)
                {
                    txpowerlevel = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.PowerLevel;

                    OnRadioStateChanged(args);
                }
            }
        }
        private AntennaModes antennamode;
        public AntennaModes antennaMode
        {
            get { return antennamode; }
            internal set
            {
                if (value != antennamode)
                {
                    antennamode = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AntennaMode;

                    OnRadioStateChanged(args);
                }
            }
        }
        private AudioLevels audiolevel;
        public AudioLevels audioLevel
        {
            get { return audiolevel; }
            internal set
            {
                if (value != audiolevel)
                {
                    audiolevel = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AudioLevel;

                    OnRadioStateChanged(args);
                }
            }
        }
        private KeylineStates keyline;
        public KeylineStates keyLine
        {
            get { return keyline; }
            internal set
            {
                if (value != keyline)
                {
                    keyline = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.Keyline;

                    OnRadioStateChanged(args);
                }

                if (value == KeylineStates.Off)
                {
                    isTuning = false;
                }
            }
        }
        private FrequencySteps frequencystep;
        public FrequencySteps frequencyStep
        {
            get { return frequencystep; }
            internal set
            {
                if (value != frequencystep)
                {
                    frequencystep = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.FrequencyStep;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState digitalvoice;
        public OnOffState digitalVoice
        {
            get { return digitalvoice; }
            internal set
            {
                if (value != digitalvoice)
                {
                    digitalvoice = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.DigitalVoice;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState analogsquelch;
        public OnOffState analogSquelch
        {
            get { return analogsquelch; }
            internal set
            {
                if (value != analogsquelch)
                {
                    analogsquelch = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AnalogSquelch;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState fmsquelch;
        public OnOffState fMSquelch
        {
            get { return fmsquelch; }
            internal set
            {
                if (value != fmsquelch)
                {
                    fmsquelch = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.FMSquelch;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState digitalsquelch;
        public OnOffState digitalSquelch
        {
            get { return digitalsquelch; }
            internal set
            {
                if (value != digitalsquelch)
                {
                    digitalsquelch = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.DigitalSquelch;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState encrypt;
        public OnOffState enCrypt
        {
            get { return encrypt; }
            internal set
            {
                if (value != encrypt)
                {
                    encrypt = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.Encryption;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState analogvoicesecurity;
        public OnOffState analogVoiceSecurity
        {
            get { return analogvoicesecurity; }
            internal set
            {
                if (value != analogvoicesecurity)
                {
                    analogvoicesecurity = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AnalogVoiceSecurity;

                    OnRadioStateChanged(args);
                }
            }
        }
        private SquelchLevels squelchlevel;
        public SquelchLevels squelchLevel
        {
            get { return squelchlevel; }
            internal set
            {
                if (value != squelchlevel)
                {
                    squelchlevel = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.SquelchLevel;

                    OnRadioStateChanged(args);
                }
            }
        }
        private EnableState rwas;
        public EnableState rWAS
        {
            get { return rwas; }
            internal set
            {
                if (value != rwas)
                {
                    rwas = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.RWAS;

                    OnRadioStateChanged(args);
                }
            }
        }
        private EnableState retransmit;
        public EnableState reTransmit
        {
            get { return retransmit; }
            internal set
            {
                if (value != retransmit)
                {
                    retransmit = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.Retransmit;

                    OnRadioStateChanged(args);
                }
            }
        }
        private bool istuning;
        public bool isTuning
        {
            get { return istuning; }
            internal set
            {
                if (value != istuning)
                {
                    istuning = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.Tuning;

                    OnRadioStateChanged(args);
                }

                if (value == true)
                {
                    keyLine = KeylineStates.Mic;
                    isTuneComplete = false;
                    isTuneMarginal = false;
                    isTuneFail = false;

                }
            }
        }

        private bool istunecomplete;
        public bool isTuneComplete
        {
            get { return istunecomplete; }
            internal set
            {

                if (value != istunecomplete)
                {
                    istunecomplete = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.TuneComplete;

                    OnRadioStateChanged(args);
                }

                if (value == true)
                {
                    isTuning = false;
                    keyLine = KeylineStates.Off;
                }
                else
                {
                    isTuneMarginal = false;
                }
            }
        }
        private bool istunemarginal;
        public bool isTuneMarginal {
            get { return istunemarginal; }
            internal set
            {
                if (value != istunemarginal)
                {
                    istunemarginal = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.TuneMarginal;

                    OnRadioStateChanged(args);
                }

                if (value == true)
                {
                    isTuneComplete = true;
                    keyLine = KeylineStates.Off;
                }
            }
        }
        private bool istunefail;
        public bool isTuneFail
        {
            get { return istunefail; }
            internal set
            {
                if (value != istunefail)
                {
                    istunefail = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.TuneFail;

                    OnRadioStateChanged(args);
                }

                if (value == true)
                {
                    isTuneComplete = false;
                    keyLine = KeylineStates.Off;
                }
            }
        }
        private int operatingchannel = -1;      //special case. must be out of range on init.
        public int operatingChannel
        {
            get { return operatingchannel; }
            internal set
            {
                if (value != operatingchannel)
                {
                    operatingchannel = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.OperatingChannel;

                    OnRadioStateChanged(args);

                    Show();
                }
            }
        }
        private string txfrequency;
        public string txFrequency
        {
            get { return txfrequency; }
            internal set
            {
                isTuneComplete = false;
                isTuneFail = false;

                //if (value != txfrequency)
                //{
                    txfrequency = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.TxFrequency;

                    OnRadioStateChanged(args);
                //}
            }
        }
        private string rxfrequency;
        public string rxFrequency
        {
            get { return rxfrequency; }
            internal set
            {
                isTuneComplete = false;
                isTuneFail = false;

                //if (value != rxfrequency)
                //{
                    rxfrequency = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.RxFrequency;

                    OnRadioStateChanged(args);
                //}
            }
        }
        private ModulationModes modulationmode;
        public ModulationModes modulationMode
        {
            get { return modulationmode; }
            internal set
            {
                if (value != modulationmode)
                {
                    modulationmode = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ModulationMode;

                    OnRadioStateChanged(args);
                }
            }
        }
        private Bandwidths bandwidth;
        public Bandwidths bandWidth
        {
            get { return bandwidth; }
            internal set
            {
                if (value != bandwidth)
                {
                    bandwidth = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.Bandwidth;

                    OnRadioStateChanged(args);
                }
            }
        }
        private AGCSpeeds agcspeed;
        public AGCSpeeds aGCSpeed
        {
            get { return agcspeed; }
            internal set
            {
                if (value != agcspeed)
                {
                    agcspeed = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AGCSpeed;

                    OnRadioStateChanged(args);
                }
            }
        }
        private FMSquelchTypes fmsquelchtype;
        public FMSquelchTypes fMSquelchType
        {
            get { return fmsquelchtype; }
            internal set
            {
                if (value != fmsquelchtype)
                {
                    fmsquelchtype = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.FMSquelchType;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState fmtxtone;
        public OnOffState fMTxTone
        {
            get { return fmtxtone; }
            internal set
            {
                if (value != fmtxtone)
                {
                    fmtxtone = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.FMTxTone;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState voicecompression;
        public OnOffState voiceCompression
        {
            get { return voicecompression; }
            internal set
            {
                if (value != voicecompression)
                {
                    voicecompression = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.VoiceCompression;

                    OnRadioStateChanged(args);
                }
            }
        }
        private FMDeviations fmdeviation;
        public FMDeviations fMDeviation
        {
            get { return fmdeviation; }
            internal set
            {
                if (value != fmdeviation)
                {
                    fmdeviation = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.FMDeviation;

                    OnRadioStateChanged(args);
                }
            }
        }
        private CWOffsets cwoffset;
        public CWOffsets cWOffset
        {
            get { return cwoffset; }
            internal set
            {
                if (value != cwoffset)
                {
                    cwoffset = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.CWOffset;

                    OnRadioStateChanged(args);
                }
            }
        }
        private BFOOffsets bfooffset;
        public BFOOffsets bFOOffset
        {
            get { return bfooffset; }
            internal set
            {
                if (value != bfooffset)
                {
                    bfooffset = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.BFOOffset;

                    OnRadioStateChanged(args);
                }
            }
        }
        private YesNoState channelrxonly;
        public YesNoState channelRxOnly
        {
            get { return channelrxonly; }
            internal set
            {
                if (value != channelrxonly)
                {
                    channelrxonly = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ChannelRXOnly;

                    OnRadioStateChanged(args);
                }
            }
        }
        private int rfgain;
        public int rFGain
        {
            get { return rfgain; }
            internal set
            {
                if (value != rfgain)
                {
                    rfgain = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.RFGain;

                    OnRadioStateChanged(args);
                }
            }
        }
        private ModemPresets modemsetting;
        public ModemPresets modemSetting
        {
            get { return modemsetting; }
            internal set
            {
                if (value != modemsetting)
                {
                    modemsetting = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ModemSetting;

                    OnRadioStateChanged(args);
                }
            }
        }
        private BacklightFunctions backlightfunction;
        public BacklightFunctions backlightFunction
        {
            get { return backlightfunction; }
            internal set
            {
                if (value != backlightfunction)
                {
                    backlightfunction = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.BacklightFunction;

                    OnRadioStateChanged(args);
                }
            }
        }
        private Intensities backlightintensity;
        public Intensities backlightIntensity
        {
            get { return backlightintensity; }
            internal set
            {
                if (value != backlightintensity)
                {
                    backlightintensity = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.BacklightIntensity;

                    OnRadioStateChanged(args);
                }
            }
        }
        private Intensities contrastintensity;
        public Intensities contrastIntensity
        {
            get { return contrastintensity; }
            internal set
            {
                if (value != contrastintensity)
                {
                    contrastintensity = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ContrastIntensity;

                    OnRadioStateChanged(args);
                }
            }
        }
        private BypassState rxpreamp;
        public BypassState rxPreamp
        {
            get { return rxpreamp; }
            internal set
            {
                if (value != rxpreamp)
                {
                    rxpreamp = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.RxPreamp;

                    OnRadioStateChanged(args);
                }
            }
        }
        private BypassState internalcoupler;
        public BypassState internalCoupler
        {
            get { return internalcoupler; }
            internal set
            {
                if (value != internalcoupler)
                {
                    internalcoupler = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.InternalCoupler;

                    OnRadioStateChanged(args);
                }
            }
        }
        private YesNoState onekwpa;
        public YesNoState onekWPA
        {
            get { return onekwpa; }
            internal set
            {
                if (value != onekwpa)
                {
                    onekwpa = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.OneKWPA;

                    OnRadioStateChanged(args);
                }
            }
        }
        private PrePostEnableState prepostfilter;
        public PrePostEnableState prepostFilter
        {
            get { return prepostfilter; }
            internal set
            {
                if (value != prepostfilter)
                {
                    prepostfilter = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.PrePostFilter;

                    OnRadioStateChanged(args);
                }
            }
        }
        private PrePostEnableState prepostrxantenna;
        public PrePostEnableState prepostRxAntenna
        {
            get { return prepostrxantenna; }
            internal set
            {
                if (value != prepostrxantenna)
                {
                    prepostrxantenna = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.PrePostRxAntenna;

                    OnRadioStateChanged(args);
                }
            }
        }
        private PrePostScanRates prepostscanrate;
        public PrePostScanRates prepostScanRate
        {
            get { return prepostscanrate; }
            internal set
            {
                if (value != prepostscanrate)
                {
                    prepostscanrate = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.PrePostScanRate;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState portremoteecho;
        public OnOffState portRemoteEcho
        {
            get { return portremoteecho; }
            internal set
            {
                if (value != portremoteecho)
                {
                    portremoteecho = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.PortRemoteEcho;

                    OnRadioStateChanged(args);
                }
            }
        }
        private ALEStates alestate;
        public ALEStates aLEState
        {
            get { return alestate; }
            internal set
            {
                if (value != alestate)
                {
                    alestate = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ALEState;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState listeningsilence;
        public OnOffState listeningSilence
        {
            get { return listeningsilence; }
            internal set
            {
                if (value != listeningsilence)
                {
                    listeningsilence = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ListeningSilence;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState linktoanycalls;
        public OnOffState linkToAnyCalls
        {
            get { return linktoanycalls; }
            internal set
            {
                if (value != linktoanycalls)
                {
                    linktoanycalls = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.LinkToAnyCalls;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState linktoallcalls;
        public OnOffState linkToAllCalls
        {
            get { return linktoallcalls; }
            internal set
            {
                if (value != linktoallcalls)
                {
                    linktoallcalls = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.LinkToAllCalls;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState listenbeforetx;
        public OnOffState listenBeforeTx
        {
            get { return listenbeforetx; }
            internal set
            {
                if (value != listenbeforetx)
                {
                    listenbeforetx = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ListenBeforeTx;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState keytocall;
        public OnOffState keyToCall
        {
            get { return keytocall; }
            internal set
            {
                if (value != keytocall)
                {
                    keytocall = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.KeyToCall;

                    OnRadioStateChanged(args);
                }
            }
        }
        private OnOffState amddisplay;
        public OnOffState aMDDisplay
        {
            get { return amddisplay; }
            internal set
            {
                if (value != amddisplay)
                {
                    amddisplay = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.AMDDisplay;

                    OnRadioStateChanged(args);
                }
            }
        }
        private int linktimeouttime;
        public int linkTimeoutTime
        {
            get { return linktimeouttime; }
            internal set
            {
                if (value != linktimeouttime)
                {
                    linktimeouttime = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.LinkTimeoutTime;

                    OnRadioStateChanged(args);
                }
            }
        }
        private int maxchannelstoscan;
        public int maxChannelsToScan
        {
            get { return maxchannelstoscan; }
            internal set
            {
                if (value != maxchannelstoscan)
                {
                    maxchannelstoscan = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.MaxChannelsToScan;

                    OnRadioStateChanged(args);
                }
            }
        }
        private int tunetime;
        public int tuneTime
        {
            get { return tunetime; }
            internal set
            {
                if (value != tunetime)
                {
                    tunetime = value;

                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.TuneTime;

                    OnRadioStateChanged(args);
                }
            }
        }


        public struct ModemPreset
        {
            public int PresetNumber { get; set; }
            public string PresetName { get; set; }
        }

        public List<ModemPreset> modemPresets = new List<ModemPreset>();

        private bool isFMChangeRestartAnalogSquelch { get; set; }
        private bool isDigitalVoiceRestart { get; set; }
        private bool isavsinstalled = true;
        public bool isAVSInstalled
        {
            get
            {
                return isavsinstalled;
            }

            internal set
            {
                isavsinstalled = value;
            }
        }
        public bool isEncryptionInstalled { get; internal set; }
        public bool isConsoleEnabled { get; set; }

        public string RemotePort { get; internal set; }

        private const int serialPortPollingInterval = 25;
        private const int serialPortSendingInterval = 125;

        private System.Threading.Timer RxTimer = null;
        private System.Threading.Timer TxTimer = null;
        private readonly object RxLockObject = new object();
        private readonly object TxLockObject = new object();

        public FalconRadio()  
        {
            RxTimer = new System.Threading.Timer(new TimerCallback(RxTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);
            TxTimer = new System.Threading.Timer(new TimerCallback(TxTimerTick), new object(), Timeout.Infinite, Timeout.Infinite);
        }
        public void OpenRemotePort()
        {
            if (!isConnectionOpen)
            {
                isConnectionOpen = RemoteSerialPortController.OpenSerialPort();

                if (isConnectionOpen)
                {
                    RemotePort = RemoteSerialPortController.comPort;

                    RxTimer.Change(0, serialPortPollingInterval);                                    //Enable RxTimer
                    TxTimer.Change(serialPortPollingInterval / 2, serialPortSendingInterval);       //Enable TxTimer

                    FlushPort();                       //Clear any garbage from the buffer   
                    InitializeRadioParameters();
                    InitializeUI();
                }                

                RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                args.PropertyChanged = Parameters.ConnectionOpen;
                OnRadioStateChanged(args);                
            }

        }

        public void CloseRemotePort()
        {
            if (isConnectionOpen)
            {
                RxTimer.Change(Timeout.Infinite, Timeout.Infinite);     //Disable the timer
                TxTimer.Change(Timeout.Infinite, Timeout.Infinite);     //Disable the timer

                isConnectionOpen = RemoteSerialPortController.CloseSerialPort();                
            }
        }
        public List<string> RefreshPortList()
        {
            return RemoteSerialPortController.RefreshPortList();
        }

        public void OpenDataPort()
        {
            //DataSerialPortController.OpenSerialPort();
        }

        public void CloseDataPort()
        {
            //DataSerialPortController.CloseSerialPort();            
        }

        private void RxTimerTick(object state)
        {
            isConnectionOpen = RemoteSerialPortController.isOpen;

            if (isConnectionOpen)
            {
                GetResponse();
            }
        }

        private void TxTimerTick(object state)
        {
            ProcessRemoteCommandQueue();
        }

        protected virtual void OnRadioStateChanged(RadioStateChangedEventArgs e)
        {
            EventHandler<RadioStateChangedEventArgs> handler = RadioStateChanged;
            handler?.Invoke(this, e);

        }
        protected virtual void OnMessageReceived(MessageReceivedEventArgs e)
        {
            EventHandler<MessageReceivedEventArgs> handler = MessageReceived;
            handler?.Invoke(this, e);

        }

        private void InitializeRadioParameters()
        {
            SerialPortEcho(OnOffState.Off);         //Turn off the echo so it doesn't clutter the buffer
            isPRC138 = true;                        //Later I'll figure out how to query the radio for this.            

            AnalogVoiceSecurity();
            Encryption();
            
            FMDeviation();                              //Must get the FM mode parameters explicitly
            FMSquelchType();
            FMSquelch();
            FMTxTone();

            VoiceCompression();
            FrequencyStep();

            Modem(ModemCommands.GetPresets);            //Poll the radio for all of the modem presets

            OperatingChannel();                                 //Now get the current channel data.

        }

        private void InitializeUI()
        {
            RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();

            args.PropertyChanged = Parameters.OperatingMode;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.PowerLevel;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.Keyline;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.ModulationMode;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.DigitalVoice;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.Bandwidth;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.AGCSpeed;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.AnalogSquelch;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.CWOffset;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.BFOOffset;
            OnRadioStateChanged(args);
            //args.PropertyChanged = Parameters.ModemSetting;
            //OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.Encryption;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.FMDeviation;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.FMSquelchType;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.SquelchLevel;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.FMTxTone;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.VoiceCompression;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.ChannelRXOnly;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.FrequencyStep;
            OnRadioStateChanged(args);
            args.PropertyChanged = Parameters.Retransmit;
            OnRadioStateChanged(args);
        }

        public void ProcessRemoteCommandQueue()
        {
            lock (RxLockObject)
            {
                RemoteSerialPortController.ProcessCommandQueue();
            }
        }

        public void GetResponse()
        {
            lock (TxLockObject)
            {
                RemoteSerialPortController.ReadSerialPort();
                RemoteSerialPortController.ProcessRawRxQueue();
                ParseResponseQueue();
            }
        }

        private void ParseResponseQueue()
        {
            while (RemoteSerialPortController.ResponseQueue.Count > 0)
            {

                string payload;
                string parameter;
                string response = RemoteSerialPortController.ResponseQueue.Dequeue();

                MessageReceivedEventArgs messageArgs = new MessageReceivedEventArgs();
                messageArgs.message = response;
                OnMessageReceived(messageArgs);

                    response = response.ToUpper();
                    response = response.Trim(' ');        //Trim any leading or trailing spaces.

                    int firstIndex = response.IndexOf(" ");

                    if (firstIndex >= 1)
                    {
                        parameter = response.Substring(0, firstIndex);
                        payload = response.Substring(firstIndex + 1, response.Length - firstIndex - 1);
                        payload = payload.Trim(' ');
                    }
                    else
                    {
                        parameter = response;
                        payload = null;
                    }

                if (parameter == Responses.SSB.ToDescription())
                {
                    operatingMode = OperatingModes.SSB;
                }
                else if (parameter == Responses.HOP.ToDescription())
                {
                    operatingMode = OperatingModes.HOP;
                }
                else if (parameter == Responses.ALE.ToDescription())
                {
                    operatingMode = OperatingModes.ALE;
                }
                else if (parameter == Responses.HOP.ToDescription())
                {
                    operatingMode = OperatingModes.HOP;
                }
                else if (parameter == Responses.TxPowerLevel.ToDescription())
                {
                    try
                    {
                        txPowerLevel = EnumEx.GetValueFromDescription<PowerLevels>(Responses.TxPowerLevel, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message,"Error");
                    }
                }
                else if (parameter == Responses.TxFrequency.ToDescription())
                {
                    txFrequency = payload;
                }
                else if (parameter == Responses.RxFrequency.ToDescription())
                {
                    rxFrequency = payload;
                }
                else if (parameter == Responses.OperatingChannel.ToDescription())
                {
                    try
                    {
                        operatingChannel = Convert.ToInt32(payload);
                    }
                    catch (FormatException ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Keyline.ToDescription())
                {
                    try
                    {
                        keyLine = EnumEx.GetValueFromDescription<KeylineStates>(Responses.Keyline, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.TuningCoupler.ToDescription())
                {
                    isTuning = true;
                }
                else if (parameter == Responses.TuneComplete.ToDescription())
                {
                    if (payload == TuneStates.Complete.ToDescription())
                    {
                        isTuneComplete = true;
                    }
                    else if (payload == TuneStates.Marginal.ToDescription())
                    {
                        isTuneMarginal = true;
                    }
                    else if (payload == TuneStates.Fail.ToDescription())
                    {
                        isTuneFail = true;
                    }
                    else
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "'", "Error");
                    }
                }
                else if (parameter == Responses.AnalogSquelch.ToDescription())
                {
                    try
                    {
                        analogSquelch = EnumEx.GetValueFromDescription<OnOffState>(Responses.AnalogSquelch, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                    finally
                    {
                        if ((modulationMode == ModulationModes.USB || modulationMode == ModulationModes.LSB) && analogSquelch == OnOffState.Off && isFMChangeRestartAnalogSquelch)
                        {
                            isFMChangeRestartAnalogSquelch = false;     //catch the flag and turn squelch back on.
                            AnalogSquelch(OnOffState.On);
                        }
                    }
                }
                else if (parameter == Responses.SquelchLevel.ToDescription())
                {
                    try
                    {
                        squelchLevel = EnumEx.GetValueFromDescription<SquelchLevels>(Responses.SquelchLevel, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.DigitalSquelch.ToDescription())
                {
                    try
                    {
                        digitalSquelch = EnumEx.GetValueFromDescription<OnOffState>(Responses.DigitalSquelch, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.FMSquelch.ToDescription())
                {
                    SetSquelchFlag();

                    try
                    {
                        fMSquelch = EnumEx.GetValueFromDescription<OnOffState>(Responses.FMSquelch, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.FMSquelchType.ToDescription())
                {
                    SetSquelchFlag();

                    try
                    {
                        fMSquelchType = EnumEx.GetValueFromDescription<FMSquelchTypes>(Responses.FMSquelchType, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.FMTxTone.ToDescription())
                {
                    SetSquelchFlag();

                    try
                    {
                        fMTxTone = EnumEx.GetValueFromDescription<OnOffState>(Responses.FMTxTone, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.DigitalVoice.ToDescription())
                {
                    try
                    {
                        digitalVoice = EnumEx.GetValueFromDescription<OnOffState>(Responses.DigitalVoice, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                    finally
                    {
                        AGCSpeed();     //Changing DV mode can silently change the AGC speed, so poll it.
                    }
                }
                else if (parameter == Responses.Bandwidth.ToDescription())
                {
                    try
                    {
                        bandWidth = EnumEx.GetValueFromDescription<Bandwidths>(Responses.Bandwidth, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.FMDeviation.ToDescription())
                {
                    SetSquelchFlag();

                    try
                    {
                        fMDeviation = EnumEx.GetValueFromDescription<FMDeviations>(Responses.FMDeviation, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.ModulationMode.ToDescription())
                {
                    try
                    {
                        modulationMode = EnumEx.GetValueFromDescription<ModulationModes>(Responses.ModulationMode, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                    finally
                    {

                        if (modulationMode == ModulationModes.USB || modulationMode == ModulationModes.LSB)
                        {
                            //There is a bug in the PRC-138 where if a property related to FM mode is changed then after changing back to SSB 
                            //the analog squelch must be cycled before it works properly again.
                            if (isFMChangeRestartAnalogSquelch)
                            {
                                //isFMChangeRestartAnalogSquelch = false;     //catch the flag and turn squelch back on.
                                AnalogSquelch(OnOffState.Off);
                            }

                            //There is a bug in the PRC-138 where if DV is on and the modulation mode is changed away from SSB upon return to SSB DV is automatically
                            //deasserted but must be reasserted before the radio works properly again.
                            if (isDigitalVoiceRestart)
                            {
                                isDigitalVoiceRestart = false;          //Clear the flag.

                                DigitalVoice(OnOffState.On);
                            }
                        }
                        else
                        {
                            if (digitalVoice == OnOffState.On)
                            {
                                isDigitalVoiceRestart = true;   //if DV was on then set the flag.

                                DigitalVoice();     //If mode changes away from USB/LSB while in DV mode, must refresh DV state.    
                            }
                        }

                        AGCSpeed();             //The radio may silently change the AGC speed on mode change, so poll it.
                        VoiceCompression();     //The radio may silently change the voice compression enable state on mode change, so poll it.

                        if (modulationMode == ModulationModes.FM)
                        {
                            SetSquelchFlag();         //If mode changes to FM we must refresh the squelch state.
                        }
                    }
                }
                else if (parameter == Responses.AGCSpeed.ToDescription())
                {
                    try
                    {
                        aGCSpeed = EnumEx.GetValueFromDescription<AGCSpeeds>(Responses.AGCSpeed, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.RWAS.ToDescription())
                {
                    try
                    {
                        rWAS = EnumEx.GetValueFromDescription<EnableState>(Responses.RWAS, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Retransmission.ToDescription())
                {
                    try
                    {
                        reTransmit = EnumEx.GetValueFromDescription<EnableState>(Responses.Retransmission, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Encrypt.ToDescription())
                {
                    try
                    {
                        enCrypt = EnumEx.GetValueFromDescription<OnOffState>(Responses.Encrypt, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Encryption.ToDescription())
                {
                    if (payload == ComSecInstalledState.NotInstalled.ToDescription())
                    {
                        isEncryptionInstalled = false;
                    }
                    else if (payload == ComSecInstalledState.Installed.ToDescription())
                    {
                        isEncryptionInstalled = true;
                    }
                    else
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "'", "Error");
                    }
                }
                else if (parameter == Responses.AnalogVoiceSecurity.ToDescription())
                {
                    if (payload == ComSecInstalledState.NotInstalled.ToDescription())
                    {
                        isAVSInstalled = false;
                    }
                    else
                    {
                        try
                        {
                            analogVoiceSecurity = EnumEx.GetValueFromDescription<OnOffState>(Responses.AnalogVoiceSecurity, payload);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error");
                        }
                    }
                }
                else if (parameter == Responses.AntennaMode.ToDescription())
                {
                    try
                    {
                        antennaMode = EnumEx.GetValueFromDescription<AntennaModes>(Responses.AntennaMode, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.VoiceCompression.ToDescription())
                {
                    try
                    {
                        voiceCompression = EnumEx.GetValueFromDescription<OnOffState>(Responses.VoiceCompression, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.BFOOffset.ToDescription())
                {
                    try
                    {
                        bFOOffset = EnumEx.GetValueFromDescription<BFOOffsets>(Responses.BFOOffset, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.CWOffset.ToDescription())
                {
                    try
                    {
                        cWOffset = EnumEx.GetValueFromDescription<CWOffsets>(Responses.CWOffset, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.FrequencyStep.ToDescription())
                {
                    try
                    {
                        frequencyStep = EnumEx.GetValueFromDescription<FrequencySteps>(Responses.FrequencyStep, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.RxOnly.ToDescription())
                {
                    try
                    {
                        channelRxOnly = EnumEx.GetValueFromDescription<YesNoState>(Responses.RxOnly, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.RFGain.ToDescription())
                {
                    try
                    {
                        rFGain = Convert.ToInt32(payload);
                    }
                    catch (FormatException ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Modem.ToDescription())
                {
                    string modemParameter;
                    int modemFirstIndex = payload.IndexOf(" ");
                    string modemPayload;

                    if (modemFirstIndex >= 1)
                    {
                        modemParameter = payload.Substring(0, modemFirstIndex);
                        modemPayload = payload.Substring(modemFirstIndex + 1, payload.Length - modemFirstIndex - 1);
                        modemPayload = modemPayload.Trim(' ');
                    }
                    else
                    {
                        modemParameter = payload;
                        modemPayload = null;
                    }

                    if (modemParameter == ModemResponses.Off.ToDescription())
                    {
                        modemSetting = ModemPresets.Off;

                        DigitalVoice();                   //Modem and DV are mutually exclusive so must poll for DV after the modem setting changes.
                        AnalogSquelch();                 //Changing modem modes silently changes squelch.
                        Bandwidth();                      //Changing modem mode silently changes bandwidth.
                    }
                    else if (modemParameter == ModemResponses.Preset.ToDescription())
                    {
                        ModemPreset preset = new ModemPreset();

                        //Preset Number
                        preset.PresetNumber = Convert.ToInt32(modemPayload.Substring(0, modemPayload.IndexOf(" ")));
                        modemPayload = modemPayload.Substring(modemPayload.IndexOf(" ") + 1);
                        modemPayload.TrimStart(' ');

                        //Preset Name
                        preset.PresetName = modemPayload.Substring(0, modemPayload.IndexOf(" "));
                        modemPayload = modemPayload.Substring(modemPayload.IndexOf(" ") + 1);
                        modemPayload.TrimStart(' ');

                        //Remainder of preset data -- write this later.

                        modemPresets.Add(preset);

                        RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                        args.PropertyChanged = Parameters.ModemPreset;

                        OnRadioStateChanged(args);

                    }
                    else
                    {
                        //Modem is on. Parse the payload to determine which preset.

                        int presetnumber = -1;

                        try
                        {
                            presetnumber = Convert.ToInt32(modemParameter);
                        }
                        catch (FormatException ex)
                        {
                            if (!isConsoleEnabled)
                            {
                                MessageBox.Show("Unrecognized " + modemParameter + " Message: '" + modemPayload + "' " + Environment.NewLine + ex.Message, "Error");
                            }
                        }
                        finally
                        {
                            if (presetnumber >= 0 && presetnumber <= 9)
                            {
                                modemSetting = (ModemPresets)presetnumber + 1; //OFF is in the 0 position, so must offset the list by 1

                                DigitalVoice();                   //Modem and DV are mutually exclusive so must poll for DV after the modem setting changes.
                                AnalogSquelch();                 //Changing modem modes silently changes squelch.
                                Bandwidth();                      //Changing modem mode silently changes bandwidth.
                            }
                        }
                    }

                }
                else if (parameter == Responses.NoModemPresets.ToDescription())
                {
                    RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                    args.PropertyChanged = Parameters.ModemPreset;

                    OnRadioStateChanged(args);
                }
                else if (parameter == Responses.Time.ToDescription())
                {
                    try
                    {
                        //Don't try and store the radio time since it always changes.
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Date.ToDescription())
                {
                    try
                    {
                        //Don't try and store the radio date.
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.Day.ToDescription())
                {
                    try
                    {
                        //Don't try and store the radio day.
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.BacklightFunction.ToDescription())
                {
                    try
                    {
                        backlightFunction = EnumEx.GetValueFromDescription<BacklightFunctions>(Responses.BacklightFunction, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.BacklightIntensity.ToDescription())
                {
                    try
                    {
                        backlightIntensity = EnumEx.GetValueFromDescription<Intensities>(Responses.BacklightIntensity, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.ContrastIntensity.ToDescription())
                {
                    try
                    {
                        contrastIntensity = EnumEx.GetValueFromDescription<Intensities>(Responses.ContrastIntensity, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.RxPreamp.ToDescription())
                {
                    try
                    {
                        rxPreamp = EnumEx.GetValueFromDescription<BypassState>(Responses.RxPreamp, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.InternalCoupler.ToDescription())
                {
                    try
                    {
                        internalCoupler = EnumEx.GetValueFromDescription<BypassState>(Responses.InternalCoupler, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.OneKWPA.ToDescription())
                {
                    try
                    {
                        onekWPA = EnumEx.GetValueFromDescription<YesNoState>(Responses.OneKWPA, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.PrePost.ToDescription())
                {
                    string prepostParameter;
                    int prepostFirstIndex = payload.IndexOf(" ");
                    string prepostPayload;

                    if (prepostFirstIndex >= 1)
                    {
                        prepostParameter = payload.Substring(0, prepostFirstIndex);
                        prepostPayload = payload.Substring(prepostFirstIndex + 1, payload.Length - prepostFirstIndex - 1);
                        prepostPayload = prepostPayload.Trim(' ');
                    }
                    else
                    {
                        prepostParameter = payload;
                        prepostPayload = null;
                    }

                    if (prepostParameter == PrePostParameters.Filter.ToDescription())
                    {
                        try
                        {
                            prepostFilter = EnumEx.GetValueFromDescription<PrePostEnableState>(Responses.PrePost, prepostPayload);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error");
                        }

                        RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                        args.PropertyChanged = Parameters.PrePostFilter;

                        OnRadioStateChanged(args);
                    }
                    else if (prepostParameter == PrePostParameters.RxAntenna.ToDescription())
                    {
                        try
                        {
                            prepostRxAntenna = EnumEx.GetValueFromDescription<PrePostEnableState>(Responses.PrePost, prepostPayload);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error");
                        }

                        RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                        args.PropertyChanged = Parameters.PrePostRxAntenna;

                        OnRadioStateChanged(args);
                    }
                    else if (prepostParameter == PrePostParameters.ScanRate.ToDescription())
                    {
                        try
                        {
                            prepostScanRate = EnumEx.GetValueFromDescription<PrePostScanRates>(Responses.PrePost, prepostPayload);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error");
                        }

                        RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                        args.PropertyChanged = Parameters.PrePostScanRate;

                        OnRadioStateChanged(args);
                    }
                }
                else if (parameter == Responses.PortRemote.ToDescription())
                {
                    string portRemoteParameter;
                    int portRemoteFirstIndex = payload.IndexOf(" ");
                    string portRemotePayload;

                    if (portRemoteFirstIndex >= 1)
                    {
                        portRemoteParameter = payload.Substring(0, portRemoteFirstIndex);
                        portRemotePayload = payload.Substring(portRemoteFirstIndex + 1, payload.Length - portRemoteFirstIndex - 1);
                        portRemotePayload = portRemotePayload.Trim(' ');
                    }
                    else
                    {
                        portRemoteParameter = payload;
                        portRemotePayload = null;
                    }

                    if (portRemoteParameter == PortParameters.Echo.ToDescription())
                    {
                        try
                        {
                            portRemoteEcho = EnumEx.GetValueFromDescription<OnOffState>(Responses.PortRemote, portRemotePayload);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Error");
                        }

                        RadioStateChangedEventArgs args = new RadioStateChangedEventArgs();
                        args.PropertyChanged = Parameters.PortRemoteEcho;

                        OnRadioStateChanged(args);
                    }
                    else
                    {
                        if (!isConsoleEnabled)
                        {
                            //Unrecognized PortRemote command
                            MessageBox.Show("Unrecognized " + portRemoteParameter + " Message: '" + portRemotePayload + "'", "Error");
                        }
                    }
                }
                else if (parameter == Responses.Scanning.ToDescription())
                {
                        aLEState = ALEStates.Scanning;                    
                }
                else if (parameter == Responses.TerminatingLink.ToDescription())
                {
                    aLEState = ALEStates.TerminatingLink;
                }
                else if (parameter == Responses.Sounding.ToDescription())
                {
                    aLEState = ALEStates.Sounding;
                }
                else if (parameter == Responses.Exchange.ToDescription())
                {
                    aLEState = ALEStates.Exchanging;
                }
                else if (parameter == Responses.ScanStopped.ToDescription())
                {
                    aLEState = ALEStates.Stopped;
                }
                else if (parameter == Responses.SignalReceived.ToDescription())
                {
                    aLEState = ALEStates.SignalReceived;
                }
                else if (parameter == Responses.Receiving.ToDescription())
                {
                    aLEState = ALEStates.ReceivingSignal;
                }
                else if (parameter == Responses.Linked.ToDescription())
                {
                    aLEState = ALEStates.Linked;
                }
                else if (parameter == Responses.ListeningSilence.ToDescription())
                {
                    try
                    {
                        listeningSilence = EnumEx.GetValueFromDescription<OnOffState>(Responses.ListeningSilence, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.LinkToAnyCalls.ToDescription())
                {
                    try
                    {
                        linkToAnyCalls = EnumEx.GetValueFromDescription<OnOffState>(Responses.LinkToAnyCalls, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.LinkToAllCalls.ToDescription())
                {
                    try
                    {
                        linkToAllCalls = EnumEx.GetValueFromDescription<OnOffState>(Responses.LinkToAllCalls, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.ListenBeforeTx.ToDescription())
                {
                    try
                    {
                        listenBeforeTx = EnumEx.GetValueFromDescription<OnOffState>(Responses.ListenBeforeTx, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.KeyToCall.ToDescription())
                {
                    try
                    {
                        keyToCall = EnumEx.GetValueFromDescription<OnOffState>(Responses.KeyToCall, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.AMDDisplay.ToDescription())
                {
                    try
                    {
                        aMDDisplay = EnumEx.GetValueFromDescription<OnOffState>(Responses.AMDDisplay, payload);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.LinkTimeoutTime.ToDescription())
                {
                    try
                    {
                        linkTimeoutTime = Convert.ToInt32(payload);
                    }
                    catch (FormatException ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.MaxChannelsToScan.ToDescription())
                {
                    try
                    {
                        maxChannelsToScan = Convert.ToInt32(payload);
                    }
                    catch (FormatException ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }
                else if (parameter == Responses.TuneTime.ToDescription())
                {
                    try
                    {
                        tuneTime = Convert.ToInt32(payload);
                    }
                    catch (FormatException ex)
                    {
                        MessageBox.Show("Unrecognized " + response + " Message: '" + payload + "' " + Environment.NewLine + ex.Message, "Error");
                    }
                }

                /*
            [Description("SCANNING")] Scanning,
            [Description("TERMINATING")] TerminatingLink,
            [Description("SOUNDING")] Sounding,
            [Description("EXCHANGE")] Exchange,
            [Description("SCAN")] ScanStopped,
            [Description("SIGNAL")] SignalReceived,
            [Description("RECEIVING")] Receiving,
            [Description("RXMSG")] ReceivedMessage,
            [Description("LINKED")] Linked,
            [Description("RAD_SIL")] ListeningSilence,
            [Description("ALL_CALL")] LinkToAllCalls,
            [Description("ANY_CALL")] LinkToAnyCalls,
            [Description("LSTN")] ListenBeforeTx,
            [Description("KEY_TO_CALL")] KeyToCall,
            [Description("AMD_DISPLAY")] AMDDisplay,
            [Description("INV")] InvalidNetAddress,
            [Description("INVALID")] InvalidIndividualAddress,
            [Description("SLFAD")] SelfAddress,
            [Description("NETAD")] NetAddress,
            [Description("INDAD")] IndividualAddress,
            [Description("TIME_OUT")] LinkTimeoutTime,
            [Description("MAXCH")] MaxChannelsToScan,
            [Description("TUNETIME")] TuneTime,
            [Description("CHGROUP")] ChannelGroup
            */

                else if (parameter == Responses.PortRemoteCommandEcho.ToDescription())
                {
                    //Do nothing. This is just the echo from the radio of the opening command to turn off the echo...
                }
                else if (parameter == Responses.Error.ToDescription())
                {
                    //Do nothing. Probably frequency out of range.
                }
                else if (parameter == "")
                {
                    //Do nothing. There are some cases where spaces get put into the response queue and then get trimmed down to an empty string.
                }
                else
                {
                    if (!isConsoleEnabled)
                    {
                        MessageBox.Show("Unrecognized Message: '" + response + "'", "Error");
                    }
                }

            }
        }
        private void SetSquelchFlag()
        {
            //There is a bug in the PRC-138 where if a property related to FM mode is changed then the analog squelch must be cycled before it works properly again.
         
            if (analogSquelch == OnOffState.On)     //If squelch was on, set the flag so it is turned back on again. Else just turn it off and leave it off.
            {
                isFMChangeRestartAnalogSquelch = true;
            }
        }
        
        private void FlushPort()        //send some crlf's to clear whatever junk is in the serial port buffer
        {
            List<string> segments = new List<string>();

            segments.Add(System.Environment.NewLine);
            segments.Add(System.Environment.NewLine);

            RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
        }

        public void SSB()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.SSB.ToDescription()));
                Show();
            }
        }

        public void HOP()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.HOP.ToDescription()));
                Show();
            }
        }

        public void ALE()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ALE.ToDescription()));
                Show();
            }
        }

        public void TxPowerLevel()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.PowerLevel.ToDescription()));
            }
        }

        public void TxPowerLevel(PowerLevels level)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.PowerLevel.ToDescription());
                segments.Add(level.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void Show()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Show.ToDescription()));
            }
        }

        public void Display()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Display.ToDescription()));
            }
        }

        public void Display(int startChan, int endChan)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Display.ToDescription());
                segments.Add(startChan.ToString());
                segments.Add(endChan.ToString());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void Frequency()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Frequency.ToDescription()));
            }
        }

        public void Frequency(string frequency)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Frequency.ToDescription());
                segments.Add(frequency);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void TxFrequency()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.TxFrequency.ToDescription()));
            }
        }

        public void TxFrequency(string frequency)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.TxFrequency.ToDescription());
                segments.Add(frequency);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void RxFrequency()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.RxFrequency.ToDescription()));
            }
        }

        public void RxFrequency(string frequency)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.RxFrequency.ToDescription());
                segments.Add(frequency);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void ListeningSilence()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ListeningSilence.ToDescription()));
            }
        }

        public void ListeningSilence(EnableState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.ListeningSilence.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void IncrementFrequency()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.IncrementFrequency.ToDescription()));
            }
        }
        public void DecrementFrequency()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.DecrementFrequency.ToDescription()));
            }
        }

        public void Retune()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.RetuneCoupler.ToDescription()));
            }
        }

        public void SerialPortEcho()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.SerialPortEcho.ToDescription()));
            }
        }

        public void SerialPortEcho(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.SerialPortEcho.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void OperatingChannel()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.OperatingChannel.ToDescription()));               
            }
        }

        public void OperatingChannel(int channel)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.OperatingChannel.ToDescription());
                segments.Add(channel.ToString());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));                
            }
        }

        public void AnalogSquelch()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.AnalogSquelch.ToDescription()));
            }
        }

        public void AnalogSquelch(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.AnalogSquelch.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void DigitalSquelch()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.DigitalSquelch.ToDescription()));
            }
        }

        public void DigitalSquelch(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.DigitalSquelch.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void FMSquelch()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.FMSquelch.ToDescription()));
            }
        }

        public void FMSquelch(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.FMSquelch.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void RxOnly()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ChannelRXOnly.ToDescription()));
            }
        }

        public void RxOnly(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.ChannelRXOnly.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void FMTxTone()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.FMTxTone.ToDescription()));
            }
        }

        public void FMTxTone(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.FMTxTone.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void VoiceCompression()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.VoiceCompression.ToDescription()));
            }
        }

        public void VoiceCompression(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.VoiceCompression.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void DigitalVoice()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.DigitalVoice.ToDescription()));
            }
        }

        public void DigitalVoice(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.DigitalVoice.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void ModulationMode()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ModulationMode.ToDescription()));
            }
        }

        public void ModulationMode(ModulationModes mode)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.ModulationMode.ToDescription());
                segments.Add(mode.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Bandwidth()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Bandwidth.ToDescription()));
            }
        }

        public void Bandwidth(Bandwidths bandwidth)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Bandwidth.ToDescription());
                segments.Add(bandwidth.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void AGCSpeed()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.AGCSpeed.ToDescription()));
            }
        }

        public void AGCSpeed(AGCSpeeds speed)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.AGCSpeed.ToDescription());
                segments.Add(speed.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void FMSquelchType()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.FMSquelchType.ToDescription()));
            }
        }

        public void FMSquelchType(FMSquelchTypes sqtype)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.FMSquelchType.ToDescription());
                segments.Add(sqtype.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void FMDeviation()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.FMDeviation.ToDescription()));
            }
        }

        public void FMDeviation(FMDeviations deviation)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.FMDeviation.ToDescription());
                segments.Add(deviation.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void SquelchLevel()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.SquelchLevel.ToDescription()));
            }
        }

        public void SquelchLevel(SquelchLevels sqlevel)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.SquelchLevel.ToDescription());
                segments.Add(sqlevel.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Encryption()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Encryption.ToDescription()));
            }
        }

        public void Encryption(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Encryption.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void AnalogVoiceSecurity()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.AnalogVoiceSecurity.ToDescription()));
            }
        }

        public void AnalogVoiceSecurity(OnOffState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.AnalogVoiceSecurity.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void BFOOffset()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.BFOOffset.ToDescription()));
            }
        }

        public void BFOOffset(BFOOffsets offset)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.BFOOffset.ToDescription());
                segments.Add(offset.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void CWOffset()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.CWOffset.ToDescription()));
            }
        }

        public void CWOffset(CWOffsets offset)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.CWOffset.ToDescription());
                segments.Add(offset.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void Retransmit()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Retransmit.ToDescription()));
            }
        }

        public void Retransmit(EnableState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Retransmit.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void FrequencyStep()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.FrequencyStep.ToDescription()));
            }
        }

        public void FrequencyStep(bool isDirectionPositive)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.FrequencyStep.ToDescription());
                segments.Add(ParseFrequencyStep(isDirectionPositive));

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        private string ParseFrequencyStep(bool isDirectionPositive)
        {
            FrequencySteps newstep;

            if (isDirectionPositive)
            {
                if ((int)frequencyStep >= Enum.GetNames(typeof(FrequencySteps)).Count() - 1)
                {
                    newstep = (FrequencySteps)0;
                }
                else
                {
                    newstep = (FrequencySteps)((int)frequencyStep + 1);
                }
            }
            else
            {
                if ((int)frequencyStep <= 0)
                {
                    newstep = (FrequencySteps)Enum.GetNames(typeof(FrequencySteps)).Count() - 1;
                }
                else
                {
                    newstep = (FrequencySteps)((int)frequencyStep - 1);
                }
            }

            return newstep.ToDescription();
        }
        public void Modem()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Modem.ToDescription()));
            }
        }

        public void Modem(ModemCommands command)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Modem.ToDescription());
                segments.Add(command.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Modem(ModemPresets presetnumber)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Modem.ToDescription());
                segments.Add(presetnumber.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Time()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Modem.ToDescription()));
            }
        }

        public void Time(string time)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Time.ToDescription());
                segments.Add(time);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Date()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Modem.ToDescription()));
            }
        }

        public void Date(string date)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Date.ToDescription());
                segments.Add(date);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void Day()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Modem.ToDescription()));
            }
        }

        public void Day(string day)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Day.ToDescription());
                segments.Add(day.ToUpper());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void BacklightFunction()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.BacklightFunction.ToDescription()));
            }
        }

        public void BacklightFunction(BacklightFunctions function)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.BacklightFunction.ToDescription());
                segments.Add(function.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void BacklightIntensity()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.BacklightIntensity.ToDescription()));
            }
        }

        public void BacklightIntensity(Intensities intensity)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.BacklightIntensity.ToDescription());
                segments.Add(intensity.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void ContrastIntensity()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ContrastIntensity.ToDescription()));
            }
        }

        public void ContrastIntensity(Intensities intensity)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.ContrastIntensity.ToDescription());
                segments.Add(intensity.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void RxPreamp()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.RxPreamp.ToDescription()));
            }
        }

        public void RxPreamp(BypassState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.RxPreamp.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void InternalCoupler()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.InternalCoupler.ToDescription()));
            }
        }

        public void InternalCoupler(BypassState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.InternalCoupler.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void OneKWPA()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.OneKWPA.ToDescription()));
            }
        }

        public void OneKWPA(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.OneKWPA.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void PrePostFilter()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.PrePostFilter.ToDescription()));
            }
        }

        public void PrePostFilter(PrePostEnableState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.PrePostFilter.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void PrePostRxAntenna()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.PrePostRxAntenna.ToDescription()));
            }
        }

        public void PrePostRxAntenna(PrePostEnableState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.PrePostRxAntenna.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void PrePostScanRate()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.PrePostScanRate.ToDescription()));
            }
        }

        public void PrePostScanRate(PrePostScanRates scanrate)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.PrePostScanRate.ToDescription());
                segments.Add(scanrate.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void AntennaMode()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.AntennaMode.ToDescription()));
            }
        }

        public void AntennaMode(AntennaModes mode)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.AntennaMode.ToDescription());
                segments.Add(mode.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void RWAS()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.RWAS.ToDescription()));
            }
        }

        public void RWAS(EnableState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.RWAS.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void Scan()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Scan.ToDescription()));
            }
        }
        public void Stop()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.Stop.ToDescription()));
            }
        }

        public void Call(string station)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Call.ToDescription());
                segments.Add(station);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void Call(string station, string channel)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.Call.ToDescription());
                segments.Add(station);
                segments.Add(channel);

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void LinkToAnyCalls()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.LinkToAnyCalls.ToDescription()));
            }
        }

        public void LinkToAnyCalls(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.LinkToAnyCalls.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }

        public void LinkToAllCalls()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.LinkToAllCalls.ToDescription()));
            }
        }

        public void LinkToAllCalls(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.LinkToAllCalls.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void ListenBeforeTx()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.ListenBeforeTx.ToDescription()));
            }
        }

        public void ListenBeforeTx(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.ListenBeforeTx.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void KeyToCall()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.KeyToCall.ToDescription()));
            }
        }

        public void KeyToCall(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.KeyToCall.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void AMDDisplay()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.AMDDisplay.ToDescription()));
            }
        }

        public void AMDDisplay(YesNoState state)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.AMDDisplay.ToDescription());
                segments.Add(state.ToDescription());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void LinkTimeoutTime()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.LinkTimeoutTime.ToDescription()));
            }
        }

        public void LinkTimeoutTime(int minutes)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.LinkTimeoutTime.ToDescription());
                segments.Add(minutes.ToString());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void MaxChannelsToScan()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.MaxChannelsToScan.ToDescription()));
            }
        }

        public void MaxChannelsToScan(int channels)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.MaxChannelsToScan.ToDescription());
                segments.Add(channels.ToString());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }
        public void TuneTime()
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(Commands.TuneTime.ToDescription()));
            }
        }

        public void TuneTime(int seconds)
        {
            if (isConnectionOpen)
            {
                List<string> segments = new List<string>();

                segments.Add(Commands.TuneTime.ToDescription());
                segments.Add(seconds.ToString());

                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(segments));
            }
        }


        /*

            [Description("SLFAD")] SelfAddress,
            [Description("NETAD")] NetAddress,
            [Description("INDAD")] IndividualAddress,

            [Description("CHGROUP")] ChannelGroup
            */

        public void RawCommand(string command)
        {
            if (isConnectionOpen)
            {
                RemoteSerialPortController.QueueCommand(CommandBuilder.CreateCommand(command));
            }
        }

    }

public class RadioStateChangedEventArgs : EventArgs
    {
        public FalconRadio.Parameters PropertyChanged { get; set; }
        public FalconRadio.Parameters PropertyChanged2 { get; set; }
        public FalconRadio.Parameters PropertyChanged3 { get; set; }
        public FalconRadio.Parameters PropertyChanged4 { get; set; }
        public FalconRadio.Parameters PropertyChanged5 { get; set; }
    }

    public class MessageReceivedEventArgs : EventArgs
    {
        public string message { get; set; }

    }

}

