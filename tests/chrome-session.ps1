param([string]$Application='artifacts/minimal/DotMic.App.exe', [string]$EvidenceDirectory='artifacts/minimal-review', [int]$ProcessId=0)
# Affected checks for the new extended title bar, responsive surface and themed tray menu.
. "$PSScriptRoot/desktop-session.ps1" -Application $Application -EvidenceDirectory $EvidenceDirectory -InspectOnly -ProcessId $ProcessId
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class StudioChrome {
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
  public static int[] Minimum(IntPtr h) {
    var p=Marshal.AllocHGlobal(40);
    try { for(int i=0;i<10;i++)Marshal.WriteInt32(p,i*4,0);SendMessage(h,0x24,IntPtr.Zero,p);return new[]{Marshal.ReadInt32(p,24),Marshal.ReadInt32(p,28)}; }
    finally {Marshal.FreeHGlobal(p);}
  }
}
'@
try {Invoke-Control (Find-Control -Id CloseButton)} catch {Write-Output 'No initial setup dialog.'}
$mainHandle=$script:main
$gain=Find-Control -Id GainSlider
$range=[Windows.Automation.RangeValuePattern]$gain.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
$dial=$gain.Current.BoundingRectangle
$cx=$dial.Left+$dial.Width/2;$cy=$dial.Top+$dial.Height/2;$radius=$dial.Width/2-6
function Dial-Point([double]$Angle){$radians=$Angle*[Math]::PI/180;Move-Pointer ($cx+[Math]::Sin($radians)*$radius) ($cy-[Math]::Cos($radians)*$radius);Start-Sleep -Milliseconds 40}
function Dial-Press([double]$Angle){Dial-Point $Angle;[DesktopCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 80}
function Dial-Release {[DesktopCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 80}
function Gain-Is([double]$Value,[string]$Context){if([Math]::Abs($range.Current.Value-$Value) -gt .51){throw "$Context expected $Value actual $($range.Current.Value)"}}
$range.SetValue(-12);[DesktopCheck]::SetForegroundWindow($mainHandle)|Out-Null
Dial-Press -135
$last=-12
for($angle=-120;$angle -le 135;$angle+=15){
  Dial-Point $angle
  $current=$range.Current.Value
  if($current -lt $last){throw "Clockwise dial inverted at angle $angle"}
  Gain-Is (-12+($angle+135)*48/270) "Clockwise sweep at $angle"
  $last=$current
}
Dial-Point 165;Gain-Is 36 'Upper stop'
Dial-Point 150;Gain-Is (36-15*48/270) 'Immediate reverse at upper stop'
Dial-Release
$range.SetValue(0);Dial-Press 179;Dial-Point 181;Gain-Is .5 'Clockwise across bottom';Dial-Point 175;Gain-Is -.5 'Reverse across bottom';Dial-Release
$range.SetValue(2.4);Dial-Press 0;Dial-Release
if([Math]::Abs($range.Current.Value-2.4) -gt .01){throw 'Stationary click changed gain'}
$range.SetValue(0)
Dial-Press 0;Move-Pointer $cx $cy;Start-Sleep -Milliseconds 60;Dial-Point 180;Gain-Is 0 'Centre crossing';Dial-Release
Write-Output 'PASS actual circular mouse drag: 270-degree sweep, same-direction tracking, lower-half angle wrap, end-stop reversal, stationary click and centre crossing.'
$before=[DesktopCheck+Rect]::new();[DesktopCheck]::GetWindowRect($mainHandle,[ref]$before)|Out-Null
$min=[StudioChrome]::Minimum($mainHandle)
if($min[0] -lt 320 -or $min[1] -lt 432){throw 'Missing native minimum tracking bounds'}
# Actual pointer drag on the dedicated noninteractive title region.
[DesktopCheck]::SetForegroundWindow($mainHandle)|Out-Null
Move-Pointer ($before.Left+80) ($before.Top+20)
[DesktopCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 100
Move-Pointer ($before.Left+120) ($before.Top+40)
Start-Sleep -Milliseconds 200;[DesktopCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
$after=[DesktopCheck+Rect]::new();[DesktopCheck]::GetWindowRect($mainHandle,[ref]$after)|Out-Null
if([Math]::Abs($after.Left-$before.Left) -lt 20){throw 'Custom title drag region did not move the real window'}
[StudioChrome]::SetWindowPos($mainHandle,[IntPtr]::Zero,$before.Left,$before.Top,0,0,0x15)|Out-Null
$frame=[Windows.Automation.AutomationElement]::FromHandle($mainHandle)
$windowPattern=[Windows.Automation.WindowPattern]$frame.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
$windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized);Start-Sleep -Milliseconds 250
if(![StudioChrome]::IsIconic($mainHandle)){throw 'Native minimize failed'}
$windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal);Start-Sleep -Milliseconds 400
if([StudioChrome]::IsIconic($mainHandle)){throw 'Native restore failed'}
Capture (Join-Path $EvidenceDirectory 'main.png')
# Narrow supported window: primary operations must remain visible and nonoverlapping.
[StudioChrome]::SetWindowPos($mainHandle,[IntPtr]::Zero,0,0,320,432,0x16)|Out-Null
Start-Sleep -Milliseconds 300
foreach($id in @('GainSlider','GainReadout','GateDetails','GateToggle','NcToggle')){
  $control=Find-Control -Id $id
  if($control.Current.IsOffscreen -or $control.Current.BoundingRectangle.Width -le 0){throw "Narrow layout hides $id"}
}
Capture (Join-Path $EvidenceDirectory 'narrow.png')
[StudioChrome]::SetWindowPos($mainHandle,[IntPtr]::Zero,0,0,($before.Right-$before.Left),($before.Bottom-$before.Top),0x16)|Out-Null
Invoke-Control (Find-Control -Id GateDetails)
Capture (Join-Path $EvidenceDirectory 'gate.png')
Invoke-Control (Find-Control -Id CloseButton)
Open-Menu
Capture (Join-Path $EvidenceDirectory 'settings.png')
Close-Menu
Write-Output 'PASS native title drag, minimum bounds, minimize/restore, narrow layout and themed main dialogs.'
# Version4 tray right-click reaches the same styled settings menu inside the real compact HWND.
[DesktopCheck]::PostMessage($mainHandle,0x8020,[IntPtr]::Zero,[IntPtr]0x7b)|Out-Null
Start-Sleep -Milliseconds 500
$frames=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id))
$fly=$frames | Where-Object {$_.Current.Name -eq 'DOT MIC · 小型'} | Select-Object -First 1
if(!$fly -or $fly.Current.IsOffscreen){throw 'Styled tray menu did not open compact window'}
$openMain=$fly.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'メインを開く'))
if(!$openMain){throw 'Styled tray right-click menu missing'}
$flyHandle=[IntPtr]$fly.Current.NativeWindowHandle
$script:main=$flyHandle
Capture (Join-Path $EvidenceDirectory 'tray-menu.png')
[Windows.Forms.SendKeys]::SendWait('{ESC}');Start-Sleep -Milliseconds 300
foreach($id in @('GainSlider','GainReadout','GateDetails','GateToggle','NcToggle')){
  $control=$fly.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
  if(!$control -or $control.Current.IsOffscreen){throw "Compact layout hides $id"}
  $bounds=$control.Current.BoundingRectangle;$outer=$fly.Current.BoundingRectangle
  if($bounds.Left -lt $outer.Left -or $bounds.Right -gt $outer.Right -or $bounds.Bottom -gt $outer.Bottom){throw "Compact layout clips $id"}
}
Capture (Join-Path $EvidenceDirectory 'compact.png')
# The new tray menu provides the main-window action, not just decorative entries.
Open-Menu
Invoke-Control (Find-Control -Name 'メインを開く')
$script:main=$mainHandle
if(![DesktopCheck]::IsWindowVisible($mainHandle)){throw 'Tray open-main action failed'}
Start-Sleep -Milliseconds 400
$mainMenu=$frame.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'MenuButton'))
Invoke-Control $mainMenu
$mainExit=$frame.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'終了'))
if(!$mainExit){throw 'Main exit menu missing after tray handoff'}
Invoke-Control $mainExit
if(!$script:app.WaitForExit(5000) -or $script:app.ExitCode -ne 0){throw 'Chrome session did not exit normally'}
Write-Output 'PASS themed tray right-click, compact bounds, main-window action and normal Exit. Current desktop/DPI only; notification delivered to owned HWND.'
