[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ChangelogPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedChangelogPath = (Resolve-Path -LiteralPath $ChangelogPath).Path
$changelog = [System.IO.File]::ReadAllText($resolvedChangelogPath)
$escapedVersion = [Regex]::Escape($Version)
$sectionPattern = "(?ms)^## \[$escapedVersion\](?:\s+-\s+[^\r\n]+)?\s*\r?\n(?<body>.*?)(?=^## \[|\z)"
$section = [Regex]::Match($changelog, $sectionPattern)

if (-not $section.Success) {
    throw "CHANGELOG.md does not contain a release section for version $Version."
}

$releaseNotes = $section.Groups["body"].Value.Trim()
if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
    throw "The CHANGELOG.md section for version $Version is empty."
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    $releaseNotes + [Environment]::NewLine,
    $utf8WithoutBom)

Write-Host "Release notes for version $Version written to $resolvedOutputPath"
