using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using SherpaOnnx;

namespace WechatVoice.Asr
{
    // Separate native process, one model reused for the whole export batch.
    internal static class Program
    {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryExW(string file,IntPtr reserved,uint flags);
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        private static int Main()
        {
            SetErrorMode(3);Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
            Console.SetIn(new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false),true));
            var json=new JavaScriptSerializer {MaxJsonLength=65536};IntPtr recognizer=IntPtr.Zero;
            try
            {
                string root=AppDomain.CurrentDomain.BaseDirectory;
                foreach(string file in new[]{"vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll","msvcp140_1.dll","DirectML.dll","onnxruntime.dll","sherpa-onnx-c-api.dll"})
                    if(LoadLibraryExW(Path.Combine(root,file),IntPtr.Zero,0x00000100|0x00001000)==IntPtr.Zero)
                    {var failure=new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"ASR依赖加载失败："+file);failure.Data["component"]=file;throw failure;}
                if(NativeSpeechApi.Utf8(NativeSpeechApi.SherpaOnnxGetVersionStr())!="2.0.1") throw new InvalidOperationException("ASR运行库版本不匹配");
                var config=new OfflineRecognizerConfig();config.FeatConfig.SampleRate=16000;config.FeatConfig.FeatureDim=80;
                config.ModelConfig.SenseVoice.Model=Path.Combine(root,"models","sensevoice","model.int8.onnx");
                config.ModelConfig.SenseVoice.Language="auto";config.ModelConfig.SenseVoice.UseInverseTextNormalization=1;
                config.ModelConfig.Tokens=Path.Combine(root,"models","sensevoice","tokens.txt");config.ModelConfig.NumThreads=2;config.ModelConfig.Provider="cpu";config.DecodingMethod="greedy_search";
                using(var utf8=new Utf8Config(config)) recognizer=NativeSpeechApi.SherpaOnnxCreateOfflineRecognizer(utf8.Pointer);
                if(recognizer==IntPtr.Zero) throw new InvalidOperationException("ASR模型加载失败");
                string line;
                while((line=Console.ReadLine())!=null)
                {
                    try
                    {
                        if(line.Length>8192) throw new InvalidDataException("ASR请求过长");
                        var request=json.Deserialize<Dictionary<string,object>>(line);int rate;float[] samples=ReadWave(Convert.ToString(request["wav"]),out rate);
                        var stream=NativeSpeechApi.SherpaOnnxCreateOfflineStream(recognizer);
                        if(stream==IntPtr.Zero) throw new InvalidOperationException("ASR音频流创建失败");
                        string text;
                        try
                        {
                            NativeSpeechApi.SherpaOnnxAcceptWaveformOffline(stream,rate,samples,samples.Length);
                            NativeSpeechApi.SherpaOnnxDecodeOfflineStream(recognizer,stream);
                            IntPtr result=NativeSpeechApi.SherpaOnnxGetOfflineStreamResultAsJson(stream);
                            try {var obj=json.Deserialize<Dictionary<string,object>>(NativeSpeechApi.Utf8(result));text=Convert.ToString(obj["text"]);}
                            finally {if(result!=IntPtr.Zero) NativeSpeechApi.SherpaOnnxDestroyOfflineStreamResultJson(result);}
                        }
                        finally {NativeSpeechApi.SherpaOnnxDestroyOfflineStream(stream);}
                        text=System.Text.RegularExpressions.Regex.Replace(text??"",@"<\|.*?\|>","").Trim();
                        if(text.Length>2000) throw new InvalidDataException("ASR文字超限");
                        Console.WriteLine(json.Serialize(new {ok=true,text=text}));
                    }
                    catch(Exception) {Console.WriteLine(json.Serialize(new {ok=false,error="ASR识别失败，WAV保留"}));}
                }
                return 0;
            }
            catch(Exception ex) {var cause=ex.GetBaseException();Console.WriteLine(json.Serialize(new {ok=false,error=ex.Message,exception_type=cause.GetType().Name,component=Convert.ToString(cause.Data["component"]),hresult=cause.HResult,winerror=cause is System.ComponentModel.Win32Exception?((System.ComponentModel.Win32Exception)cause).NativeErrorCode:(int?)null}));return 1;}
            finally {if(recognizer!=IntPtr.Zero) NativeSpeechApi.SherpaOnnxDestroyOfflineRecognizer(recognizer);}
        }
        private static float[] ReadWave(string path,out int rate)
        {
            using(var stream=File.OpenRead(path)) using(var reader=new BinaryReader(stream))
            {
                if(stream.Length>18*1024*1024 || new string(reader.ReadChars(4))!="RIFF") throw new InvalidDataException();
                reader.ReadUInt32();if(new string(reader.ReadChars(4))!="WAVE") throw new InvalidDataException();
                rate=0;int format=0,channels=0,bits=0;
                while(stream.Position+8<=stream.Length)
                {
                    string chunk=new string(reader.ReadChars(4));uint size=reader.ReadUInt32();long end=stream.Position+size;
                    if(end>stream.Length) throw new InvalidDataException();
                    if(chunk=="fmt ") {if(size<16)throw new InvalidDataException();format=reader.ReadUInt16();channels=reader.ReadUInt16();rate=reader.ReadInt32();reader.ReadUInt32();reader.ReadUInt16();bits=reader.ReadUInt16();}
                    else if(chunk=="data")
                    {
                        if(format!=1 || channels!=1 || bits!=16 || rate<8000 || rate>48000 || size%2!=0 || size>rate*2*90) throw new InvalidDataException();
                        var samples=new float[size/2];for(int i=0;i<samples.Length;i++)samples[i]=reader.ReadInt16()/32768F;return samples;
                    }
                    stream.Position=end+(size%2);
                }
                throw new InvalidDataException();
            }
        }
    }
}
