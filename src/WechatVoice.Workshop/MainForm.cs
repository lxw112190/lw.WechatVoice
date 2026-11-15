using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace WechatVoice.Workshop
{
    public partial class MainForm : Form
    {
        private string workspace, engine, session, cancelPath, lastOutput;
        private bool busy, closing, loading;
        private int page, total, operation;
        private bool cancelling;
        private int voiceSort;
        private bool voiceDescending;
        private readonly string[] voiceSortFields={"stamp","sender","transcript","recognition_status","label","bytes","local_id"};
        private readonly string[] voiceSortTitles={"发送时间","发送者","识别文字","识别状态","会话","原始大小","本地编号"};
        private UiPreferences preferences=new UiPreferences();
        private SoundPlayer player;
        private bool advanced,selectingAccount;
        private bool favoriteMode;
        private readonly List<ContactChoice> conversations=new List<ContactChoice>();
        private ContactChoice activeContact;
        public MainForm() { InitializeComponent(); }
        private void MainForm_Shown(object sender, EventArgs e)
        {
            workspace = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workspace");
            engine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine", "VoiceWorker.exe");
            Diagnostics.Write(workspace,"startup",null,Diagnostics.EnvironmentInfo());
            outputText.Text = Path.Combine(workspace, "exports");
            try
            {
                preferences=UiPreferences.Load(Path.Combine(workspace,"ui-preferences.json"));
                outputText.Text=preferences.Output;contactSort.SelectedIndex=preferences.Sort;contactKind.SelectedIndex=preferences.Kind;
                voiceSort=preferences.VoiceSort;voiceDescending=preferences.VoiceDescending;
            }
            catch(Exception ex){Log("读取界面设置失败，使用默认设置："+ex.Message);}
            UpdateVoiceHeaders();
            RefreshProcess_Click(this,EventArgs.Empty);
            string saved=Path.Combine(workspace,"account-path.txt");
            string preferred=null;
            if(File.Exists(saved) && new FileInfo(saved).Length<4096)
                try{preferred=File.ReadAllText(saved).Trim();}catch{ }
            ScanAccounts(preferred);
            if(accountBox.Items.Count==0)
            {
                if(preferred!=null && Directory.Exists(preferred))SelectSourceRoot(preferred);
                else foreach(string candidate in new[]{Path.Combine(AccountScanner.DocumentsDirectory(),"xwechat_files"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"WeChat Files")})
                    if(Directory.Exists(candidate)){SelectSourceRoot(candidate);break;}
            }
            Log("面向 Windows 微信 4.x。已自动扫描本机微信账号，可点“扫描账号”重新扫描或“选择位置”手动指定；登录后选择账号和进程，点击连接微信。");
            Log("既有真实账号实测版本：4.0.5.16；其他4.x可尝试连接，尚不承诺全部兼容。");
            Log("也支持已解密的 db_storage 副本。不会联网、修改源数据或自动关闭微信。");
        }
        private void Log(string text)
        {
            if (logText.Lines.Length > 400) logText.Lines = logText.Lines.Skip(100).ToArray();
            logText.AppendText(DateTime.Now.ToString("HH:mm:ss  ") + text + Environment.NewLine);
            // Free-form messages may contain account/output paths. Keep them on screen only.
        }
        private void ExportDiagnostics_Click(object sender,EventArgs e)
        {
            using(var dialog=new SaveFileDialog {Title="导出诊断包",Filter="诊断压缩包 (*.zip)|*.zip",FileName="微信语音诊断-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip"})
                if(dialog.ShowDialog(this)==DialogResult.OK)
                    try{Diagnostics.ExportZip(workspace,dialog.FileName);MessageBox.Show(this,"诊断包已保存。请连同问题发生时间、操作步骤和错误截图一起发回。\r\n不含数据库、录音、密钥或识别文字。","导出完成");}
                    catch(Exception ex){MessageBox.Show(this,"诊断包导出失败："+ex.Message);}
        }
        private void SourceBrowse_Click(object sender, EventArgs e)
        {
            try
            {
            using (var dialog = new FolderBrowserDialog { Description = "请选择本人/获授权的单个微信账号目录，或 db_storage 目录" })
                if (dialog.ShowDialog(this) == DialogResult.OK) SelectSourceRoot(dialog.SelectedPath);
            }
            catch(Exception ex){Log(ex.Message);MessageBox.Show(this,"无法读取此目录："+ex.Message);}
        }
        private void SelectSourceRoot(string path)
        {
            if(!Directory.Exists(path))return;
            selectingAccount=true;
            try
            {
                accountBox.Items.Clear();
                if(Directory.Exists(Path.Combine(path,"db_storage")) || Path.GetFileName(path).Equals("db_storage",StringComparison.OrdinalIgnoreCase) || Directory.GetFiles(path,"media_*.db").Length>0)
                    accountBox.Items.Add(new AccountChoice{Path=path,Text=Path.GetFileName(path)});
                else foreach(string child in Directory.GetDirectories(path))
                    if(Directory.Exists(Path.Combine(child,"db_storage")))accountBox.Items.Add(new AccountChoice{Path=child,Text=Path.GetFileName(child)});
                accountBox.SelectedIndex=accountBox.Items.Count==1?0:-1;
                sourceText.Text=accountBox.SelectedItem is AccountChoice?((AccountChoice)accountBox.SelectedItem).Path:"";
                if(accountBox.Items.Count==0)Log("此位置未发现账号，可选择单个账号、xwechat_files或解密库目录。");
            }
            finally{selectingAccount=false;}
            InvalidateSession();
        }
        private void Account_SelectedIndexChanged(object sender,EventArgs e)
        {
            if(selectingAccount)return;
            var selected=accountBox.SelectedItem as AccountChoice;sourceText.Text=selected==null?"":selected.Path;
        }
        private void ScanAccount_Click(object sender,EventArgs e)
        {
            if(busy)return;
            string preferred=null;
            var choice=accountBox.SelectedItem as AccountChoice;
            if(choice!=null && Directory.Exists(choice.Path))preferred=choice.Path;
            else if(Directory.Exists(sourceText.Text))preferred=sourceText.Text;
            ScanAccounts(preferred);
        }
        private void ScanAccounts(string preferredPath)
        {
            if(busy)return;
            selectingAccount=true;
            try
            {
                var accounts=AccountScanner.ListAccounts();
                int match=-1;
                for(int i=0;i<accounts.Count && match<0;i++)
                    if(PathsEqual(accounts[i].Path,preferredPath))match=i;
                // Keep a preferred location the scan cannot see (custom data folder).
                if(match<0 && preferredPath!=null)
                {
                    var preferred=AccountScanner.FromPath(preferredPath);
                    if(preferred!=null)
                    {
                        int existing=accounts.FindIndex(a=>PathsEqual(a.Path,preferred.Path));
                        match=existing>=0?existing:accounts.Count;
                        if(existing<0)accounts.Add(preferred);
                    }
                }
                accountBox.Items.Clear();
                foreach(var account in accounts)
                    accountBox.Items.Add(new AccountChoice{Path=account.Path,Text=AccountDisplay(account)});
                if(accounts.Count>0)
                {
                    accountBox.SelectedIndex=match>=0?match:0;
                    var choice=(AccountChoice)accountBox.SelectedItem;
                    if(choice!=null)sourceText.Text=choice.Path;
                }
                else Log("未扫描到微信账号，可点“选择位置”手动指定账号目录。");
            }
            catch(Exception ex){Log("扫描账号失败，可点“选择位置”手动指定："+ex.Message);}
            finally{selectingAccount=false;}
        }
        private static string AccountDisplay(AccountInfo account)
        {
            return account.Wxid+" · 最近使用 "+(account.Recent==DateTime.MinValue?"未知":account.Recent.ToString("yyyy-MM-dd HH:mm"));
        }
        private static bool PathsEqual(string a,string b)
        {
            if(string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))return false;
            try
            {
                return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\','/'),Path.GetFullPath(b.Trim()).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
            }
            catch{return false;}
        }
        private void SourceText_TextChanged(object sender, EventArgs e)
        {
            if(!selectingAccount && Directory.Exists(sourceText.Text))
            {
                selectingAccount=true;accountBox.Items.Clear();accountBox.Items.Add(new AccountChoice{Path=sourceText.Text,Text=Path.GetFileName(sourceText.Text)});accountBox.SelectedIndex=0;selectingAccount=false;
            }
            InvalidateSession();
        }
        private void Advanced_Click(object sender,EventArgs e)
        {
            advanced=!advanced;processLabel.Visible=processBox.Visible=refreshButton.Visible=readButton.Visible=advanced;
            sourcePanel.RowStyles[1].Height=advanced?32:0;root.RowStyles[1].Height=advanced?160:128;
            root.RowStyles[8].Height=advanced?100:0;logText.Visible=advanced;
            exportSelectedButton.Visible=advanced;
            advancedButton.Text=advanced?"收起高级":"高级 / 诊断";PerformLayout();
        }
        private void InvalidateSession()
        {
            StopAudio();lastOutput=null;pageLabel.Text="尚未连接";
            session = null; page = 0; total = 0;activeContact=null;conversations.Clear();
            bool previous=loading;loading=true;
            try{voiceList.Items.Clear();contactList.Items.Clear();contactSearch.Text="";contactSummary.Text="尚未连接";}
            finally{loading=previous;}
            UpdateActions();
        }
        private void RefreshProcess_Click(object sender, EventArgs e)
        {
            processBox.Items.Clear();
            foreach (string name in new[] { "Weixin", "WeChat" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                        try { processBox.Items.Add(new ProcessChoice { Id = process.Id,Memory=process.WorkingSet64, Text = process.ProcessName + " · PID " + process.Id + " · " + process.MainModule.FileVersionInfo.FileVersion }); }
                        catch { Log("一个微信进程无法读取版本，请检查是否由其他用户或管理员启动。"); }
            var choices=processBox.Items.Cast<ProcessChoice>().OrderByDescending(p=>p.Memory).ToArray();processBox.Items.Clear();processBox.Items.AddRange(choices);
            if (processBox.Items.Count > 0) processBox.SelectedIndex = 0; else Log("未发现正在运行的电脑微信；已解密副本可直接读取。");
        }
        private bool SourceReady()
        {
            if (!consentBox.Checked) { MessageBox.Show(this, "请先确认仅处理本人或已获授权的账号数据。"); return false; }
            if (!Directory.Exists(sourceText.Text)) { MessageBox.Show(this, "请点“选择位置”，然后选择一个账号。"); return false; }
            return true;
        }
        private async void Prepare_Click(object sender, EventArgs e)
        {
            if (!SourceReady()) return;
            if(!advanced || processBox.SelectedItem==null)RefreshProcess_Click(this,EventArgs.Empty);
            var selected = processBox.SelectedItem as ProcessChoice;
            if (selected == null) { MessageBox.Show(this, "请登录电脑微信4.x；需要指定进程时，可展开“高级 / 诊断”刷新并选择。"); return; }
            StopAudio();
            var request = new Dictionary<string, object> { ["action"] = "connect", ["source"] = sourceText.Text, ["pid"] = selected.Id, ["consent"] = true };
            var result = await RunAsync(request);
            await AcceptCatalog(result);
        }
        private async void Read_Click(object sender, EventArgs e)
        {
            if (!SourceReady()) return;
            StopAudio();
            var result = await RunAsync(new Dictionary<string, object> { ["action"] = "catalog", ["source"] = sourceText.Text, ["consent"] = true });
            await AcceptCatalog(result);
        }
        private async Task AcceptCatalog(Dictionary<string, object> result)
        {
            if (result == null) return;
            if(closing || IsDisposed)return;
            InvalidateSession();
            session = WorkerClient.Get(result, "session"); page = 0;
            favoriteMode = result.ContainsKey("favorites") && Convert.ToBoolean(result["favorites"]);
            voiceSortTitles[0] = favoriteMode ? "收藏时间" : "发送时间";
            voiceSortTitles[4] = favoriteMode ? "收藏标题" : "会话";
            titleLabel.Text = favoriteMode ? "微信语音导出工坊 · 收藏语音" : "微信语音导出工坊";
            exportAllButton.Text = favoriteMode ? "导出收藏…" : "会话导出…";
            if (favoriteMode) contactKind.SelectedIndex = 0;
            UpdateVoiceHeaders();
            try{Directory.CreateDirectory(workspace);File.WriteAllText(Path.Combine(workspace,"account-path.txt"),sourceText.Text,new System.Text.UTF8Encoding(false));}
            catch(Exception ex){Log("未能记住账号位置："+ex.Message);}
            Log("读取本地语音 " + WorkerClient.Get(result, "count") + " 条；数据库 " + WorkerClient.Get(result, "databases") + " / " + WorkerClient.Get(result, "expected_databases") + "。");
            foreach (object issue in WorkerClient.Array(result, "issues")) Log("需注意：" + issue);
            if (result.ContainsKey("snapshot_note")) Log(WorkerClient.Get(result, "snapshot_note"));
            if(result.ContainsKey("key_report") && result["key_report"] is Dictionary<string,object>)
                Log("本次连接微信版本："+WorkerClient.Get((Dictionary<string,object>)result["key_report"],"version"));
            await LoadPage(true);
        }
        private async void Favorites_Click(object sender, EventArgs e)
        {
            if (!SourceReady()) return;
            if (!advanced || processBox.SelectedItem == null) RefreshProcess_Click(this, EventArgs.Empty);
            var selected = processBox.SelectedItem as ProcessChoice;
            StopAudio();
            var request = new Dictionary<string,object> { ["action"] = selected == null ? "catalog" : "connect", ["source"] = sourceText.Text, ["consent"] = true, ["favorites"] = true };
            if (selected != null) request["pid"] = selected.Id;
            await AcceptCatalog(await RunAsync(request));
        }
        private Dictionary<string, object> Filter(string action)
        {
            var request = new Dictionary<string, object> { ["action"] = action, ["session"] = session, ["page"] = page,["sort"]=voiceSortFields[voiceSort],["descending"]=voiceDescending };
            var contact = activeContact;
            if (contact != null && contact.User.Length > 0) request["chat"] = contact.User;
            if (dateBox.Checked)
            {
                request["after"] = new DateTimeOffset(fromDate.Value.Date).ToUnixTimeSeconds();
                request["before"] = new DateTimeOffset(toDate.Value.Date.AddDays(1)).ToUnixTimeSeconds();
            }
            return request;
        }
        private async Task LoadPage(bool contacts = false,HashSet<string> selectedIds=null)
        {
            if (session == null || busy) return;
            StopAudio();
            var query=Filter("list");query["include_contacts"]=contacts;
            var result = await RunAsync(query);
            if(closing || IsDisposed)return;
            if(result==null)
            {
                // Never leave another conversation's visible rows under the new export scope.
                loading=true;try{voiceList.Items.Clear();total=0;page=0;pageLabel.Text="列表未加载，请重新选择会话或重新连接。";}finally{loading=false;UpdateActions();}
                return;
            }
            loading = true;
            try
            {
                if (contacts)
                {
                    conversations.Clear();activeContact=new ContactChoice { User="",Label=favoriteMode?"全部收藏":"全部会话",Text=favoriteMode?"全部收藏":"全部会话" };
                    foreach (Dictionary<string, object> c in WorkerClient.Array(result, "contacts"))
                        conversations.Add(new ContactChoice { User=WorkerClient.Get(c,"user"),Label=WorkerClient.Get(c,"label"),Count=Convert.ToInt64(c["count"]),Latest=c.ContainsKey("latest_stamp")?Convert.ToInt64(c["latest_stamp"]):0,Text=WorkerClient.Get(c,"label")+" ("+WorkerClient.Get(c,"count")+")" });
                    ApplyConversationView();
                }
                voiceList.BeginUpdate(); voiceList.Items.Clear();
                foreach (Dictionary<string, object> row in WorkerClient.Array(result, "items"))
                {
                    string time;
                    try { time = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(row["stamp"])).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"); }
                    catch { time = "未知时间"; }
                    var item = new ListViewItem(time) { Tag = WorkerClient.Get(row, "id") };
                    string transcript=WorkerClient.Get(row,"transcript"),recognition=WorkerClient.Get(row,"recognition_status");
                    item.SubItems.Add(WorkerClient.Get(row,"sender"));item.SubItems.Add(transcript);
                    item.SubItems.Add(recognition=="READY"?"已识别":recognition=="EMPTY"?"无识别文字":recognition=="ERROR"?"失败，可重试":"未识别");
                    item.SubItems.Add(WorkerClient.Get(row,"label"));
                    item.SubItems.Add((Convert.ToInt64(row["bytes"]) / 1024.0).ToString("0.0") + " KB"); item.SubItems.Add(WorkerClient.Get(row, "local_id"));item.ToolTipText=transcript;
                    voiceList.Items.Add(item);
                    if(selectedIds!=null && selectedIds.Contains((string)item.Tag))item.Selected=true;
                }
                total = Convert.ToInt32(result["count"]);
                pageLabel.Text = "第 " + (page + 1) + " / " + Math.Max(1, (total + 49) / 50) + " 页 · " + total + " 条 · "+voiceSortTitles[voiceSort]+(voiceDescending?" ↓":" ↑");
                statusLabel.Text=total==0?"此会话没有可导出的本地语音。":"已加载 "+total+" 条语音 · 点击列头排序 · 双击试听";
            }
            finally { voiceList.EndUpdate(); loading = false; UpdateActions(); }
        }
        private async void ContactList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if(!loading && !busy && contactList.SelectedItem is ContactChoice)
            {activeContact=(ContactChoice)contactList.SelectedItem;UpdateConversationSummary();page=0;await LoadPage();}
        }
        private void ConversationFilter_Changed(object sender,EventArgs e){if(!loading)ApplyConversationView();}
        private void ClearConversationSearch_Click(object sender,EventArgs e){contactSearch.Clear();contactSearch.Focus();}
        private void ApplyConversationView()
        {
            if(session==null)return;
            string query=contactSearch.Text.Trim();int kind=contactKind.SelectedIndex;
            IEnumerable<ContactChoice> view=conversations.Where(c=>(query.Length==0 || c.Label.IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0 || c.User.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) && (kind<=0 || (kind==2)==c.User.EndsWith("@chatroom",StringComparison.OrdinalIgnoreCase)));
            var names=StringComparer.CurrentCultureIgnoreCase;
            switch(contactSort.SelectedIndex)
            {
                case 1:view=view.OrderBy(c=>c.Latest).ThenBy(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
                case 2:view=view.OrderBy(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
                case 3:view=view.OrderByDescending(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
                case 4:view=view.OrderByDescending(c=>c.Count).ThenBy(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
                case 5:view=view.OrderBy(c=>c.Count).ThenBy(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
                default:view=view.OrderByDescending(c=>c.Latest).ThenBy(c=>c.Label,names).ThenBy(c=>c.User,StringComparer.Ordinal);break;
            }
            bool previous=loading;loading=true;contactList.BeginUpdate();
            try
            {
                contactList.Items.Clear();
                if(query.Length==0 && kind<=0)contactList.Items.Add(new ContactChoice { User="",Label=favoriteMode?"全部收藏":"全部会话",Text=favoriteMode?"全部收藏":"全部会话" });
                foreach(var item in view)contactList.Items.Add(item);
                if(activeContact!=null)
                    for(int i=0;i<contactList.Items.Count;i++)if(((ContactChoice)contactList.Items[i]).User==activeContact.User){contactList.SelectedIndex=i;break;}
                UpdateConversationSummary();
            }
            finally{contactList.EndUpdate();loading=previous;}
        }
        private void UpdateConversationSummary()
        {
            int count=contactList.Items.Cast<ContactChoice>().Count(c=>c.User.Length>0);
            contactSummary.Text="显示 "+count+" / "+conversations.Count+" 个会话\r\n当前："+(activeContact==null?"未选择":activeContact.Label);
        }
        private async void ContactList_DoubleClick(object sender, EventArgs e){if(!busy && contactList.SelectedItem is ContactChoice)await Export(false);}
        private async void Filter_Click(object sender, EventArgs e) { page = 0; await LoadPage(); }
        private async void Previous_Click(object sender, EventArgs e) { if (page > 0) { page--; await LoadPage(); } }
        private async void Next_Click(object sender, EventArgs e) { if ((page + 1) * 50 < total) { page++; await LoadPage(); } }
        private async void Preview_Click(object sender, EventArgs e)
        {
            if (voiceList.SelectedItems.Count != 1) { MessageBox.Show(this, "请选择一条语音试听。"); return; }
            StopAudio(); var request = Filter("preview"); request["id"] = voiceList.SelectedItems[0].Tag;
            var result = await RunAsync(request);
            if (result != null && !closing && !IsDisposed) { try { player = new SoundPlayer(WorkerClient.Get(result, "path")); player.Load(); player.Play(); statusLabel.Text="正在试听 · 切换会话或翻页将停止播放"; } catch (Exception ex) { Log("播放失败：" + ex.Message);statusLabel.Text="试听失败，请查看诊断。"; } }
        }
        private void Stop_Click(object sender, EventArgs e) { StopAudio();if(!busy)statusLabel.Text="试听已停止"; }
        private void VoiceList_DoubleClick(object sender,EventArgs e){if(!busy && voiceList.SelectedItems.Count==1)Preview_Click(sender,e);}
        private void UpdateVoiceHeaders()
        {
            for(int i=0;i<voiceList.Columns.Count;i++)voiceList.Columns[i].Text=voiceSortTitles[i]+(i==voiceSort?(voiceDescending?" ↓":" ↑"):"");
        }
        private async void VoiceList_ColumnClick(object sender,ColumnClickEventArgs e)
        {
            if(busy || session==null || e.Column<0 || e.Column>=voiceSortFields.Length)return;
            var selectedIds=new HashSet<string>(voiceList.SelectedItems.Cast<ListViewItem>().Select(i=>(string)i.Tag));
            if(voiceSort==e.Column)voiceDescending=!voiceDescending;else{voiceSort=e.Column;voiceDescending=false;}
            page=0;UpdateVoiceHeaders();SavePreferences();await LoadPage(false,selectedIds);
        }
        private void VoiceList_SelectionChanged(object sender,EventArgs e){if(!loading)UpdateActions();}
        private void StopAudio() { if (player != null) { player.Stop(); player.Dispose(); player = null; } }
        private void OutputBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择导出文件夹（不能放在微信账号目录中）" })
                if (dialog.ShowDialog(this) == DialogResult.OK) {outputText.Text = dialog.SelectedPath;SavePreferences();}
        }
        private async void ExportAll_Click(object sender, EventArgs e) { await Export(false); }
        private async void ExportSelected_Click(object sender, EventArgs e) { await Export(true); }
        private void VoiceList_MouseDown(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Right || busy)return;
            var item=voiceList.HitTest(e.Location).Item;
            if(item==null || !item.Selected)
            {
                foreach(ListViewItem selected in voiceList.SelectedItems.Cast<ListViewItem>().ToArray())selected.Selected=false;
                if(item!=null){item.Selected=true;item.Focused=true;}
            }
        }
        private void VoiceMenu_Opening(object sender,System.ComponentModel.CancelEventArgs e)
        {
            contextExport.Enabled=!busy && session!=null && voiceList.SelectedItems.Count>0;
            e.Cancel=!contextExport.Enabled;
        }
        private async void RecognizePage_Click(object sender,EventArgs e)
        {
            if(busy || session==null || voiceList.Items.Count==0)return;
            StopAudio();var request=Filter("recognize_page");request["ids"]=voiceList.Items.Cast<ListViewItem>().Select(i=>(string)i.Tag).ToArray();
            var result=await RunAsync(request);if(result==null || closing || IsDisposed)return;
            await LoadPage();if(closing || IsDisposed)return;
            statusLabel.Text=(Convert.ToBoolean(result["cancelled"])?"已取消，已完成的识别保留":"本页识别完成")+" · 新识别 "+WorkerClient.Get(result,"recognized")+" · 复用缓存 "+WorkerClient.Get(result,"cached")+" · 失败 "+WorkerClient.Get(result,"failed");
        }
        private async Task Export(bool selected)
        {
            if (session == null || busy) return;
            var conversation=activeContact;
            if(!selected && !favoriteMode && (conversation==null || conversation.User.Length==0)){MessageBox.Show(this,"请先在左侧选择一个具体会话，再进入导出页面。");return;}
            var request = Filter("export");
            try{request["output"]=UiPreferences.ResolveOutput(outputText.Text);}
            catch(Exception ex){MessageBox.Show(this,ex.Message);return;}
            if (selected)
            {
                if (voiceList.SelectedItems.Count == 0) { MessageBox.Show(this, "请先选择语音（可按 Ctrl / Shift 多选）。"); return; }
                request["ids"] = voiceList.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
            }
            using(var dialog=new ExportForm(selected?"选中的语音（"+voiceList.SelectedItems.Count+" 条）":(conversation==null?"全部收藏语音":conversation.Text),fromDate.Value,toDate.Value,dateBox.Checked,async options=>
            {
                var query=new Dictionary<string,object>(request);query["action"]="count";query["workspace"]=workspace;query.Remove("after");query.Remove("before");
                foreach(var pair in options)query[pair.Key]=pair.Value;
                var counted=await WorkerClient.RunAsync(engine,query,p=>{});return Convert.ToInt32(counted["count"]);
            }))
            {
                dialog.ApplyPreferences(preferences);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                request.Remove("after");request.Remove("before");foreach(var pair in dialog.Options)request[pair.Key]=pair.Value;
                dialog.StorePreferences(preferences);SavePreferences();
            }
            StopAudio(); var result = await RunAsync(request);
            if (result != null && !closing && !IsDisposed)
            {
                lastOutput = WorkerClient.Get(result, "output");
                if(!Convert.ToBoolean(result["cancelled"]))progressBar.Value=100;
                string summary=(Convert.ToBoolean(result["cancelled"]) ? "已取消" : "导出结束") + "：WAV成功 " + WorkerClient.Get(result, "completed") + "，失败 " + WorkerClient.Get(result, "failed") + "，识别成功 "+WorkerClient.Get(result,"recognized")+"，识别未完成 "+WorkerClient.Get(result,"recognition_failed")+"。";
                Log(summary + lastOutput);statusLabel.Text=summary+" 点击“打开输出目录”查看文件。";
            }
        }
        private void Cancel_Click(object sender, EventArgs e)
        {
            if (busy && cancelPath != null) try { File.WriteAllText(cancelPath, "cancel");cancelling=true;cancelButton.Enabled=false; statusLabel.Text = "正在取消，保留已生成文件…"; } catch (Exception ex) { Log(ex.Message); }
        }
        private void OpenOutput_Click(object sender, EventArgs e)
        {
            try
            {
                string path = lastOutput ?? UiPreferences.ResolveOutput(outputText.Text);
                if (Directory.Exists(path))Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else statusLabel.Text="导出目录尚未创建，完成首次导出后可打开。";
            }
            catch(Exception ex){Log(ex.Message);}
        }
        private async Task<Dictionary<string, object>> RunAsync(Dictionary<string, object> request)
        {
            if (busy) return null;
            int revision=++operation;
            try
            {
                Directory.CreateDirectory(workspace); cancelPath = Path.Combine(workspace, "cancel-" + Guid.NewGuid().ToString("N"));
                request["workspace"] = workspace; request["cancel"] = cancelPath;
                busy = true;cancelling=false; UpdateActions();progressBar.Value=0; progressBar.Style = ProgressBarStyle.Marquee; statusLabel.Text = "处理中…";
                var result=await WorkerClient.RunAsync(engine, request, value=>Progress(value,revision));
                statusLabel.Text="操作完成";return result;
            }
            catch(OperationCanceledException ex){statusLabel.Text="已取消；已生成文件保留。";Log(ex.Message);return null;}
            catch (Exception ex) { statusLabel.Text="操作失败，可点击“导出诊断包”反馈。";Log(ex.Message);string id=Convert.ToString(ex.Data["request_id"]);if(id.Length>0)Log("请求编号："+id); if (!closing) MessageBox.Show(this, ex.Message+"\r\n\r\n可点击“导出诊断包”反馈。"+(id.Length>0?"\r\n请求编号："+id:""), "微信语音导出工坊", MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }
            finally
            {
                ++operation;busy = false; progressBar.Style = ProgressBarStyle.Blocks;
                if (cancelPath != null) try { File.Delete(cancelPath); } catch { }
                cancelPath = null; UpdateActions(); if (closing) Close();
            }
        }
        private void Progress(Dictionary<string, object> value,int revision)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try{BeginInvoke((Action)(() =>
            {
                if(IsDisposed || closing || !busy || cancelling || revision!=operation)return;
                string phase = WorkerClient.Get(value, "phase");
                if (phase == "favorites") { statusLabel.Text = "整理收藏语音：匹配 " + WorkerClient.Get(value,"success") + " / " + WorkerClient.Get(value,"total"); return; }
                if (phase == "keys") statusLabel.Text = "只读验证密钥：" + WorkerClient.Get(value, "matched") + " / " + WorkerClient.Get(value, "encrypted") + " · 已扫描 " + WorkerClient.Get(value, "scanned_mb") + " MB";
                else if (phase == "snapshot") statusLabel.Text = "创建只读副本：" + WorkerClient.Get(value, "current") + " / " + WorkerClient.Get(value, "total");
                else if (phase == "snapshot_detail")
                {
                    string stage = WorkerClient.Get(value, "snapshot_stage"), title;
                    switch (stage)
                    {
                        case "copy_main": title = "复制源库"; break;
                        case "copy_wal": title = "复制近期数据"; break;
                        case "decrypt": title = "解密副本"; break;
                        case "analyse_pages": title = "分析有效页"; break;
                        case "verify_wal": title = "校验近期数据"; break;
                        case "replay_wal": title = "合并近期数据"; break;
                        default: title = "检查副本完整性"; break;
                    }
                    long done = Convert.ToInt64(value["bytes_current"]), total = Convert.ToInt64(value["bytes_total"]);
                    progressBar.Style = ProgressBarStyle.Blocks;
                    progressBar.Value = total <= 0 ? 0 : (int)Math.Min(100, 100L * done / total);
                    string speed = Convert.ToDouble(value["speed_mb_s"]) > 0 && stage != "verify_db" && stage != "analyse_pages" ? " · " + Convert.ToDouble(value["speed_mb_s"]).ToString("F1") + " MiB/s" : "";
                    statusLabel.Text = "第 " + WorkerClient.Get(value, "current") + " / " + WorkerClient.Get(value, "total") + " 个库 · " + title + " " + (done / 1048576.0).ToString("F1") + " / " + (total / 1048576.0).ToString("F1") + " MiB" + speed;
                }
                else if (phase == "snapshot_retry") { progressBar.Style = ProgressBarStyle.Marquee; statusLabel.Text = "微信正在写入，重试在线副本 " + WorkerClient.Get(value, "attempt") + " / 3…"; }
                else if (phase == "catalog") { progressBar.Style = ProgressBarStyle.Marquee; statusLabel.Text = "整理语音：" + WorkerClient.Get(value, "count"); }
                else if(phase=="page_recognition")
                {
                    int done=Convert.ToInt32(value["processed"]),count=Convert.ToInt32(value["total"]);progressBar.Style=ProgressBarStyle.Blocks;progressBar.Value=count==0?0:Math.Min(100,100*done/count);
                    statusLabel.Text="识别本页 "+done+" / "+count+" · 缓存 "+WorkerClient.Get(value,"cached")+" · 失败 "+WorkerClient.Get(value,"failed");
                }
                else if (phase == "recognize" || phase == "export")
                {
                    lastOutput=WorkerClient.Get(value,"output");
                    int done=Convert.ToInt32(value["processed"]),count=Convert.ToInt32(value["total"]);
                    progressBar.Style=ProgressBarStyle.Blocks;progressBar.Value=count==0?0:(int)Math.Min(100,100L*done/count);
                    string remaining=value["remaining_seconds"]==null?"估算中":TimeSpan.FromSeconds(Convert.ToDouble(value["remaining_seconds"])).ToString(@"hh\:mm\:ss");
                    statusLabel.Text=(phase=="recognize"?"本地识别第 "+(done+1)+" 条 · ":"导出中 · ")+done+" / "+count+" · 成功 "+WorkerClient.Get(value,"completed")+" · 失败 "+WorkerClient.Get(value,"failed")+" · 预计剩余 "+remaining;
                }
            }));}catch(InvalidOperationException){ }
        }
        private void UpdateActions()
        {
            sourcePanel.Enabled = !busy; filtersPanel.Enabled = !busy; contactList.Enabled = !busy; voiceList.Enabled = !busy;
            contactSearch.Enabled=contactKind.Enabled=contactSort.Enabled=clearContactSearch.Enabled=!busy;
            outputPanel.Enabled = !busy; prepareButton.Enabled = !busy; readButton.Enabled = !busy;
            previewButton.Enabled = !busy && session != null && voiceList.SelectedItems.Count==1; exportAllButton.Enabled = !busy && session != null && total > 0 && (favoriteMode || activeContact!=null && activeContact.User.Length>0);
            exportSelectedButton.Enabled = !busy && session != null && voiceList.SelectedItems.Count>0; previousButton.Enabled = !busy && page > 0;
            nextButton.Enabled = !busy && (page + 1) * 50 < total; cancelButton.Enabled = busy && !cancelling;
            recognizePageButton.Enabled=!busy && session!=null && voiceList.Items.Count>0;
        }
        private void SavePreferences()
        {
            if(workspace==null)return;
            preferences.Output=outputText.Text;preferences.Sort=contactSort.SelectedIndex;preferences.Kind=contactKind.SelectedIndex;
            preferences.VoiceSort=voiceSort;preferences.VoiceDescending=voiceDescending;
            try{preferences.Save(Path.Combine(workspace,"ui-preferences.json"));}catch(Exception ex){Log("未能保存界面设置："+ex.Message);}
        }
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopAudio(); if (busy) { e.Cancel = true; closing = true; Cancel_Click(sender, e); }else SavePreferences();
        }
        private sealed class AccountChoice {public string Path,Text;public override string ToString(){return Text;}}
        private sealed class ProcessChoice { public int Id; public long Memory; public string Text; public override string ToString() { return Text; } }
        private sealed class ContactChoice { public string User, Text,Label;public long Count,Latest; public override string ToString() { return Text; } }
    }
}
