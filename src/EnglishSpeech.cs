using System;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;

namespace HoverLex
{
    public interface IEnglishSpeech : IDisposable
    {
        event Action Changed;
        bool IsSpeaking { get; }
        string Error { get; }
        string Play(string text);
        void Stop();
    }

    public sealed class EnglishSpeech : IEnglishSpeech
    {
        private readonly object sync=new object();
        private readonly Stream output;
        private SpeechSynthesizer synth;
        private Prompt current;
        private bool disposed;
        private string error="";
        public event Action Changed;
        public bool IsSpeaking { get { lock(sync) return current!=null; } }
        public string Error { get { lock(sync) return error; } }
        public EnglishSpeech() { }
        internal EnglishSpeech(Stream waveOutput) { output=waveOutput; }
        public string Play(string text)
        {
            Stop();
            lock(sync) {
                if(disposed) return "朗读已关闭";
                if(String.IsNullOrWhiteSpace(text) || text.Length>12000) return "没有可朗读的英文译文";
                try {
                    if(synth==null) {
                        synth=new SpeechSynthesizer();
                        var voice=synth.GetInstalledVoices().FirstOrDefault(v=>v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName=="en");
                        if(voice==null) { synth.Dispose(); synth=null; return "请在 Windows 语音设置中安装英语语音"; }
                        synth.SelectVoice(voice.VoiceInfo.Name);
                        if(output==null) synth.SetOutputToDefaultAudioDevice(); else synth.SetOutputToWaveStream(output);
                        synth.SpeakCompleted+=Completed;
                    }
                    error=""; current=synth.SpeakAsync(text);
                } catch(Exception e) { current=null; error="朗读失败："+e.Message; return error; }
            }
            Notify(); return "";
        }
        private void Completed(object sender,SpeakCompletedEventArgs e)
        {
            lock(sync) {
                if(disposed || e.Prompt!=current) return;
                current=null; error=e.Error==null ? "" : "朗读失败："+e.Error.Message;
            }
            Notify();
        }
        public void Stop()
        {
            lock(sync) { current=null; if(synth!=null && !disposed) try { synth.SpeakAsyncCancelAll(); } catch { } }
            Notify();
        }
        private void Notify() { Action handler=Changed; if(handler!=null) handler(); }
        public void Dispose()
        {
            SpeechSynthesizer released;
            lock(sync) {
                if(disposed) return; disposed=true; current=null;
                released=synth; synth=null; Changed=null;
            }
            if(released!=null) { released.SpeakCompleted-=Completed; released.Dispose(); }
        }
    }
}
