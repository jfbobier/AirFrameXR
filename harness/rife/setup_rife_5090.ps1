$ErrorActionPreference = 'Continue'
$lab = "$env:USERPROFILE\rife_lab"
New-Item -ItemType Directory -Force $lab, "$lab\models" | Out-Null
Set-Location $lab
$log = "$lab\setup.log"
"start $(Get-Date -Format o)" | Out-File $log
& python -m venv "$lab\venv" *>> $log
& "$lab\venv\Scripts\python.exe" -m pip install --upgrade pip *>> $log
& "$lab\venv\Scripts\python.exe" -m pip install torch --index-url https://download.pytorch.org/whl/cu128 *>> $log
& "$lab\venv\Scripts\python.exe" -m pip install numpy *>> $log
Invoke-WebRequest -Uri https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.4.pkl -OutFile "$lab\models\flownet_v4.4.pkl" *>> $log
Invoke-WebRequest -Uri https://codeload.github.com/HolyWu/vs-rife/zip/3488617283db7c428a83ba4a19382285da698b6a -OutFile "$lab\vs-rife.zip" *>> $log
Expand-Archive -Force "$lab\vs-rife.zip" "$lab\src" *>> $log
& "$lab\venv\Scripts\python.exe" -c "import torch,sys; print('TORCH', torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0), sys.version)" *>> $log
"done $(Get-Date -Format o)" | Out-File -Append $log
