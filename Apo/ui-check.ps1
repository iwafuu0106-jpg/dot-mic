param([string]$Application='artifacts/apo-production/ui-rc/DotMic.App.exe',[string]$EvidenceDirectory='artifacts/apo-production/runs/worker-ui-20261007-01/ui-check',[switch]$StartupOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class ApoUiCheck {
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string text);
}
'@
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$cli=Join-Path (Split-Path $PSScriptRoot) 'artifacts/apo-production/build/Release/DotMic.ApoControl.exe'
function State { $text=& $cli get;if($LASTEXITCODE){throw 'CAPX read failed'};return ($text | ConvertFrom-Json) }
$before=State
if($before.MasterBypass -ne 1 -or $before.GainDb -ne 0 -or $before.GateEnabled -ne 0 -or $before.NcEnabled -ne 0){throw 'Safe UI test baseline required'}
if(Get-Process DotMic.App -ErrorAction SilentlyContinue){throw 'Existing product process; do not replace/terminate it'}
$app=Start-Process (Resolve-Path $Application).Path -PassThru;$app.Handle | Out-Null
$script:frames=@();$main=$null
function Frames {
 $pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
 return [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition)
}
function Find([string]$id,[string]$name='') {
 $condition=if($id){[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)}else{[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)}
 foreach($frame in (Frames)){if($id -eq 'GainSlider' -and $script:flyOnly -and $frame.Current.Name -notlike '*小型*'){continue};$element=$frame.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition);if($element -and !$element.Current.IsOffscreen){return $element}}
 throw "Actual visible UI control absent: $id $name"
}
function Invoke($element){([Windows.Automation.InvokePattern]$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke();Start-Sleep -Milliseconds 250}
function Toggle($id){$element=Find $id;([Windows.Automation.TogglePattern]$element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle();Start-Sleep -Milliseconds 600}
try {
 for($i=0;$i -lt 60;++$i){Start-Sleep -Milliseconds 100;$main=@(Frames | Where-Object {$_.Current.Name -eq 'DOT MIC'}) | Select-Object -First 1;if($main){break}}
 if(!$main){throw 'No actual main HWND'};Start-Sleep -Milliseconds 500
 $gain=Find 'GainSlider';$range=[Windows.Automation.RangeValuePattern]$gain.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
 if($StartupOnly){
  if(!$gain.Current.IsEnabled){throw 'Unexpected startup modal blocks controls'}
  Invoke (Find 'MenuButton');Invoke (Find '' '終了');$app.WaitForExit(10000) | Out-Null;if(!$app.HasExited){throw 'Normal Exit failed'}
  [pscustomobject]@{Time=(Get-Date -Format o);Result='PASS_TARGETED_STARTUP_NO_SPURIOUS_DIALOG_AND_NORMAL_EXIT';AudioAcceptance=$false} | ConvertTo-Json | Set-Content "$EvidenceDirectory/startup-result.json" -Encoding UTF8
  Write-Output 'PASS updated startup awaits existing CAPX read; no spurious connection dialog; normal Exit. Control-binding evidence reused.';return
 }
 $range.SetValue(-6);Start-Sleep -Milliseconds 650;if((State).GainDb -ne -6){throw 'UI knob did not commit GainDb'}
 Toggle 'GateToggle';if((State).GateEnabled -ne 1){throw 'UI Gate did not write CAPX'};Toggle 'GateToggle'
 Toggle 'NcToggle';if((State).NcEnabled -ne 1){throw 'UI NC did not write CAPX'};Toggle 'NcToggle'
 Invoke (Find 'MenuButton');Toggle 'MasterBypassToggle';if((State).MasterBypass -ne 0){throw 'UI Master Bypass write failed'};Toggle 'MasterBypassToggle'
 # Owned tray notification only, not a physical Explorer click or audio acceptance.
 [ApoUiCheck]::PostMessage([IntPtr]$main.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null;Start-Sleep -Milliseconds 400
 [ApoUiCheck]::PostMessage([IntPtr]$main.Current.NativeWindowHandle,0x8020,[IntPtr]::Zero,[IntPtr]0x400) | Out-Null;Start-Sleep -Milliseconds 500
 $script:flyOnly=$true
 $flyGain=Find 'GainSlider';$flyRange=[Windows.Automation.RangeValuePattern]$flyGain.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
 if($flyRange.Current.Value -ne -6){throw 'Main/tray state not shared'};$flyRange.SetValue(0);Start-Sleep -Milliseconds 650
 if((State).GainDb -ne 0){throw 'Tray Gain did not commit shared CAPX state'}
 Invoke (Find 'MenuButton');Invoke (Find '' '終了');$app.WaitForExit(10000) | Out-Null;if(!$app.HasExited){throw 'Normal app Exit failed'}
 $final=State;if($final.MasterBypass -ne 1 -or $final.GainDb -ne 0 -or $final.GateEnabled -ne 0 -or $final.NcEnabled -ne 0){throw 'Final safety settings differ'}
 [pscustomobject]@{Time=(Get-Date -Format o);Pid=$app.Id;Result='PASS_EXISTING_MAIN_TRAY_CONTROLS_TO_NON_ADMIN_CAPX';Gain=$true;Gate=$true;NC=$true;MasterBypass=$true;MainTrayShared=$true;NormalExit=$true;ActualAudio=$false;RecordedPcm=$false;Final=$final} | ConvertTo-Json -Depth 5 | Set-Content "$EvidenceDirectory/result.json" -Encoding UTF8
 Write-Output 'PASS existing Main/tray shared state, Gain/Gate/NC/Bypass CAPX writes, debounced User commit, safe normal Exit. Audio acceptance remains separate.'
}finally {
 foreach($pair in @(@('NcEnabled',0),@('GateEnabled',0),@('GainDb',0),@('MasterBypass',1))){& $cli set $pair[0] $pair[1] | Out-Null}
 # Never force-kill UI on failure. Leave owned process available for inspection.
}
