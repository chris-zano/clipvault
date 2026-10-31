<#
.SYNOPSIS
    Puts text on the clipboard, retrying a few times.

.DESCRIPTION
    `Set-Clipboard` occasionally fails with "Requested Clipboard operation
    did not succeed" when another process (including ClipVault's own
    watcher, mid-read) briefly holds the clipboard open - this retries
    rather than treating that as a real failure. Use it to feed ClipVault's
    watcher a real OS clipboard event, the same role `scripts/gtkcopy.py`
    plays for the Linux build.

.PARAMETER Text
    The text to copy.

.EXAMPLE
    .\copy-text.ps1 "hello from a test"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Text,

    [int]$Attempts = 6,
    [int]$DelayMs = 250
)

for ($i = 0; $i -lt $Attempts; $i++) {
    try {
        Set-Clipboard -Value $Text -ErrorAction Stop
        Start-Sleep -Milliseconds $DelayMs
        if ((Get-Clipboard) -eq $Text) {
            Write-Host "Copied: $Text"
            exit 0
        }
    }
    catch {
        # retry
    }
    Start-Sleep -Milliseconds $DelayMs
}

Write-Error "Could not set the clipboard after $Attempts attempts."
exit 1
