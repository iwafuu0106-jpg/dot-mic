param([Parameter(Mandatory=$true)][string]$TraceDirectory, [switch]$PrintEvents)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$TraceDirectory=[IO.Path]::GetFullPath($TraceDirectory)
[xml]$doc=Get-Content (Join-Path $TraceDirectory 'events.xml') -Raw
$ns=New-Object Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('e','http://schemas.microsoft.com/win/2004/08/events/event')
$rows=@(foreach($event in $doc.SelectNodes('//e:Event',$ns)){
  $system=$event.SelectSingleNode('e:System',$ns)
  $provider=$system.SelectSingleNode('e:Provider',$ns)
  $id=$system.SelectSingleNode('e:EventID',$ns).InnerText
  if($provider.GetAttribute('Guid') -ine '{289603df-b7ed-4e5c-9aa4-a70c89c887fc}' -and
     !($provider.GetAttribute('Guid') -ieq '{ae4bd3be-f36f-45b6-8d21-bdd6fb832853}' -and [int]$id -in @(50,123,124,125,126,127,128,129,130))){continue}
  $data=[ordered]@{}
  foreach($item in $event.SelectNodes('e:EventData/e:Data',$ns)){$data[$item.GetAttribute('Name')]=$item.InnerText.Trim()}
  $execution=$system.SelectSingleNode('e:Execution',$ns)
  $correlation=$system.SelectSingleNode('e:Correlation',$ns)
  $render=$event.SelectSingleNode('e:RenderingInfo/e:Task',$ns)
  [pscustomobject]@{
    Provider=$provider.GetAttribute('Name');Id=[int]$id
    Time=$system.SelectSingleNode('e:TimeCreated',$ns).GetAttribute('SystemTime')
    Pid=if($execution){$execution.GetAttribute('ProcessID')}else{$null}
    Activity=if($correlation){$correlation.GetAttribute('ActivityID')}else{$null}
    RelatedActivity=if($correlation){$correlation.GetAttribute('RelatedActivityID')}else{$null}
    Task=if($render){$render.InnerText}else{$null};Data=$data
  }
})
$path=Join-Path $TraceDirectory 'relevant-events.json'
$rows | ConvertTo-Json -Depth 8 | Set-Content $path -Encoding UTF8
if($PrintEvents){$rows | ConvertTo-Json -Depth 8}
else {
  [pscustomobject]@{Path=$path;RelevantEvents=$rows.Count;
    Initializations=@($rows | Where-Object {$_.Provider -eq 'DotMic.CaptureApoGate' -and $_.Task -eq 'Initialize'}).Count;
    Summaries=@($rows | Where-Object {$_.Provider -eq 'DotMic.CaptureApoGate' -and $_.Task -eq 'StreamSummary'}).Count} |
    ConvertTo-Json
}
Write-Host 'Metadata extraction only. No automatic gate PASS or RAW verdict; correlate endpoint/client/activity, summaries and trace loss.'
