namespace PRC_138_Remote_Control
{
    partial class frmConfiguration
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(frmConfiguration));
            this.groupBox5 = new System.Windows.Forms.GroupBox();
            this.tableLayoutPanel9 = new System.Windows.Forms.TableLayoutPanel();
            this.cmbAntennaMode = new System.Windows.Forms.ComboBox();
            this.label9 = new System.Windows.Forms.Label();
            this.label15 = new System.Windows.Forms.Label();
            this.label13 = new System.Windows.Forms.Label();
            this.label14 = new System.Windows.Forms.Label();
            this.cmbBacklightFunction = new System.Windows.Forms.ComboBox();
            this.cmbContrast = new System.Windows.Forms.ComboBox();
            this.cmbBacklightIntensity = new System.Windows.Forms.ComboBox();
            this.btnDateTime = new System.Windows.Forms.Button();
            this.label16 = new System.Windows.Forms.Label();
            this.cmbPrePostScanRate = new System.Windows.Forms.ComboBox();
            this.chkRxAntenna = new System.Windows.Forms.CheckBox();
            this.chkPrePostFilter = new System.Windows.Forms.CheckBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.chk1kWPA = new System.Windows.Forms.CheckBox();
            this.label5 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.chkInternalCoupler = new System.Windows.Forms.CheckBox();
            this.chkRxPreamp = new System.Windows.Forms.CheckBox();
            this.label3 = new System.Windows.Forms.Label();
            this.tabModes = new System.Windows.Forms.TabControl();
            this.tabRadio = new System.Windows.Forms.TabPage();
            this.tabChannels = new System.Windows.Forms.TabPage();
            this.tabSSB = new System.Windows.Forms.TabPage();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.tableLayoutPanel6 = new System.Windows.Forms.TableLayoutPanel();
            this.nudRWASKey = new ExtensionMethods.NumericUpDownEx();
            this.label6 = new System.Windows.Forms.Label();
            this.chkRWAS = new System.Windows.Forms.CheckBox();
            this.label17 = new System.Windows.Forms.Label();
            this.label7 = new System.Windows.Forms.Label();
            this.chkRWASForceWakeup = new System.Windows.Forms.CheckBox();
            this.label8 = new System.Windows.Forms.Label();
            this.chkRWASUnkeyMask = new System.Windows.Forms.CheckBox();
            this.tabHop = new System.Windows.Forms.TabPage();
            this.tabALE = new System.Windows.Forms.TabPage();
            this.groupBox2 = new System.Windows.Forms.GroupBox();
            this.tableLayoutPanel3 = new System.Windows.Forms.TableLayoutPanel();
            this.label22 = new System.Windows.Forms.Label();
            this.checkBox5 = new System.Windows.Forms.CheckBox();
            this.label11 = new System.Windows.Forms.Label();
            this.checkBox1 = new System.Windows.Forms.CheckBox();
            this.label18 = new System.Windows.Forms.Label();
            this.checkBox2 = new System.Windows.Forms.CheckBox();
            this.label21 = new System.Windows.Forms.Label();
            this.checkBox4 = new System.Windows.Forms.CheckBox();
            this.label20 = new System.Windows.Forms.Label();
            this.checkBox3 = new System.Windows.Forms.CheckBox();
            this.label19 = new System.Windows.Forms.Label();
            this.numericUpDownEx1 = new ExtensionMethods.NumericUpDownEx();
            this.gbLQA = new System.Windows.Forms.GroupBox();
            this.tableLayoutPanel1 = new System.Windows.Forms.TableLayoutPanel();
            this.tableLayoutPanel2 = new System.Windows.Forms.TableLayoutPanel();
            this.rdoSound = new System.Windows.Forms.RadioButton();
            this.rdoExchange = new System.Windows.Forms.RadioButton();
            this.maskedTextBox1 = new System.Windows.Forms.MaskedTextBox();
            this.label12 = new System.Windows.Forms.Label();
            this.label10 = new System.Windows.Forms.Label();
            this.btnAddLQA = new System.Windows.Forms.Button();
            this.txtInterval = new System.Windows.Forms.MaskedTextBox();
            this.listView1 = new System.Windows.Forms.ListView();
            this.tabConsole = new System.Windows.Forms.TabPage();
            this.chkEnableConsole = new System.Windows.Forms.CheckBox();
            this.btnSend = new System.Windows.Forms.Button();
            this.txtConsole = new System.Windows.Forms.TextBox();
            this.txtSend = new System.Windows.Forms.TextBox();
            this.btnClose = new System.Windows.Forms.Button();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.lblStatus = new System.Windows.Forms.ToolStripStatusLabel();
            this.groupBox5.SuspendLayout();
            this.tableLayoutPanel9.SuspendLayout();
            this.tabModes.SuspendLayout();
            this.tabRadio.SuspendLayout();
            this.tabSSB.SuspendLayout();
            this.groupBox1.SuspendLayout();
            this.tableLayoutPanel6.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudRWASKey)).BeginInit();
            this.tabALE.SuspendLayout();
            this.groupBox2.SuspendLayout();
            this.tableLayoutPanel3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericUpDownEx1)).BeginInit();
            this.gbLQA.SuspendLayout();
            this.tableLayoutPanel1.SuspendLayout();
            this.tableLayoutPanel2.SuspendLayout();
            this.tabConsole.SuspendLayout();
            this.statusStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // groupBox5
            // 
            this.groupBox5.Controls.Add(this.tableLayoutPanel9);
            this.groupBox5.Location = new System.Drawing.Point(6, 6);
            this.groupBox5.Name = "groupBox5";
            this.groupBox5.Size = new System.Drawing.Size(554, 431);
            this.groupBox5.TabIndex = 9;
            this.groupBox5.TabStop = false;
            this.groupBox5.Text = "Configuration";
            // 
            // tableLayoutPanel9
            // 
            this.tableLayoutPanel9.ColumnCount = 2;
            this.tableLayoutPanel9.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 146F));
            this.tableLayoutPanel9.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 116F));
            this.tableLayoutPanel9.Controls.Add(this.cmbAntennaMode, 1, 4);
            this.tableLayoutPanel9.Controls.Add(this.label9, 0, 4);
            this.tableLayoutPanel9.Controls.Add(this.label15, 0, 8);
            this.tableLayoutPanel9.Controls.Add(this.label13, 0, 9);
            this.tableLayoutPanel9.Controls.Add(this.label14, 0, 10);
            this.tableLayoutPanel9.Controls.Add(this.cmbBacklightFunction, 1, 8);
            this.tableLayoutPanel9.Controls.Add(this.cmbContrast, 1, 9);
            this.tableLayoutPanel9.Controls.Add(this.cmbBacklightIntensity, 1, 10);
            this.tableLayoutPanel9.Controls.Add(this.btnDateTime, 1, 0);
            this.tableLayoutPanel9.Controls.Add(this.label16, 0, 7);
            this.tableLayoutPanel9.Controls.Add(this.cmbPrePostScanRate, 1, 7);
            this.tableLayoutPanel9.Controls.Add(this.chkRxAntenna, 1, 6);
            this.tableLayoutPanel9.Controls.Add(this.chkPrePostFilter, 1, 5);
            this.tableLayoutPanel9.Controls.Add(this.label1, 0, 6);
            this.tableLayoutPanel9.Controls.Add(this.label2, 0, 5);
            this.tableLayoutPanel9.Controls.Add(this.chk1kWPA, 1, 3);
            this.tableLayoutPanel9.Controls.Add(this.label5, 0, 3);
            this.tableLayoutPanel9.Controls.Add(this.label4, 0, 2);
            this.tableLayoutPanel9.Controls.Add(this.chkInternalCoupler, 1, 2);
            this.tableLayoutPanel9.Controls.Add(this.chkRxPreamp, 1, 1);
            this.tableLayoutPanel9.Controls.Add(this.label3, 0, 1);
            this.tableLayoutPanel9.Location = new System.Drawing.Point(6, 19);
            this.tableLayoutPanel9.Name = "tableLayoutPanel9";
            this.tableLayoutPanel9.RowCount = 11;
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel9.Size = new System.Drawing.Size(262, 386);
            this.tableLayoutPanel9.TabIndex = 2;
            // 
            // cmbAntennaMode
            // 
            this.cmbAntennaMode.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.cmbAntennaMode.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbAntennaMode.FormattingEnabled = true;
            this.cmbAntennaMode.Location = new System.Drawing.Point(161, 147);
            this.cmbAntennaMode.Name = "cmbAntennaMode";
            this.cmbAntennaMode.Size = new System.Drawing.Size(86, 21);
            this.cmbAntennaMode.TabIndex = 12;
            this.cmbAntennaMode.SelectionChangeCommitted += new System.EventHandler(this.cmbAntennaMode_SelectionChangeCommitted);
            // 
            // label9
            // 
            this.label9.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label9.AutoSize = true;
            this.label9.Location = new System.Drawing.Point(63, 151);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(80, 13);
            this.label9.TabIndex = 33;
            this.label9.Text = "Antenna Mode:";
            // 
            // label15
            // 
            this.label15.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label15.AutoSize = true;
            this.label15.Location = new System.Drawing.Point(45, 291);
            this.label15.Name = "label15";
            this.label15.Size = new System.Drawing.Size(98, 13);
            this.label15.TabIndex = 26;
            this.label15.Text = "Backlight Function:";
            // 
            // label13
            // 
            this.label13.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label13.AutoSize = true;
            this.label13.Location = new System.Drawing.Point(70, 326);
            this.label13.Name = "label13";
            this.label13.Size = new System.Drawing.Size(73, 13);
            this.label13.TabIndex = 25;
            this.label13.Text = "LCD Contrast:";
            // 
            // label14
            // 
            this.label14.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label14.AutoSize = true;
            this.label14.Location = new System.Drawing.Point(47, 361);
            this.label14.Name = "label14";
            this.label14.Size = new System.Drawing.Size(96, 13);
            this.label14.TabIndex = 26;
            this.label14.Text = "Backlight Intensity:";
            // 
            // cmbBacklightFunction
            // 
            this.cmbBacklightFunction.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.cmbBacklightFunction.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbBacklightFunction.FormattingEnabled = true;
            this.cmbBacklightFunction.Location = new System.Drawing.Point(161, 287);
            this.cmbBacklightFunction.Name = "cmbBacklightFunction";
            this.cmbBacklightFunction.Size = new System.Drawing.Size(86, 21);
            this.cmbBacklightFunction.TabIndex = 29;
            this.cmbBacklightFunction.SelectionChangeCommitted += new System.EventHandler(this.cmbBacklightFunction_SelectionChangeCommitted);
            // 
            // cmbContrast
            // 
            this.cmbContrast.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.cmbContrast.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbContrast.FormattingEnabled = true;
            this.cmbContrast.Location = new System.Drawing.Point(161, 322);
            this.cmbContrast.Name = "cmbContrast";
            this.cmbContrast.Size = new System.Drawing.Size(86, 21);
            this.cmbContrast.TabIndex = 28;
            this.cmbContrast.SelectionChangeCommitted += new System.EventHandler(this.cmbContrast_SelectionChangeCommitted);
            // 
            // cmbBacklightIntensity
            // 
            this.cmbBacklightIntensity.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.cmbBacklightIntensity.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbBacklightIntensity.FormattingEnabled = true;
            this.cmbBacklightIntensity.Location = new System.Drawing.Point(161, 357);
            this.cmbBacklightIntensity.Name = "cmbBacklightIntensity";
            this.cmbBacklightIntensity.Size = new System.Drawing.Size(86, 21);
            this.cmbBacklightIntensity.TabIndex = 27;
            this.cmbBacklightIntensity.SelectionChangeCommitted += new System.EventHandler(this.cmbBacklightIntensity_SelectionChangeCommitted);
            // 
            // btnDateTime
            // 
            this.btnDateTime.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.btnDateTime.Location = new System.Drawing.Point(149, 5);
            this.btnDateTime.Name = "btnDateTime";
            this.btnDateTime.Size = new System.Drawing.Size(109, 24);
            this.btnDateTime.TabIndex = 32;
            this.btnDateTime.Text = "Set Date/Time";
            this.btnDateTime.UseVisualStyleBackColor = true;
            this.btnDateTime.Click += new System.EventHandler(this.btnDateTime_Click);
            // 
            // label16
            // 
            this.label16.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label16.AutoSize = true;
            this.label16.Location = new System.Drawing.Point(37, 256);
            this.label16.Name = "label16";
            this.label16.Size = new System.Drawing.Size(106, 13);
            this.label16.TabIndex = 5;
            this.label16.Text = "Pre/Post Scan Rate:";
            // 
            // cmbPrePostScanRate
            // 
            this.cmbPrePostScanRate.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.cmbPrePostScanRate.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbPrePostScanRate.FormattingEnabled = true;
            this.cmbPrePostScanRate.Location = new System.Drawing.Point(161, 252);
            this.cmbPrePostScanRate.Name = "cmbPrePostScanRate";
            this.cmbPrePostScanRate.Size = new System.Drawing.Size(86, 21);
            this.cmbPrePostScanRate.TabIndex = 9;
            this.cmbPrePostScanRate.SelectionChangeCommitted += new System.EventHandler(this.cmbPrePostScanRate_SelectionChangeCommitted);
            // 
            // chkRxAntenna
            // 
            this.chkRxAntenna.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkRxAntenna.AutoCheck = false;
            this.chkRxAntenna.AutoSize = true;
            this.chkRxAntenna.Location = new System.Drawing.Point(196, 220);
            this.chkRxAntenna.Name = "chkRxAntenna";
            this.chkRxAntenna.Size = new System.Drawing.Size(15, 14);
            this.chkRxAntenna.TabIndex = 14;
            this.chkRxAntenna.UseVisualStyleBackColor = true;
            this.chkRxAntenna.Click += new System.EventHandler(this.chkRxAntenna_Click);
            // 
            // chkPrePostFilter
            // 
            this.chkPrePostFilter.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkPrePostFilter.AutoCheck = false;
            this.chkPrePostFilter.AutoSize = true;
            this.chkPrePostFilter.Location = new System.Drawing.Point(196, 185);
            this.chkPrePostFilter.Name = "chkPrePostFilter";
            this.chkPrePostFilter.Size = new System.Drawing.Size(15, 14);
            this.chkPrePostFilter.TabIndex = 17;
            this.chkPrePostFilter.UseVisualStyleBackColor = true;
            this.chkPrePostFilter.Click += new System.EventHandler(this.chkPrePostFilter_Click);
            // 
            // label1
            // 
            this.label1.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(31, 221);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(112, 13);
            this.label1.TabIndex = 12;
            this.label1.Text = "Separate Rx Antenna:";
            // 
            // label2
            // 
            this.label2.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(21, 186);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(122, 13);
            this.label2.TabIndex = 12;
            this.label2.Text = "Pre/Post Scan Enabled:";
            // 
            // chk1kWPA
            // 
            this.chk1kWPA.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chk1kWPA.AutoCheck = false;
            this.chk1kWPA.AutoSize = true;
            this.chk1kWPA.Location = new System.Drawing.Point(196, 115);
            this.chk1kWPA.Name = "chk1kWPA";
            this.chk1kWPA.Size = new System.Drawing.Size(15, 14);
            this.chk1kWPA.TabIndex = 17;
            this.chk1kWPA.UseVisualStyleBackColor = true;
            this.chk1kWPA.Click += new System.EventHandler(this.chk1kWPA_Click);
            // 
            // label5
            // 
            this.label5.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(51, 116);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(92, 13);
            this.label5.TabIndex = 32;
            this.label5.Text = "1kW PA Installed:";
            // 
            // label4
            // 
            this.label4.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(17, 81);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(126, 13);
            this.label4.TabIndex = 31;
            this.label4.Text = "Internal Coupler Enabled:";
            // 
            // chkInternalCoupler
            // 
            this.chkInternalCoupler.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkInternalCoupler.AutoCheck = false;
            this.chkInternalCoupler.AutoSize = true;
            this.chkInternalCoupler.Location = new System.Drawing.Point(196, 80);
            this.chkInternalCoupler.Name = "chkInternalCoupler";
            this.chkInternalCoupler.Size = new System.Drawing.Size(15, 14);
            this.chkInternalCoupler.TabIndex = 10;
            this.chkInternalCoupler.UseVisualStyleBackColor = true;
            this.chkInternalCoupler.Click += new System.EventHandler(this.chkInternalCoupler_Click);
            // 
            // chkRxPreamp
            // 
            this.chkRxPreamp.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkRxPreamp.AutoCheck = false;
            this.chkRxPreamp.AutoSize = true;
            this.chkRxPreamp.Location = new System.Drawing.Point(196, 45);
            this.chkRxPreamp.Name = "chkRxPreamp";
            this.chkRxPreamp.Size = new System.Drawing.Size(15, 14);
            this.chkRxPreamp.TabIndex = 14;
            this.chkRxPreamp.UseVisualStyleBackColor = true;
            this.chkRxPreamp.Click += new System.EventHandler(this.chkRxPreamp_Click);
            // 
            // label3
            // 
            this.label3.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(39, 46);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(104, 13);
            this.label3.TabIndex = 30;
            this.label3.Text = "Rx Preamp Enabled:";
            // 
            // tabModes
            // 
            this.tabModes.Controls.Add(this.tabRadio);
            this.tabModes.Controls.Add(this.tabChannels);
            this.tabModes.Controls.Add(this.tabSSB);
            this.tabModes.Controls.Add(this.tabHop);
            this.tabModes.Controls.Add(this.tabALE);
            this.tabModes.Controls.Add(this.tabConsole);
            this.tabModes.Enabled = false;
            this.tabModes.Location = new System.Drawing.Point(12, 12);
            this.tabModes.Name = "tabModes";
            this.tabModes.SelectedIndex = 0;
            this.tabModes.Size = new System.Drawing.Size(574, 469);
            this.tabModes.TabIndex = 10;
            // 
            // tabRadio
            // 
            this.tabRadio.Controls.Add(this.groupBox5);
            this.tabRadio.Location = new System.Drawing.Point(4, 22);
            this.tabRadio.Name = "tabRadio";
            this.tabRadio.Padding = new System.Windows.Forms.Padding(3);
            this.tabRadio.Size = new System.Drawing.Size(566, 443);
            this.tabRadio.TabIndex = 0;
            this.tabRadio.Text = "Radio";
            this.tabRadio.UseVisualStyleBackColor = true;
            // 
            // tabChannels
            // 
            this.tabChannels.Location = new System.Drawing.Point(4, 22);
            this.tabChannels.Name = "tabChannels";
            this.tabChannels.Padding = new System.Windows.Forms.Padding(3);
            this.tabChannels.Size = new System.Drawing.Size(566, 443);
            this.tabChannels.TabIndex = 1;
            this.tabChannels.Text = "Channels";
            this.tabChannels.UseVisualStyleBackColor = true;
            // 
            // tabSSB
            // 
            this.tabSSB.Controls.Add(this.groupBox1);
            this.tabSSB.Location = new System.Drawing.Point(4, 22);
            this.tabSSB.Name = "tabSSB";
            this.tabSSB.Padding = new System.Windows.Forms.Padding(3);
            this.tabSSB.Size = new System.Drawing.Size(566, 443);
            this.tabSSB.TabIndex = 5;
            this.tabSSB.Text = "SSB";
            this.tabSSB.UseVisualStyleBackColor = true;
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.tableLayoutPanel6);
            this.groupBox1.Location = new System.Drawing.Point(6, 6);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(554, 431);
            this.groupBox1.TabIndex = 2;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Configuration";
            // 
            // tableLayoutPanel6
            // 
            this.tableLayoutPanel6.ColumnCount = 2;
            this.tableLayoutPanel6.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 142F));
            this.tableLayoutPanel6.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 121F));
            this.tableLayoutPanel6.Controls.Add(this.nudRWASKey, 1, 1);
            this.tableLayoutPanel6.Controls.Add(this.label6, 0, 0);
            this.tableLayoutPanel6.Controls.Add(this.chkRWAS, 1, 0);
            this.tableLayoutPanel6.Controls.Add(this.label17, 0, 1);
            this.tableLayoutPanel6.Controls.Add(this.label7, 0, 2);
            this.tableLayoutPanel6.Controls.Add(this.chkRWASForceWakeup, 1, 2);
            this.tableLayoutPanel6.Controls.Add(this.label8, 0, 3);
            this.tableLayoutPanel6.Controls.Add(this.chkRWASUnkeyMask, 1, 3);
            this.tableLayoutPanel6.Location = new System.Drawing.Point(6, 19);
            this.tableLayoutPanel6.Name = "tableLayoutPanel6";
            this.tableLayoutPanel6.RowCount = 11;
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.666667F));
            this.tableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel6.Size = new System.Drawing.Size(263, 386);
            this.tableLayoutPanel6.TabIndex = 1;
            // 
            // nudRWASKey
            // 
            this.nudRWASKey.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.nudRWASKey.Enabled = false;
            this.nudRWASKey.Location = new System.Drawing.Point(180, 42);
            this.nudRWASKey.Maximum = new decimal(new int[] {
            99,
            0,
            0,
            0});
            this.nudRWASKey.Name = "nudRWASKey";
            this.nudRWASKey.Size = new System.Drawing.Size(44, 20);
            this.nudRWASKey.TabIndex = 2;
            // 
            // label6
            // 
            this.label6.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(54, 11);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(85, 13);
            this.label6.TabIndex = 12;
            this.label6.Text = "RWAS Enabled:";
            // 
            // chkRWAS
            // 
            this.chkRWAS.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkRWAS.AutoCheck = false;
            this.chkRWAS.AutoSize = true;
            this.chkRWAS.Location = new System.Drawing.Point(195, 10);
            this.chkRWAS.Name = "chkRWAS";
            this.chkRWAS.Size = new System.Drawing.Size(15, 14);
            this.chkRWAS.TabIndex = 16;
            this.chkRWAS.UseVisualStyleBackColor = true;
            this.chkRWAS.Click += new System.EventHandler(this.chkRWAS_Click);
            // 
            // label17
            // 
            this.label17.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label17.AutoSize = true;
            this.label17.Location = new System.Drawing.Point(75, 46);
            this.label17.Name = "label17";
            this.label17.Size = new System.Drawing.Size(64, 13);
            this.label17.TabIndex = 31;
            this.label17.Text = "RWAS Key:";
            // 
            // label7
            // 
            this.label7.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(22, 81);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(117, 13);
            this.label7.TabIndex = 33;
            this.label7.Text = "RWAS Force Wakeup:";
            // 
            // chkRWASForceWakeup
            // 
            this.chkRWASForceWakeup.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkRWASForceWakeup.AutoCheck = false;
            this.chkRWASForceWakeup.AutoSize = true;
            this.chkRWASForceWakeup.Enabled = false;
            this.chkRWASForceWakeup.Location = new System.Drawing.Point(195, 80);
            this.chkRWASForceWakeup.Name = "chkRWASForceWakeup";
            this.chkRWASForceWakeup.Size = new System.Drawing.Size(15, 14);
            this.chkRWASForceWakeup.TabIndex = 10;
            this.chkRWASForceWakeup.UseVisualStyleBackColor = true;
            // 
            // label8
            // 
            this.label8.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(33, 116);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(106, 13);
            this.label8.TabIndex = 34;
            this.label8.Text = "RWAS Unkey Mask:";
            // 
            // chkRWASUnkeyMask
            // 
            this.chkRWASUnkeyMask.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.chkRWASUnkeyMask.AutoCheck = false;
            this.chkRWASUnkeyMask.AutoSize = true;
            this.chkRWASUnkeyMask.Enabled = false;
            this.chkRWASUnkeyMask.Location = new System.Drawing.Point(195, 115);
            this.chkRWASUnkeyMask.Name = "chkRWASUnkeyMask";
            this.chkRWASUnkeyMask.Size = new System.Drawing.Size(15, 14);
            this.chkRWASUnkeyMask.TabIndex = 18;
            this.chkRWASUnkeyMask.UseVisualStyleBackColor = true;
            // 
            // tabHop
            // 
            this.tabHop.Location = new System.Drawing.Point(4, 22);
            this.tabHop.Name = "tabHop";
            this.tabHop.Size = new System.Drawing.Size(566, 443);
            this.tabHop.TabIndex = 2;
            this.tabHop.Text = "Hop";
            this.tabHop.UseVisualStyleBackColor = true;
            // 
            // tabALE
            // 
            this.tabALE.Controls.Add(this.groupBox2);
            this.tabALE.Controls.Add(this.gbLQA);
            this.tabALE.Location = new System.Drawing.Point(4, 22);
            this.tabALE.Name = "tabALE";
            this.tabALE.Size = new System.Drawing.Size(566, 443);
            this.tabALE.TabIndex = 3;
            this.tabALE.Text = "ALE";
            this.tabALE.UseVisualStyleBackColor = true;
            // 
            // groupBox2
            // 
            this.groupBox2.Controls.Add(this.tableLayoutPanel3);
            this.groupBox2.Location = new System.Drawing.Point(6, 186);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Size = new System.Drawing.Size(554, 254);
            this.groupBox2.TabIndex = 3;
            this.groupBox2.TabStop = false;
            this.groupBox2.Text = "Configuration";
            // 
            // tableLayoutPanel3
            // 
            this.tableLayoutPanel3.ColumnCount = 2;
            this.tableLayoutPanel3.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 142F));
            this.tableLayoutPanel3.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 121F));
            this.tableLayoutPanel3.Controls.Add(this.label22, 0, 2);
            this.tableLayoutPanel3.Controls.Add(this.checkBox5, 1, 2);
            this.tableLayoutPanel3.Controls.Add(this.label11, 0, 0);
            this.tableLayoutPanel3.Controls.Add(this.checkBox1, 1, 0);
            this.tableLayoutPanel3.Controls.Add(this.label18, 0, 1);
            this.tableLayoutPanel3.Controls.Add(this.checkBox2, 1, 1);
            this.tableLayoutPanel3.Controls.Add(this.label21, 0, 5);
            this.tableLayoutPanel3.Controls.Add(this.checkBox4, 1, 5);
            this.tableLayoutPanel3.Controls.Add(this.label20, 0, 4);
            this.tableLayoutPanel3.Controls.Add(this.checkBox3, 1, 4);
            this.tableLayoutPanel3.Controls.Add(this.label19, 0, 3);
            this.tableLayoutPanel3.Controls.Add(this.numericUpDownEx1, 1, 3);
            this.tableLayoutPanel3.Location = new System.Drawing.Point(6, 19);
            this.tableLayoutPanel3.Name = "tableLayoutPanel3";
            this.tableLayoutPanel3.RowCount = 6;
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 16.66667F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel3.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel3.Size = new System.Drawing.Size(263, 200);
            this.tableLayoutPanel3.TabIndex = 1;
            // 
            // label22
            // 
            this.label22.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label22.AutoSize = true;
            this.label22.Location = new System.Drawing.Point(63, 76);
            this.label22.Name = "label22";
            this.label22.Size = new System.Drawing.Size(76, 13);
            this.label22.TabIndex = 35;
            this.label22.Text = "Link Time-Out:";
            // 
            // checkBox5
            // 
            this.checkBox5.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.checkBox5.AutoCheck = false;
            this.checkBox5.AutoSize = true;
            this.checkBox5.Enabled = false;
            this.checkBox5.Location = new System.Drawing.Point(195, 75);
            this.checkBox5.Name = "checkBox5";
            this.checkBox5.Size = new System.Drawing.Size(15, 14);
            this.checkBox5.TabIndex = 19;
            this.checkBox5.UseVisualStyleBackColor = true;
            // 
            // label11
            // 
            this.label11.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label11.AutoSize = true;
            this.label11.Location = new System.Drawing.Point(51, 10);
            this.label11.Name = "label11";
            this.label11.Size = new System.Drawing.Size(88, 13);
            this.label11.TabIndex = 12;
            this.label11.Text = "Link to Any Calls:";
            // 
            // checkBox1
            // 
            this.checkBox1.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.checkBox1.AutoCheck = false;
            this.checkBox1.AutoSize = true;
            this.checkBox1.Location = new System.Drawing.Point(195, 9);
            this.checkBox1.Name = "checkBox1";
            this.checkBox1.Size = new System.Drawing.Size(15, 14);
            this.checkBox1.TabIndex = 16;
            this.checkBox1.UseVisualStyleBackColor = true;
            // 
            // label18
            // 
            this.label18.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label18.AutoSize = true;
            this.label18.Location = new System.Drawing.Point(58, 43);
            this.label18.Name = "label18";
            this.label18.Size = new System.Drawing.Size(81, 13);
            this.label18.TabIndex = 31;
            this.label18.Text = "Link to All Calls:";
            // 
            // checkBox2
            // 
            this.checkBox2.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.checkBox2.AutoCheck = false;
            this.checkBox2.AutoSize = true;
            this.checkBox2.Enabled = false;
            this.checkBox2.Location = new System.Drawing.Point(195, 42);
            this.checkBox2.Name = "checkBox2";
            this.checkBox2.Size = new System.Drawing.Size(15, 14);
            this.checkBox2.TabIndex = 10;
            this.checkBox2.UseVisualStyleBackColor = true;
            // 
            // label21
            // 
            this.label21.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label21.AutoSize = true;
            this.label21.Location = new System.Drawing.Point(75, 176);
            this.label21.Name = "label21";
            this.label21.Size = new System.Drawing.Size(64, 13);
            this.label21.TabIndex = 35;
            this.label21.Text = "Key To Call:";
            // 
            // checkBox4
            // 
            this.checkBox4.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.checkBox4.AutoCheck = false;
            this.checkBox4.AutoSize = true;
            this.checkBox4.Enabled = false;
            this.checkBox4.Location = new System.Drawing.Point(195, 175);
            this.checkBox4.Name = "checkBox4";
            this.checkBox4.Size = new System.Drawing.Size(15, 14);
            this.checkBox4.TabIndex = 36;
            this.checkBox4.UseVisualStyleBackColor = true;
            // 
            // label20
            // 
            this.label20.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label20.AutoSize = true;
            this.label20.Location = new System.Drawing.Point(24, 142);
            this.label20.Name = "label20";
            this.label20.Size = new System.Drawing.Size(115, 13);
            this.label20.TabIndex = 34;
            this.label20.Text = "Listen Before Transmit:";
            // 
            // checkBox3
            // 
            this.checkBox3.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.checkBox3.AutoCheck = false;
            this.checkBox3.AutoSize = true;
            this.checkBox3.Enabled = false;
            this.checkBox3.Location = new System.Drawing.Point(195, 141);
            this.checkBox3.Name = "checkBox3";
            this.checkBox3.Size = new System.Drawing.Size(15, 14);
            this.checkBox3.TabIndex = 18;
            this.checkBox3.UseVisualStyleBackColor = true;
            // 
            // label19
            // 
            this.label19.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label19.AutoSize = true;
            this.label19.Location = new System.Drawing.Point(17, 109);
            this.label19.Name = "label19";
            this.label19.Size = new System.Drawing.Size(122, 13);
            this.label19.TabIndex = 33;
            this.label19.Text = "Link Time-Out (Minutes):";
            // 
            // numericUpDownEx1
            // 
            this.numericUpDownEx1.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.numericUpDownEx1.Enabled = false;
            this.numericUpDownEx1.Location = new System.Drawing.Point(180, 105);
            this.numericUpDownEx1.Maximum = new decimal(new int[] {
            99,
            0,
            0,
            0});
            this.numericUpDownEx1.Name = "numericUpDownEx1";
            this.numericUpDownEx1.Size = new System.Drawing.Size(44, 20);
            this.numericUpDownEx1.TabIndex = 2;
            // 
            // gbLQA
            // 
            this.gbLQA.Controls.Add(this.tableLayoutPanel1);
            this.gbLQA.Controls.Add(this.listView1);
            this.gbLQA.Location = new System.Drawing.Point(6, 6);
            this.gbLQA.Name = "gbLQA";
            this.gbLQA.Size = new System.Drawing.Size(554, 174);
            this.gbLQA.TabIndex = 0;
            this.gbLQA.TabStop = false;
            this.gbLQA.Text = "LQA";
            // 
            // tableLayoutPanel1
            // 
            this.tableLayoutPanel1.ColumnCount = 6;
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 20.49181F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14.7541F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14.7541F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14.7541F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14.7541F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 20.4918F));
            this.tableLayoutPanel1.Controls.Add(this.tableLayoutPanel2, 0, 0);
            this.tableLayoutPanel1.Controls.Add(this.maskedTextBox1, 4, 0);
            this.tableLayoutPanel1.Controls.Add(this.label12, 1, 0);
            this.tableLayoutPanel1.Controls.Add(this.label10, 3, 0);
            this.tableLayoutPanel1.Controls.Add(this.btnAddLQA, 5, 0);
            this.tableLayoutPanel1.Controls.Add(this.txtInterval, 2, 0);
            this.tableLayoutPanel1.Location = new System.Drawing.Point(6, 115);
            this.tableLayoutPanel1.Name = "tableLayoutPanel1";
            this.tableLayoutPanel1.RowCount = 1;
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel1.Size = new System.Drawing.Size(542, 54);
            this.tableLayoutPanel1.TabIndex = 1;
            // 
            // tableLayoutPanel2
            // 
            this.tableLayoutPanel2.ColumnCount = 1;
            this.tableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel2.Controls.Add(this.rdoSound, 0, 0);
            this.tableLayoutPanel2.Controls.Add(this.rdoExchange, 0, 1);
            this.tableLayoutPanel2.Location = new System.Drawing.Point(3, 3);
            this.tableLayoutPanel2.Name = "tableLayoutPanel2";
            this.tableLayoutPanel2.RowCount = 2;
            this.tableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel2.Size = new System.Drawing.Size(84, 48);
            this.tableLayoutPanel2.TabIndex = 2;
            // 
            // rdoSound
            // 
            this.rdoSound.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.rdoSound.AutoSize = true;
            this.rdoSound.Location = new System.Drawing.Point(3, 3);
            this.rdoSound.Name = "rdoSound";
            this.rdoSound.Size = new System.Drawing.Size(56, 17);
            this.rdoSound.TabIndex = 0;
            this.rdoSound.TabStop = true;
            this.rdoSound.Text = "Sound";
            this.rdoSound.UseVisualStyleBackColor = true;
            // 
            // rdoExchange
            // 
            this.rdoExchange.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.rdoExchange.AutoSize = true;
            this.rdoExchange.Location = new System.Drawing.Point(3, 27);
            this.rdoExchange.Name = "rdoExchange";
            this.rdoExchange.Size = new System.Drawing.Size(73, 17);
            this.rdoExchange.TabIndex = 1;
            this.rdoExchange.TabStop = true;
            this.rdoExchange.Text = "Exchange";
            this.rdoExchange.UseVisualStyleBackColor = true;
            // 
            // maskedTextBox1
            // 
            this.maskedTextBox1.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.maskedTextBox1.Font = new System.Drawing.Font("Consolas", 11.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.maskedTextBox1.Location = new System.Drawing.Point(364, 14);
            this.maskedTextBox1.Mask = "00:00";
            this.maskedTextBox1.Name = "maskedTextBox1";
            this.maskedTextBox1.Size = new System.Drawing.Size(47, 25);
            this.maskedTextBox1.TabIndex = 38;
            this.maskedTextBox1.ValidatingType = typeof(System.DateTime);
            // 
            // label12
            // 
            this.label12.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.label12.AutoSize = true;
            this.label12.Location = new System.Drawing.Point(124, 14);
            this.label12.Name = "label12";
            this.label12.Size = new System.Drawing.Size(53, 26);
            this.label12.TabIndex = 34;
            this.label12.Text = "Interval (HH:MM):";
            // 
            // label10
            // 
            this.label10.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.label10.AutoSize = true;
            this.label10.Location = new System.Drawing.Point(279, 14);
            this.label10.Name = "label10";
            this.label10.Size = new System.Drawing.Size(58, 26);
            this.label10.TabIndex = 35;
            this.label10.Text = "Start Time (HH:MM):";
            // 
            // btnAddLQA
            // 
            this.btnAddLQA.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.btnAddLQA.Location = new System.Drawing.Point(447, 15);
            this.btnAddLQA.Name = "btnAddLQA";
            this.btnAddLQA.Size = new System.Drawing.Size(75, 23);
            this.btnAddLQA.TabIndex = 36;
            this.btnAddLQA.Text = "Add";
            this.btnAddLQA.UseVisualStyleBackColor = true;
            // 
            // txtInterval
            // 
            this.txtInterval.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.txtInterval.Font = new System.Drawing.Font("Consolas", 11.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtInterval.Location = new System.Drawing.Point(206, 14);
            this.txtInterval.Mask = "00:00";
            this.txtInterval.Name = "txtInterval";
            this.txtInterval.Size = new System.Drawing.Size(47, 25);
            this.txtInterval.TabIndex = 37;
            this.txtInterval.ValidatingType = typeof(System.DateTime);
            // 
            // listView1
            // 
            this.listView1.Font = new System.Drawing.Font("Consolas", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.listView1.FullRowSelect = true;
            this.listView1.HideSelection = false;
            this.listView1.Location = new System.Drawing.Point(6, 19);
            this.listView1.MultiSelect = false;
            this.listView1.Name = "listView1";
            this.listView1.Size = new System.Drawing.Size(542, 90);
            this.listView1.TabIndex = 0;
            this.listView1.UseCompatibleStateImageBehavior = false;
            // 
            // tabConsole
            // 
            this.tabConsole.Controls.Add(this.chkEnableConsole);
            this.tabConsole.Controls.Add(this.btnSend);
            this.tabConsole.Controls.Add(this.txtConsole);
            this.tabConsole.Controls.Add(this.txtSend);
            this.tabConsole.Location = new System.Drawing.Point(4, 22);
            this.tabConsole.Name = "tabConsole";
            this.tabConsole.Size = new System.Drawing.Size(566, 443);
            this.tabConsole.TabIndex = 4;
            this.tabConsole.Text = "Console";
            this.tabConsole.UseVisualStyleBackColor = true;
            // 
            // chkEnableConsole
            // 
            this.chkEnableConsole.AutoSize = true;
            this.chkEnableConsole.Location = new System.Drawing.Point(492, 416);
            this.chkEnableConsole.Name = "chkEnableConsole";
            this.chkEnableConsole.Size = new System.Drawing.Size(59, 17);
            this.chkEnableConsole.TabIndex = 12;
            this.chkEnableConsole.Text = "Enable";
            this.chkEnableConsole.UseVisualStyleBackColor = true;
            this.chkEnableConsole.CheckedChanged += new System.EventHandler(this.chkEnableConsole_CheckedChanged);
            // 
            // btnSend
            // 
            this.btnSend.Enabled = false;
            this.btnSend.Location = new System.Drawing.Point(426, 412);
            this.btnSend.Name = "btnSend";
            this.btnSend.Size = new System.Drawing.Size(61, 22);
            this.btnSend.TabIndex = 1;
            this.btnSend.Text = "Send";
            this.btnSend.UseVisualStyleBackColor = true;
            this.btnSend.Click += new System.EventHandler(this.btnSend_Click);
            // 
            // txtConsole
            // 
            this.txtConsole.Font = new System.Drawing.Font("Consolas", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtConsole.Location = new System.Drawing.Point(14, 12);
            this.txtConsole.Multiline = true;
            this.txtConsole.Name = "txtConsole";
            this.txtConsole.ReadOnly = true;
            this.txtConsole.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtConsole.Size = new System.Drawing.Size(537, 394);
            this.txtConsole.TabIndex = 1;
            this.txtConsole.TabStop = false;
            // 
            // txtSend
            // 
            this.txtSend.Enabled = false;
            this.txtSend.Font = new System.Drawing.Font("Consolas", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtSend.Location = new System.Drawing.Point(14, 413);
            this.txtSend.Name = "txtSend";
            this.txtSend.Size = new System.Drawing.Size(407, 20);
            this.txtSend.TabIndex = 0;
            this.txtSend.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtSend_KeyDown);
            // 
            // btnClose
            // 
            this.btnClose.Location = new System.Drawing.Point(507, 484);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(75, 23);
            this.btnClose.TabIndex = 11;
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            this.btnClose.Click += new System.EventHandler(this.btnClose_Click);
            // 
            // statusStrip1
            // 
            this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.lblStatus});
            this.statusStrip1.Location = new System.Drawing.Point(0, 513);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new System.Drawing.Size(592, 22);
            this.statusStrip1.TabIndex = 12;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // lblStatus
            // 
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(0, 17);
            // 
            // frmConfiguration
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(592, 535);
            this.Controls.Add(this.statusStrip1);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.tabModes);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.Name = "frmConfiguration";
            this.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
            this.Text = "Radio Settings";
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.frmConfiguration_FormClosed);
            this.VisibleChanged += new System.EventHandler(this.frmConfiguration_VisibleChanged);
            this.groupBox5.ResumeLayout(false);
            this.tableLayoutPanel9.ResumeLayout(false);
            this.tableLayoutPanel9.PerformLayout();
            this.tabModes.ResumeLayout(false);
            this.tabRadio.ResumeLayout(false);
            this.tabSSB.ResumeLayout(false);
            this.groupBox1.ResumeLayout(false);
            this.tableLayoutPanel6.ResumeLayout(false);
            this.tableLayoutPanel6.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudRWASKey)).EndInit();
            this.tabALE.ResumeLayout(false);
            this.groupBox2.ResumeLayout(false);
            this.tableLayoutPanel3.ResumeLayout(false);
            this.tableLayoutPanel3.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numericUpDownEx1)).EndInit();
            this.gbLQA.ResumeLayout(false);
            this.tableLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.PerformLayout();
            this.tableLayoutPanel2.ResumeLayout(false);
            this.tableLayoutPanel2.PerformLayout();
            this.tabConsole.ResumeLayout(false);
            this.tabConsole.PerformLayout();
            this.statusStrip1.ResumeLayout(false);
            this.statusStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.GroupBox groupBox5;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel9;
        private System.Windows.Forms.Label label15;
        private System.Windows.Forms.Label label13;
        private System.Windows.Forms.Label label14;
        private System.Windows.Forms.ComboBox cmbBacklightFunction;
        private System.Windows.Forms.ComboBox cmbContrast;
        private System.Windows.Forms.ComboBox cmbBacklightIntensity;
        private System.Windows.Forms.CheckBox chkInternalCoupler;
        private System.Windows.Forms.CheckBox chk1kWPA;
        private System.Windows.Forms.CheckBox chkPrePostFilter;
        private System.Windows.Forms.CheckBox chkRxAntenna;
        private System.Windows.Forms.ComboBox cmbPrePostScanRate;
        private System.Windows.Forms.Label label16;
        private System.Windows.Forms.CheckBox chkRxPreamp;
        private System.Windows.Forms.TabControl tabModes;
        private System.Windows.Forms.TabPage tabRadio;
        private System.Windows.Forms.TabPage tabChannels;
        private System.Windows.Forms.TabPage tabHop;
        private System.Windows.Forms.TabPage tabALE;
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.Button btnDateTime;
        private System.Windows.Forms.TabPage tabConsole;
        private System.Windows.Forms.Button btnSend;
        private System.Windows.Forms.TextBox txtConsole;
        private System.Windows.Forms.TextBox txtSend;
        private System.Windows.Forms.CheckBox chkEnableConsole;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.ComboBox cmbAntennaMode;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.TabPage tabSSB;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel6;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.CheckBox chkRWASUnkeyMask;
        private System.Windows.Forms.CheckBox chkRWASForceWakeup;
        private System.Windows.Forms.CheckBox chkRWAS;
        private System.Windows.Forms.Label label17;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.ToolStripStatusLabel lblStatus;
        private ExtensionMethods.NumericUpDownEx nudRWASKey;
        private System.Windows.Forms.GroupBox gbLQA;
        private System.Windows.Forms.ListView listView1;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
        private System.Windows.Forms.RadioButton rdoExchange;
        private System.Windows.Forms.RadioButton rdoSound;
        private System.Windows.Forms.Label label12;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel2;
        private System.Windows.Forms.MaskedTextBox maskedTextBox1;
        private System.Windows.Forms.Button btnAddLQA;
        private System.Windows.Forms.MaskedTextBox txtInterval;
        private System.Windows.Forms.GroupBox groupBox2;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel3;
        private ExtensionMethods.NumericUpDownEx numericUpDownEx1;
        private System.Windows.Forms.Label label11;
        private System.Windows.Forms.CheckBox checkBox1;
        private System.Windows.Forms.Label label18;
        private System.Windows.Forms.Label label19;
        private System.Windows.Forms.CheckBox checkBox2;
        private System.Windows.Forms.Label label20;
        private System.Windows.Forms.CheckBox checkBox3;
        private System.Windows.Forms.Label label21;
        private System.Windows.Forms.CheckBox checkBox4;
        private System.Windows.Forms.Label label22;
        private System.Windows.Forms.CheckBox checkBox5;
    }
}