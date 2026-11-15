using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class FavoritesUiCheck
{
    private static void Require(bool value,string message) { if(!value)throw new Exception(message); }
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); int result=1;
        var type=Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0])).GetType("WechatVoice.Workshop.MainForm",true);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var form=(Form)Activator.CreateInstance(type))
        {
            Func<string,object> field=name=>type.GetField(name,flags).GetValue(form);
            form.Shown+=async (s,e)=>
            {
                try
                {
                    // Inject only the existing synthetic session. Never click connect
                    // or favorites, enumerate keys, or read a real WeChat database.
                    type.GetField("workspace",flags).SetValue(form,args[2]);
                    type.GetField("engine",flags).SetValue(form,System.IO.Path.GetFullPath(args[1]));
                    var report=new Dictionary<string,object>{{"session",args[3]},{"favorites",true},{"count",7},{"databases",2},{"expected_databases",2},{"issues",new string[0]}};
                    await (Task)type.GetMethod("AcceptCatalog",flags).Invoke(form,new object[]{report});
                    Require((bool)field("favoriteMode"),"Favorite mode not activated");
                    Require(((ListView)field("voiceList")).Items.Count==7,"Favorite rows not loaded");
                    var button=(Button)field("exportAllButton");
                    Require(button.Enabled && button.Text=="导出收藏…","All favorites cannot be exported");
                    var query=(Dictionary<string,object>)type.GetMethod("Filter",flags).Invoke(form,new object[]{"export"});
                    Require(!query.ContainsKey("chat"),"All-favorites export unexpectedly restricted to one item");
                    Require(((ColumnHeader)field("timeColumn")).Text.StartsWith("收藏时间"),"Favorite timestamp label wrong");
                    form.Size=form.MinimumSize; Application.DoEvents();
                    var entry=(Button)field("favoriteButton");
                    var bounds=entry.RectangleToScreen(entry.ClientRectangle);
                    for(Control parent=entry.Parent;parent!=null;parent=parent.Parent)
                        Require(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds),"Favorite entry clipped at minimum size");
                    Console.WriteLine("PASS: synthetic favorite session loads in real message loop; all-favorites export enabled and unrestricted, correct date label, minimum-size entry visible.");
                    result=0;
                }
                catch(Exception ex){Console.WriteLine("FAIL: "+ex);}
                finally{form.Close();}
            };
            Application.Run(form);
        }
        return result;
    }
}
