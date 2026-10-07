param([Parameter(Mandatory=$true)][string]$RunDirectory,[int]$Seconds=60)
$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator metadata measurement only; UI/settings remain unprivileged'}
$root=Split-Path $PSScriptRoot;$run=[IO.Path]::GetFullPath($RunDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts/apo-production/runs'))+'\'
if(!$run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Owned production run required'}
if($Seconds -lt 60 -or $Seconds -gt 90){throw 'One scoped 60-90 second normal-use measurement only'}
if(Test-Path "$run/performance.json"){throw 'Measurement already exists; do not repeat without a diagnosed reason'}
$state=Get-Content "$run/state.json" -Raw | ConvertFrom-Json
$expected=($state.Files | Where-Object Name -eq 'DotMic.ApoGate.dll').Hash
$hosts=@(Get-Process audiodg | Where-Object {@($_.Modules | Where-Object {$_.ModuleName -eq 'DotMic.ApoGate.dll' -and (Get-FileHash $_.FileName).Hash -eq $expected}).Count})
if($hosts.Count -ne 1){throw 'Current NC payload must actually be loaded in exactly one host; no feasibility rerun'}
$ui=@(Get-Process DotMic.App -ErrorAction SilentlyContinue);if($ui.Count -ne 1){throw 'Existing product UI must be running for UI CPU measurement'}
$sessions=& "$root/artifacts/apo-gate/build/Release/DotMic.ApoProbe.exe" capture-sessions | ConvertFrom-Json
if(!@($sessions.sessions | Where-Object {$_.state -eq 1 -and $_.instanceId -match 'Discord.exe'}).Count){throw 'Normal Discord capture must already be active; never select another microphone or join a call automatically'}
$cli="$root/artifacts/apo-production/build/Release/DotMic.ApoControl.exe"
function Metrics { $text=& $cli performance;if($LASTEXITCODE){throw 'NC metadata unavailable'};return ($text[0] | ConvertFrom-Json) }
$before=Metrics;if($before.State -ne 2 -or $before.Runs -le 0 -or $before.AdoptedFrames -le 0){throw 'Effective NC ON and actual wet adoption are required'}
$hostProcess=$hosts[0];$uiProcess=$ui[0];$hostProcess.Refresh();$uiProcess.Refresh()
$hostCpu=$hostProcess.TotalProcessorTime.TotalSeconds;$uiCpu=$uiProcess.TotalProcessorTime.TotalSeconds
$hostMemory=$hostProcess.PrivateMemorySize64;$uiMemory=$uiProcess.PrivateMemorySize64
$watch=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Seconds $Seconds;$watch.Stop()
$after=Metrics;$hostProcess.Refresh();$uiProcess.Refresh()
$runs=$after.Runs-$before.Runs;$wet=$after.AdoptedFrames-$before.AdoptedFrames;$dry=$after.FallbackFrames-$before.FallbackFrames
[pscustomobject]@{Time=(Get-Date -Format o);Seconds=$watch.Elapsed.TotalSeconds;HostPid=$hostProcess.Id;UiPid=$uiProcess.Id;SignedDllHash=$expected;
    AudiodgCpuCores=($hostProcess.TotalProcessorTime.TotalSeconds-$hostCpu)/$watch.Elapsed.TotalSeconds;
    UiCpuCores=($uiProcess.TotalProcessorTime.TotalSeconds-$uiCpu)/$watch.Elapsed.TotalSeconds;
    HostPrivateBytesBefore=$hostMemory;HostPrivateBytesAfter=$hostProcess.PrivateMemorySize64;UiPrivateBytesBefore=$uiMemory;UiPrivateBytesAfter=$uiProcess.PrivateMemorySize64;
    RunDelta=$runs;WetFrames=$wet;DryFallbackFrames=$dry;WetAdoptionRate=if($wet+$dry){$wet/($wet+$dry)}else{0};
    HopMeanMs=$after.HopMeanMs;HopP99Ms=$after.HopP99Ms;QueueHighWater=$after.HighWater;
    FaultDelta=$after.Faults-$before.Faults;InvalidPacketDelta=$after.InvalidPackets-$before.InvalidPackets;
    CallbackTicksDelta=$after.ProcessTicks-$before.ProcessTicks;CallbackMaximumTicks=$after.ProcessMaximum;
    Before=$before;After=$after;RecordedPcm=$false;
    Scope='One normal Discord capture interval; CPU in logical-core equivalents. Hop mean/p99 and high-water are cumulative for the current graph. InvalidPacket is an APO invalid-input proxy, not an exposed hardware xrun counter. UI CPU is total incremental DOT MIC process CPU, not an animation-only subtraction.'} |
    ConvertTo-Json -Depth 8 | Set-Content "$run/performance.json" -Encoding UTF8
if($after.State -ne 2 -or $runs -le 0 -or $wet -le 0 -or $after.Faults -ne $before.Faults){throw 'Measured NC interval failed; inspect only allocation/tensor/state/FFT/copies/polling/property frequency first'}
Write-Output "Scoped normal-use result saved: $run/performance.json. Listening/call acceptance is separate; no automatic legacy cleanup."
