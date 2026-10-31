<#
.SYNOPSIS
    Puts a random-pixel test image on the clipboard.

.DESCRIPTION
    Plays the same role as `scripts/gtkcopyimage.py` in the Linux build:
    random pixels on purpose, since a solid fill compresses to a few hundred
    bytes and would not exercise a large-image code path (the DIB wrapping
    in ClipboardWatcher, or an INCR-style transfer on the Linux side).
    Retries `SetImage`, which can fail transiently the same way
    `Set-Clipboard` does - see copy-text.ps1.

.PARAMETER Width
.PARAMETER Height

.EXAMPLE
    .\copy-image.ps1 1200 900
#>
param(
    [int]$Width = 400,
    [int]$Height = 300,

    [int]$Attempts = 6,
    [int]$DelayMs = 250
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bitmap = New-Object System.Drawing.Bitmap $Width, $Height
$rng = New-Object System.Random
for ($y = 0; $y -lt $Height; $y++) {
    for ($x = 0; $x -lt $Width; $x++) {
        $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(
            255, $rng.Next(256), $rng.Next(256), $rng.Next(256)))
    }
}

for ($i = 0; $i -lt $Attempts; $i++) {
    try {
        [System.Windows.Forms.Clipboard]::SetImage($bitmap)
        Write-Host "Copied a $Width x $Height random-pixel image."
        exit 0
    }
    catch {
        Start-Sleep -Milliseconds $DelayMs
    }
}

Write-Error "Could not set the clipboard image after $Attempts attempts."
exit 1
