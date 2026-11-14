using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace WechatVoice.Workshop
{
    public partial class ExportForm : Form
    {
        private readonly Func<Dictionary<string,object>,Task<int>> count;
        private bool checking,closing;
        public Dictionary<string,object> Options {get;private set;}
        public ExportForm(){InitializeComponent();}
        public ExportForm(string conversation,DateTime from,DateTime to,bool range,Func<Dictionary<string,object>,Task<int>> count)
            : this()
        {
            this.count=count;conversationLabel.Text=conversation;
            fromDate.Value=from.Date;toDate.Value=to.Date;rangeBox.Checked=range;
        }
        internal void ApplyPreferences(UiPreferences value)
        {
            asrBox.Checked=value.Transcribe;silkBox.Checked=value.KeepSilk;rangeBox.Checked=value.Range;
            DateTime date;
            if(DateTime.TryParseExact(value.From,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out date) && date>=fromDate.MinDate && date<=fromDate.MaxDate)fromDate.Value=date;
            if(DateTime.TryParseExact(value.To,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out date) && date>=toDate.MinDate && date<=toDate.MaxDate)toDate.Value=date;
            Range_Changed(this,EventArgs.Empty);
        }
        internal void StorePreferences(UiPreferences value)
        {
            value.Transcribe=asrBox.Checked;value.KeepSilk=silkBox.Checked;value.Range=rangeBox.Checked;
            value.From=fromDate.Value.ToString("yyyy-MM-dd");value.To=toDate.Value.ToString("yyyy-MM-dd");
        }
        private void Range_Changed(object sender,EventArgs e)
        {
            if(checking || fromDate==null || toDate==null || summaryLabel==null)return;
            fromDate.Enabled=toDate.Enabled=rangeBox.Checked;startButton.Enabled=true;
            summaryLabel.Text="日期已设置；点击“查看数量”或“开始导出”确认范围。\r\n结束日包含整天，不会影响其他会话。";
        }
        private void AllDates_Click(object sender,EventArgs e){rangeBox.Checked=false;Range_Changed(sender,e);}
        private void LastWeek_Click(object sender,EventArgs e){SetRecentDays(7);}
        private void LastMonth_Click(object sender,EventArgs e){SetRecentDays(30);}
        private void SetRecentDays(int days){fromDate.Value=DateTime.Today.AddDays(1-days);toDate.Value=DateTime.Today;rangeBox.Checked=true;Range_Changed(this,EventArgs.Empty);}
        private void ExportForm_FormClosing(object sender,FormClosingEventArgs e){closing=true;}
        private Dictionary<string,object> BuildOptions()
        {
            var options=new Dictionary<string,object> { ["transcribe"]=asrBox.Checked,["keep_silk"]=silkBox.Checked };
            if(rangeBox.Checked)
            {
                if(fromDate.Value.Date>toDate.Value.Date) throw new InvalidOperationException("开始日期不能晚于结束日期。");
                options["after"]=new DateTimeOffset(fromDate.Value.Date).ToUnixTimeSeconds();
                options["before"]=new DateTimeOffset(toDate.Value.Date.AddDays(1)).ToUnixTimeSeconds();
            }
            return options;
        }
        private async Task<int> CountRange()
        {
            if(checking || closing || count==null)return -1;checking=true;startButton.Enabled=countButton.Enabled=false;
            rangeBox.Enabled=fromDate.Enabled=toDate.Enabled=asrBox.Enabled=silkBox.Enabled=false;
            allDatesButton.Enabled=lastWeekButton.Enabled=lastMonthButton.Enabled=false;summaryLabel.Text="正在统计此会话的语音…";
            int value=-1;
            try
            {
                value=await count(BuildOptions());if(closing || IsDisposed)return -1;
                summaryLabel.Text=value==0?"此范围没有本地语音。可调整日期或选择“全部日期”。":"此范围共 "+value+" 条语音。\r\n命名：时间_唯一编号_识别文字摘要.wav\r\n"+(asrBox.Checked?"本地识别首次需要加载模型；识别失败仍保留 WAV。":"未启用识别，以“未识别语音”作为摘要。后续可重新导出识别。");return value;
            }
            catch(Exception ex){if(!closing && !IsDisposed)summaryLabel.Text=ex.Message;return -1;}
            finally
            {
                checking=false;
                if(!closing && !IsDisposed)
                {
                    startButton.Enabled=value>0;countButton.Enabled=true;rangeBox.Enabled=asrBox.Enabled=silkBox.Enabled=true;
                    fromDate.Enabled=toDate.Enabled=rangeBox.Checked;allDatesButton.Enabled=lastWeekButton.Enabled=lastMonthButton.Enabled=true;
                }
            }
        }
        private async void ExportForm_Shown(object sender,EventArgs e){await CountRange();}
        private async void Count_Click(object sender,EventArgs e){await CountRange();}
        private async void Start_Click(object sender,EventArgs e)
        {
            if(await CountRange()<=0 || closing || IsDisposed)return;
            Options=BuildOptions();DialogResult=DialogResult.OK;Close();
        }
    }
}
