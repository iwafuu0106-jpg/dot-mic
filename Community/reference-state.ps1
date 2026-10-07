param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Read-only reference diagnostics requires administrator'}
if(Test-Path $OutputDirectory){throw 'Reference snapshot already exists; never overwrite'}
New-Item -ItemType Directory $OutputDirectory | Out-Null
$root=Split-Path $PSScriptRoot
$old=Join-Path $root 'artifacts/apo-production/runs/worker-ui-20261007-03'
$status=Get-Content "$old/status-20261007-143233-3270.json" -Raw | ConvertFrom-Json
$matches=[regex]::Matches($status.RuntimeId,'\{[^{}]+\}')
if($matches.Count -ne 2){throw 'Unexpected runtime endpoint identifier; cannot snapshot an unspecified target'}
$endpointGuid=$matches[$matches.Count-1].Value
if($endpointGuid -notmatch '^\{[0-9a-fA-F-]{36}\}$'){throw 'Capture registry GUID is not identified'}
$clsid='{8F611FC3-1A33-477D-9820-F8B9A11D1030}'
Add-Type @'
using System;using System.ComponentModel;using System.Runtime.InteropServices;
public static class DotMicReferenceSecurity {
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegOpenKeyEx(IntPtr h,string path,uint options,uint rights,out IntPtr key);
 [DllImport("advapi32.dll")] static extern int RegGetKeySecurity(IntPtr h,uint info,byte[] bytes,ref uint length);
 [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr h);
 public static byte[] Read(string path){IntPtr h;int e=RegOpenKeyEx(new IntPtr(unchecked((int)0x80000002)),path,0,0x20119,out h);if(e!=0)throw new Win32Exception(e,path+" error="+e);try{uint n=0;e=RegGetKeySecurity(h,7,null,ref n);if(e!=122)throw new Win32Exception(e,"Security size");byte[] b=new byte[n];e=RegGetKeySecurity(h,7,b,ref n);if(e!=0)throw new Win32Exception(e,"Security read");return b;}finally{RegCloseKey(h);}}
}
'@
$scopes=@("HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\$endpointGuid", "HKLM\SOFTWARE\Classes\CLSID\$clsid", "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\Plugins\AudioProcessingObjects\$clsid")
$records=@();$index=0
foreach($key in $scopes){$provider=$key.Replace('HKLM\','Registry::HKEY_LOCAL_MACHINE\');$exists=Test-Path -LiteralPath $provider;$row=[ordered]@{Key=$key;Exists=$exists};if($exists){$file=Join-Path $OutputDirectory "scope-$index.reg";& "$env:SystemRoot/System32/reg.exe" export $key $file /y | Out-Null;if($LASTEXITCODE){throw "Registry64 read-only export failed: $key"};$sd=[DotMicReferenceSecurity]::Read($key.Substring(5));$security=New-Object Security.AccessControl.RawSecurityDescriptor($sd,0);$row.Owner=$security.Owner.Value;$row.SecurityDescriptorBase64=[Convert]::ToBase64String($sd);$row.SecurityScope='Owner/Group/DACL; no SACL mutation planned'};$records+=,[pscustomobject]$row;$index++}
$base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
try{$audio=$base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Audio');if(!$audio){throw 'Audio configuration key unavailable'};try{$name='DisableProtectedAudioDG';$exists=$audio.GetValueNames() -contains $name;$protected=[pscustomobject]@{Exists=$exists;Kind=if($exists){$audio.GetValueKind($name).ToString()}else{$null};Value=if($exists){$audio.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}else{$null}}}finally{$audio.Dispose()}}finally{$base.Dispose()}
$boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
$secure=Confirm-SecureBootUEFI
$guard=Get-CimInstance -Namespace root/Microsoft/Windows/DeviceGuard -ClassName Win32_DeviceGuard
$bcd=& "$env:SystemRoot/System32/bcdedit.exe" /enum '{current}'
if($LASTEXITCODE){throw 'Boot policy read failed'};$bcd | Set-Content "$OutputDirectory/boot-policy.txt" -Encoding UTF8
$records | ConvertTo-Json -Depth 8 | Set-Content "$OutputDirectory/registry-scopes.json" -Encoding UTF8
[pscustomobject]@{Time=(Get-Date -Format o);Boot=$boot;Windows=[Environment]::OSVersion.Version.ToString();Endpoint=$status.RuntimeId;SecureBoot=$secure;SecurityServicesConfigured=$guard.SecurityServicesConfigured;SecurityServicesRunning=$guard.SecurityServicesRunning;DisableProtectedAudioDG=$protected;RegistryWrites=$false;ServicesRestarted=$false;Scope='Read-only baseline; installation requires additional raw per-value transaction snapshot'} | ConvertTo-Json -Depth 8 | Set-Content "$OutputDirectory/reference-machine.json" -Encoding UTF8
Get-ChildItem $OutputDirectory -File | ForEach-Object {[pscustomobject]@{Path=$_.Name;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}} | ConvertTo-Json -Depth 4 | Set-Content "$OutputDirectory/sha256.json" -Encoding UTF8
Get-ChildItem $OutputDirectory -File | ForEach-Object {$_.IsReadOnly=$true}
Write-Output 'PASS read-only current endpoint/COM/APO/ProtectedAudio/boot reference snapshot'
