param([string]$OutputDirectory='artifacts/community-source-build')
# Builds portable Community UI/Setup/control only, without machine integration.
# The accepted DSP/model payload remains independently pinned, never rebuilt here.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Push-Location $root
try {
 function Native([scriptblock]$command){& $command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
 $payload=Join-Path $root 'reference-payload'
 $lock=Get-Content Community/Setup/ApoPayload.lock.json -Raw -Encoding UTF8 | ConvertFrom-Json
 foreach($file in $lock){if((Get-FileHash -LiteralPath (Join-Path $payload ([IO.Path]::GetFileName($file.Path)))).Hash -ne $file.Hash){throw 'Pinned accepted payload hash mismatch'}}
 $output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
 if(Test-Path $output){throw 'Choose a fresh source-build output'}
 & ./ApoGate/gate.ps1 restore
 $cmake=Get-Command cmake -ErrorAction SilentlyContinue
 $exe=if($cmake){$cmake.Source}else{Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
 $build=Join-Path $output 'native-build'
 Native {& $exe -S Community -B $build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0'}
 Native {& $exe --build $build --config Release --parallel 2}
 Native {dotnet publish Community/Setup/DotMic.Setup.csproj -c Release -r win-x64 --self-contained true -o $output}
 Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o "$output/UI" -p:Platform=x64 -p:RestoreLockedMode=true}
 Copy-Item "$build/Release/DotMic.Integration.dll" $output
 Copy-Item "$build/Release/DotMic.Integration.dll" "$output/UI"
 Copy-Item "$build/Release/DotMic.ApoSettings.dll" "$output/UI"
 '{"Version":"0.4.0-community","Integration":"legacy","PaidSigningRequired":false}' | Set-Content "$output/UI/community.json" -Encoding UTF8
 New-Item -ItemType Directory "$output/APO" | Out-Null
 Get-ChildItem $payload -File | Where-Object {$_.Extension -in @('.dll','.onnx')} | Copy-Item -Destination "$output/APO"
 $files=@(Get-ChildItem $output -Recurse -File | Where-Object {$_.FullName -notlike "$build\*"} | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($output.Length+1).Replace('\','/');Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
 [pscustomobject]@{Version='0.4.0-community';ApoHash=($lock | Where-Object Path -eq 'APO/DotMic.ApoGate.dll').Hash;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$output/payload.json" -Encoding UTF8
 Native {dotnet "$output/DotMic.Setup.dll" --fixtures "$output/source-build-fixtures.txt"}
 [pscustomobject]@{Time=(Get-Date -Format o);Output=$output;PinnedDspModelUnchanged=$true;RegistryAssociationWrites=$false;ServicesRestarted=$false;SourceBuildOnly=$true;AcceptedReleaseReplacement=$false} | ConvertTo-Json | Set-Content "$output/build-result.json" -Encoding UTF8
}finally{Pop-Location}
