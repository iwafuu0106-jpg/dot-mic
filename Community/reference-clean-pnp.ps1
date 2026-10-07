param([Parameter(Mandatory=$true)][ValidateSet('remove','remove-development','inspect','restore')][string]$Action,
 [Parameter(Mandatory=$true)][string]$CandidateDirectory,
 [Parameter(Mandatory=$true)][string]$OutputDirectory)
# Reference-PC migration only. Never part of the general Community install recipe.
$ErrorActionPreference='Stop'
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Scoped reference migration requires administrator'}
$candidate=(Resolve-Path -LiteralPath $CandidateDirectory).Path
$root=Split-Path (Split-Path $candidate)
# The caller supplies a normal workspace path; locate the frozen export relative to it.
$reference=Join-Path $root 'reference/installed-packages-0.3.4-local'
if(!(Test-Path "$reference/sha256.json")){throw 'Frozen exact installed-package export missing'}
$hashes=Get-Content "$reference/sha256.json" -Raw -Encoding UTF8 | ConvertFrom-Json
foreach($file in $hashes){if((Get-FileHash -LiteralPath (Join-Path $reference $file.Path)).Hash -ne $file.Hash){throw 'Immutable package recovery export differs'}}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$hardware='USB\VID_3142&PID_00C1&MI_00\9&28ED31B4&0&0000'
$pnp="$env:SystemRoot/System32/pnputil.exe"
function Inventory([string]$label){
 $device=Get-PnpDevice -InstanceId $hardware
 $properties=Get-PnpDeviceProperty -InstanceId $hardware -KeyName 'DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_DriverVersion','DEVPKEY_Device_ContainerId','DEVPKEY_Device_ProblemCode'
 $dot=@(Get-PnpDevice | Where-Object {$_.InstanceId -like '*DOTMICGATE*' -or $_.InstanceId -like '*VEN_DOTM*'} | Select-Object Status,Class,FriendlyName,InstanceId)
 $drivers=@(Get-WindowsDriver -Online -All | Where-Object {$_.ProviderName -eq 'DOT MIC'} | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
 $result=[pscustomobject]@{Time=(Get-Date -Format o);PhysicalDevice=$device | Select-Object Status,Class,FriendlyName,InstanceId;PhysicalProperties=$properties | Select-Object KeyName,Data;DotMicDevices=$dot;DotMicDrivers=$drivers;Services=@(Get-Service Audiosrv,RtkAudioUniversalService,LightingService | Select-Object Name,Status);Boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime;NoNewTrustImported=$true;NoOsRestartExecuted=$true}
 $result | ConvertTo-Json -Depth 8 | Set-Content "$OutputDirectory/$label.json" -Encoding UTF8
 return $result
}
if($Action -eq 'inspect'){Inventory 'pnp-inspect' | ConvertTo-Json -Depth 8;return}
if($Action -eq 'remove-development'){
 $development=Join-Path $root 'reference/installed-development-pnp-20261007'
 $approved=@('oem161.inf','oem162.inf','oem164.inf','oem165.inf','oem171.inf','oem172.inf','oem173.inf','oem174.inf','oem181.inf','oem182.inf','oem184.inf','oem185.inf')
 if(Test-Path "$OutputDirectory/development-removal-intent.json"){throw 'Do not replay development-package removal; inspect existing result'}
 $pins=Get-Content "$development/sha256.json" -Raw -Encoding UTF8 | ConvertFrom-Json
 foreach($file in $pins){if((Get-FileHash -LiteralPath (Join-Path $development $file.Path)).Hash -ne $file.Hash){throw 'Frozen development recovery export differs'}}
 $packages=Get-Content "$development/packages.json" -Raw -Encoding UTF8 | ConvertFrom-Json
 $before=Inventory 'development-pnp-before'
 if($before.DotMicDrivers.Count -ne $approved.Count){throw 'Additional/different DOT MIC package requires separate consent'}
 foreach($name in $approved){
  $current=@($before.DotMicDrivers | Where-Object Driver -eq $name);$saved=@($packages | Where-Object Driver -eq $name)
  if($current.Count -ne 1 -or $saved.Count -ne 1 -or $current[0].ProviderName -ne 'DOT MIC' -or $current[0].ClassName -ne $saved[0].ClassName -or $current[0].Version.ToString() -ne $saved[0].Version){throw 'Approved development-package identity mismatch'}
 }
 [pscustomobject]@{Time=(Get-Date -Format o);UserExplicitlyConsented=$true;Packages=$approved;RecoveryExport=$development;NoVendorDriverRemoval=$true;NoAutomaticReboot=$true} | ConvertTo-Json -Depth 4 | Set-Content "$OutputDirectory/development-removal-intent.json" -Encoding UTF8
 $results=@()
 # Extensions first, so deleted APO components cannot be immediately recreated.
 foreach($name in @('oem162.inf','oem173.inf')+@($approved | Where-Object {$_ -notin @('oem162.inf','oem173.inf')})){
  & $pnp /delete-driver $name /uninstall *> "$OutputDirectory/remove-$name.txt"
  $code=$LASTEXITCODE;$results+=,[pscustomobject]@{Driver=$name;ExitCode=$code;RebootRequired=($code -eq 3010)}
  $results | ConvertTo-Json -Depth 4 | Set-Content "$OutputDirectory/development-removal-results.json" -Encoding UTF8
  if($code -notin @(0,3010)){throw "Approved development-package removal failed: $name ($code)"}
 }
 $after=Inventory 'development-pnp-after'
 if($after.DotMicDrivers.Count -gt 0){throw 'A DOT MIC driver-store package remains; independence pending'}
 $beforeInf=($before.PhysicalProperties | Where-Object KeyName -eq 'DEVPKEY_Device_DriverInfPath').Data
 $afterInf=($after.PhysicalProperties | Where-Object KeyName -eq 'DEVPKEY_Device_DriverInfPath').Data
 if($beforeInf -ne $afterInf){throw 'Physical USB audio driver changed unexpectedly'}
 [pscustomobject]@{Time=(Get-Date -Format o);Status='AllApprovedDotMicPackagesRemoved';DriverStoreDotMicPackageCount=0;PhysicalUsbDriverUnchanged=$true;PhysicalUsbInf=$afterInf;RebootRequired=$true;Reason='Original Extension uninstall returned3010; software device retirement and clean Community proof require manual reboot';NoOsRestartExecuted=$true;LiveCleanCommunityVerification='PENDING'} | ConvertTo-Json | Set-Content "$OutputDirectory/result-development.json" -Encoding UTF8
 Get-Content "$OutputDirectory/result-development.json" -Raw -Encoding UTF8;return
}
if($Action -eq 'remove'){
 if(Test-Path "$OutputDirectory/removal-intent.json"){throw 'Do not replay package removal; inspect existing result first'}
 $before=Inventory 'pnp-before'
 foreach($pair in @(@('oem175.inf','Extension','0.2.2.0'),@('oem186.inf','AudioProcessingObject','0.3.4.0'))){
  $driver=@($before.DotMicDrivers | Where-Object {$_.Driver -eq $pair[0]})
  if($driver.Count -ne 1 -or $driver[0].ClassName -ne $pair[1] -or $driver[0].Version.ToString() -ne $pair[2]){throw 'Expected original DOT MIC package identity changed; do not delete any other driver'}
 }
 $plan="$OutputDirectory/setup-plan-before-removal.json"
 $process=Start-Process -FilePath "$candidate/DotMic.Setup.exe" -ArgumentList @('--diagnose-reference','"{0.0.1.00000000}.{001.{8782E065-66FF-42C6-BB55-FA7FF7E9DE39}}"',('"'+$plan+'"')) -PassThru -Wait
 if($process.ExitCode){throw 'Fresh complete registry/security snapshot failed before package removal'}
 $prepared=Get-Content $plan -Raw -Encoding UTF8 | ConvertFrom-Json
 [pscustomobject]@{Time=(Get-Date -Format o);UserExplicitlyConsented=$true;Packages=@('oem175.inf','oem186.inf');Snapshot=$prepared.Snapshot;RecoveryExport=$reference;NoVendorDriverRemoval=$true;NoForce=$true;NoAutomaticReboot=$true;Status='RemovalPending'} | ConvertTo-Json | Set-Content "$OutputDirectory/removal-intent.json" -Encoding UTF8
 $results=@()
 foreach($driver in @('oem175.inf','oem186.inf')){
  & $pnp /delete-driver $driver /uninstall *> "$OutputDirectory/remove-$driver.txt"
  $code=$LASTEXITCODE;$results+=,[pscustomobject]@{Driver=$driver;ExitCode=$code;RebootRequired=($code -eq 3010)}
  $results | ConvertTo-Json -Depth 4 | Set-Content "$OutputDirectory/removal-results.json" -Encoding UTF8
  if($code -notin @(0,3010)){throw "Scoped driver removal failed: $driver ($code). Preserve snapshots; do not continue silently."}
 }
 $after=Inventory 'pnp-after'
 if($after.DotMicDrivers.Count -gt 0){throw 'A DOT MIC package remains; do not claim no-PnP independence'}
 $beforeInf=($before.PhysicalProperties | Where-Object KeyName -eq 'DEVPKEY_Device_DriverInfPath').Data
 $afterInf=($after.PhysicalProperties | Where-Object KeyName -eq 'DEVPKEY_Device_DriverInfPath').Data
 if($beforeInf -ne $afterInf){throw 'Physical USB audio driver unexpectedly changed; stop for recovery'}
 [pscustomobject]@{Time=(Get-Date -Format o);Status='PackagesRemoved';DotMicPackagesAbsent=$true;PhysicalUsbDriverUnchanged=$true;OriginalSnapshot=$prepared.Snapshot;RebootRequired=(@($results | Where-Object RebootRequired).Count -gt 0);NoOsRestartExecuted=$true;LiveCommunityVerification='PENDING'} | ConvertTo-Json | Set-Content "$OutputDirectory/result.json" -Encoding UTF8
 Get-Content "$OutputDirectory/result.json" -Raw -Encoding UTF8
}else{
 foreach($driver in @('oem175.inf','oem186.inf')){
  $inf=@(Get-ChildItem (Join-Path $reference $driver) -File -Filter '*.inf')
  if($inf.Count -ne 1){throw 'Exact exported INF not unique'}
  & $pnp /add-driver $inf[0].FullName /install *> "$OutputDirectory/restore-$driver.txt"
  if($LASTEXITCODE -notin @(0,3010)){throw 'Exact original package reinstallation failed; do not import new trust or substitute payload'}
 }
 Inventory 'pnp-restored' | ConvertTo-Json -Depth 8
}
