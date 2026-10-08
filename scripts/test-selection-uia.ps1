param([string]$Output = (Join-Path $PSScriptRoot '../artifacts/selection-uia'))
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="HoverLex owned UIA selection fixture" Width="720" Height="340" WindowStartupLocation="CenterScreen">
<RichTextBox x:Name="editor" IsReadOnly="True" FontSize="24" Margin="20"><FlowDocument><Paragraph>First paragraph.</Paragraph><Paragraph>Curiosity makes learning easier.</Paragraph><Paragraph>Last paragraph.</Paragraph></FlowDocument></RichTextBox>
</Window>
'@
$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$editor = $window.FindName('editor')
$script:step = 0
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(200)
$timer.Add_Tick({
    try {
        if ($script:step -eq 0) {
            $window.Activate() | Out-Null
            $editor.Focus() | Out-Null
            $paragraph = $editor.Document.Blocks.FirstBlock.NextBlock
            $editor.Selection.Select($paragraph.ContentStart,$paragraph.ContentEnd)
            $script:step = 1
            return
        }
        if ($script:step -eq 1) {
            $handle = (New-Object Windows.Interop.WindowInteropHelper $window).Handle
            $point = $editor.PointToScreen((New-Object Windows.Point 40,65))
            $executable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../dist/HoverLex/HoverLex.exe'))
            $script:probe = Start-Process -FilePath $executable -ArgumentList @('--selection-probe',$handle.ToInt64(),[int]$point.X,[int]$point.Y) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Output 'selected.json') -RedirectStandardError (Join-Path $Output 'stderr.txt') -PassThru
            $script:step = 2
            $script:started = Get-Date
            return
        }
        if ($script:probe.HasExited) {
            $actual = Get-Content (Join-Path $Output 'selected.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($actual.Text -eq 'Curiosity makes learning easier.' -and $actual.Id.StartsWith('selected-uia:') -and $editor.Selection.Text -eq $actual.Text) {
                [IO.File]::WriteAllText((Join-Path $Output 'result.txt'),'PASS UIA readonly document returns the selected sentence and preserves its range')
            } else {
                [IO.File]::WriteAllText((Join-Path $Output 'result.txt'),('FAIL UIA selection: ' + ($actual | ConvertTo-Json -Compress)))
            }
            $timer.Stop()
            $window.Close()
        } elseif (((Get-Date) - $script:started).TotalSeconds -gt 8) {
            Stop-Process -Id $script:probe.Id
            throw 'Owned selection probe timed out'
        }
    } catch {
        [IO.File]::WriteAllText((Join-Path $Output 'result.txt'),('FAIL ' + $_.Exception.Message))
        $timer.Stop()
        $window.Close()
    }
})
$window.Add_ContentRendered({ $timer.Start() })
$window.ShowDialog() | Out-Null
Get-Content (Join-Path $Output 'result.txt')
