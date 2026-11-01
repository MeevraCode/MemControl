# Installs MemControl for the current user: a Desktop shortcut (opens the window) and a Startup shortcut (starts in
# the tray with Windows), then starts it. To remove: delete both "MemControl" shortcuts and choose Exit in its tray menu.
$exe = Join-Path $PSScriptRoot 'MemControl.exe'
if (-not (Test-Path $exe)) { & (Join-Path $PSScriptRoot 'build.cmd') }
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Desktop')) {
  $link = $shell.CreateShortcut((Join-Path $folder 'MemControl.lnk'))
  $link.TargetPath = $exe
  $link.Arguments = $(if ($folder -eq [Environment]::GetFolderPath('Desktop')) { '/show' } else { '' })
  $link.WorkingDirectory = $PSScriptRoot
  $link.Description = 'RAM cleaner and kernel memory leak watch'
  $link.Save()
}
Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot
