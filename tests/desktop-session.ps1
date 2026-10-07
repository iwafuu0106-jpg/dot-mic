param([string]$Application='artifacts/distribution-check/DotMic.App.exe', [switch]$InspectOnly, [int]$ProcessId=0,
  [ValidateSet('All','Pointer','Entry','Details','Menu','Motion','Measure','Flyout')][string]$StartAt='All', [switch]$SkipHiddenMeasurement,
  [string]$EvidenceDirectory='artifacts')
# One local desktop session. Real WinUI controls, no audio recording and no speaker fallback.
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms,System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DesktopCheck {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("user32.dll")] public static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,int data,UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
  private delegate bool EnumProc(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc,IntPtr p);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int n);
  public static IntPtr MainWindow(int pid) {
    IntPtr found=IntPtr.Zero;
    EnumWindows((h,p)=> {uint id;GetWindowThreadProcessId(h,out id);if(id==(uint)pid){var s=new System.Text.StringBuilder(100);GetWindowText(h,s,100);if(s.ToString()=="DOT MIC"){found=h;return false;}}return true;},IntPtr.Zero);
    return found;
  }
}
'@
[DesktopCheck]::SetProcessDPIAware() | Out-Null
$exe=(Resolve-Path $Application).Path
$script:app=if($ProcessId){Get-Process -Id $ProcessId}else{Start-Process $exe -PassThru}
$script:app.Handle | Out-Null # Retain the OS process handle so Get-Process can read ExitCode after Exit.
$script:main=[IntPtr]::Zero
for($attempt=0;$attempt -lt 50;$attempt++){
  Start-Sleep -Milliseconds 100;$script:app.Refresh();if($script:app.HasExited){throw "Distribution startup failed: $($script:app.ExitCode)"}
  $script:main=[DesktopCheck]::MainWindow($script:app.Id);if($script:main -ne [IntPtr]::Zero){break}
}
Start-Sleep -Milliseconds 500
if($script:main -eq [IntPtr]::Zero){throw 'No actual WinUI window; inspect application last-error.log, not PASS'}
function Find-Control([string]$Id='', [string]$Name='', $Kind=$null) {
  $property=if($Id){[Windows.Automation.AutomationElement]::AutomationIdProperty}else{[Windows.Automation.AutomationElement]::NameProperty}
  $value=if($Id){$Id}else{$Name}; $condition=[Windows.Automation.PropertyCondition]::new($property,$value)
  if($Kind){$condition=[Windows.Automation.AndCondition]::new($condition,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$Kind))}
  $pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id)
  $frames=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition)
  foreach($frame in $frames){$element=$frame.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition);if($element){return $element}}
  throw "Actual control missing: $Id $Name"
}
function Invoke-Control($Control){([Windows.Automation.InvokePattern]$Control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke();Start-Sleep -Milliseconds 250}
function Toggle-Control($Control){([Windows.Automation.TogglePattern]$Control.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle();Start-Sleep -Milliseconds 150}
function Keys([string]$Text){[DesktopCheck]::SetForegroundWindow($script:main)|Out-Null;[Windows.Forms.SendKeys]::SendWait($Text);Start-Sleep -Milliseconds 100}
function Open-Menu {Invoke-Control (Find-Control -Id MenuButton)}
function Close-Menu {Keys '{ESC}'}
function Move-Pointer([double]$X,[double]$Y){
  # SetCursorPos alone changes position but does not deliver WinUI pointer movement here.
  $screen=[Windows.Forms.SystemInformation]::VirtualScreen
  [DesktopCheck]::mouse_event(0xC001,[uint32](($X-$screen.Left)*65535/($screen.Width-1)),[uint32](($Y-$screen.Top)*65535/($screen.Height-1)),0,[UIntPtr]::Zero)
}
function Capture([string]$Path){
  [DesktopCheck]::SetForegroundWindow($script:main)|Out-Null;Start-Sleep -Milliseconds 250
  $rect=[DesktopCheck+Rect]::new();[DesktopCheck]::GetWindowRect($script:main,[ref]$rect)|Out-Null
  $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top);$g=[Drawing.Graphics]::FromImage($bitmap)
  # Screen copying is black on this desktop; capture only the owned HWND's real rendering.
  $dc=$g.GetHdc();$ok=[DesktopCheck]::PrintWindow($script:main,$dc,2);$g.ReleaseHdc($dc)
  if(!$ok){$g.Dispose();$bitmap.Dispose();throw 'Owned HWND rendering capture unavailable'}
  $bitmap.Save((Join-Path $pwd $Path));$g.Dispose();$bitmap.Dispose()
}
$root=[Windows.Automation.AutomationElement]::FromHandle($script:main)
if($InspectOnly){
  $root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    [pscustomobject]@{id=$_.Current.AutomationId;name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName;enabled=$_.Current.IsEnabled}
  } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory 'UIA-inspection.json') -Encoding UTF8
  Capture (Join-Path $EvidenceDirectory 'ui-setup.png')
  Write-Output "Actual distribution process $($script:app.Id), HWND $script:main, DPI $([DesktopCheck]::GetDpiForWindow($script:main)). Inspect-only, not PASS."
  return
}
# Historical full desktop/UI measurement session. For current CAPX binding use
# Apo/ui-check.ps1; do not rerun these GUI measurements as APO audio evidence.
try {Invoke-Control (Find-Control -Id CloseButton)} catch {Write-Output 'Setup close button not present; proceeding with the existing main surface.'}
$gain=Find-Control -Id GainSlider
$range=[Windows.Automation.RangeValuePattern]$gain.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
if($StartAt -eq 'All'){
$range.SetValue(6);Start-Sleep -Milliseconds 100;if([Math]::Abs($range.Current.Value-6) -gt .01){throw 'Connected gain failed'}
$gain.SetFocus();Keys '{HOME}';if([Math]::Abs($range.Current.Value) -gt .01){throw 'Gain Home reset failed'}
Keys '{UP}';if([Math]::Abs($range.Current.Value-.5) -gt .01){throw 'Gain arrow failed'}
Keys '+{UP}';if([Math]::Abs($range.Current.Value-.6) -gt .01){throw 'Gain fine arrow failed'}
Keys '{HOME}'
$rect=$gain.Current.BoundingRectangle
Move-Pointer ($rect.Left+20) ($rect.Top+35)
[DesktopCheck]::mouse_event(0x800,0,0,120,[UIntPtr]::Zero);Start-Sleep -Milliseconds 100
if([Math]::Abs($range.Current.Value-1) -gt .01){throw 'Wheel gain failed'}
Write-Output 'PASS gain UIA, Home, arrows, Shift fine step and actual wheel.'
}
$rect=$gain.Current.BoundingRectangle
if($StartAt -in @('All','Pointer')){
$beforeDrag=$range.Current.Value
[DesktopCheck]::SetForegroundWindow($script:main)|Out-Null
Move-Pointer ($rect.Left+20) ($rect.Top+35);Start-Sleep -Milliseconds 100
[DesktopCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 100
Move-Pointer ($rect.Left+20) ($rect.Top+15)
Start-Sleep -Milliseconds 100;[DesktopCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 100
if($range.Current.Value -le $beforeDrag){throw 'Actual pointer-captured drag failed'}
Keys '{HOME}'
Write-Output 'PASS actual pointer-captured drag.'
}
if($StartAt -in @('All','Pointer','Entry')){
(Find-Control -Id GainReadout).SetFocus();Keys '{ENTER}'
$editor=Find-Control -Id GainEditor;([Windows.Automation.ValuePattern]$editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('2.4');Keys '{ENTER}'
if([Math]::Abs($range.Current.Value-2.4) -gt .01){throw 'Direct finite gain entry failed'}
Write-Output 'PASS direct finite gain entry.'
}
if($StartAt -in @('All','Pointer','Entry','Details')){
Toggle-Control (Find-Control -Id GateToggle);Toggle-Control (Find-Control -Id GateToggle)
Toggle-Control (Find-Control -Id NcToggle);Toggle-Control (Find-Control -Id NcToggle)
Invoke-Control (Find-Control -Id GateDetails);Invoke-Control (Find-Control -Id CloseButton)
Write-Output 'PASS gate/NC request toggles and gate details; no audio endpoints.'
}
if($StartAt -in @('All','Pointer','Entry','Details','Menu')){
Open-Menu
$startup=Find-Control -Name 'サインイン時に起動';Toggle-Control $startup
$registered=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name DotMic).DotMic
if($registered -ne ('"'+$exe+'" --startup')){throw 'Own startup readback failed'}
Toggle-Control $startup
if((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties.Name -contains 'DotMic'){throw 'Own startup removal failed'}
Close-Menu
Write-Output 'PASS own HKCU startup registration, exact readback and removal.'
}
if($StartAt -in @('All','Pointer','Entry','Details','Menu','Motion')){
foreach($modeName in @('Reduced','Off','Full')){
  Open-Menu;$mode=Find-Control -Name 'モーション';$mode.SetFocus()
  ([Windows.Automation.ExpandCollapsePattern]$mode.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand();Start-Sleep -Milliseconds 150
  $item=Find-Control -Name $modeName -Kind ([Windows.Automation.ControlType]::ListItem)
  ([Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select();Start-Sleep -Milliseconds 150;Close-Menu
}
Write-Output 'PASS Reduced, Off and Full selections.'
Capture (Join-Path $EvidenceDirectory 'ui-main.png')
}
if($StartAt -ne 'Flyout'){
$csv=@()
if($SkipHiddenMeasurement){$csv=@(Import-Csv (Join-Path $EvidenceDirectory 'gui-measurements.csv') | Where-Object {$_.condition -eq 'GUI_hidden_no_endpoints'})}
# Only available conditions: GUI hidden/Full with endpoints absent. Not reported as NC ON audio performance.
[DesktopCheck]::PostMessage($script:main,0x10,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null;Start-Sleep -Milliseconds 400
if([DesktopCheck]::IsWindowVisible($script:main)){throw 'Resident close failed to hide'}
$conditions=if($SkipHiddenMeasurement){@('GUI_Full_operations_no_endpoints')}else{@('GUI_hidden_no_endpoints','GUI_Full_operations_no_endpoints')}
foreach($condition in $conditions){
  if($condition -like '*Full*'){ $activate=[DesktopCheck]::RegisterWindowMessage('DotMic.Activate.3.2.09BAF257');[DesktopCheck]::PostMessage($script:main,$activate,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null;Start-Sleep -Milliseconds 400 }
  $script:app.Refresh();$cpu=$script:app.TotalProcessorTime.TotalSeconds;$clock=[Diagnostics.Stopwatch]::StartNew()
  for($i=0;$i -lt 60;$i++){
    if($condition -like '*Full*'){
      $range.SetValue(($i%12)*.5)
      if($i%12 -eq 0){
        [DesktopCheck]::SetForegroundWindow($script:main)|Out-Null
        Move-Pointer ($rect.Left+20) ($rect.Top+40);Start-Sleep -Milliseconds 50;[DesktopCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 50
        for($j=1;$j -le 10;$j++){Move-Pointer ($rect.Left+20) ($rect.Top+40-$j*2);Start-Sleep -Milliseconds 20}
        [DesktopCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero);Open-Menu;Close-Menu
      }
    }
    Start-Sleep -Seconds 1
  }
  $script:app.Refresh();$seconds=$clock.Elapsed.TotalSeconds
  $csv += [pscustomobject]@{scope='WinUI_no_endpoints';condition=$condition;seconds=$seconds;core_equivalent=($script:app.TotalProcessorTime.TotalSeconds-$cpu)/$seconds;private_MiB=$script:app.PrivateMemorySize64/1MB;dpi=[DesktopCheck]::GetDpiForWindow($script:main)}
}
$csv | Export-Csv (Join-Path $EvidenceDirectory 'gui-measurements.csv') -NoTypeInformation -Encoding UTF8
if($SkipHiddenMeasurement){Write-Output 'PASS resident hide and completed GUI Full measurement; prior hidden measurement reused, not re-executed (no audio endpoints).'}
else{Write-Output 'PASS resident hide and completed GUI hidden/Full measurement (no audio endpoints).'}
}
$range.SetValue(0);Start-Sleep -Milliseconds 600
# Shell version4 select handler opens the real shared small surface; not a simulated window.
[DesktopCheck]::PostMessage($script:main,0x8020,[IntPtr]::Zero,[IntPtr]0x400)|Out-Null;Start-Sleep -Milliseconds 400
$pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:app.Id)
$flyFrame=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition) | Where-Object {$_.Current.Name -eq 'DOT MIC · 小型'} | Select-Object -First 1
if(!$flyFrame){throw 'Actual small HWND missing'}
if($flyFrame.Current.IsOffscreen){throw 'Tray flyout is offscreen'}
$flyGain=$flyFrame.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'GainSlider'))
if(!$flyGain){throw 'Actual small gain control missing'}
$flyRange=[Windows.Automation.RangeValuePattern]$flyGain.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
if([Math]::Abs($flyRange.Current.Value) -gt .01){throw 'Shared small gain did not match main'}
$flyRange.SetValue(.5);Start-Sleep -Milliseconds 150
if([Math]::Abs($range.Current.Value-.5) -gt .01){throw 'Small-to-main shared gain failed'}
$flyRange.SetValue(0);Start-Sleep -Milliseconds 150
$flyHandle=[IntPtr]$flyFrame.Current.NativeWindowHandle;[DesktopCheck]::SetForegroundWindow($flyHandle)|Out-Null;[Windows.Forms.SendKeys]::SendWait('{ESC}');Start-Sleep -Milliseconds 300
if([DesktopCheck]::IsWindowVisible($flyHandle)){throw 'Small surface Esc dismissal failed'}
[DesktopCheck]::PostMessage($script:main,[DesktopCheck]::RegisterWindowMessage('DotMic.Activate.3.2.09BAF257'),[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null;Start-Sleep -Milliseconds 400
[DesktopCheck]::SetForegroundWindow($script:main)|Out-Null
$range.SetValue(1.7);Start-Sleep -Milliseconds 600
Open-Menu;Invoke-Control (Find-Control -Name '終了');if(!$script:app.WaitForExit(5000)){throw 'Explicit Exit did not terminate'}
if($script:app.ExitCode -ne 0){throw "Abnormal Exit code $($script:app.ExitCode)"}
$saved=Get-Content (Join-Path $env:LOCALAPPDATA 'DotMic/settings.json') -Raw|ConvertFrom-Json
if($saved.Gain -ne 1.7 -or $saved.Gate -or $saved.Nc -or $saved.Motion -ne 0){throw 'Settings save failed'}
Write-Output 'PASS shared main/small gain, small Escape and normal Exit; saved nondefault gain 1.7.'
# Restart is part of the same distribution session, after (not alongside) the first process.
$script:app=Start-Process $exe -PassThru;$script:app.Handle | Out-Null;$script:main=[IntPtr]::Zero
for($attempt=0;$attempt -lt 50;$attempt++){
  Start-Sleep -Milliseconds 100;$script:app.Refresh();if($script:app.HasExited){throw 'Persistence restart failed'}
  $script:main=[DesktopCheck]::MainWindow($script:app.Id);if($script:main -ne [IntPtr]::Zero){break}
}
Start-Sleep -Milliseconds 700
Invoke-Control (Find-Control -Id CloseButton)
$restored=Find-Control -Id GainSlider;$restoredRange=[Windows.Automation.RangeValuePattern]$restored.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
if([Math]::Abs($restoredRange.Current.Value-1.7) -gt .01){throw 'Actual persisted gain was not restored'}
foreach($id in @('GateToggle','NcToggle')){if(([Windows.Automation.TogglePattern](Find-Control -Id $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off){throw 'Restored safe toggle mismatch'}}
Open-Menu
$mode=Find-Control -Name 'モーション';$selected=([Windows.Automation.SelectionPattern]$mode.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
if($selected.Count -ne 1 -or $selected[0].Current.Name -ne 'Full'){throw 'Restored Full motion mismatch'}
Close-Menu;$restoredRange.SetValue(0);Start-Sleep -Milliseconds 600
Open-Menu;Invoke-Control (Find-Control -Name '終了');if(!$script:app.WaitForExit(5000) -or $script:app.ExitCode -ne 0){throw 'Final safe Exit failed'}
$saved=Get-Content (Join-Path $env:LOCALAPPDATA 'DotMic/settings.json') -Raw|ConvertFrom-Json
if($saved.Gain -ne 0 -or $saved.Gate -or $saved.Nc -or $saved.Motion -ne 0){throw 'Final safe saved settings failed'}
Write-Output 'PASS UI persistence restart, gain/toggles/Full readback and safe final Exit only. This historical UI session does not establish APO audio acceptance. Tray flyout notification delivered directly to the owned HWND; no Explorer restart or OS sign-in.'
