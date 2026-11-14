namespace WechatVoice.Workshop
{
    partial class ExportForm
    {
        private System.ComponentModel.IContainer components;
        private System.Windows.Forms.Label conversationLabel,summaryLabel;
        private System.Windows.Forms.DateTimePicker fromDate,toDate;
        private System.Windows.Forms.CheckBox rangeBox,asrBox,silkBox;
        private System.Windows.Forms.Button countButton,startButton,cancelButton;
        private System.Windows.Forms.Button allDatesButton,lastWeekButton,lastMonthButton;
        protected override void Dispose(bool disposing){if(disposing && components!=null)components.Dispose();base.Dispose(disposing);}
        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.components=new System.ComponentModel.Container();
            var layout=new System.Windows.Forms.TableLayoutPanel();layout.Dock=System.Windows.Forms.DockStyle.Fill;layout.Padding=new System.Windows.Forms.Padding(20);layout.ColumnCount=1;layout.RowCount=9;
            foreach(float h in new[]{48F,36F,30F,38F,36F,34F,34F,92F,42F})layout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute,h));
            this.conversationLabel=new System.Windows.Forms.Label();this.conversationLabel.Dock=System.Windows.Forms.DockStyle.Fill;this.conversationLabel.Font=new System.Drawing.Font("Microsoft YaHei UI",13F,System.Drawing.FontStyle.Bold);this.conversationLabel.AutoEllipsis=true;
            this.rangeBox=new System.Windows.Forms.CheckBox();this.rangeBox.Text="按日期范围筛选（包含开始日和结束日）";this.rangeBox.Dock=System.Windows.Forms.DockStyle.Fill;
            var quickDates=new System.Windows.Forms.FlowLayoutPanel();quickDates.Dock=System.Windows.Forms.DockStyle.Fill;quickDates.WrapContents=false;quickDates.Margin=System.Windows.Forms.Padding.Empty;
            this.allDatesButton=new System.Windows.Forms.Button();this.allDatesButton.Text="全部日期";this.allDatesButton.Width=110;this.allDatesButton.Height=28;this.allDatesButton.Click+=new System.EventHandler(this.AllDates_Click);
            this.lastWeekButton=new System.Windows.Forms.Button();this.lastWeekButton.Text="最近 7 天";this.lastWeekButton.Width=110;this.lastWeekButton.Height=28;this.lastWeekButton.Click+=new System.EventHandler(this.LastWeek_Click);
            this.lastMonthButton=new System.Windows.Forms.Button();this.lastMonthButton.Text="最近 30 天";this.lastMonthButton.Width=110;this.lastMonthButton.Height=28;this.lastMonthButton.Click+=new System.EventHandler(this.LastMonth_Click);
            quickDates.Controls.Add(this.allDatesButton);quickDates.Controls.Add(this.lastWeekButton);quickDates.Controls.Add(this.lastMonthButton);
            var dates=new System.Windows.Forms.FlowLayoutPanel();dates.Dock=System.Windows.Forms.DockStyle.Fill;dates.WrapContents=false;
            this.fromDate=new System.Windows.Forms.DateTimePicker();this.toDate=new System.Windows.Forms.DateTimePicker();this.fromDate.Format=this.toDate.Format=System.Windows.Forms.DateTimePickerFormat.Short;this.fromDate.Width=this.toDate.Width=180;
            dates.Controls.Add(this.fromDate);dates.Controls.Add(new System.Windows.Forms.Label {Text="至",Width=28,TextAlign=System.Drawing.ContentAlignment.MiddleCenter});dates.Controls.Add(this.toDate);
            this.countButton=new System.Windows.Forms.Button();this.countButton.Text="查看数量";this.countButton.Width=150;this.countButton.Height=28;this.countButton.Click+=new System.EventHandler(this.Count_Click);
            this.asrBox=new System.Windows.Forms.CheckBox();this.asrBox.Text="本地识别语音，用文字摘要命名 WAV";this.asrBox.Checked=true;this.asrBox.Dock=System.Windows.Forms.DockStyle.Fill;this.asrBox.Name="asrBox";
            this.silkBox=new System.Windows.Forms.CheckBox();this.silkBox.Text="另外保留原始 SILK 文件";this.silkBox.Dock=System.Windows.Forms.DockStyle.Fill;
            this.summaryLabel=new System.Windows.Forms.Label();this.summaryLabel.Dock=System.Windows.Forms.DockStyle.Fill;this.summaryLabel.Text="正在统计…";
            var buttons=new System.Windows.Forms.FlowLayoutPanel();buttons.Dock=System.Windows.Forms.DockStyle.Fill;buttons.FlowDirection=System.Windows.Forms.FlowDirection.RightToLeft;buttons.Margin=System.Windows.Forms.Padding.Empty;
            this.cancelButton=new System.Windows.Forms.Button();this.cancelButton.Text="返回";this.cancelButton.Width=100;this.cancelButton.Height=34;this.cancelButton.DialogResult=System.Windows.Forms.DialogResult.Cancel;
            this.startButton=new System.Windows.Forms.Button();this.startButton.Text="开始导出 WAV";this.startButton.Width=170;this.startButton.Height=34;this.startButton.Name="startButton";this.startButton.Click+=new System.EventHandler(this.Start_Click);
            this.startButton.BackColor=System.Drawing.Color.FromArgb(36,117,102);this.startButton.ForeColor=System.Drawing.Color.White;this.startButton.FlatStyle=System.Windows.Forms.FlatStyle.Flat;this.startButton.FlatAppearance.BorderSize=0;
            buttons.Controls.Add(this.cancelButton);buttons.Controls.Add(this.startButton);
            layout.Controls.Add(this.conversationLabel,0,0);layout.Controls.Add(quickDates,0,1);layout.Controls.Add(this.rangeBox,0,2);layout.Controls.Add(dates,0,3);layout.Controls.Add(this.countButton,0,4);layout.Controls.Add(this.asrBox,0,5);layout.Controls.Add(this.silkBox,0,6);layout.Controls.Add(this.summaryLabel,0,7);layout.Controls.Add(buttons,0,8);
            this.fromDate.ValueChanged+=new System.EventHandler(this.Range_Changed);this.toDate.ValueChanged+=new System.EventHandler(this.Range_Changed);this.rangeBox.CheckedChanged+=new System.EventHandler(this.Range_Changed);
            this.Controls.Add(layout);this.Text="会话语音导出";this.ClientSize=new System.Drawing.Size(520,450);this.FormBorderStyle=System.Windows.Forms.FormBorderStyle.FixedDialog;this.MaximizeBox=false;this.MinimizeBox=false;this.StartPosition=System.Windows.Forms.FormStartPosition.CenterParent;
            this.Font=new System.Drawing.Font("Microsoft YaHei UI",9F);this.AutoScaleDimensions=new System.Drawing.SizeF(96F,96F);this.AutoScaleMode=System.Windows.Forms.AutoScaleMode.Dpi;this.CancelButton=this.cancelButton;this.Shown+=new System.EventHandler(this.ExportForm_Shown);
            this.FormClosing+=new System.Windows.Forms.FormClosingEventHandler(this.ExportForm_FormClosing);
            this.ResumeLayout(false);
        }
    }
}
