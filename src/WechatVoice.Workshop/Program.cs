using System;
using System.IO;
using System.Windows.Forms;
namespace WechatVoice.Workshop
{
    internal static class Program
    {
        [STAThread] private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string diagnosticsWorkspace=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"workspace");
            Application.ThreadException+=(sender,e)=>{Diagnostics.Write(diagnosticsWorkspace,"ui_unhandled",null,new System.Collections.Generic.Dictionary<string,object>{{"exception_type",e.Exception.GetType().Name}});MessageBox.Show("程序出现异常："+e.Exception.Message+"\r\n请导出诊断包并反馈操作步骤。","微信语音导出工坊");};
            AppDomain.CurrentDomain.UnhandledException+=(sender,e)=>Diagnostics.Write(diagnosticsWorkspace,"fatal",null,new System.Collections.Generic.Dictionary<string,object>{{"exception_type",e.ExceptionObject.GetType().Name}});
            bool smoke = Array.IndexOf(args, "--smoke-ui") >= 0;
            var form = new MainForm();
            if (smoke)
            {
                var timer = new Timer { Interval = 1200 };
                timer.Tick += (sender, e) => { timer.Stop(); form.Close(); timer.Dispose(); };
                form.Shown += (sender, e) => timer.Start();
            }
            Application.Run(form);
        }
    }
}
