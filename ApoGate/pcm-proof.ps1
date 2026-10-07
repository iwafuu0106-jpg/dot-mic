param(
  [Parameter(Mandatory=$true)][ValidateSet('prepare','attenuate','restore','verify','quarter-on','quarter-off','cleanup-switch')][string]$Action,
  [Parameter(Mandatory=$true)][string]$RunDirectory,
  [string]$BaseRunDirectory='',
  [switch]$PerGraphSwitch,
  [string]$OwnershipRunDirectory=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$root=Split-Path $PSScriptRoot
$run=[IO.Path]::GetFullPath($RunDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-gate/development'))+'\'
if(!$run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Run must be a new child of artifacts/apo-gate/development'}
$probe=Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe'
$kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sign=Join-Path $kit 'bin/10.0.19041.0/x64/signtool.exe'
$statePath=Join-Path $run 'state.json'
function Save($value,$path){$value | ConvertTo-Json -Depth 12 | Set-Content $path -Encoding UTF8}
function Native($exe,[string[]]$arguments,$log){
  $text=& $exe @arguments 2>&1; $code=$LASTEXITCODE
  $text | Out-File $log -Encoding UTF8; $text | Write-Host
  if($code -notin @(0,3010)){throw "Command failed: $code (see $log)"}
  return $code
}
function Snapshot($name){
  $info=New-Object Diagnostics.ProcessStartInfo
  $info.FileName=$probe;$info.Arguments='inspect';$info.UseShellExecute=$false
  $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.CreateNoWindow=$true
  $info.StandardOutputEncoding=[Text.UTF8Encoding]::new();$info.StandardErrorEncoding=[Text.UTF8Encoding]::new()
  $process=[Diagnostics.Process]::Start($info)
  try {
    $output=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(20000)){$process.Kill();$process.WaitForExit();throw 'Owned metadata probe timed out (20s); no audio/app process stopped'}
    $output.Result | Set-Content (Join-Path $run $name) -Encoding UTF8
    if($process.ExitCode -ne 0){throw "Endpoint metadata failed: $($errorText.Result)"}
    $value=$output.Result | ConvertFrom-Json
  } finally {$process.Dispose()}
  Save $value (Join-Path $run $name); return $value
}
function Drivers {
  return @(Get-WindowsDriver -Online -All | Where-Object {
    [IO.Path]::GetFileName($_.OriginalFileName) -in @('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')
  } | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
}
if($Action -eq 'prepare'){
  if(Test-Path $run){throw 'Preserve evidence: choose a new RunDirectory'}
  if(!$BaseRunDirectory){throw 'BaseRunDirectory required for exact signed no-op rollback'}
  $base=[IO.Path]::GetFullPath($BaseRunDirectory)
  if(!$base.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected base run'}
  $old=Get-Content (Join-Path $base 'state.json') -Raw | ConvertFrom-Json
  $receipt=Get-Content (Join-Path $base 'installation.json') -Raw | ConvertFrom-Json
  if($receipt.Result -notlike 'PNPUTIL_ACCEPTED*'){throw 'Base run is not installed'}
  foreach($file in $old.Files){if((Get-FileHash (Join-Path $old.Package $file.Name)).Hash -ne $file.SHA256){throw 'Base payload changed'}}
  New-Item -ItemType Directory $run | Out-Null
  $before=Snapshot 'endpoint-before.json'
  $state=[pscustomobject]@{Created=(Get-Date -Format o);BaseRun=$base;Thumbprint=$old.Thumbprint;
    ExpectedDrivers=$receipt.PublishedDrivers;OriginalNoopHash=(Get-FileHash (Join-Path $old.Package 'DotMic.ApoGate.dll')).Hash;
    Active='PREPARED';Packages=@();PerGraphSwitch=[bool]$PerGraphSwitch;SwitchKey='HKLM:\SOFTWARE\DOT MIC\ApoGate\PCMProof-8F611FC3';SwitchOwned=$false;
    Purpose='TEMPORARY FIXED PCM PROOF ONLY; NOT PRODUCTION'}
  if($PerGraphSwitch){
    if(Test-Path $state.SwitchKey){throw 'Temporary proof key already exists; preserve and investigate'}
    if(!$OwnershipRunDirectory){throw 'OwnershipRunDirectory required after previous component updates'}
    $owner=[IO.Path]::GetFullPath($OwnershipRunDirectory)
    if(!$owner.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected ownership run'}
    $previous=Get-Content (Join-Path $owner 'state.json') -Raw | ConvertFrom-Json
    if($previous.Active -ne 'restore'){throw 'Previous attempt not restored'}
    $state.ExpectedDrivers=$previous.ExpectedDrivers
  }
  foreach($phase in @('attenuate','restore')){
    $package=Join-Path $run $phase; New-Item -ItemType Directory $package | Out-Null
    $version=if($PerGraphSwitch){if($phase -eq 'attenuate'){'0.1.4.0'}else{'0.1.5.0'}}else{if($phase -eq 'attenuate'){'0.1.2.0'}else{'0.1.3.0'}}
    $inf=Get-Content (Join-Path $old.Package 'DotMic.ApoGate.inf') -Raw
    $inf=[regex]::Replace($inf,'(?m)^DriverVer=.*$','DriverVer=10/07/2026,'+$version)
    $inf | Set-Content (Join-Path $package 'DotMic.ApoGate.inf') -Encoding ASCII
    $dll=Join-Path $package 'DotMic.ApoGate.dll'
    if($phase -eq 'attenuate'){
      $build=if($PerGraphSwitch){'build-switch'}else{'build-quarter'}
      Copy-Item (Join-Path $root "artifacts/apo-gate/$build/Release/DotMic.ApoGate.dll") $dll
      $test=if($PerGraphSwitch){'selftest'}else{'selftest-quarter'}
      $null=Native $probe @($test,$dll) (Join-Path $run 'selftest-prepared.txt')
      $null=Native $sign @('sign','/v','/fd','sha256','/s','My','/sha1',$old.Thumbprint,$dll) (Join-Path $run 'sign-quarter.txt')
    }else{
      Copy-Item (Join-Path $old.Package 'DotMic.ApoGate.dll') $dll
      if((Get-FileHash $dll).Hash -ne $state.OriginalNoopHash){throw 'No-op rollback not byte-identical'}
      $null=Native $probe @('selftest',$dll) (Join-Path $run 'selftest-rollback.txt')
    }
    $null=Native "$kit/Tools/10.0.28000.0/x64/infverif.exe" @('/w',(Join-Path $package 'DotMic.ApoGate.inf')) (Join-Path $run "$phase-infverif.txt")
    $null=Native "$kit/bin/10.0.28000.0/x86/Inf2Cat.exe" @("/driver:$package",'/os:10_NI_X64,10_GE_X64','/uselocaltime') (Join-Path $run "$phase-inf2cat.txt")
    $cat=Join-Path $package 'DotMic.ApoGate.cat'
    $null=Native $sign @('sign','/v','/fd','sha256','/s','My','/sha1',$old.Thumbprint,$cat) (Join-Path $run "$phase-sign-cat.txt")
    $state.Packages+=,[pscustomobject]@{Phase=$phase;Version=$version;Path=$package;Files=@(Get-ChildItem $package -File | ForEach-Object {[pscustomobject]@{Name=$_.Name;Hash=(Get-FileHash $_.FullName).Hash}})}
  }
  Save $state $statePath
  Write-Host 'Prepared two component-only packages. No trust/PnP/endpoint changes. Restore uses the exact original signed no-op DLL.'
  exit 0
}
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Explicit administrator terminal required'}
$state=Get-Content $statePath -Raw | ConvertFrom-Json
if($Action -in @('quarter-on','quarter-off','cleanup-switch')){
  if(!$state.PSObject.Properties['PerGraphSwitch'] -or !$state.PerGraphSwitch){throw 'Not a switch proof run'}
  $sessions=(& $probe capture-sessions) -join "`n" | ConvertFrom-Json
  if($LASTEXITCODE -ne 0 -or @($sessions.sessions | Where-Object {$_.state -eq 1}).Count){throw 'Close capture normally: gain is fixed for the entire locked graph'}
  $key=$state.SwitchKey
  if(Test-Path $key){
    $existing=Get-ItemProperty -LiteralPath $key
    if(!$state.SwitchOwned -or $existing.OwnedBy -cne $run){throw 'Temporary key not owned by this run'}
    if(@((Get-Item -LiteralPath $key).GetValueNames() | Where-Object {$_ -notin @('OwnedBy','QuarterGain')}).Count -or @(Get-ChildItem -LiteralPath $key).Count){throw 'Unexpected values/subkeys: preserve and investigate'}
  }elseif($Action -eq 'quarter-on'){
    New-Item $key -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name OwnedBy -Value $run -PropertyType String | Out-Null
    $state.SwitchOwned=$true; Save $state $statePath
  }elseif($state.SwitchOwned){throw 'Owned switch unexpectedly missing'}
  if($Action -eq 'cleanup-switch'){
    if(Test-Path $key){Remove-ItemProperty -LiteralPath $key -Name QuarterGain,OwnedBy -ErrorAction SilentlyContinue;Remove-Item -LiteralPath $key}
    $state.SwitchOwned=$false; Save $state $statePath
  }elseif(Test-Path $key){
    $gain=if($Action -eq 'quarter-on'){1}else{0}
    New-ItemProperty -LiteralPath $key -Name QuarterGain -Value $gain -PropertyType DWord -Force | Out-Null
  }
  Save ([pscustomobject]@{Time=(Get-Date -Format o);Action=$Action;Key=$key;Applies='NEXT_LOCKED_GRAPH_ONLY';PcmDelivery='NOT_PROVEN_BY_FLAG'}) (Join-Path $run ($Action+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json'))
  Write-Host "Temporary switch: $Action. Never read by APOProcess; default/absent is exact no-op."
  exit 0
}
if($Action -eq 'verify'){
  # Read-only verification after PnP's asynchronous endpoint enumeration settles.
  # Never repeat installation solely because the immediate endpoint read raced PnP.
  $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
  $after=Snapshot "endpoint-settled-$stamp.json"
  $before=Get-Content (Join-Path $run 'endpoint-before.json') -Raw | ConvertFrom-Json
  foreach($key in @('connectedDeviceId')){if($before.$key -cne $after.$key){throw "Physical identity changed: $key"}}
  foreach($key in @('friendlyName','stableId','containerId')){if($before.$key.value -cne $after.$key.value){throw "Identity changed: $key"}}
  $child=@(Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -like '*DOTMICGATE*'})
  if($child.Count -ne 1 -or $child[0].Status -ne 'OK'){throw 'Component not present/healthy'}
  $selected=Get-PnpDeviceProperty -InstanceId $child[0].InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath'
  $owned=@(Drivers | Where-Object {$_.Driver -eq $selected.Data})
  if($owned.Count -ne 1){throw 'Selected component package not owned'}
  $phase=if($owned[0].Version -in @('0.1.2.0','0.1.4.0')){'attenuate'}elseif($owned[0].Version -in @('0.1.3.0','0.1.5.0')){'restore'}else{throw 'Unexpected selected component version'}
  if(@($state.ExpectedDrivers | Where-Object {$_.Driver -eq $owned[0].Driver -and $_.OriginalFileName -eq $owned[0].OriginalFileName}).Count -ne 1){throw 'Selected package receipt mismatch'}
  $state.Active=$phase; Save $state $statePath
  Save ([pscustomobject]@{Time=(Get-Date -Format o);Phase=$phase;Driver=$owned[0];SamePhysicalIdentity=$true;
    BeforeRuntimeId=$before.endpointId;CurrentRuntimeId=$after.endpointId;RuntimeIdChanged=($before.endpointId -cne $after.endpointId);
    DiscordInputUnchanged='NOT_YET_CONFIRMED';PcmDelivery='NOT_YET_VERIFIED'}) (Join-Path $run "settled-verification-$stamp.json")
  Write-Host "Verified settled component/physical identity: $phase. Discord input unchanged still requires user confirmation and stream correlation."
  exit 0
}
if($Action -eq 'attenuate' -and $state.Active -ne 'PREPARED'){throw 'Attenuation already attempted; do not repeat'}
if($Action -eq 'restore' -and $state.Active -notin @('attenuate','attenuate_RESTART_REQUIRED','attenuate_REENUMERATION_PENDING','attenuate_FAILED')){throw 'No owned attenuation update to restore'}
$sessions=(& $probe capture-sessions) -join "`n" | ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or @($sessions.sessions | Where-Object {$_.state -eq 1}).Count){throw 'Close active capture normally before component update'}
$current=@(Drivers)
foreach($expected in $state.ExpectedDrivers){
  if(@($current | Where-Object {$_.Driver -eq $expected.Driver -and $_.OriginalFileName -eq $expected.OriginalFileName -and $_.Version -eq $expected.Version}).Count -ne 1){throw 'Installed ownership receipt no longer matches; do not update'}
}
$ext=@($current | Where-Object {$_.ClassName -eq 'Extension'})
if($ext.Count -ne 1){throw 'Ambiguous installed extension'}
$package=@($state.Packages | Where-Object {$_.Phase -eq $Action})[0]
foreach($file in $package.Files){if((Get-FileHash (Join-Path $package.Path $file.Name)).Hash -ne $file.Hash){throw 'Payload changed'}}
foreach($store in @('Root','TrustedPublisher')){if(!(Test-Path "Cert:\LocalMachine\$store\$($state.Thumbprint)")){throw 'Prior development trust missing; do not add new trust automatically'}}
$before=Snapshot "$Action-before.json"
$journal=[pscustomobject]@{Started=(Get-Date -Format o);Action=$Action;BeforeDrivers=$current;AfterDrivers=@();ExitCode=$null;Result='IN_PROGRESS';Error=$null}
Save $journal (Join-Path $run "$Action-update.json")
try {
  foreach($file in @('DotMic.ApoGate.dll','DotMic.ApoGate.cat')){
    $null=Native $sign @('verify','/pa','/v',(Join-Path $package.Path $file)) (Join-Path $run "$Action-verify-$file.txt")
  }
  foreach($file in @('DotMic.ApoGate.inf','DotMic.ApoGate.dll')){
    $null=Native $sign @('verify','/pa','/v','/c',(Join-Path $package.Path 'DotMic.ApoGate.cat'),(Join-Path $package.Path $file)) (Join-Path $run "$Action-member-$file.txt")
  }
  $journal.ExitCode=Native "$env:SystemRoot/System32/pnputil.exe" @('/add-driver',(Join-Path $package.Path 'DotMic.ApoGate.inf'),'/install') (Join-Path $run "$Action-pnputil.txt")
  $state.Active=if($journal.ExitCode -eq 3010){$Action+'_RESTART_REQUIRED'}else{$Action}
  $journal.Result='COMPONENT_UPDATE_ACCEPTED; PCM_DELIVERY_NOT_YET_VERIFIED'
  $after=Snapshot "$Action-after.json"
  foreach($key in @('connectedDeviceId')){if($before.$key -cne $after.$key){throw "Physical identity changed: $key"}}
  foreach($key in @('friendlyName','stableId','containerId')){if($before.$key.value -cne $after.$key.value){throw "Identity changed: $key"}}
  Save ([pscustomobject]@{BeforeRuntimeId=$before.endpointId;AfterRuntimeId=$after.endpointId;
    RuntimeIdChanged=($before.endpointId -cne $after.endpointId);SamePhysicalIdentity=$true;DiscordInputUnchanged='NOT_YET_CONFIRMED'}) (Join-Path $run "$Action-identity.json")
} catch {
  $journal.Error=$_.Exception.Message
  if($journal.ExitCode -in @(0,3010) -and $journal.Error -match 'Target physical microphone not present'){
    $state.Active=$Action+'_REENUMERATION_PENDING'
    $journal.Result='COMPONENT_ACCEPTED; ENDPOINT_REENUMERATION_PENDING; RUN_VERIFY_NOT_REINSTALL'
  }else{
    $state.Active=$Action+'_FAILED'; $journal.Result='FAILED';throw
  }
} finally {
  $journal.AfterDrivers=@(Drivers); $state.ExpectedDrivers=$journal.AfterDrivers
  Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -like '*DOTMICGATE*'} | ForEach-Object {
    [pscustomobject]@{Id=$_.InstanceId;Status=$_.Status;Properties=@(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_DriverVersion','DEVPKEY_Device_ProblemCode' | Select-Object KeyName,Data)}
  } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run "$Action-component.json") -Encoding UTF8
  $journal | Add-Member -NotePropertyName Finished -NotePropertyValue (Get-Date -Format o)
  Save $journal (Join-Path $run "$Action-update.json"); Save $state $statePath
  if(@($journal.AfterDrivers | Where-Object {$_.Driver -eq $ext[0].Driver -and $_.OriginalFileName -eq $ext[0].OriginalFileName}).Count -ne 1){Write-Warning 'Extension ownership changed unexpectedly; STOP'}
}
Write-Host "Component-only update: $($state.Active). Confirm the newly loaded DLL in ETW, not merely PnP acceptance. No Extension install/removal, DriverStore overwrite, forced restart, or trust change."
