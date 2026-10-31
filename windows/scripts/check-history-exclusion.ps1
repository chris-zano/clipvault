<#
.SYNOPSIS
    Checks that the current clipboard content is excluded from Windows'
    own Win+V clipboard history and Cloud Clipboard sync.

.DESCRIPTION
    Run this right after copying a secret out of ClipVault's vault (the
    "copy" button on a vault entry). It checks for the same three clipboard
    formats ClipboardWriter.SetSecretText sets - their presence, not their
    content, is what tells Windows to leave that clipboard content alone.
    There is no public API to query the Win+V history panel directly, so
    this is the closest a script gets to that check; the panel itself is the
    final word; see README.md's "Testing by hand" section.

.EXAMPLE
    .\check-history-exclusion.ps1
#>
Add-Type -Namespace ClipVaultCheck -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern uint RegisterClipboardFormat(string name);
[DllImport("user32.dll")]
public static extern bool IsClipboardFormatAvailable(uint format);
'@

$formats = @(
    "ExcludeClipboardContentFromMonitorProcessing",
    "CanIncludeInClipboardHistory",
    "CanUploadToCloudClipboard"
)

$allPresent = $true
foreach ($name in $formats) {
    $id = [ClipVaultCheck.Native]::RegisterClipboardFormat($name)
    $present = [ClipVaultCheck.Native]::IsClipboardFormatAvailable($id)
    $mark = if ($present) { "OK" } else { "MISSING" }
    Write-Host "$mark  $name"
    if (-not $present) { $allPresent = $false }
}

if ($allPresent) {
    Write-Host "`nAll three exclusion formats are present - this content should not appear in Win+V history or Cloud Clipboard."
    exit 0
}
else {
    Write-Warning "`nAt least one exclusion format is missing. If you just copied a vault secret, this is a real bug; if you copied something else, this is expected - only vault secrets set these."
    exit 1
}
