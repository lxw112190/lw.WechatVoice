using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace WechatVoice.Workshop
{
    // UI choices only. No credentials, conversation IDs, transcripts or source paths.
    internal sealed class UiPreferences
    {
        public UiPreferences() { }
        public string Output { get; set; } = @"workspace\exports";
        public int Sort { get; set; }
        public int Kind { get; set; }
        public int VoiceSort { get; set; }
        public bool VoiceDescending { get; set; }
        public bool Transcribe { get; set; } = true;
        public bool KeepSilk { get; set; }
        public bool Range { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        internal static UiPreferences Load(string path)
        {
            if (!File.Exists(path)) return new UiPreferences();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("界面设置过大，已使用默认设置。");
            var value = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<UiPreferences>(File.ReadAllText(path, Encoding.UTF8)) ?? new UiPreferences();
            value.Sort = Math.Max(0, Math.Min(5, value.Sort)); value.Kind = Math.Max(0, Math.Min(2, value.Kind));
            value.VoiceSort=Math.Max(0,Math.Min(6,value.VoiceSort));
            ResolveOutput(value.Output); return value;
        }
        internal static string ResolveOutput(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 16000) throw new ArgumentException("请选择有效的导出文件夹。");
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
        }
        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
