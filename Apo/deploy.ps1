param(
  [Parameter(Mandatory=$true)][ValidateSet('install','verify','prepare-rollback','rollback','remove')][string]$Action,
  [Parameter(Mandatory=$true)][string]$RunDirectory,
  [string]$BaselineRunDirectory='',
  [string]$OwnershipRunDirectory='',
  [switch]$ComponentOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$root=Split-Path $PSScriptRoot
$run=[IO.Path]::GetFullPath($RunDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-production/runs'))+'\'
if(!$run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Run must be an owned production candidate directory'}
$statePath=Join-Path $run 'state.json'
$state=Get-Content $statePath -Raw | ConvertFrom-Json
$probe=Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe'
$kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sign=Join-Path $kit 'bin/10.0.19041.0/x64/signtool.exe'
function Save($value,$path){$value | ConvertTo-Json -Depth 14 | Set-Content $path -Encoding UTF8}
function Native($exe,[string[]]$arguments,$log){
  $output=& $exe @arguments 2>&1;$code=$LASTEXITCODE;$output | Out-File $log -Encoding UTF8
  if($code -notin @(0,3010)){throw "Command failed: $exe exit $code (see $log)"};return $code
}
function Drivers {
  @(Get-WindowsDriver -Online -All | Where-Object {[IO.Path]::GetFileName($_.OriginalFileName) -in @('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')} |
    Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
}
function SameDriver($a,$b){return $a.Driver -eq $b.Driver -and $a.OriginalFileName -eq $b.OriginalFileName -and $a.ProviderName -eq $b.ProviderName -and $a.ClassName -eq $b.ClassName -and $a.Version -eq $b.Version}
function InstallationDelta($before,$current,$files,$attempted){
  foreach($driver in $before){if(@($current | Where-Object {SameDriver $_ $driver}).Count -ne 1){throw 'Existing owned package changed during install; do not adopt observed state'}}
  $added=@($current | Where-Object {$_.Driver -notin @($before | ForEach-Object Driver)})
  foreach($driver in $added){
    $name=[IO.Path]::GetFileName($driver.OriginalFileName)
    if($name -notin @($attempted)){throw 'Unrequested INF addition during install; ownership not adopted'}
    $file=@($files | Where-Object Name -eq $name)
    if($driver.ProviderName -ne 'DOT MIC' -or $file.Count -ne 1 -or (Get-FileHash $driver.OriginalFileName).Hash -ne $file[0].Hash){throw 'Unexpected concurrent package addition; ownership not adopted'}
    if($name -ieq 'DotMic.ApoGate.inf'){
      foreach($file in @($files | Where-Object {$_.Name -notmatch '\.(inf|cat)$'})){
        $payload=Join-Path (Split-Path $driver.OriginalFileName) $file.Name
        if((Get-FileHash $payload).Hash -ne $file.Hash){throw "Installed component payload is not intended: $($file.Name)"}
      }
    }
  }return $added
}
function ClosedCapture {
  $text=& $probe capture-sessions | Out-String;if($LASTEXITCODE){throw 'Capture session query failed'}
  if(@(($text | ConvertFrom-Json).sessions | Where-Object state -eq 1).Count){throw 'Normally close all fifine capture clients first; never force-terminate them'}
}
function Inspect($path){
  $stable='{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}'
  $text=& $probe inspect-current $stable | Out-String;if($LASTEXITCODE){throw 'Target endpoint unavailable; preserve receipt and diagnose only this deployment'}
  $text | Set-Content $path -Encoding UTF8;return ($text | ConvertFrom-Json)
}
function VerifyPackage($directory,$files){
  foreach($file in $files){if((Get-FileHash (Join-Path $directory $file.Name)).Hash -ne $file.Hash){throw "Owned package changed: $($file.Name)"}}
  $null=Native $sign @('verify','/pa','/v',"$directory/DotMic.ApoGate.dll") "$run/verify-dll.txt"
  foreach($pair in @(@('DotMic.ApoGate.cat','DotMic.ApoGate.dll'),@('DotMic.ApoGate.cat','DotMic.ApoGate.inf'),@('DotMic.Fifine.Extension.cat','DotMic.Fifine.Extension.inf'))){
    $null=Native $sign @('verify','/pa','/v','/c',(Join-Path $directory $pair[0]),(Join-Path $directory $pair[1])) "$run/catalog-$($pair[1]).txt"
  }
  foreach($file in @($files | Where-Object {$_.Name -notin @('DotMic.ApoGate.dll','DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf','DotMic.ApoGate.cat','DotMic.Fifine.Extension.cat')})){
    $null=Native $sign @('verify','/pa','/v','/c',"$directory/DotMic.ApoGate.cat",(Join-Path $directory $file.Name)) "$run/catalog-$($file.Name).txt"
  }
}
if($Action -eq 'prepare-rollback'){
  if(Test-Path "$run/rollback"){throw 'Rollback payload already exists; never overwrite it'}
  if(!$BaselineRunDirectory){throw 'Baseline run required'}
  $base=[IO.Path]::GetFullPath($BaselineRunDirectory)
  $baseAllowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-gate/development'))+'\'
  if(!$base.StartsWith($baseAllowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid owned baseline run'}
  $baseline=Get-Content "$base/state.json" -Raw | ConvertFrom-Json
  if($baseline.CompletedProof -ne 'PASS_TEMPORARY_FIXED_PCM_DELIVERY_ONLY' -or $baseline.SwitchOwned){throw 'Use the completed PCM proof baseline with temporary switch removed'}
  $old=($baseline.Packages | Where-Object Phase -eq 'restore').Path
  $version=[version]$state.Version
  $rollbackVersion=[version]::new($version.Major,$version.Minor,$version.Build+1,0).ToString()
  $directory=Join-Path $run 'rollback';New-Item -ItemType Directory $directory | Out-Null
  Copy-Item "$old/DotMic.ApoGate.dll" $directory
  if((Get-FileHash "$directory/DotMic.ApoGate.dll").Hash -ne $baseline.OriginalNoopHash){throw 'Rollback must be byte-identical original signed no-op'}
  foreach($name in @('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')){
    $text=Get-Content (Join-Path $root "ApoGate/inf/$name") -Raw
    $text=[regex]::Replace($text,'DriverVer=[^\r\n]+',"DriverVer=10/07/2026,$rollbackVersion")
    $text | Set-Content (Join-Path $directory $name) -Encoding ASCII
  }
  $null=Native "$kit/Tools/10.0.28000.0/x64/infverif.exe" @('/w',"$directory/DotMic.ApoGate.inf","$directory/DotMic.Fifine.Extension.inf") "$run/rollback-infverif.txt"
  $null=Native "$kit/bin/10.0.28000.0/x86/Inf2Cat.exe" @("/driver:$directory",'/os:10_NI_X64,10_GE_X64','/uselocaltime') "$run/rollback-inf2cat.txt"
  foreach($cat in Get-ChildItem $directory -Filter '*.cat'){$null=Native $sign @('sign','/v','/fd','sha256','/s','My','/sha1',$state.Thumbprint,$cat.FullName) "$run/rollback-sign-$($cat.Name).txt"}
  $files=@(Get-ChildItem $directory -File | ForEach-Object {[pscustomobject]@{Name=$_.Name;Hash=(Get-FileHash $_.FullName).Hash}})
  VerifyPackage $directory $files
  $state | Add-Member -NotePropertyName BaselineRun -NotePropertyValue $base -Force
  $expected=$baseline.ExpectedDrivers
  if($OwnershipRunDirectory){
    $ownership=[IO.Path]::GetFullPath($OwnershipRunDirectory)
    if(!$ownership.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid production ownership directory'}
    $previous=Get-Content "$ownership/state.json" -Raw | ConvertFrom-Json
    if(!$previous.Installed -or $previous.Thumbprint -ne $state.Thumbprint){throw 'Ownership does not cover installed local production candidate'}
    $expected=$previous.ExpectedDrivers
    $state | Add-Member -NotePropertyName PreviousOwnershipRun -NotePropertyValue $ownership -Force
    $state | Add-Member -NotePropertyName AddedDrivers -NotePropertyValue @($previous.AddedDrivers) -Force
  }
  $state | Add-Member -NotePropertyName ExpectedDrivers -NotePropertyValue $expected -Force
  $state | Add-Member -NotePropertyName Rollback -NotePropertyValue ([pscustomobject]@{Package=$directory;Version=$rollbackVersion;Files=$files;Default='EXACT_ORIGINAL_NOOP';OriginalHash=$baseline.OriginalNoopHash}) -Force
  Save $state $statePath;Write-Host 'Rollback staged; no package installation, security changes or restart.';exit 0
}
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Explicit administrator terminal required for deployment. Ordinary Settings CLI/UI does not require elevation.'}
function OwnedDrivers {
  $current=@(Drivers)
  if($current.Count -ne @($state.ExpectedDrivers).Count){throw 'Driver ownership set changed; inspect before deployment/removal'}
  foreach($driver in $current){
    $match=@($state.ExpectedDrivers | Where-Object {$_.Driver -eq $driver.Driver -and $_.OriginalFileName -eq $driver.OriginalFileName -and $_.Version -eq $driver.Version -and $_.ProviderName -eq $driver.ProviderName})
    if($match.Count -ne 1){throw 'Existing driver is not covered by this run receipt'}
  }return $current
}
if($Action -eq 'verify'){
  $null=OwnedDrivers
  $stamp=Get-Date -Format 'yyyyMMdd-HHmmss-ffff'
  $endpoint=Inspect "$run/endpoint-verify-$stamp.json"
  $before=Get-Content "$run/endpoint-before.json" -Raw | ConvertFrom-Json
  foreach($p in @('friendlyName','stableId','containerId')){if($before.$p.value -cne $endpoint.$p.value){throw "Persistent identity changed: $p"}}
  if($before.connectedDeviceId -cne $endpoint.connectedDeviceId){throw 'Physical interface changed'}
  $loaded=@(Get-Process audiodg -ErrorAction SilentlyContinue | ForEach-Object {$hostId=$_.Id;$_.Modules | Where-Object ModuleName -eq 'DotMic.ApoGate.dll' | ForEach-Object {[pscustomobject]@{HostPid=$hostId;Path=$_.FileName;Hash=(Get-FileHash $_.FileName).Hash}}})
  $selected=@(Get-PnpDevice -PresentOnly | Where-Object InstanceId -like '*DOTMICGATE*' | ForEach-Object {Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_DriverVersion','DEVPKEY_Device_ProblemCode' | Select-Object KeyName,Data})
  $rollbackActive=$state.PSObject.Properties['ActiveTarget'] -and $state.ActiveTarget -eq 'rollback'
  $targetFiles=if($rollbackActive){$state.Rollback.Files}else{$state.Files}
  $targetVersion=if($rollbackActive){$state.Rollback.Version}else{$state.Version}
  $expected=($targetFiles | Where-Object Name -eq 'DotMic.ApoGate.dll').Hash
  $version=($selected | Where-Object KeyName -eq 'DEVPKEY_Device_DriverVersion').Data
  $status=if($version -ne $targetVersion){'SELECTED_VERSION_MISMATCH'}elseif(!$loaded.Count){'NOT_YET_LOADED'}elseif(@($loaded | Where-Object Hash -ne $expected).Count){'OLD_OR_DIFFERENT_MODULE; SAFE_RESTART_REQUIRED'}elseif($rollbackActive){'ORIGINAL_NOOP_ROLLBACK_MODULE_HASH_MATCH'}else{'NEW_MODULE_HASH_MATCH; DSP_AND_SETTINGS_NOT_YET_PROVEN'}
  Save ([pscustomobject]@{Time=(Get-Date -Format o);Boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o');Selected=$selected;Loaded=$loaded;ModuleStatus=$status;RuntimeId=$endpoint.endpointId;NameStableContainerPhysicalSame=$true}) "$run/status-$stamp.json"
  Write-Host $status;exit 0
}
ClosedCapture
foreach($store in @('Root','TrustedPublisher')){if(!(Test-Path "Cert:\LocalMachine\$store\$($state.Thumbprint)")){throw 'Existing certificate trust missing; do not change trust implicitly'}}
$beforeDrivers=@(OwnedDrivers)
if($Action -eq 'remove'){
  if(!$state.PSObject.Properties['AddedDrivers']){throw 'No installed-driver receipt'}
  $receipt=[pscustomobject]@{Time=(Get-Date -Format o);Result='IN_PROGRESS';Removed=@();Error=$null}
  Save $receipt "$run/removal.json"
  try{
    foreach($original in @('DotMic.Fifine.Extension.inf','DotMic.ApoGate.inf')){
      foreach($driver in @($state.AddedDrivers | Where-Object {[IO.Path]::GetFileName($_.OriginalFileName) -eq $original})){
        if($driver.Driver -notmatch '^oem[0-9]+\.inf$'){throw 'Invalid published name'}
        $actual=@(Get-WindowsDriver -Online -All | Where-Object Driver -eq $driver.Driver)
        if(!$actual.Count){$receipt.Removed+=,$driver.Driver;Save $receipt "$run/removal.json";continue}
        if($actual.Count -ne 1 -or !(SameDriver $actual[0] $driver)){throw 'Published name no longer refers to the owned package; deletion refused'}
        $null=Native "$env:SystemRoot/System32/pnputil.exe" @('/delete-driver',$driver.Driver,'/uninstall') "$run/remove-$($driver.Driver).txt"
        $receipt.Removed+=,$driver.Driver;Save $receipt "$run/removal.json"
      }
    }$receipt.Result='OWN_ADDITIONS_REMOVED; BASELINE_NOOP_RETAINED; REBOOT_MAY_BE_REQUIRED'
  }catch{$receipt.Result='PARTIAL_FAILURE';$receipt.Error=$_.Exception.Message;throw}
  finally{
    $observed=@(Drivers);$conflict=$false
    foreach($driver in $observed){if(@($beforeDrivers | Where-Object {SameDriver $_ $driver}).Count -ne 1){$conflict=$true}}
    foreach($driver in $beforeDrivers){if(!@($observed | Where-Object {SameDriver $_ $driver}).Count -and $driver.Driver -notin @($receipt.Removed)){$conflict=$true}}
    if($conflict){$receipt.Result='OWNERSHIP_CONFLICT_STOP';$receipt.Error='Unexpected post-removal driver state, not adopted';Save $observed "$run/unexpected-drivers.json"}
    else {
      $state.ExpectedDrivers=$observed
      $state.AddedDrivers=@($state.AddedDrivers | Where-Object {$_.Driver -in @($observed | ForEach-Object Driver)})
      $state.Installed=@($state.AddedDrivers).Count -gt 0
    }
    Save $state $statePath;Save $receipt "$run/removal.json"
    if($conflict){throw 'Unexpected package changes during removal; see receipt, no ownership adopted'}
  }
  exit 0
}
if($Action -eq 'install'){
  if($state.Installed -or (Test-Path "$run/install.json")){throw 'Install already attempted; verify existing receipt instead of retrying'}
  if(!$state.PSObject.Properties['Rollback']){throw 'Prepare exact-original-noop rollback first'}
  $directory=$state.Package;$files=$state.Files;$receiptPath="$run/install.json"
}else{
  if(!$state.PSObject.Properties['Rollback']){throw 'No prepared owned rollback'}
  if(Test-Path "$run/rollback-install.json"){throw 'Rollback already attempted; inspect receipt'}
  $directory=$state.Rollback.Package;$files=$state.Rollback.Files;$receiptPath="$run/rollback-install.json"
}
VerifyPackage $directory $files
$before=Inspect "$run/endpoint-before-$Action.json"
if($Action -eq 'install'){Copy-Item "$run/endpoint-before-install.json" "$run/endpoint-before.json"}
$receipt=[pscustomobject]@{Time=(Get-Date -Format o);Result='IN_PROGRESS';AttemptedInfs=@();ExitCodes=@();AddedDrivers=@();Error=$null}
Save $receipt $receiptPath
try{
  $infs=if($Action -eq 'install' -and $ComponentOnly){@('DotMic.ApoGate.inf')}else{@('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')}
  foreach($inf in $infs){
    $receipt.AttemptedInfs+=,$inf;Save $receipt $receiptPath
    $code=Native "$env:SystemRoot/System32/pnputil.exe" @('/add-driver',"$directory/$inf",'/install') "$run/$Action-$inf.txt"
    $receipt.ExitCodes+=,[pscustomobject]@{Inf=$inf;Code=$code};Save $receipt $receiptPath
  }
  $receipt.Result='PACKAGES_ACCEPTED; SETTLED_ENDPOINT_AND_NEW_MODULE_LOAD_PENDING'
}catch{$receipt.Result='PARTIAL_FAILURE';$receipt.Error=$_.Exception.Message;throw}
finally{
  $current=@(Drivers)
  try{
    $added=@(InstallationDelta $beforeDrivers $current $files $receipt.AttemptedInfs)
    $receipt.AddedDrivers=$added
    $existingAdded=if($state.PSObject.Properties['AddedDrivers']){@($state.AddedDrivers)}else{@()}
    $state | Add-Member -NotePropertyName AddedDrivers -NotePropertyValue @($existingAdded+$added) -Force
    $state.ExpectedDrivers=$current;$state.Installed=$true
    if($receipt.Result -like 'PACKAGES_ACCEPTED*'){$state | Add-Member -NotePropertyName ActiveTarget -NotePropertyValue $Action -Force}
  }catch{
    $receipt.Result='OWNERSHIP_CONFLICT_STOP';$receipt.Error=$_.Exception.Message;Save $current "$run/unexpected-drivers.json"
    Save $receipt $receiptPath;throw
  }
  Save $state $statePath;Save $receipt $receiptPath
}
Write-Host 'No audio process/service termination, endpoint disabling, identity edits, trust changes or OS restart performed. Verify settled endpoint; confirm safe restart timing if old DLL remains loaded.'
