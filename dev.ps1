param(
  [ValidateSet('restore','build','check','measure','reference','freeze','package','fixture-build')][string]$Action='build',
  [string]$RunDirectory='artifacts/apo-production/runs/worker-ui-20261007-03',
  [string]$OutputDirectory='artifacts/apo-production/ui-build',
  [string]$DeliveryDirectory='artifacts/apo-production/releases/0.3.4-local'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
  function Native([scriptblock]$Command){& $Command;if($LASTEXITCODE){throw "Command failed: $LASTEXITCODE"}}
  function CMake {
    $cmd=Get-Command cmake -ErrorAction SilentlyContinue;if($cmd){return $cmd.Source}
    $path=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2019/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if(Test-Path $path){return $path};throw 'CMake3.20+ required'
  }
  function Restore([bool]$Fixtures=$false){
    New-Item -ItemType Directory -Force .deps,models,licenses | Out-Null
    $lock=Get-Content dependencies.lock.json -Raw | ConvertFrom-Json
    $archives=@(@('ort',$lock.onnxruntime.url,'onnxruntime-win-x64-1.23.2',$lock.onnxruntime.sha256),@('kissfft',$lock.kissfft.url,'kissfft-131.1.0',$lock.kissfft.sha256))
    if($Fixtures){$archives+=,@('samplerate',$lock.libsamplerate.url,'libsamplerate-0.2.2',$lock.libsamplerate.sha256)}
    foreach($entry in $archives){if(!(Test-Path ".deps/$($entry[2])")){Invoke-WebRequest $entry[1] -OutFile ".deps/$($entry[0]).zip";if((Get-FileHash ".deps/$($entry[0]).zip").Hash.ToLowerInvariant() -ne $entry[3]){throw 'Dependency archive hash mismatch'};Expand-Archive ".deps/$($entry[0]).zip" .deps}}
    if(!(Test-Path .deps/vclibs)){Invoke-WebRequest $lock.visualCppRuntime.url -OutFile .deps/vclibs.zip;if((Get-FileHash .deps/vclibs.zip).Hash.ToLowerInvariant() -ne $lock.visualCppRuntime.sha256){throw 'VC runtime package hash mismatch'};Expand-Archive .deps/vclibs.zip .deps/vclibs}
    if(!(Test-Path models/dpdfnet2_48khz_hr.onnx)){Invoke-WebRequest $lock.model.url -OutFile models/dpdfnet2_48khz_hr.onnx}
    if((Get-FileHash models/dpdfnet2_48khz_hr.onnx).Hash.ToLowerInvariant() -ne $lock.model.sha256){throw 'Model hash mismatch'}
    # Pinned headers only; never calls a gate/proof experiment.
    & ./ApoGate/gate.ps1 restore
    Native {dotnet restore App/DotMic.App.csproj --locked-mode -r win-x64}
  }
  switch($Action){
    'restore' {Restore}
    'build' {
      if(Test-Path $OutputDirectory){throw 'Choose a fresh UI output; old releases are immutable'}
      Restore
      & ./Apo/build.ps1 build
      Native {dotnet publish App/DotMic.App.csproj -c Release -r win-x64 --self-contained true -o $OutputDirectory -p:Platform=x64 -p:RestoreLockedMode=true}
      Copy-Item artifacts/apo-production/build/Release/DotMic.ApoSettings.dll $OutputDirectory
    }
    'check' {& ./Apo/build.ps1 check}
    'measure' {& ./Apo/measure.ps1 -RunDirectory ([IO.Path]::GetFullPath($RunDirectory)) -Seconds 60}
    'fixture-build' {
      Restore $true;$cmake=CMake
      Native {& $cmake -S . -B artifacts/apo-production/offline-build -G 'Visual Studio 16 2019' -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.19041.0' '-DDOTMIC_BUILD_OFFLINE_FIXTURES=ON'}
      Native {& $cmake --build artifacts/apo-production/offline-build --config Release --target DotMic.FixtureCheck --parallel 2}
    }
    'reference' {
      if(!(Test-Path tests/official_streaming.py)){Invoke-WebRequest (Get-Content dependencies.lock.json -Raw | ConvertFrom-Json).officialReference.url -OutFile tests/official_streaming.py}
      Native {& .deps/py/Scripts/python.exe tests/reference.py}
    }
    'freeze' {
      if(!(Test-Path "$OutputDirectory/DotMic.App.exe")){throw 'Build a fresh UI first'}
      $base=(Resolve-Path $OutputDirectory).Path;$hashes="$base.freeze.json"
      if(Test-Path $hashes){throw 'Existing freeze; do not overwrite'}
      Get-ChildItem $base -File -Recurse | Where-Object {$_.Extension -notin @('.log','.pdb')} | ForEach-Object {[pscustomobject]@{Path=$_.FullName.Substring($base.Length+1);Hash=(Get-FileHash $_.FullName).Hash}} | ConvertTo-Json -Depth 4 | Set-Content $hashes -Encoding UTF8
    }
    'package' {& ./Apo/release.ps1 -RunDirectory ([IO.Path]::GetFullPath($RunDirectory)) -UiDirectory ([IO.Path]::GetFullPath($OutputDirectory)) -DeliveryDirectory ([IO.Path]::GetFullPath($DeliveryDirectory))}
  }
}finally{Pop-Location}
