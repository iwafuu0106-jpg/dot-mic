param([Parameter(Mandatory=$true)][string]$CandidateDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Scoped Setup lifecycle requires administrator'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$config=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64).OpenSubKey('SOFTWARE\DOT MIC\Community')
if(!$config){throw 'No committed Community integration to uninstall'}
try{$snapshot=$config.GetValue('OriginReceipt')}finally{$config.Dispose()}
function Receipt{$e=Get-Content -LiteralPath $snapshot -Raw -Encoding UTF8 | ConvertFrom-Json;return ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($e.Data)) | ConvertFrom-Json)}
$before=Receipt
if($before.Status -ne 'Committed' -or $before.Target.StableId -cne '{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}' -or !$before.ProtectedAudioWasAlreadyOne){throw 'Original ownership/identity/protection baseline differs from consent'}
$p=Start-Process -FilePath (Join-Path (Resolve-Path $CandidateDirectory).Path 'DotMic.Setup.exe') -PassThru
function Frames{return [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)))}
$window=$null
try{
 for($i=0;$i -lt 100;$i++){Start-Sleep -Milliseconds 100;$window=@(Frames | Where-Object {$_.Current.Name -like 'DOT MIC Community Setup*'}) | Select-Object -First 1;if($window){break}}
 if(!$window){throw 'Owned Setup window missing'}
 function Element([string]$id){$e=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)));if(!$e){throw "Owned Setup control absent: $id"};return $e}
 function Invoke([string]$id){((Element $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()}
 function On([string]$id){$t=(Element $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);if($t.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$t.Toggle()}}
 On 'AudioRestartConsent';Invoke 'UninstallButton';Start-Sleep -Milliseconds 350
 $text=((Element 'ChangeInformation').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value
 if($text -notmatch 'RtkAudioUniversalService' -or $text -notmatch 'LightingService'){throw 'Actual dependent-service targets not displayed before additional consent'}
 $actual=Receipt
 foreach($service in $actual.AudioDependents){if($service.Name -notin @('RtkAudioUniversalService','LightingService')){throw 'New dependent-service target; do not silently authorize'}}
 On 'DependentServiceConsent';Invoke 'UninstallButton'
 $dialog=$null;for($i=0;$i -lt 50;$i++){Start-Sleep -Milliseconds 100;$dialog=@(Frames | Where-Object {$_.Current.Name -eq 'Uninstall'}) | Select-Object -First 1;if($dialog){break}}
 if(!$dialog){if((Receipt).Status -ne 'RolledBack'){throw 'Owned Uninstall confirmation not found and transaction has not completed'}}else{
  $ok=$dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'1')))
  if(!$ok){throw 'Owned Uninstall OK control missing'}
  ($ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
 }
 $until=(Get-Date).AddSeconds(90);$after=$null
 while((Get-Date) -lt $until){$after=Receipt;if($after.Status -in @('RolledBack','RecoveryRequired')){break};$unexpected=@(Frames | Where-Object {$_.Current.ClassName -eq '#32770' -and $_.Current.Name -notin @('Uninstall')});if($unexpected.Count -gt 0){throw 'Unexpected conflict dialog requires explicit user choice; not auto-overwritten'};Start-Sleep -Milliseconds 100}
 [pscustomobject]@{Time=(Get-Date -Format o);Result=if($after.Status -eq 'RolledBack'){'PASS_NORMAL_UNINSTALL_AND_PHYSICAL_CAPTURE'}else{'FAIL'};Snapshot=$snapshot;Status=$after.Status;ProtectedAudioBefore1Retained=$true;AdvancedKeys=$after.AdvancedKeys;Diagnostics=$after.Diagnostics;ServiceStates=@(Get-Service Audiosrv,RtkAudioUniversalService,LightingService | Select-Object Name,Status);PcmRecorded=$false;OsRestartExecuted=$false} | ConvertTo-Json -Depth 7 | Set-Content "$OutputDirectory/result.json" -Encoding UTF8
 Copy-Item -LiteralPath $snapshot -Destination "$OutputDirectory/snapshot-after.json"
 if($after.Status -ne 'RolledBack'){throw "Normal uninstall incomplete: $($after.Status); snapshot=$snapshot"}
}finally{if($window -and !$p.HasExited){try{($window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()}catch{}}}
