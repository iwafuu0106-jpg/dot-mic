param([string]$OutputDirectory='artifacts/apo-community/unsigned-production-payload')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$reference=Join-Path $root 'artifacts/apo-community/reference/0.3.4-local/package'
function ImageHash([string]$path){
 $bytes=[IO.File]::ReadAllBytes($path)
 $pe=[BitConverter]::ToInt32($bytes,0x3c);$optional=$pe+24
 if([BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b){throw 'Expected production x64 PE32+ image'}
 $entry=$optional+112+4*8;$offset=[BitConverter]::ToInt32($bytes,$entry);$size=[BitConverter]::ToInt32($bytes,$entry+4)
 if($offset -ne 0 -and ($size -le 0 -or $offset+$size -ne $bytes.Length)){throw 'Signature table is not a bounded end-of-file overlay'}
 for($i=0;$i -lt 4;$i++){$bytes[$optional+64+$i]=0}
 for($i=0;$i -lt 8;$i++){$bytes[$entry+$i]=0}
 if($offset -gt 0){$image=New-Object byte[] $offset;[Array]::Copy($bytes,$image,$offset)}else{$image=$bytes}
 $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash($image))).Replace('-','')}finally{$sha.Dispose()}
}
if(Test-Path $output){
 $saved=Get-Content "$output/provenance.json" -Raw | ConvertFrom-Json
 foreach($file in $saved.Files){if((Get-FileHash -LiteralPath (Join-Path $output $file.Name)).Hash -ne $file.Hash){throw 'Frozen unsigned payload changed'};if($file.SignatureRemoved -and (ImageHash (Join-Path $reference $file.Name)) -ne (ImageHash (Join-Path $output $file.Name))){throw 'Reference executable image changed'}}
 return
}
New-Item -ItemType Directory $output | Out-Null
Get-ChildItem $reference -File | Where-Object {$_.Extension -in @('.dll','.onnx')} | Copy-Item -Destination $output
$signTool=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.19041.0/x64/signtool.exe'
$files=@()
foreach($file in Get-ChildItem $output -File){
 $removed=$file.Name -in @('DotMic.ApoGate.dll','DotMic.Inference.dll')
 if($removed){$file.IsReadOnly=$false;& $signTool remove /s $file.FullName;if($LASTEXITCODE){throw 'Removal of development signature failed'};if((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'NotSigned'){throw 'Community DLL still has an Authenticode signature'};if((ImageHash (Join-Path $reference $file.Name)) -ne (ImageHash $file.FullName)){throw 'Executable image changed beyond signature/checksum metadata'}}
 $files+=,[pscustomobject]@{Name=$file.Name;Hash=(Get-FileHash -LiteralPath $file.FullName).Hash;ReferenceHash=(Get-FileHash -LiteralPath (Join-Path $reference $file.Name)).Hash;SignatureRemoved=$removed;ImageHash=if($removed){ImageHash $file.FullName}else{$null}}
}
[pscustomobject]@{Time=(Get-Date -Format o);Reference='0.3.4-local';CodeChanged=$false;SignatureOnlyDerivation=$true;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$output/provenance.json" -Encoding UTF8
Get-ChildItem $output -File | ForEach-Object {$_.IsReadOnly=$true}
