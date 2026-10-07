param([string]$Application='artifacts/apo-community/candidates/0.4.0-community-06/UI/DotMic.App.exe',[Parameter(Mandatory=$true)][string]$EvidenceDirectory,[string]$ReuseGainEvidence='', [switch]$WetOnly)
$ErrorActionPreference='Stop'
if(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Actual UI must use an ordinary token'}
if(Get-Process DotMic.App -ErrorAction SilentlyContinue){throw 'Existing DOT MIC UI; do not replace or terminate it'}
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$cli=Join-Path (Split-Path $PSScriptRoot) 'artifacts/apo-production/build/Release/DotMic.ApoControl.exe'
function Get-Json([string]$command){$text=@(& $cli $command);if($LASTEXITCODE){throw "CAPX $command failed"};return ($text[0] | ConvertFrom-Json)}
$baseline=Get-Json 'get';if($baseline.MasterBypass -ne 1 -or $baseline.GainDb -ne 0 -or $baseline.GateEnabled -ne 0 -or $baseline.NcEnabled -ne 0){throw 'Safe actual-UI baseline required'}
$capture=Get-Json 'meters';Start-Sleep -Milliseconds 250;$progress=Get-Json 'meters'
if($progress.Running -ne 1 -or $progress.Frames -le $capture.Frames){throw 'Discord capture is not advancing; do not substitute a test stream'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$app=Start-Process (Resolve-Path $Application).Path -PassThru;$app.Handle | Out-Null
function Frames {return [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)))}
function Find([string]$id,[string]$name=''){$condition=if($id){[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)}else{[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)};foreach($frame in (Frames)){$e=$frame.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition);if($e -and !$e.Current.IsOffscreen){return $e}};throw "Owned visible UI control missing: $id $name"}
function Invoke($control){($control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke();Start-Sleep -Milliseconds 150}
function Toggle([string]$id){((Find $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle();Start-Sleep -Milliseconds 450}
$record=[ordered]@{Time=(Get-Date -Format o);OrdinaryToken=$true;UserReportedDiscordStandardAndUnchangedPhysicalInput=$true;NoSyntheticCaptureStarted=$true;RecordedPcm=$false;UiPid=$app.Id;CaptureBefore=$capture;CaptureProgress=$progress;Result='PENDING'}
try {
 $main=$null;for($i=0;$i -lt 60;$i++){Start-Sleep -Milliseconds 100;$main=@(Frames | Where-Object {$_.Current.Name -eq 'DOT MIC'}) | Select-Object -First 1;if($main){break}}
 if(!$main){throw 'Owned Main window missing'};Start-Sleep -Milliseconds 400
 if (!$WetOnly) {
 $range=(Find 'GainSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
 if($ReuseGainEvidence){
  $prior=Get-Content -LiteralPath $ReuseGainEvidence -Raw -Encoding UTF8 | ConvertFrom-Json
  if($prior.GainSettings.MasterBypass -ne 0 -or $prior.GainSettings.GainDb -ne -6 -or [Math]::Abs($prior.Gain.ConsumedGain-[Math]::Pow(10,-6/20.0)) -gt 0.00001){throw 'Prior actual Gain item was not passed'}
  $record.GainEvidenceReused=(Resolve-Path $ReuseGainEvidence).Path;$record.Gain=$prior.Gain;$record.GainSettings=$prior.GainSettings
  $range.SetValue(-6);Start-Sleep -Milliseconds 600 # Current Bypass already ON; do not repeat the passed OFF/Gain item.
 }else{
  $range.SetValue(-6);Start-Sleep -Milliseconds 600
  Invoke (Find 'MenuButton');Toggle 'MasterBypassToggle';Invoke (Find 'MenuButton')
  $gain=Get-Json 'meters';$settings=Get-Json 'get';$record.Gain=$gain;$record.GainSettings=$settings
  if($settings.GainDb -ne -6 -or $settings.MasterBypass -ne 0 -or [Math]::Abs($gain.ConsumedGain-[Math]::Pow(10,-6/20.0)) -gt 0.00001){throw 'Actual UI Gain -6 was not consumed in Discord graph'}
  Invoke (Find 'MenuButton');Toggle 'MasterBypassToggle';Invoke (Find 'MenuButton')
 }
 $bypass=Get-Json 'meters';$record.Bypass=$bypass;$record.BypassSettings=Get-Json 'get'
 if($record.BypassSettings.MasterBypass -ne 1 -or $record.BypassSettings.GainDb -ne -6){throw 'UI Bypass ON was not committed'}
 $record.BypassDiagnosticScope='ConsumedGain is the configured smoothing target, not bypass output amplitude. Live Parameters MasterBypass1 is correlated separately; exact-PCM DSP proof is reused.'
 $range.SetValue(0);Start-Sleep -Milliseconds 600
 } else { $record.Scope='Post-reboot no-PnP NC wet/OFF continuation only; previously accepted Gain/Bypass/Limiter/performance items are not repeated.' }
 Invoke (Find 'MenuButton');Toggle 'MasterBypassToggle';Invoke (Find 'MenuButton')
 $beforeOn=Get-Json 'nc';$record.BeforeNcOn=$beforeOn
 Toggle 'NcToggle'
 $until=(Get-Date).AddSeconds(6);$wet=$null
 # Raw NcState is Off0/Starting1/On2/FaultBypassed3/Stopping4. Managed
 # UI maps rawOn2 to display3; diagnostics must use the raw enum, not UI states.
 while((Get-Date) -lt $until){$wet=Get-Json 'nc';if($wet.Error -ne 0 -or $wet.State -eq 3){throw 'NC fault during legacy live adoption'};if($wet.State -eq 2 -and $wet.Runs -gt $beforeOn.Runs -and $wet.AdoptedFrames -gt $beforeOn.AdoptedFrames){break};Start-Sleep -Milliseconds 100}
 $record.Wet=$wet
 if($wet.State -ne 2 -or $wet.Runs -le $beforeOn.Runs -or $wet.AdoptedFrames -le $beforeOn.AdoptedFrames){throw 'Real-model wet was not adopted'}
 Start-Sleep -Milliseconds 1200;$record.WetSpeechWindow=Get-Json 'nc'
 Toggle 'NcToggle'
 $until=(Get-Date).AddSeconds(3);$off=$null;while((Get-Date) -lt $until){$off=Get-Json 'nc';if($off.State -eq 0){break};Start-Sleep -Milliseconds 50}
 if($off.State -ne 0){throw 'Effective OFF/idle acknowledgement not reached'}
 $offFrames=Get-Json 'meters';Start-Sleep -Milliseconds 800;$offAfter=Get-Json 'nc';$offFramesAfter=Get-Json 'meters'
 foreach($counter in @('Runs','Stft','Istft','StateUpdates','Jobs')){if($off.$counter -ne $offAfter.$counter){throw "NC OFF counter advanced: $counter"}}
 if($offFramesAfter.Frames -le $offFrames.Frames){throw 'Capture did not continue during OFF freeze'}
 $record.OffBefore=$off;$record.OffAfter=$offAfter;$record.OffFramesBefore=$offFrames;$record.OffFramesAfter=$offFramesAfter
 $record.Result=if($WetOnly){'PASS_POSTBOOT_NO_PNP_DISCORD_UI_WET_OFF_STOP'}else{'PASS_ACTUAL_COMMUNITY_DISCORD_UI_GAIN_BYPASS_WET_OFF_STOP'};$record.UserListeningConfirmation='PENDING'
}catch{$record.Result='FAIL';$record.Error=($_ | Out-String);throw}finally{
 foreach($pair in @(@('NcEnabled',0),@('GateEnabled',0),@('GainDb',0),@('MasterBypass',1))){& $cli set $pair[0] $pair[1] | Out-Null}
 $record.Final=Get-Json 'get'
 try{Invoke (Find 'MenuButton');Invoke (Find '' '終了');$app.WaitForExit(10000) | Out-Null;$record.NormalUiExit=$app.HasExited}catch{$record.NormalUiExit=$false;$record.ExitError=($_ | Out-String)}
 $record | ConvertTo-Json -Depth 7 | Set-Content "$EvidenceDirectory/result.json" -Encoding UTF8
}
if($WetOnly){Write-Output 'PASS postboot Discord NC wet / five OFF counters frozen; no Gain/Bypass/Limiter/performance rerun; safety settings restored.'}else{Write-Output 'PASS actual unchanged Discord capture / ordinary existing UI Gain & Bypass / real NC wet / five OFF counters frozen; safety settings restored. Listening confirmation still required.'}
