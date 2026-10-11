# Publishes a new TSDraw version: bumps VERSION, builds, commits, tags, pushes, and creates a
# GitHub release with TSDraw.exe attached. Everyone's TSDraw installs it automatically
# (within ~6 hours, or right away via tray -> Check for updates).
#
#   .\release.cmd                          patch bump: 0.3.0 -> 0.3.1
#   .\release.cmd minor -Notes "New pens"  minor bump: 0.3.1 -> 0.4.0
#   .\release.cmd none                     release the current VERSION as-is
param(
    [ValidateSet("patch", "minor", "major", "none")][string]$Bump = "patch",
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Run($what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

$v = [version](Get-Content VERSION -Raw).Trim()
$new = switch ($Bump) {
    "major" { "{0}.0.0" -f ($v.Major + 1) }
    "minor" { "{0}.{1}.0" -f $v.Major, ($v.Minor + 1) }
    "patch" { "{0}.{1}.{2}" -f $v.Major, $v.Minor, ($v.Build + 1) }
    default { $v.ToString() }
}
$tag = "v$new"
if (git tag --list $tag) { throw "$tag already exists - bump the version" }
[IO.File]::WriteAllText("$PSScriptRoot\VERSION", "$new`n")

Run "build" { cmd /c build.cmd }
Run "git add" { git add -A }
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Run "git commit" { git commit -q -m "Release $tag$(if ($Notes) { ": $Notes" })" } }
Run "git tag" { git tag $tag }
Run "git push" { git push -q origin HEAD }
Run "git push tag" { git push -q origin $tag }
$notesArgs = if ($Notes) { @("--notes", $Notes) } else { @("--generate-notes") }
Run "gh release" { gh release create $tag dist\TSDraw.exe dist\TSDraw.zip --title "TSDraw $new" @notesArgs }
Write-Host "Released TSDraw $new - friends' TSDraw will pick it up automatically."
