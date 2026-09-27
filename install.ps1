# Installs the latest Treering for Windows.
#
#   irm https://raw.githubusercontent.com/RedholeTechnologies/Teering/main/install.ps1 | iex
#
# It goes to %LOCALAPPDATA%\Programs\Treering, onto your PATH, and into the Start menu.
# Nothing needs administrator rights. Run it again to update.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$base = 'https://github.com/RedholeTechnologies/Teering/releases/latest/download'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Treering'
$temp = Join-Path ([IO.Path]::GetTempPath()) ("treering-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $temp | Out-Null

try {
    Write-Host 'Downloading Treering...'
    Invoke-WebRequest "$base/treering-win-x64.zip" -OutFile "$temp\treering.zip" -UseBasicParsing
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile "$temp\SHA256SUMS" -UseBasicParsing

    # The download is checked against the checksum published with the release.
    $line = Get-Content "$temp\SHA256SUMS" | Where-Object { $_ -match '\s\*?treering-win-x64\.zip$' } | Select-Object -First 1
    if (-not $line) { throw 'The release has no checksum for the Windows build.' }
    $expected = ($line -split '\s+')[0]
    $actual = (Get-FileHash "$temp\treering.zip" -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { throw 'The download does not match its checksum. Try again.' }

    # A running Treering holds its file open; it is stopped so the new one can take its place.
    Get-Process treering -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Force $dir | Out-Null
    Expand-Archive "$temp\treering.zip" -DestinationPath $dir -Force

    $path = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ((($path -split ';') | Where-Object { $_ -eq $dir }).Count -eq 0) {
        [Environment]::SetEnvironmentVariable('Path', ($(if ($path) { "$path;" } else { '' }) + $dir), 'User')
    }

    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Treering.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($menu)
    $shortcut.TargetPath = Join-Path $dir 'treering.exe'
    $shortcut.WorkingDirectory = $dir
    $shortcut.Description = 'Treering - a map of your code over time'
    $shortcut.Save()

    Write-Host ''
    Write-Host "Treering is installed in $dir."
    Write-Host 'Open it from the Start menu, or run "treering" in a new terminal. It opens in your browser.'
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
