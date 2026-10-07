param(
  [Parameter(Mandatory=$true)][ValidateSet('prepare','install','diagnose','remove')][string]$Action,
  [Parameter(Mandatory=$true)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot
$run=[IO.Path]::GetFullPath($RunDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-gate/development'))+[IO.Path]::DirectorySeparatorChar
if(!$run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'RunDirectory must be a child of artifacts/apo-gate/development'}
$statePath=Join-Path $run 'state.json'
$receiptPath=Join-Path $run 'installation.json'
$kit=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$signTool=Join-Path $kit 'bin/10.0.19041.0/x64/signtool.exe'
$probe=Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe'
function Save-Json($value,[string]$path){$value | ConvertTo-Json -Depth 12 | Set-Content $path -Encoding UTF8}
function Native([string]$exe,[string[]]$arguments,[string]$log){
  $output=& $exe @arguments 2>&1
  $exit=$LASTEXITCODE
  $output | Out-File $log -Encoding UTF8
  $output | Write-Host
  if($exit -ne 0 -and $exit -ne 3010){throw "$exe failed: $exit (see $log)"}
  return $exit
}
function Assert-Admin {
  $principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator terminal required. No automatic elevation in this script.'}
}
function Inspect([string]$name){
  # The C++ probe emits UTF-8; PowerShell 5.1 otherwise decodes it as the OEM codepage.
  $info=New-Object Diagnostics.ProcessStartInfo
  $info.FileName=$probe;$info.Arguments='inspect';$info.UseShellExecute=$false
  if(Test-Path $statePath){
    $cached=Get-Content $statePath -Raw | ConvertFrom-Json
    if($cached.PSObject.Properties['PersistentIdentity'] -and $cached.PersistentIdentity.Kind -eq 'StableId'){
      # Windows command-line quoting only; never normalize or parse the opaque stable ID.
      $escaped=[regex]::Replace($cached.PersistentIdentity.Value,'(\\*)"','$1$1\"')
      $escaped=[regex]::Replace($escaped,'(\\+)$','$1$1')
      $info.Arguments='inspect-current "'+$escaped+'"'
    }
  }
  $info.RedirectStandardOutput=$true;$info.StandardOutputEncoding=[Text.UTF8Encoding]::new()
  $info.RedirectStandardError=$true;$info.StandardErrorEncoding=[Text.UTF8Encoding]::new()
  $info.CreateNoWindow=$true
  $process=[Diagnostics.Process]::Start($info)
  try {
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(20000)){
      $process.Kill();$process.WaitForExit()
      throw 'Owned endpoint diagnostic timed out after 20 seconds; no audio-service/process was stopped'
    }
    $text=$stdout.Result
    $text | Set-Content (Join-Path $run $name) -Encoding UTF8
    if($process.ExitCode -ne 0){
      $stderr.Result | Set-Content (Join-Path $run ($name+'.error.txt')) -Encoding UTF8
      throw "Endpoint inspection failed: $($stderr.Result)"
    }
    return ($text | ConvertFrom-Json)
  } finally {$process.Dispose()}
}
function Gate-Drivers {
  return @(Get-WindowsDriver -Online -All | Where-Object {
    [IO.Path]::GetFileName($_.OriginalFileName) -in @('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')
  } | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version)
}
function Diagnose {
  Assert-Admin
  $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
  $dir=Join-Path $run "diagnostics-$stamp"
  New-Item -ItemType Directory $dir | Out-Null
  Save-Json ([pscustomobject]@{
    Time=(Get-Date -Format o)
    SecureBoot=(Confirm-SecureBootUEFI)
    HVCI=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity').Enabled
    Drivers=@(Gate-Drivers)
    Devices=@(Get-PnpDevice -PresentOnly | Where-Object {
      $_.InstanceId -like 'USB\VID_3142&PID_00C1&MI_00*' -or $_.InstanceId -like '*DOTMICGATE*' -or $_.InstanceId -like '*MSAPOFXPROXY*'
    } | ForEach-Object {
      [pscustomobject]@{Status=$_.Status;Class=$_.Class;Name=$_.FriendlyName;Id=$_.InstanceId;
        Properties=@(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode','DEVPKEY_Device_DriverInfPath','DEVPKEY_Device_Parent','DEVPKEY_Device_Service' -ErrorAction SilentlyContinue | Select-Object KeyName,Data)}
    })
  }) (Join-Path $dir 'system.json')
  & bcdedit /enum '{current}' | Out-File (Join-Path $dir 'boot.txt') -Encoding UTF8
  # Status only: never collect recovery passwords or encryption key material.
  & manage-bde -status $env:SystemDrive | Out-File (Join-Path $dir 'bitlocker-status.txt') -Encoding UTF8
  Get-Content "$env:SystemRoot/INF/setupapi.dev.log" -Tail 2000 | Out-File (Join-Path $dir 'setupapi-tail.txt') -Encoding UTF8
  $since=(Get-Item $statePath).CreationTime
  # Bound the read: this machine emits unrelated Chrome CI events at high volume.
  # Absence in this recent window is not proof that no earlier rejection occurred.
  $events=@(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-CodeIntegrity/Operational';StartTime=$since} -MaxEvents 1000 -ErrorAction SilentlyContinue | Where-Object {$_.Message -match 'DotMic|audiodg'})
  'Recent 1000 CI events only, filtered to DotMic/audiodg. Not an exhaustive rejection check.' | Set-Content (Join-Path $dir 'code-integrity-scope.txt') -Encoding UTF8
  $events | ForEach-Object {$_.ToXml()} | Out-File (Join-Path $dir 'code-integrity.xml') -Encoding UTF8
  $events | Select-Object TimeCreated,Id,Message | Format-List | Out-File (Join-Path $dir 'code-integrity.txt') -Encoding UTF8
  try {$null=Inspect "endpoint-diagnostics-$stamp.json"}
  catch { $_ | Out-String | Set-Content (Join-Path $dir 'endpoint-inspection-error.txt') -Encoding UTF8 }
  Write-Host "Diagnostics: $dir (no boot/security/audio-service changes)"
}
if($Action -eq 'prepare'){
  if(Test-Path $run){throw 'Choose a new run directory; never overwrite evidence'}
  New-Item -ItemType Directory $run | Out-Null
  $package=Join-Path $run 'package'
  New-Item -ItemType Directory $package | Out-Null
  $before=Inspect 'endpoint-before.json'
  # Runtime endpoint IDs can change at installation. Match the exact observed USB interface instead.
  $physical='{2}.\\?\usb#vid_3142&pid_00c1&mi_00#9&28ed31b4&0&0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\global'
  if($before.connectedDeviceId -ine $physical -or $before.hardwareConnectorSubtype -ne '{DFF21BE1-F70F-11D0-B917-00A0C9223196}'){
    throw 'Unexpected physical microphone/interface'
  }
  Get-PnpDeviceProperty -InstanceId 'USB\VID_3142&PID_00C1&MI_00\9&28ED31B4&0&0000' -KeyName 'DEVPKEY_Device_ContainerId','DEVPKEY_Device_HardwareIds','DEVPKEY_Device_CompatibleIds','DEVPKEY_Device_Parent','DEVPKEY_Device_DriverInfPath' |
    Select-Object KeyName,Type,Data | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run 'physical-pnp-before.json') -Encoding UTF8
  Copy-Item (Join-Path $PSScriptRoot 'inf/*.inf') $package
  $dll=Join-Path $package 'DotMic.ApoGate.dll'
  Copy-Item (Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoGate.dll') $dll
  $null=Native $probe @('selftest',$dll) (Join-Path $run 'selftest-before-signing.txt')
  # Load as a resource/data file only; do not execute DLL entry points.
  Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GateManifest {
  [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
  [DllImport("kernel32", SetLastError=true)] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
  [DllImport("kernel32")] public static extern bool FreeLibrary(IntPtr module);
}
'@
  $module=[GateManifest]::LoadLibraryEx($dll,[IntPtr]::Zero,0x22)
  if($module -eq [IntPtr]::Zero){throw 'Resource-only DLL inspection failed'}
  try {
    foreach($id in 1..3){if([GateManifest]::FindResource($module,[IntPtr]$id,[IntPtr]24) -ne [IntPtr]::Zero){throw 'Embedded manifest is forbidden for this APO'}}
  } finally {[void][GateManifest]::FreeLibrary($module)}
  $cert=New-SelfSignedCertificate -Type CodeSigningCert -Subject ("CN=DOT MIC local APO gate "+[guid]::NewGuid().ToString('N')) -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(30)
  $cer=Join-Path $run 'development.cer'
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  # Only the public certificate is exported. Signing key remains non-exportable in CurrentUser.
  Save-Json ([pscustomobject]@{Created=(Get-Date -Format o);Thumbprint=$cert.Thumbprint;Subject=$cert.Subject;Expires=$cert.NotAfter.ToString('o');Package=$package;Certificate=$cer;ManifestPresent=$false;TrustInstalled=$false;Purpose='LOCAL NO-OP DEVELOPMENT ONLY; NOT PRODUCTION';Files=@()}) $statePath
  $null=Native $signTool @('sign','/v','/fd','sha256','/s','My','/sha1',$cert.Thumbprint,$dll) (Join-Path $run 'sign-dll.txt')
  $null=Native "$kit/Tools/10.0.28000.0/x64/infverif.exe" @('/w',(Join-Path $package 'DotMic.ApoGate.inf'),(Join-Path $package 'DotMic.Fifine.Extension.inf')) (Join-Path $run 'infverif.txt')
  $null=Native "$kit/bin/10.0.28000.0/x86/Inf2Cat.exe" @("/driver:$package",'/os:10_NI_X64,10_GE_X64','/uselocaltime') (Join-Path $run 'inf2cat.txt')
  foreach($cat in Get-ChildItem $package -Filter '*.cat'){
    $null=Native $signTool @('sign','/v','/fd','sha256','/s','My','/sha1',$cert.Thumbprint,$cat.FullName) (Join-Path $run ("sign-"+$cat.BaseName+'.txt'))
  }
  $state=Get-Content $statePath -Raw | ConvertFrom-Json
  $identity=if($before.stableId.vt -eq 31 -and $before.stableId.value){
    [pscustomobject]@{Kind='StableId';Value=$before.stableId.value;ContainerId=$before.containerId.value;ConnectedDeviceId=$before.connectedDeviceId}
  }else{
    [pscustomobject]@{Kind='ContainerAndPhysicalInterface';Value=$before.containerId.value;ContainerId=$before.containerId.value;ConnectedDeviceId=$before.connectedDeviceId}
  }
  $state | Add-Member -NotePropertyName PersistentIdentity -NotePropertyValue $identity
  $state.Files=@(Get-ChildItem $package -File | ForEach-Object {[pscustomobject]@{Name=$_.Name;SHA256=(Get-FileHash $_.FullName).Hash}})
  Save-Json $state $statePath
  Write-Host 'Prepared local self-signed no-op package. NOT trusted/installed. Secure Boot, HVCI and TESTSIGNING unchanged.'
  exit 0
}
Assert-Admin
$state=Get-Content $statePath -Raw | ConvertFrom-Json
if($Action -eq 'diagnose'){Diagnose;exit 0}
if($Action -eq 'install'){
  if(Test-Path $receiptPath){throw 'Installation already attempted; inspect existing receipt first'}
  if(@(Gate-Drivers).Count){throw 'Existing gate driver packages found; preserve and investigate before installing'}
  foreach($file in $state.Files){if((Get-FileHash (Join-Path $state.Package $file.Name)).Hash -ne $file.SHA256){throw "Payload changed: $($file.Name)"}}
  $public=New-Object Security.Cryptography.X509Certificates.X509Certificate2($state.Certificate)
  if($public.Thumbprint -ne $state.Thumbprint){throw 'Certificate thumbprint mismatch'}
  $receipt=[pscustomobject]@{Started=(Get-Date -Format o);Finished=$null;Thumbprint=$state.Thumbprint;AddedTrustStores=@();PublishedDrivers=@();ExitCodes=@();Result='IN_PROGRESS';Error=$null}
  Save-Json $receipt $receiptPath
  try {
    foreach($store in @('Root','TrustedPublisher')){
      if(!(Test-Path "Cert:\LocalMachine\$store\$($state.Thumbprint)")){
        Import-Certificate -FilePath $state.Certificate -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        $receipt.AddedTrustStores+=,$store
        Save-Json $receipt $receiptPath
      }
    }
    foreach($name in @('DotMic.ApoGate.dll','DotMic.ApoGate.cat','DotMic.Fifine.Extension.cat')){
      $null=Native $signTool @('verify','/pa','/v',(Join-Path $state.Package $name)) (Join-Path $run ("verify-$name.txt"))
    }
    foreach($pair in @(@('DotMic.ApoGate.cat','DotMic.ApoGate.inf'),@('DotMic.ApoGate.cat','DotMic.ApoGate.dll'),@('DotMic.Fifine.Extension.cat','DotMic.Fifine.Extension.inf'))){
      $null=Native $signTool @('verify','/pa','/v','/c',(Join-Path $state.Package $pair[0]),(Join-Path $state.Package $pair[1])) (Join-Path $run ("catalog-$($pair[1]).txt"))
    }
    foreach($inf in @('DotMic.ApoGate.inf','DotMic.Fifine.Extension.inf')){
      $code=Native "$env:SystemRoot/System32/pnputil.exe" @('/add-driver',(Join-Path $state.Package $inf),'/install') (Join-Path $run ("install-$inf.txt"))
      $receipt.ExitCodes+=,[pscustomobject]@{Inf=$inf;Code=$code}
      $receipt.PublishedDrivers=@(Gate-Drivers)
      Save-Json $receipt $receiptPath
    }
    $after=Inspect 'endpoint-after-install.json'
    $before=Get-Content (Join-Path $run 'endpoint-before.json') -Raw | ConvertFrom-Json
    if($after.friendlyName.value -cne $before.friendlyName.value -or $after.connectedDeviceId -ine $before.connectedDeviceId -or
       $after.hardwareConnectorSubtype -ne $before.hardwareConnectorSubtype -or $after.containerId.value -ine $before.containerId.value){
      throw 'Physical microphone or original friendly name changed: STOP'
    }
    Save-Json ([pscustomobject]@{BeforeRuntimeId=$before.endpointId;AfterRuntimeId=$after.endpointId;RuntimeIdChanged=($after.endpointId -cne $before.endpointId);BeforeStableId=$before.stableId;AfterStableId=$after.stableId;StableIdSame=($before.stableId.value -ceq $after.stableId.value);SamePhysicalInterface=$true;SameContainer=$true;SameFriendlyName=$true}) (Join-Path $run 'identity-after-install.json')
    $receipt.Result='PNPUTIL_ACCEPTED; APO_LOAD_AND_DISCORD_NOT_YET_VERIFIED'
  }catch{$receipt.Result='FAILED';$receipt.Error=$_.Exception.Message;throw}
  finally {
    $receipt.PublishedDrivers=@(Gate-Drivers)
    $receipt.Finished=Get-Date -Format o
    Save-Json $receipt $receiptPath
    Diagnose
  }
}else{
  $receipt=Get-Content $receiptPath -Raw | ConvertFrom-Json
  if($receipt.Result -eq 'REMOVED'){throw 'Run already removed'}
  Copy-Item $receiptPath (Join-Path $run ('installation-before-remove-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json'))
  # Delete only names saved by this run and still mapped to the exact original gate INF.
  foreach($original in @('DotMic.Fifine.Extension.inf','DotMic.ApoGate.inf')){
    foreach($driver in @($receipt.PublishedDrivers | Where-Object {[IO.Path]::GetFileName($_.OriginalFileName) -eq $original})){
      if($driver.Driver -notmatch '^oem[0-9]+\.inf$'){throw 'Invalid published INF name'}
      $current=@(Gate-Drivers | Where-Object {$_.Driver -eq $driver.Driver -and $_.OriginalFileName -eq $driver.OriginalFileName})
      if($current.Count -ne 1){throw 'Driver receipt no longer matches; do not delete'}
      $null=Native "$env:SystemRoot/System32/pnputil.exe" @('/delete-driver',$driver.Driver,'/uninstall') (Join-Path $run ("remove-$($driver.Driver).txt"))
    }
  }
  if(@(Gate-Drivers).Count){throw 'Gate packages still present; do not remove trust yet'}
  foreach($store in $receipt.AddedTrustStores){Remove-Item "Cert:\LocalMachine\$store\$($state.Thumbprint)" -ErrorAction SilentlyContinue}
  $receipt.Result='REMOVED';$receipt.Finished=Get-Date -Format o
  Save-Json $receipt $receiptPath
  $null=Inspect 'endpoint-after-remove.json'
  Write-Host 'Owned gate packages/trust removed. Physical device, inbox driver and boot settings untouched.'
}
