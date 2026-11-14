using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using WechatVoice.Workshop;
internal static class UiBehaviorCheck
{
    private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    private static Dictionary<string,object> Options(ExportForm form){return (Dictionary<string,object>)typeof(ExportForm).GetMethod("BuildOptions",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);}
    private static void Accounts(string root)
    {
        // Synthetic xwechat_files root: db_storage accounts only, all_users and
        // folders without db_storage skipped, most recent use first.
        string xwechat=Path.Combine(root,"xwechat_files");
        foreach(var pair in new[]{new Tuple<string,DateTime>("wxid_alpha_1234",new DateTime(2026,1,5)),
            new Tuple<string,DateTime>("wxid_beta_abcd",new DateTime(2026,3,2)),
            new Tuple<string,DateTime>("plainname",new DateTime(2026,1,10)),
            new Tuple<string,DateTime>("all_users",new DateTime(2030,1,1)),
            new Tuple<string,DateTime>("no_storage",new DateTime(2030,1,1))})
        {
            string account=Path.Combine(xwechat,pair.Item1);
            if(pair.Item1=="all_users" || pair.Item1=="no_storage"){Directory.CreateDirectory(account);continue;}
            string storage=Path.Combine(account,"db_storage");
            Directory.CreateDirectory(Path.Combine(storage,"message"));
            string file=Path.Combine(storage,"message","message_0.db");
            File.WriteAllText(file,"synthetic");File.SetLastWriteTime(file,pair.Item2);
        }
        var accounts=AccountScanner.ListAccountsFromRoots(new[]{xwechat});
        Require(accounts.Count==3,"Account scan did not list exactly the db_storage accounts");
        Require(accounts[0].Wxid=="wxid_beta" && accounts[0].Recent==new DateTime(2026,3,2),"Most recent account should sort first: "+accounts[0].Wxid);
        Require(accounts[1].Wxid=="plainname","Name without hex suffix kept as-is: "+accounts[1].Wxid);
        Require(accounts[2].Wxid=="wxid_alpha","Least recent account last");
        Require(AccountScanner.SplitWxid("wxid_foo_bar")=="wxid_foo_bar","Non-hex suffix must not be trimmed");
        Require(AccountScanner.SplitWxid("wxid_foo_0A9f")=="wxid_foo","Hex suffix trimmed");
        Require(AccountScanner.SplitWxid("wxid")=="wxid","Plain name unchanged");
        Require(AccountScanner.FromPath(Path.Combine(xwechat,"wxid_alpha_1234")).Wxid=="wxid_alpha","Account root path recovery");
        Require(AccountScanner.FromPath(Path.Combine(xwechat,"wxid_alpha_1234","db_storage")).Wxid=="wxid_alpha","db_storage path recovery");
        Require(AccountScanner.FromPath(Path.Combine(xwechat,"no_storage"))==null,"Folder without db_storage rejected");
        Console.WriteLine("PASS: account scan lists db_storage accounts, skips all_users, sorts by recent use, splits wxid suffix.");
    }
    private static void Preferences(string root)
    {
        Directory.CreateDirectory(root);string path=Path.Combine(root,"ui-preferences.json");
        var value=UiPreferences.Load(path);Require(value.Transcribe && !value.KeepSilk,"First-run defaults changed");
        value.Output=@"中文 导出\语音";value.Sort=4;value.Kind=2;value.VoiceSort=5;value.VoiceDescending=true;value.Transcribe=false;value.KeepSilk=true;value.Range=true;value.From="2026-01-02";value.To="2026-01-03";
        value.Save(path);value.Sort=5;value.Save(path);var loaded=UiPreferences.Load(path);
        Require(loaded.Output==value.Output && loaded.Sort==5 && loaded.Kind==2 && !loaded.Transcribe && loaded.KeepSilk && loaded.From==value.From,"Preferences roundtrip/atomic replacement failed");
        Require(loaded.VoiceSort==5 && loaded.VoiceDescending,"Voice sort preference lost");
        Require(UiPreferences.ResolveOutput(value.Output)==Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,value.Output)),"Relative output uses worker cwd");
        Require(Directory.GetFiles(root,"*.tmp").Length==0,"Temporary settings left behind");
        File.WriteAllText(path,"{\"Sort\":999,\"Kind\":-3}");loaded=UiPreferences.Load(path);Require(loaded.Sort==5 && loaded.Kind==0,"Enum bounds not clamped");
        File.WriteAllText(path,"broken json");bool rejected=false;try{UiPreferences.Load(path);}catch{rejected=true;}Require(rejected,"Corrupt preferences not detected");
        File.WriteAllText(path,new string('x',65537));rejected=false;try{UiPreferences.Load(path);}catch{rejected=true;}Require(rejected,"Oversized preferences accepted");
    }
    private static async Task Dialogs(Form host)
    {
        using(var form=new ExportForm("合成会话",DateTime.Today,DateTime.Today,false,options=>Task.FromResult(3)))
        {
            form.Show(host);await Task.Delay(30);
            Require(!Field<DateTimePicker>(form,"fromDate").Enabled,"All-date mode should disable dates");
            Field<Button>(form,"lastWeekButton").PerformClick();var options=Options(form);
            Require(Convert.ToInt64(options["after"])==new DateTimeOffset(DateTime.Today.AddDays(-6)).ToUnixTimeSeconds(),"7-day start boundary");
            Require(Convert.ToInt64(options["before"])==new DateTimeOffset(DateTime.Today.AddDays(1)).ToUnixTimeSeconds(),"End day not inclusive");
            Field<Button>(form,"lastMonthButton").PerformClick();options=Options(form);
            Require(Convert.ToInt64(options["after"])==new DateTimeOffset(DateTime.Today.AddDays(-29)).ToUnixTimeSeconds(),"30-day boundary");
            Field<Button>(form,"allDatesButton").PerformClick();Require(!Options(form).ContainsKey("after"),"All-date mode leaked old range");
            Field<CheckBox>(form,"asrBox").Checked=false;Field<CheckBox>(form,"silkBox").Checked=true;
            var prefs=new UiPreferences();form.StorePreferences(prefs);Require(!prefs.Transcribe && prefs.KeepSilk && !prefs.Range,"Export choices not saved");
            foreach(string name in new[]{"allDatesButton","lastWeekButton","lastMonthButton","startButton","cancelButton"})
            {
                var button=Field<Button>(form,name);Rectangle bounds=button.RectangleToScreen(button.ClientRectangle);
                for(Control parent=button.Parent;parent!=null;parent=parent.Parent)Require(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),"Clipped button: "+name);
            }
            form.Close();
        }
        using(var form=new ExportForm("没有语音的范围",DateTime.Today,DateTime.Today,true,options=>Task.FromResult(0)))
        {
            form.Show(host);await Task.Delay(20);Require(!Field<Button>(form,"startButton").Enabled,"Zero count still allows export");
            Require(Field<Label>(form,"summaryLabel").Text.Contains("没有本地语音"),"Empty range guidance missing");form.Close();
        }
        var pending=new TaskCompletionSource<int>();
        using(var form=new ExportForm("关闭中的统计",DateTime.Today,DateTime.Today,false,options=>pending.Task))
        {
            form.Show(host);await Task.Delay(20);Require(!Field<Button>(form,"startButton").Enabled,"Count didn't enter busy state");
            form.Close();pending.SetResult(12);await Task.Delay(40);Require(form.IsDisposed,"Closing during count was blocked");
        }
        Console.WriteLine("PASS: preferences/relative paths; quick ranges/inclusive dates; empty count; async count close; unclipped buttons.");
    }
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles();int result=1;
        using(var host=new Form {Text="UI behavior regression",Size=new Size(300,100),ShowInTaskbar=false})
        {
            host.Shown+=async (sender,e)=>
            {
                try{Preferences(Path.GetFullPath(args[0]));Accounts(Path.GetFullPath(args[0]));await Dialogs(host);result=0;}
                catch(Exception ex){Console.WriteLine(ex);}
                finally{host.Close();}
            };
            Application.Run(host);
        }
        return result;
    }
}
