param([string]$OutputDirectory='artifacts/source-candidate-0.5.0-rc.1', [string]$DependencyRoot=(Join-Path (Split-Path $PSScriptRoot) '.deps'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path -LiteralPath $output){throw 'Choose a fresh candidate output; historical artifacts must remain unchanged'}
 $deps=(Resolve-Path -LiteralPath $DependencyRoot).Path
 $accepted=Get-Content Community/Setup/ApoPayload.lock.json -Raw -Encoding UTF8 | ConvertFrom-Json
 foreach($file in $accepted){
  $source=Join-Path "$root/reference-payload" ([IO.Path]::GetFileName($file.Path))
  if((Get-FileHash -LiteralPath $source).Hash -ne $file.Hash){throw 'Accepted dependency/model changed'}
 }
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 Native {& $exe -S Apo -B "$output/apo" -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0' "-DDOTMIC_DEPS_ROOT=$deps"}
 Native {& $exe --build "$output/apo" --config Release --target DotMic.ApoGate DotMic.ApoMultiMicCheck --parallel 2}
 Native {& "$output/apo/Release/DotMic.ApoMultiMicCheck.exe"}
 Native {& $exe -S Community -B "$output/native" -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0' "-DDOTMIC_DEPS_ROOT=$deps"}
 Native {& $exe --build "$output/native" --config Release --target DotMic.Integration DotMic.CommunitySettings DotMic.MetadataCheck --parallel 2}
 Native {& "$output/native/Release/DotMic.MetadataCheck.exe"}
 $base=Join-Path $output 'accepted-dependencies'
 New-Item -ItemType Directory "$base/APO" | Out-Null
 foreach($file in $accepted){Copy-Item -LiteralPath (Join-Path "$root/reference-payload" ([IO.Path]::GetFileName($file.Path))) -Destination (Join-Path $base $file.Path)}
 Copy-Item "$root/licenses" $base -Recurse
 Copy-Item "$root/LICENSE" $base
 [pscustomobject]@{Version='0.4.0-community';Files=$accepted} | ConvertTo-Json -Depth 5 | Set-Content "$base/payload.json" -Encoding UTF8
 $candidate=@($accepted | ForEach-Object {[pscustomobject]@{Path=$_.Path;Hash=if($_.Path -eq 'APO/DotMic.ApoGate.dll'){(Get-FileHash "$output/apo/Release/DotMic.ApoGate.dll").Hash}else{$_.Hash}}})
 $candidate | ConvertTo-Json -Depth 5 | Set-Content "$output/ApoCandidate.lock.json" -Encoding UTF8
 & ./Community/package-install.ps1 -BaseDirectory $base -NativeDirectory "$output/native/Release" -CandidateApo "$output/apo/Release/DotMic.ApoGate.dll" -CandidateLock "$output/ApoCandidate.lock.json" -OutputDirectory "$output/package"
} finally { Pop-Location }
