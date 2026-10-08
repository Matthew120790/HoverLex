param([string]$Report,[int]$Seconds=180,[switch]$AnyEnglish)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$source=@'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class InstalledWechatObserver {
    public sealed class ClipboardState { public bool Available,HadText; public string Text=""; }
    public delegate bool Each(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Each callback,IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window,Each callback,IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] static extern IntPtr ReadText(IntPtr window,uint message,IntPtr count,StringBuilder text,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr data);
    public static ClipboardState ClipboardSnapshot() {
        var state=new ClipboardState();
        if(!OpenClipboard(IntPtr.Zero)) return state;
        try {
            state.HadText=IsClipboardFormatAvailable(13);
            if(state.HadText) {
                IntPtr data=GetClipboardData(13);
                if(data==IntPtr.Zero || GlobalSize(data).ToUInt64()>4000000) return state;
                IntPtr pointer=GlobalLock(data); if(pointer==IntPtr.Zero) return state;
                try { state.Text=Marshal.PtrToStringUni(pointer) ?? ""; } finally { GlobalUnlock(data); }
            }
            state.Available=true; return state;
        } finally { CloseClipboard(); }
    }
    public static int Card(int pid,string expected) {
        int result=0;
        EnumWindows((window,state)=> {
            uint owner; GetWindowThreadProcessId(window,out owner);
            if(owner!=pid || !IsWindowVisible(window)) return true;
            bool phrase=false,title=false,ready=false,failure=false;
            EnumChildWindows(window,(child,arg)=> {
                var kind=new StringBuilder(256); GetClassName(child,kind,256);
                bool label=kind.ToString().IndexOf("STATIC",StringComparison.OrdinalIgnoreCase)>=0;
                bool button=kind.ToString().IndexOf("BUTTON",StringComparison.OrdinalIgnoreCase)>=0;
                if(!label && !button) return true;
                var text=new StringBuilder(2200); IntPtr value;
                if(ReadText(child,13,new IntPtr(text.Capacity),text,2,100,out value)==IntPtr.Zero) return true;
                string actual=text.ToString();
                if(label && (expected=="" ? System.Text.RegularExpressions.Regex.IsMatch(actual,"[A-Za-z]") && actual!="Tab" : actual==expected)) phrase=true;
                if(label && actual=="\u9009\u4e2d\u82f1\u6587 \u2192 \u4e2d\u6587") title=true;
                if(button && actual=="\u590d\u5236" && IsWindowEnabled(child)) ready=true;
                if(label && (actual.Contains("\u672a\u8bfb\u5230\u9009\u4e2d\u6587\u5b57") || actual.StartsWith("\u7ffb\u8bd1\u5931\u8d25\uff1a"))) failure=true;
                return true;
            },IntPtr.Zero);
            if(phrase && title) result=Math.Max(result,ready ? 2 : 1);
            if(failure) result=Math.Max(result,3);
            return true;
        },IntPtr.Zero);
        return result;
    }
}
'@
Add-Type $source
$install=Join-Path $env:LOCALAPPDATA 'HoverLex'
$current=Get-Content (Join-Path $install 'current.json') -Raw | ConvertFrom-Json
$mainPath=[IO.Path]::GetFullPath((Join-Path $install ($current.Directory+'/HoverLex.exe')))
$main=Get-CimInstance Win32_Process -Filter "Name='HoverLex.exe'" | Where-Object {$_.ExecutablePath -eq $mainPath -and $_.CommandLine -notmatch '--input-watch|--selection-probe|--probe|--replace-input'} | Select-Object -First 1
$app=if($main){Get-Process -Id $main.ProcessId}else{$null}
if(-not $app){throw 'The installed v0.11.2 main process is not running.'}
$wechat=Get-Process Weixin,WeChat -ErrorAction SilentlyContinue | Where-Object {$_.MainWindowHandle -ne 0} | Select-Object -First 1
if(-not $wechat){throw 'WeChat main window is unavailable.'}
$null=[InstalledWechatObserver]::ShowWindow($wechat.MainWindowHandle,9)
$null=[InstalledWechatObserver]::SetForegroundWindow($wechat.MainWindowHandle)
function Read-ClipboardSnapshot {
    for($attempt=0;$attempt -lt 15;$attempt++){
        $value=[InstalledWechatObserver]::ClipboardSnapshot()
        if($value.Available){return $value}
        Start-Sleep -Milliseconds 100
    }
    return [pscustomobject]@{Available=$false;HadText=$false;Text=''}
}
$previous=Read-ClipboardSnapshot
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('READY observing only the installed app; no second keyboard listener, screenshots, or input')
[IO.File]::WriteAllLines($Report,$lines)
$clock=[Diagnostics.Stopwatch]::StartNew()
$last=0
$expected=if($AnyEnglish){''}else{'Curiosity makes learning easier.'}
while($clock.Elapsed.TotalSeconds -lt $Seconds){
    $state=[InstalledWechatObserver]::Card($app.Id,$expected)
    if($state -ne $last -and $state -ne 0){$lines.Add('INFO card state='+$state+' elapsedMs='+$clock.ElapsedMilliseconds);[IO.File]::WriteAllLines($Report,$lines);$last=$state}
    if($state -eq 2){
        $lines.Add('PASS installed v0.11.2 shows a completed Chinese selection card for the selected English')
        $after=Read-ClipboardSnapshot
        $preserved=$previous.Available -and $after.Available -and $after.HadText -eq $previous.HadText -and $after.Text -eq $previous.Text
        $lines.Add($(if(-not $previous.Available -or -not $after.Available){'BLOCKED'}elseif($preserved){'PASS'}else{'FAIL'})+' installed selection workflow preserves clipboard text')
        [IO.File]::WriteAllLines($Report,$lines)
        if($preserved){exit 0}else{exit 1}
    }
    Start-Sleep -Milliseconds 100
}
$lines.Add('BLOCKED no completed installed-app WeChat selection card observed')
[IO.File]::WriteAllLines($Report,$lines)
exit 3
