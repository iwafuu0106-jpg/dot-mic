param([Parameter(Mandatory=$true)][ValidateSet('start','stop')][string]$Action,[Parameter(Mandatory=$true)][string]$TraceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator ETW terminal required; ordinary settings CLI/UI is not elevated'}
$directory=[IO.Path]::GetFullPath($TraceDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot) 'artifacts/apo-production/runs'))+'\'
if(!$directory.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Choose a trace directory inside an owned production run'}
$statePath=Join-Path $directory 'session.json'
function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "ETW command failed: $LASTEXITCODE"}}
if($Action -eq 'start'){
  if(Test-Path $statePath){throw 'Existing evidence: choose a fresh trace directory'}
  New-Item -ItemType Directory -Force $directory | Out-Null
  $name='DotMic-Production-'+[guid]::NewGuid().ToString('N')
  $etl=Join-Path $directory 'production-metadata.etl'
  Native {& logman create trace $name -p '{75669AAF-E7A1-4DBD-9D1E-790F0D19500B}' 0xffffffffffffffff 5 -o $etl -f bin -max 32 -nb 16 64 -bs 64 -ets}
  [pscustomobject]@{Name=$name;ETL=$etl;Started=(Get-Date -Format o);Stopped=$false;Scope='Production settings/configuration/summary metadata only; no PCM; no feasibility/RAW re-test'} |
    ConvertTo-Json | Set-Content $statePath -Encoding UTF8
}else{
  $state=Get-Content $statePath -Raw | ConvertFrom-Json
  if($state.Stopped){throw 'Owned trace already stopped'}
  if($state.Name -notmatch '^DotMic-Production-[0-9a-f]{32}$'){throw 'Invalid trace ownership'}
  Native {& logman stop $state.Name -ets}
  $state.Stopped=$true;$state | Add-Member -NotePropertyName Finished -NotePropertyValue (Get-Date -Format o)
  $state | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
  Native {& tracerpt $state.ETL -of XML -o "$directory/events.xml" -summary "$directory/trace-summary.txt" -y}
  [xml]$xml=Get-Content "$directory/events.xml" -Raw
  $ns=New-Object Xml.XmlNamespaceManager($xml.NameTable);$ns.AddNamespace('e','http://schemas.microsoft.com/win/2004/08/events/event')
  $events=@(foreach($event in $xml.SelectNodes('//e:Event',$ns)){
    $system=$event.SelectSingleNode('e:System',$ns);$provider=$system.SelectSingleNode('e:Provider',$ns)
    if($provider.GetAttribute('Name') -ne 'DotMic.ProductionMfx'){continue}
    $data=[ordered]@{};foreach($item in $event.SelectNodes('e:EventData/e:Data',$ns)){$data[$item.GetAttribute('Name')]=$item.InnerText.Trim()}
    $task=$event.SelectSingleNode('e:RenderingInfo/e:Task',$ns);$execution=$system.SelectSingleNode('e:Execution',$ns)
    [pscustomobject]@{Task=if($task){$task.InnerText}else{$null};Time=$system.SelectSingleNode('e:TimeCreated',$ns).GetAttribute('SystemTime');Pid=$execution.GetAttribute('ProcessID');Data=$data}
  })
  $events | ConvertTo-Json -Depth 6 | Set-Content "$directory/production-events.json" -Encoding UTF8
  Write-Host 'Metadata extracted; not an automatic DSP/Discord/performance PASS. Check requested/consumed parameters, counters, user observations and trace loss.'
}
