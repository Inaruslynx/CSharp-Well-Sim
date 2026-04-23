namespace Well_Sim
{
    partial class frmWellSim
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnStart = new Button();
            btnStop = new Button();
            txtAddress = new TextBox();
            label1 = new Label();
            label2 = new Label();
            txtPort = new TextBox();
            groupBox1 = new GroupBox();
            txtStatus = new TextBox();
            label3 = new Label();
            txtUsername = new TextBox();
            txtPassword = new TextBox();
            label4 = new Label();
            groupBox2 = new GroupBox();
            radAes256_Sha256_RsaPss = new RadioButton();
            radAes128_Sha256_RsaOaep = new RadioButton();
            radBasic256Sha256 = new RadioButton();
            radNone = new RadioButton();
            groupBox3 = new GroupBox();
            txtRate = new TextBox();
            label5 = new Label();
            groupBox1.SuspendLayout();
            groupBox2.SuspendLayout();
            groupBox3.SuspendLayout();
            SuspendLayout();
            // 
            // btnStart
            // 
            btnStart.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnStart.Location = new Point(12, 275);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(148, 63);
            btnStart.TabIndex = 0;
            btnStart.Text = "Start";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // btnStop
            // 
            btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnStop.Location = new Point(367, 275);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(148, 63);
            btnStop.TabIndex = 1;
            btnStop.Text = "Stop";
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // txtAddress
            // 
            txtAddress.Location = new Point(77, 16);
            txtAddress.Name = "txtAddress";
            txtAddress.Size = new Size(127, 23);
            txtAddress.TabIndex = 2;
            txtAddress.Text = "192.168.140.240";
            txtAddress.TextAlign = HorizontalAlignment.Right;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(6, 19);
            label1.Name = "label1";
            label1.Size = new Size(65, 15);
            label1.TabIndex = 3;
            label1.Text = "IP Address:";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(6, 50);
            label2.Name = "label2";
            label2.Size = new Size(32, 15);
            label2.TabIndex = 4;
            label2.Text = "Port:";
            // 
            // txtPort
            // 
            txtPort.Location = new Point(77, 47);
            txtPort.Name = "txtPort";
            txtPort.Size = new Size(127, 23);
            txtPort.TabIndex = 5;
            txtPort.Text = "62541";
            txtPort.TextAlign = HorizontalAlignment.Right;
            // 
            // groupBox1
            // 
            groupBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            groupBox1.Controls.Add(txtStatus);
            groupBox1.Location = new Point(21, 206);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(494, 63);
            groupBox1.TabIndex = 6;
            groupBox1.TabStop = false;
            groupBox1.Text = "Status";
            // 
            // txtStatus
            // 
            txtStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtStatus.Enabled = false;
            txtStatus.Location = new Point(11, 22);
            txtStatus.Name = "txtStatus";
            txtStatus.ReadOnly = true;
            txtStatus.Size = new Size(474, 23);
            txtStatus.TabIndex = 0;
            txtStatus.Text = "Off";
            txtStatus.TextAlign = HorizontalAlignment.Right;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(6, 83);
            label3.Name = "label3";
            label3.Size = new Size(63, 15);
            label3.TabIndex = 7;
            label3.Text = "Username:";
            // 
            // txtUsername
            // 
            txtUsername.Location = new Point(75, 80);
            txtUsername.Name = "txtUsername";
            txtUsername.Size = new Size(129, 23);
            txtUsername.TabIndex = 8;
            txtUsername.Text = "opcuauser";
            // 
            // txtPassword
            // 
            txtPassword.Location = new Point(75, 111);
            txtPassword.Name = "txtPassword";
            txtPassword.Size = new Size(129, 23);
            txtPassword.TabIndex = 9;
            txtPassword.Text = "password";
            txtPassword.UseSystemPasswordChar = true;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(6, 114);
            label4.Name = "label4";
            label4.Size = new Size(60, 15);
            label4.TabIndex = 10;
            label4.Text = "Password:";
            // 
            // groupBox2
            // 
            groupBox2.Controls.Add(radAes256_Sha256_RsaPss);
            groupBox2.Controls.Add(radAes128_Sha256_RsaOaep);
            groupBox2.Controls.Add(radBasic256Sha256);
            groupBox2.Controls.Add(radNone);
            groupBox2.Location = new Point(237, 12);
            groupBox2.Name = "groupBox2";
            groupBox2.Size = new Size(183, 137);
            groupBox2.TabIndex = 11;
            groupBox2.TabStop = false;
            groupBox2.Text = "Security";
            // 
            // radAes256_Sha256_RsaPss
            // 
            radAes256_Sha256_RsaPss.AutoSize = true;
            radAes256_Sha256_RsaPss.Location = new Point(19, 103);
            radAes256_Sha256_RsaPss.Name = "radAes256_Sha256_RsaPss";
            radAes256_Sha256_RsaPss.Size = new Size(144, 19);
            radAes256_Sha256_RsaPss.TabIndex = 3;
            radAes256_Sha256_RsaPss.TabStop = true;
            radAes256_Sha256_RsaPss.Text = "Aes256_Sha256_RsaPss";
            radAes256_Sha256_RsaPss.UseVisualStyleBackColor = true;
            // 
            // radAes128_Sha256_RsaOaep
            // 
            radAes128_Sha256_RsaOaep.AutoSize = true;
            radAes128_Sha256_RsaOaep.Location = new Point(19, 78);
            radAes128_Sha256_RsaOaep.Name = "radAes128_Sha256_RsaOaep";
            radAes128_Sha256_RsaOaep.Size = new Size(155, 19);
            radAes128_Sha256_RsaOaep.TabIndex = 2;
            radAes128_Sha256_RsaOaep.TabStop = true;
            radAes128_Sha256_RsaOaep.Text = "Aes128_Sha256_RsaOaep";
            radAes128_Sha256_RsaOaep.UseVisualStyleBackColor = true;
            // 
            // radBasic256Sha256
            // 
            radBasic256Sha256.AutoSize = true;
            radBasic256Sha256.Checked = true;
            radBasic256Sha256.Location = new Point(19, 53);
            radBasic256Sha256.Name = "radBasic256Sha256";
            radBasic256Sha256.Size = new Size(107, 19);
            radBasic256Sha256.TabIndex = 1;
            radBasic256Sha256.TabStop = true;
            radBasic256Sha256.Text = "Basic256Sha256";
            radBasic256Sha256.UseVisualStyleBackColor = true;
            // 
            // radNone
            // 
            radNone.AutoSize = true;
            radNone.Location = new Point(19, 28);
            radNone.Name = "radNone";
            radNone.Size = new Size(54, 19);
            radNone.TabIndex = 0;
            radNone.Text = "None";
            radNone.UseVisualStyleBackColor = true;
            // 
            // groupBox3
            // 
            groupBox3.Controls.Add(txtRate);
            groupBox3.Controls.Add(label5);
            groupBox3.Controls.Add(label1);
            groupBox3.Controls.Add(txtAddress);
            groupBox3.Controls.Add(label4);
            groupBox3.Controls.Add(label2);
            groupBox3.Controls.Add(txtPassword);
            groupBox3.Controls.Add(txtPort);
            groupBox3.Controls.Add(txtUsername);
            groupBox3.Controls.Add(label3);
            groupBox3.Location = new Point(21, 12);
            groupBox3.Name = "groupBox3";
            groupBox3.Size = new Size(210, 173);
            groupBox3.TabIndex = 12;
            groupBox3.TabStop = false;
            groupBox3.Text = "Connection Info";
            // 
            // txtRate
            // 
            txtRate.Location = new Point(113, 140);
            txtRate.Name = "txtRate";
            txtRate.Size = new Size(91, 23);
            txtRate.TabIndex = 12;
            txtRate.Text = "500";
            txtRate.TextAlign = HorizontalAlignment.Right;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(6, 144);
            label5.Name = "label5";
            label5.Size = new Size(101, 15);
            label5.TabIndex = 11;
            label5.Text = "Update Rate (ms):";
            // 
            // frmWellSim
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(527, 350);
            Controls.Add(groupBox3);
            Controls.Add(groupBox2);
            Controls.Add(groupBox1);
            Controls.Add(btnStop);
            Controls.Add(btnStart);
            Name = "frmWellSim";
            Text = "Well Simulator";
            groupBox1.ResumeLayout(false);
            groupBox1.PerformLayout();
            groupBox2.ResumeLayout(false);
            groupBox2.PerformLayout();
            groupBox3.ResumeLayout(false);
            groupBox3.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private Button btnStart;
        private Button btnStop;
        private TextBox txtAddress;
        private Label label1;
        private Label label2;
        private TextBox txtPort;
        private GroupBox groupBox1;
        private TextBox txtStatus;
        private Label label3;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private Label label4;
        private GroupBox groupBox2;
        private RadioButton radAes256_Sha256_RsaPss;
        private RadioButton radAes128_Sha256_RsaOaep;
        private RadioButton radBasic256Sha256;
        private RadioButton radNone;
        private GroupBox groupBox3;
        private TextBox txtRate;
        private Label label5;
    }
}
