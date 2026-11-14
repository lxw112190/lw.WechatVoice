using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
internal static class FormsCheck
{
    private sealed class DesignContext : LicenseContext { public override LicenseUsageMode UsageMode { get { return LicenseUsageMode.Designtime; } } }
    private static void RequireUnclipped(Control control)
    {
        Rectangle bounds=control.RectangleToScreen(control.ClientRectangle);
        for(Control parent=control.Parent;parent!=null;parent=parent.Parent)
            if(!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))throw new Exception("Control clipped: "+control.Text);
    }
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0])); var type = assembly.GetType("WechatVoice.Workshop.MainForm", true);
        Directory.CreateDirectory(args[1]);
        if (args.Length == 2)
        {
            var before = LicenseManager.CurrentContext;
            try
            {
                LicenseManager.CurrentContext = new DesignContext();
                using (var surface = new DesignSurface(type))
                {
                    if (surface.LoadErrors.Count != 0) throw new Exception("DesignSurface loading failed");
                    var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost)); var form = (Form)host.RootComponent;
                    form.CreateControl();
                    using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(args[1], "designer.png")); }
                }
                using(var surface=new DesignSurface(assembly.GetType("WechatVoice.Workshop.ExportForm",true)))
                    if(surface.LoadErrors.Count!=0)throw new Exception("Export form DesignSurface failed");
                Console.WriteLine("PASS: .NET Framework DesignSurface loads standard form. VS2022 IDE not automated."); return 0;
            }
            finally { LicenseManager.CurrentContext = before; }
        }
        // Dev-owned synthetic fixture, normal Application.Run and actual named event handlers.
        using (var form = (Form)Activator.CreateInstance(type))
        {
            int stage = 0, result = 1,sortStep=0;string selectedVoice=null;bool conversationChecks=false,recognitionStarted=false,snapshotProgressChecked=false; var started = DateTime.UtcNow; var timer = new Timer { Interval = 150 };
            Func<string, object> field = name => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Func<object,string,string> itemText=(item,name)=>Convert.ToString(item.GetType().GetField(name).GetValue(item));
            timer.Tick += (sender, e) =>
            {
                try
                {
                    if ((DateTime.UtcNow - started).TotalSeconds > 40) throw new TimeoutException("Synthetic GUI flow timed out");
                    foreach(Form dialog in Application.OpenForms)
                        if(dialog.GetType().Name=="ExportForm")
                        {
                            var flags=BindingFlags.NonPublic|BindingFlags.Instance;var dt=dialog.GetType();
                            ((CheckBox)dt.GetField("asrBox",flags).GetValue(dialog)).Checked=false;
                            var start=(Button)dt.GetField("startButton",flags).GetValue(dialog);
                            if(start.Enabled){var buttonBox=start.RectangleToScreen(start.ClientRectangle);var clientBox=dialog.RectangleToScreen(dialog.ClientRectangle);if(!clientBox.Contains(buttonBox))throw new Exception("Export button clipped outside dialog");using(var shot=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));shot.Save(Path.Combine(args[1],"export-page.png"));}start.PerformClick();}
                            return;
                        }
                    if ((bool)field("busy")) return;
                    if (stage == 0)
                    {
                        if (!snapshotProgressChecked)
                        {
                            snapshotProgressChecked = true;
                            type.GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, true);
                            var progress = new System.Collections.Generic.Dictionary<string, object> {
                                ["phase"] = "snapshot_detail", ["snapshot_stage"] = "decrypt", ["current"] = 2, ["total"] = 6,
                                ["bytes_current"] = 32L * 1024 * 1024, ["bytes_total"] = 64L * 1024 * 1024, ["speed_mb_s"] = 120.5 };
                            type.GetMethod("Progress", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, new object[] { progress, (int)field("operation") });
                            form.BeginInvoke((Action)(() => {
                                try
                                {
                                    if (((ProgressBar)field("progressBar")).Value != 50 || !((Label)field("statusLabel")).Text.Contains("解密副本") || !((Label)field("statusLabel")).Text.Contains("MiB/s"))
                                        throw new Exception("Detailed snapshot progress was not rendered");
                                    using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(args[1], "snapshot-progress.png")); }
                                }
                                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "runtime-error.txt"), error.ToString()); timer.Stop(); form.Close(); }
                                finally { type.GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, false); }
                            }));
                            return;
                        }
                        if(((ComboBox)field("processBox")).Visible || ((TextBox)field("logText")).Visible)throw new Exception("Advanced controls should be collapsed by default");
                        var layout=(TableLayoutPanel)field("root");if(layout.RowStyles[8].Height!=0)throw new Exception("Diagnostic row not collapsed");
                        RequireUnclipped((Button)field("prepareButton"));RequireUnclipped((Button)field("advancedButton"));
                        RequireUnclipped((Button)field("diagnosticsButton"));
                        RequireUnclipped((Button)field("scanAccountButton"));RequireUnclipped((Button)field("browseSourceButton"));
                        string appRoot = Path.GetDirectoryName(Path.GetFullPath(args[0]));
                        type.GetField("workspace",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,Path.Combine(appRoot,"workspace"));
                        type.GetField("engine",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,Path.Combine(appRoot,"engine","VoiceWorker.exe"));
                        ((TextBox)field("sourceText")).Text = Path.GetFullPath(args[2]);
                        ((CheckBox)field("consentBox")).Checked = true;
                        ((TextBox)field("outputText")).Text = Path.GetFullPath(args[3]);
                        ((Button)field("advancedButton")).PerformClick();
                        ((Button)field("readButton")).PerformClick(); stage = 1;
                    }
                    else if (stage == 1 && field("session") != null && ((ListView)field("voiceList")).Items.Count > 0)
                    {
                        var contacts=(ListBox)field("contactList");
                        if(itemText(field("activeContact"),"User")!="chat")
                        {for(int i=0;i<contacts.Items.Count;i++)if(itemText(contacts.Items[i],"User")=="chat"){contacts.SelectedIndex=i;return;}throw new Exception("Test conversation missing");}
                        if(!conversationChecks)
                        {
                            var search=(TextBox)field("contactSearch");var kind=(ComboBox)field("contactKind");var sort=(ComboBox)field("contactSort");
                            Func<string> first=()=>itemText(contacts.Items[itemText(contacts.Items[0],"User")==""?1:0],"User");
                            sort.SelectedIndex=4;if(first()!="chat")throw new Exception("Numeric count descending failed");
                            sort.SelectedIndex=5;if(first()!="friend_alice")throw new Exception("Numeric count ascending failed");
                            sort.SelectedIndex=0;if(first()!="friend_alice")throw new Exception("Recent sort failed");
                            sort.SelectedIndex=1;if(first()!="chat")throw new Exception("Old recent sort failed");
                            sort.SelectedIndex=2;string[] ascending=new string[contacts.Items.Count-1];for(int i=1;i<contacts.Items.Count;i++)ascending[i-1]=itemText(contacts.Items[i],"User");
                            sort.SelectedIndex=3;for(int i=1;i<contacts.Items.Count;i++)if(itemText(contacts.Items[i],"User")!=ascending[ascending.Length-i])throw new Exception("Reverse name sort failed");
                            kind.SelectedIndex=2;if(contacts.Items.Count!=1 || first()!="demo@chatroom")throw new Exception("Group filter failed");
                            search.Text="no match";if(contacts.Items.Count!=0 || itemText(field("activeContact"),"User")!="chat")throw new Exception("Empty filter changed current conversation");
                            var frozen=(System.Collections.Generic.Dictionary<string,object>)type.GetMethod("Filter",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{"export"});
                            if(Convert.ToString(frozen["chat"])!="chat")throw new Exception("Filter changed export scope");
                            search.Text="DEMO";if(contacts.Items.Count!=1)throw new Exception("Case-insensitive account search failed");
                            kind.SelectedIndex=0;search.Text="alice";if(contacts.Items.Count!=1 || first()!="friend_alice")throw new Exception("Name search failed");
                            ((Button)field("clearContactSearch")).PerformClick();kind.SelectedIndex=1;if(contacts.Items.Count!=2)throw new Exception("Single-chat filter failed");
                            kind.SelectedIndex=0;sort.SelectedIndex=4;
                            if(contacts.Items.Count!=4 || contacts.SelectedItem==null || itemText(contacts.SelectedItem,"User")!="chat")throw new Exception("Selection not restored after filtering");
                            conversationChecks=true;
                        }
                        if((bool)field("advanced"))((Button)field("advancedButton")).PerformClick();
                        if(!recognitionStarted){recognitionStarted=true;((Button)field("recognizePageButton")).PerformClick();return;}
                        foreach(ListViewItem row in ((ListView)field("voiceList")).Items)
                            if(row.SubItems[3].Text!="已识别" && row.SubItems[3].Text!="无识别文字")throw new Exception("Page recognition results not restored from cache");
                        var sortedList=(ListView)field("voiceList");
                        if(sortStep==0)
                        {
                            selectedVoice=(string)sortedList.Items[0].Tag;sortedList.Items[0].Selected=true;sortStep=1;
                            type.GetMethod("VoiceList_ColumnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{sortedList,new ColumnClickEventArgs(0)});return;
                        }
                        if(sortStep==1)
                        {
                            if(!sortedList.Columns[0].Text.Contains("↓") || String.CompareOrdinal(sortedList.Items[0].Text,sortedList.Items[2].Text)<=0)throw new Exception("Global descending time sort not applied");
                            if(sortedList.SelectedItems.Count!=1 || (string)sortedList.SelectedItems[0].Tag!=selectedVoice)throw new Exception("Visible selected identity lost when sorting");
                            sortStep=2;type.GetMethod("VoiceList_ColumnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{sortedList,new ColumnClickEventArgs(5)});return;
                        }
                        if(sortStep==2)
                        {
                            if(!sortedList.Columns[5].Text.Contains("↑") || !((Label)field("pageLabel")).Text.Contains("原始大小"))throw new Exception("Size sorting feedback missing");
                            sortStep=3;type.GetMethod("VoiceList_ColumnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{sortedList,new ColumnClickEventArgs(0)});return;
                        }
                        foreach(ListViewItem row in sortedList.Items)row.Selected=false;
                        var list = (ListView)field("voiceList"); list.Items[0].Selected = true;
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(args[1], "runtime-catalog.png")); }
                        list.Items[1].Selected=true;
                        var flags=BindingFlags.NonPublic|BindingFlags.Instance;var menu=(ContextMenuStrip)field("voiceMenu");
                        type.GetMethod("VoiceList_MouseDown",flags).Invoke(form,new object[]{list,new MouseEventArgs(MouseButtons.Right,1,list.Items[0].Bounds.X+10,list.Items[0].Bounds.Y+5,0)});
                        if(list.SelectedItems.Count!=2)throw new Exception("Right click discarded multi-selection");
                        var opening=new CancelEventArgs();type.GetMethod("VoiceMenu_Opening",flags).Invoke(form,new object[]{menu,opening});
                        if(opening.Cancel || !((ToolStripMenuItem)field("contextExport")).Enabled)throw new Exception("Selected export context menu disabled");
                        RequireUnclipped((Button)field("recognizePageButton"));
                        stage = 2;((ToolStripMenuItem)field("contextExport")).PerformClick();
                    }
                    else if (stage == 2 && field("lastOutput") != null)
                    {
                        string selectedOutput=(string)field("lastOutput");
                        if(!File.Exists(Path.Combine(selectedOutput,"manifest.json")) || Directory.GetFiles(selectedOutput,"*.wav").Length!=2)throw new Exception("Context export did not freeze exactly two selected items");
                        stage=3;((Button)field("exportAllButton")).PerformClick();
                    }
                    else if (stage == 3 && field("lastOutput") != null)
                    {
                        string output = (string)field("lastOutput");
                        if (!File.Exists(Path.Combine(output, "manifest.json")) || Directory.GetFiles(output, "*.wav").Length != 3) throw new Exception("Bundled GUI export output incomplete");
                        if(((ProgressBar)field("progressBar")).Value!=100 || !((Label)field("statusLabel")).Text.Contains("WAV成功 3"))throw new Exception("Final export summary/progress was overwritten");
                        if(!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])),"workspace","ui-preferences.json")))throw new Exception("UI preferences weren't saved");
                        form.Size = new Size(1000, 760); form.PerformLayout();
                        RequireUnclipped((Button)field("prepareButton"));RequireUnclipped((Button)field("advancedButton"));RequireUnclipped((Button)field("exportAllButton"));
                        RequireUnclipped((TextBox)field("contactSearch"));RequireUnclipped((ComboBox)field("contactSort"));RequireUnclipped((ComboBox)field("contactKind"));
                        RequireUnclipped((Button)field("diagnosticsButton"));
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(args[1], "runtime-minimum.png")); }
                        File.WriteAllText(Path.Combine(args[1], "runtime-check.txt"), "PASS: real Application.Run, source selection, read snapshots, paged catalogue, UTF-8 worker IPC, three SILK/WAV exports, manifest, minimum-size layout, close. Synthetic data only.");
                        result = 0; timer.Stop(); form.Close();
                    }
                }
                catch (Exception error) { File.WriteAllText(Path.Combine(args[1], "runtime-error.txt"), error.ToString()); timer.Stop(); form.Close(); }
            };
            form.Shown += (sender, e) => timer.Start(); Application.Run(form); timer.Dispose();
            Console.WriteLine(result == 0 ? "PASS: synthetic GUI export flow" : "FAIL: synthetic GUI export flow"); return result;
        }
    }
}
