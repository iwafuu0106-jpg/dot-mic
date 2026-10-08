param([string]$OutputDirectory='artifacts/source-0.5.0-rc.1')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path -LiteralPath $output){throw 'Choose a fresh source candidate output'}
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName='git'
 $start.Arguments='ls-files --cached --others --exclude-standard -z'
 $start.WorkingDirectory=$root
 $start.UseShellExecute=$false
 $start.RedirectStandardOutput=$true
 $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
 $process=[Diagnostics.Process]::Start($start)
 try {
  $names=$process.StandardOutput.ReadToEnd()
  $process.WaitForExit()
  if($process.ExitCode -ne 0){throw 'Cannot enumerate source files'}
  $files=@($names.Split([char]0) | Where-Object {$_ -ne ''})
 } finally {$process.Dispose()}
 $source=Join-Path $output 'DOT MIC 0.5.0-rc.1 source'
 New-Item -ItemType Directory $source | Out-Null
 $entries=@()
 foreach($relative in $files | Sort-Object -Unique){
  if($relative -match '(^|/)(\.git|\.deps|bin|obj|artifacts)(/|$)' -or $relative -match '\.(pfx|p12|key)$'){throw "Unsafe source inclusion: $relative"}
  $origin=Join-Path $root $relative
  if(!(Test-Path -LiteralPath $origin -PathType Leaf)){throw "Tracked source missing: $relative"}
  $destination=Join-Path $source $relative
  New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
  Copy-Item -LiteralPath $origin -Destination $destination
  $entries += [pscustomobject]@{Path=$relative;Sha256=(Get-FileHash -LiteralPath $destination).Hash}
 }
 $commit=& git rev-parse HEAD
 if($LASTEXITCODE -ne 0){throw 'Cannot record base commit'}
 $manifest=[pscustomobject]@{Version='0.5.0-rc.1';CandidateOnly=$true;BaseCommit=$commit;WorkingTreeChangesIncluded=$true;Published=$false;Files=$entries}
 $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $source 'candidate-source.json') -Encoding UTF8
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=Join-Path $output 'DOT MIC 0.5.0-rc.1 source.zip'
 [IO.Compression.ZipFile]::CreateFromDirectory($source,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
 [pscustomobject]@{Version='0.5.0-rc.1';ZipName=[IO.Path]::GetFileName($zip);Sha256=(Get-FileHash -LiteralPath $zip).Hash;Files=$entries.Count;BaseCommit=$commit;Committed=$false;Published=$false} | ConvertTo-Json | Set-Content (Join-Path $output 'source-result.json') -Encoding UTF8
 Get-Content (Join-Path $output 'source-result.json') -Raw
} finally { Pop-Location }
