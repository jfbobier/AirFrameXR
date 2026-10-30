# Runs perfreport.exe over the fixture sessions harness/perfreport_check.py staged in runs\<rid>\fix, twice each (the output must be byte-identical), output to runs\<rid>\out.txt.
param($rid)
$b = "$env:AIRFRAMEXR_ROOT\out\bench"
$r = "$env:AIRFRAMEXR_ROOT\runs\$rid"
$o = "$r\out.txt"
Set-Content $o ''
foreach ($d in Get-ChildItem "$r\fix" -Directory) {
    Add-Content $o "=== $($d.Name)"
    & "$b\perfreport.exe" $d.FullName --warmup 5 | Add-Content $o
    Copy-Item "$($d.FullName)\report.md" "$($d.FullName)\report1.md" -Force
    & "$b\perfreport.exe" $d.FullName --warmup 5 | Out-Null
    $same = (Get-FileHash "$($d.FullName)\report.md").Hash -eq (Get-FileHash "$($d.FullName)\report1.md").Hash
    Add-Content $o "deterministic: $same"
}
