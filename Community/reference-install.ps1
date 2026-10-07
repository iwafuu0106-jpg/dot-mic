param([Parameter(Mandatory=$true)][string]$CandidateDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$DependentServicesConsented,[switch]$VerifyFailureOnce,[int]$ExistingSetupPid=0)
$ErrorActionPreference='Stop'
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Scoped reference Setup automation requires administrator'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$exe=Join-Path (Resolve-Path $CandidateDirectory).Path 'DotMic.Setup.exe'
$trace='DotMicCommunityProof-'+(Get-Date -Format yyyyMMdd-HHmmss)
$etl=Join-Path $OutputDirectory 'legacy-install-metadata.etl'
& "$env:SystemRoot/System32/logman.exe" start $trace -p '{75669AAF-E7A1-4DBD-9D1E-790F0D19500B}' 0xffffffffffffffff 5 -o $etl -ets | Out-File "$OutputDirectory/trace-start.txt" -Encoding UTF8
if($LASTEXITCODE){throw 'Owned metadata-only production trace could not start'}
$p=$null
try {
 if($ExistingSetupPid){$p=Get-Process -Id $ExistingSetupPid;if(![string]::Equals($p.MainModule.FileName,$exe,[StringComparison]::OrdinalIgnoreCase)){throw 'Explicitly launched Repair Setup executable differs from expected candidate'}}
 else{$p=if($VerifyFailureOnce){Start-Process -FilePath $exe -ArgumentList '--verify-failure-once' -PassThru}else{Start-Process -FilePath $exe -PassThru}}
 $window=$null;$until=(Get-Date).AddSeconds(30)
 while((Get-Date) -lt $until -and !$window){$windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)));foreach($w in $windows){if($w.Current.Name -like 'DOT MIC Community Setup*'){$window=$w;break}};if(!$window){Start-Sleep -Milliseconds 100}}
 if(!$window){throw 'Owned Setup window did not open; no Install click sent'}
 function Element([string]$id){$element=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)));if(!$element){throw "Own Setup control not found: $id"};return $element}
 function Invoke([string]$id){$control=Element $id;$pattern=$control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern);$pattern.Invoke()}
 function Text(){return ((Element 'ChangeInformation').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value}
 $combo=Element 'MicrophoneList';($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
 $items=$window.FindAll([Windows.Automation.TreeScope]::Descendants,(New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
 $selected=@($items | Where-Object {$_.Current.Name -match '19C93595-428D-5B69-ACE9-D8BC67E41BD4'})
 if($selected.Count -ne 1){throw 'Target ContainerId is not unique in owned Setup selector'}
 ($selected[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 ($combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
 Invoke 'PreviewButton'
 $until=(Get-Date).AddSeconds(45);$plan=''
 while((Get-Date) -lt $until){$plan=Text;if($plan -match 'Snapshot: C:'){break};if($plan -match '(Exception|Transaction.*failed)'){throw $plan};Start-Sleep -Milliseconds 100}
 if($plan -notmatch 'Snapshot: (C:[^\r\n]+)'){throw "Prepared snapshot not displayed: $plan"}
 $snapshot=$Matches[1];$envelope=Get-Content -LiteralPath $snapshot -Raw -Encoding UTF8 | ConvertFrom-Json
 $bytes=[Convert]::FromBase64String($envelope.Data);$sha=[Security.Cryptography.SHA256]::Create();try{$actual=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}
 if($actual -ne $envelope.Sha256){throw 'Prepared snapshot checksum mismatch'}
 $receipt=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
 if($receipt.Target.StableId -cne '{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}' -or $receipt.Target.ContainerId -ine '{19C93595-428D-5B69-ACE9-D8BC67E41BD4}' -or !$receipt.ProtectedAudioWasAlreadyOne){throw 'Snapshot target/protected-audio baseline differs from explicit consent'}
 if($plan -match '追加変更が必要'){throw 'Unexpected additional permission change; ask new explicit consent'}
 if($receipt.AudioDependents.Count -gt 0){
  if(!$DependentServicesConsented){throw 'Additional dependent-service interruption requires explicit user consent'}
  foreach($service in $receipt.AudioDependents){if($service.Name -notin @('RtkAudioUniversalService','LightingService')){throw 'Unexpected additional dependent service; ask new explicit consent'}}
 }
 $plan | Set-Content "$OutputDirectory/setup-displayed-plan.txt" -Encoding UTF8
 foreach($id in @('ReplacementConsent','ProtectedAudioConsent','AudioRestartConsent')){$toggle=(Element $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$toggle.Toggle()}}
 if($receipt.AudioDependents.Count -gt 0){$toggle=(Element 'DependentServiceConsent').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern);if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$toggle.Toggle()}}
 if(((Element 'AdvancedConsent').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off){throw 'Advanced permission is not part of this consent'}
 [pscustomobject]@{Time=(Get-Date -Format o);Snapshot=$snapshot;UserExplicitlyConsented=$true;TargetStableId=$receipt.Target.StableId;AudioInterruptionAuthorized=$true;DependentServicesAuthorized=[bool]$DependentServicesConsented;DependentServices=$receipt.AudioDependents;AdvancedPermissionAuthorized=$false;OsRebootAuthorized=$false} | ConvertTo-Json -Depth 4 | Set-Content "$OutputDirectory/consent.json" -Encoding UTF8
 Invoke 'InstallButton'
 $until=(Get-Date).AddSeconds(90);$result=''
 while((Get-Date) -lt $until){$result=Text;$envelope=Get-Content -LiteralPath $snapshot -Raw -Encoding UTF8 | ConvertFrom-Json;$current=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.Data)) | ConvertFrom-Json;if($current.Status -in @('Committed','RolledBack','RecoveryRequired')){break};Start-Sleep -Milliseconds 150}
 $result | Set-Content "$OutputDirectory/setup-result.txt" -Encoding UTF8
 Copy-Item -LiteralPath $snapshot -Destination "$OutputDirectory/snapshot-after.json"
 [pscustomobject]@{Time=(Get-Date -Format o);Snapshot=$snapshot;Status=$current.Status;SetupPid=$p.Id;AdvancedKeys=$current.AdvancedKeys;Target=$current.Target;Diagnostics=$current.Diagnostics;PcmRecorded=$false;ForcedTermination=$false;OsRestartExecuted=$false} | ConvertTo-Json -Depth 7 | Set-Content "$OutputDirectory/install-result.json" -Encoding UTF8
 if($VerifyFailureOnce){if($current.Status -ne 'RolledBack' -or ($current.Diagnostics -join ' ') -notmatch '人工Verify失敗'){throw "Artificial Verify failure rollback not proved: $($current.Status)"}}
 elseif($current.Status -ne 'Committed'){throw "Community install did not commit: $($current.Status). Recovery snapshot: $snapshot"}
}finally{
 & "$env:SystemRoot/System32/logman.exe" stop $trace -ets | Out-File "$OutputDirectory/trace-stop.txt" -Encoding UTF8
 if($p -and !$p.HasExited){if($window){try{($window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()}catch{}}} # Normal owned-window close only.
}
