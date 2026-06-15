namespace hsclient;

partial class Form1
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
        this.serverLabel = new System.Windows.Forms.Label();
        this.serverTextBox = new System.Windows.Forms.TextBox();
        this.keyLabel = new System.Windows.Forms.Label();
        this.keyTextBox = new System.Windows.Forms.TextBox();
        this.connectButton = new System.Windows.Forms.Button();
        this.statusDot = new System.Windows.Forms.Label();
        this.statusLabel = new System.Windows.Forms.Label();
        this.SuspendLayout();
        //
        // serverLabel
        //
        this.serverLabel.AutoSize = true;
        this.serverLabel.Location = new System.Drawing.Point(20, 22);
        this.serverLabel.Name = "serverLabel";
        this.serverLabel.Size = new System.Drawing.Size(80, 20);
        this.serverLabel.TabIndex = 0;
        this.serverLabel.Text = "服务器地址";
        //
        // serverTextBox
        //
        this.serverTextBox.Location = new System.Drawing.Point(20, 48);
        this.serverTextBox.Name = "serverTextBox";
        this.serverTextBox.Size = new System.Drawing.Size(400, 27);
        this.serverTextBox.TabIndex = 1;
        //
        // keyLabel
        //
        this.keyLabel.AutoSize = true;
        this.keyLabel.Location = new System.Drawing.Point(20, 86);
        this.keyLabel.Name = "keyLabel";
        this.keyLabel.Size = new System.Drawing.Size(60, 20);
        this.keyLabel.TabIndex = 2;
        this.keyLabel.Text = "接入密钥";
        //
        // keyTextBox
        //
        this.keyTextBox.Location = new System.Drawing.Point(20, 112);
        this.keyTextBox.Name = "keyTextBox";
        this.keyTextBox.Size = new System.Drawing.Size(300, 27);
        this.keyTextBox.TabIndex = 3;
        this.keyTextBox.UseSystemPasswordChar = true;
        //
        // connectButton
        //
        this.connectButton.Location = new System.Drawing.Point(326, 111);
        this.connectButton.Name = "connectButton";
        this.connectButton.Size = new System.Drawing.Size(94, 29);
        this.connectButton.TabIndex = 4;
        this.connectButton.Text = "连接";
        this.connectButton.UseVisualStyleBackColor = true;
        this.connectButton.Click += new System.EventHandler(this.connectButton_Click);
        //
        // statusDot
        //
        this.statusDot.AutoSize = false;
        this.statusDot.BackColor = System.Drawing.Color.Gray;
        this.statusDot.Location = new System.Drawing.Point(20, 159);
        this.statusDot.Name = "statusDot";
        this.statusDot.Size = new System.Drawing.Size(16, 16);
        this.statusDot.TabIndex = 5;
        //
        // statusLabel
        //
        this.statusLabel.AutoSize = true;
        this.statusLabel.Location = new System.Drawing.Point(44, 159);
        this.statusLabel.Name = "statusLabel";
        this.statusLabel.Size = new System.Drawing.Size(40, 20);
        this.statusLabel.TabIndex = 6;
        this.statusLabel.Text = "离线";
        //
        // Form1
        //
        this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(440, 204);
        this.Controls.Add(this.statusLabel);
        this.Controls.Add(this.statusDot);
        this.Controls.Add(this.connectButton);
        this.Controls.Add(this.keyTextBox);
        this.Controls.Add(this.keyLabel);
        this.Controls.Add(this.serverTextBox);
        this.Controls.Add(this.serverLabel);
        this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.Name = "Form1";
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        this.Text = "专网客户端";
        this.ResumeLayout(false);
        this.PerformLayout();
    }

    #endregion

    private System.Windows.Forms.Label serverLabel;
    private System.Windows.Forms.TextBox serverTextBox;
    private System.Windows.Forms.Label keyLabel;
    private System.Windows.Forms.TextBox keyTextBox;
    private System.Windows.Forms.Button connectButton;
    private System.Windows.Forms.Label statusDot;
    private System.Windows.Forms.Label statusLabel;
}
