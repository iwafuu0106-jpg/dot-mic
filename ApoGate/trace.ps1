param([Parameter(Mandatory=$true)][ValidateSet('start','stop')][string]$Action,
      [string]$TraceDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Probe output is UTF-8, not the PowerShell 5.1 OEM codepage.
[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$root=Split-Path $PSScriptRoot
if(!$TraceDirectory){$TraceDirectory=Join-Path $root 'artifacts/apo-gate/session'}
$TraceDirectory=[IO.Path]::GetFullPath($TraceDirectory)
$statePath=Join-Path $TraceDirectory 'session.json'
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  throw 'An explicitly opened administrator terminal is required for this metadata ETW session. This script never elevates itself.'
}
function Native([scriptblock]$command){& $command;if($LASTEXITCODE -ne 0){throw "Command failed: exit $LASTEXITCODE"}}
if($Action -eq 'start'){
  if(Test-Path $statePath){throw 'Existing session evidence: choose a new TraceDirectory; never overwrite a previous run.'}
  New-Item -ItemType Directory -Force $TraceDirectory | Out-Null
  $probe=Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe'
  $before=& $probe inspect
  if($LASTEXITCODE -ne 0){throw 'Read-only endpoint probe failed'}
  $before -join "`n" | Set-Content (Join-Path $TraceDirectory 'endpoint-before.json') -Encoding UTF8
  $name='DotMicCaptureGate-'+[guid]::NewGuid().ToString('N')
  $etl=Join-Path $TraceDirectory 'capture-metadata.etl'
  $providers=Join-Path $TraceDirectory 'providers.txt'
  @('{289603df-b7ed-4e5c-9aa4-a70c89c887fc} 0xffffffffffffffff 5',
    '{ae4bd3be-f36f-45b6-8d21-bdd6fb832853} 0x1201000000000000 4') |
    Set-Content $providers -Encoding ASCII
  # Provider payloads are COM/stream/timing metadata, not microphone samples.
  # logman accepts one -p; use -pf to enable both providers in the same session.
  # Audio init/stream keywords at Information level; omit high-volume per-block verbose events.
  # Sequential file prevents silent loss of the beginning by circular overwriting.
  Native {logman create trace $name -o $etl -f bin -max 64 -pf $providers -ets}
  try {
    [pscustomobject]@{Name=$name;Started=(Get-Date -Format o);Stopped=$null;Etl=$etl;DiscordPids=@(Get-Process Discord -ErrorAction SilentlyContinue | ForEach-Object Id);AudioDgPids=@(Get-Process audiodg -ErrorAction SilentlyContinue | ForEach-Object Id);SampleRecording=$false} |
      ConvertTo-Json | Set-Content $statePath -Encoding UTF8
  }catch{logman stop $name -ets;throw}
  Write-Host 'Metadata trace started. Use the current physical fifine microphone for a short local Discord mic test, not a third-party call. No selftest or other capture streams during this run. Discord may retain capture after its mic test stops; verify capture-sessions and request normal user exit if a final summary is needed.'
}else{
  if(!(Test-Path $statePath)){throw 'No owned session state to stop'}
  $state=Get-Content $statePath -Raw | ConvertFrom-Json
  if($state.Stopped){throw 'Session is already stopped'}
  if($state.Name -notmatch '^DotMicCaptureGate-[a-f0-9]{32}$'){throw 'Unexpected session identity'}
  Native {logman stop $state.Name -ets}
  $state.Stopped=Get-Date -Format o
  $state | Add-Member -NotePropertyName DiscordPidsAtStop -NotePropertyValue @(Get-Process Discord -ErrorAction SilentlyContinue | ForEach-Object Id)
  $state | Add-Member -NotePropertyName AudioDgPidsAtStop -NotePropertyValue @(Get-Process audiodg -ErrorAction SilentlyContinue | ForEach-Object Id)
  $state | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
  $probe=Join-Path $root 'artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe'
  $after=& $probe inspect
  if($LASTEXITCODE -ne 0){throw 'Post-call endpoint probe failed'}
  $after -join "`n" | Set-Content (Join-Path $TraceDirectory 'endpoint-after.json') -Encoding UTF8
  Native {tracerpt $state.Etl -o (Join-Path $TraceDirectory 'events.xml') -of XML -summary (Join-Path $TraceDirectory 'trace-summary.txt') -y}
  $before=Get-Content (Join-Path $TraceDirectory 'endpoint-before.json') -Raw | ConvertFrom-Json
  $after=Get-Content (Join-Path $TraceDirectory 'endpoint-after.json') -Raw | ConvertFrom-Json
  if($before.endpointId -ne $after.endpointId -or $before.friendlyName.value -cne $after.friendlyName.value){throw 'Endpoint ID or friendly name changed: gate cannot pass'}
  Write-Host 'Endpoint ID/name comparison passed. This is NOT automatic gate PASS: correlate Discord stream/mode and audiodg Initialize/StreamSummary, and check trace loss.'
}
