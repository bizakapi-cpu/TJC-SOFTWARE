[CmdletBinding()]
param(
	[string]$ReleaseNotes = "TJC Service Desk update"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$toolDirectories = @(
    (Join-Path $env:ProgramFiles 'Git\cmd'),
    (Join-Path $env:ProgramFiles 'GitHub CLI'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd')
)
foreach ($directory in $toolDirectories) {
    if ((Test-Path -LiteralPath $directory -PathType Container) -and $env:Path -notlike "*$directory*") {
        $env:Path = "$directory;$env:Path"
    }
}

$repository = 'bizakapi-cpu/TJC-SOFTWARE'
$branch = 'main'
$projectFile = 'TJCServiceDesk.csproj'
$manifestFile = 'version.json'
$scriptFile = 'publish-update.ps1'
$projectPath = Join-Path $PSScriptRoot $projectFile
$manifestPath = Join-Path $PSScriptRoot $manifestFile

function Invoke-NativeCommand {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory = $true)][string]$FilePath,
		[Parameter(Mandatory = $true)][string[]]$Arguments,
		[Parameter(Mandatory = $true)][string]$FailureMessage,
		[int[]]$AllowedExitCodes = @(0)
	)

	try {
		$commandOutput = @(& $FilePath @Arguments 2>&1)
		$exitCode = $LASTEXITCODE
	}
	catch {
		throw "$FailureMessage`n$($_.Exception.Message)"
	}

	$outputText = ($commandOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
	if ($null -eq $exitCode -or $AllowedExitCodes -notcontains [int]$exitCode) {
		$detail = if ([string]::IsNullOrWhiteSpace($outputText)) { '' } else { "`n$outputText" }
		throw "$FailureMessage (exit code: $exitCode).$detail"
	}

	[pscustomobject]@{
		ExitCode = [int]$exitCode
		Output = $outputText
	}
}

Push-Location $PSScriptRoot
try {
	if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git was not found on PATH.' }
	if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) was not found on PATH.' }
	if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK was not found on PATH.' }

	$repositoryRootResult = Invoke-NativeCommand -FilePath 'git' -Arguments @('rev-parse', '--show-toplevel') -FailureMessage 'Could not determine the Git repository root.'
	$repositoryRoot = $repositoryRootResult.Output.Trim()
	if ([string]::IsNullOrWhiteSpace($repositoryRoot)) {
		throw 'Git returned an empty repository root. Run this script from a Git working tree at the project root.'
	}
	if ([IO.Path]::GetFullPath($repositoryRoot) -ne [IO.Path]::GetFullPath($PSScriptRoot)) {
		throw 'Run this script from a Git working tree at the project root.'
	}

	$remoteUrlResult = Invoke-NativeCommand -FilePath 'git' -Arguments @('remote', 'get-url', 'origin') -FailureMessage "Could not read Git remote 'origin'."
	$remoteUrl = $remoteUrlResult.Output.Trim()
	if ([string]::IsNullOrWhiteSpace($remoteUrl) -or $remoteUrl -notmatch 'bizakapi-cpu/TJC-SOFTWARE(?:\.git)?$') {
		throw "Git remote 'origin' must point to $repository."
	}

	$branchResult = Invoke-NativeCommand -FilePath 'git' -Arguments @('branch', '--show-current') -FailureMessage 'Could not determine the current Git branch.'
	$currentBranch = $branchResult.Output.Trim()
	if ([string]::IsNullOrWhiteSpace($currentBranch)) {
		throw 'Git returned an empty branch name. Check that a branch is checked out.'
	}
	if ($currentBranch -ne $branch) {
		throw "Check out '$branch' before publishing a release."
	}

	$stagedChangesResult = Invoke-NativeCommand -FilePath 'git' -Arguments @('diff', '--cached', '--quiet') -FailureMessage 'Could not inspect staged Git changes.' -AllowedExitCodes @(0, 1)
	if ($stagedChangesResult.ExitCode -eq 1) {
		throw 'Commit or unstage existing staged changes before publishing; the script will not include them.'
	}

	$statusResult = Invoke-NativeCommand -FilePath 'git' -Arguments @('status', '--porcelain', '--untracked-files=all') -FailureMessage 'Could not inspect working-tree status.'
	if (-not [string]::IsNullOrWhiteSpace($statusResult.Output)) {
		throw "Commit or revert all project changes before publishing so the release source matches the executable.`n$($statusResult.Output)"
	}

	try {
		$null = Invoke-NativeCommand -FilePath 'gh' -Arguments @('auth', 'status', '--hostname', 'github.com') -FailureMessage 'GitHub CLI authentication check failed.'
	}
	catch {
		throw "Authenticate with GitHub CLI first by running: gh auth login`n$($_.Exception.Message)"
	}

	[xml]$project = Get-Content -LiteralPath $projectPath -Raw
	$versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1
	if (-not $versionNode -or [string]$versionNode -notmatch '^\d+\.\d+\.\d+$') {
		throw 'The project must contain a valid three-part <Version> value.'
	}

	$projectVersion = [version]$versionNode
	$manifestVersion = $projectVersion
	if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
		try {
			$manifestJson = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
			if ([string]::IsNullOrWhiteSpace($manifestJson)) {
				throw 'The file is empty.'
			}
			$manifest = ConvertFrom-Json -InputObject $manifestJson -ErrorAction Stop
		}
		catch {
			throw "Could not read a valid version manifest at '$manifestPath': $($_.Exception.Message)"
		}
		if ($null -eq $manifest -or $manifest -isnot [System.Management.Automation.PSCustomObject]) {
			throw 'version.json must contain a JSON object.'
		}

		$manifestVersionProperty = $manifest.PSObject.Properties['version']
		if ($null -eq $manifestVersionProperty) {
			$manifestVersionProperty = $manifest.PSObject.Properties['latestVersion']
		}
		if ($null -ne $manifestVersionProperty -and -not [string]::IsNullOrWhiteSpace([string]$manifestVersionProperty.Value)) {
			$manifestVersionText = [string]$manifestVersionProperty.Value
			if ($manifestVersionText -notmatch '^\d+\.\d+\.\d+$') {
				throw 'version.json "version" must be a valid three-part version (for example, 1.2.3).'
			}
			$manifestVersion = [version]$manifestVersionText
		}
	}

	if ($projectVersion -gt $manifestVersion) {
		$nextVersion = $projectVersion.ToString(3)
	}
	else {
		$nextVersion = '{0}.{1}.{2}' -f $manifestVersion.Major, $manifestVersion.Minor, ($manifestVersion.Build + 1)
	}
	$tag = "v$nextVersion"
	$releaseCheck = & gh release view $tag --repo $repository 2>&1
	$releaseCheckExitCode = $LASTEXITCODE
	$releaseCheckText = ($releaseCheck | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
	if ($releaseCheckExitCode -eq 0) {
		throw "GitHub release $tag already exists."
	}
	if ($releaseCheckText -notmatch '(?i)(HTTP\s+404|404\s+Not Found|release not found|could not find release|no release found)') {
		throw "Could not verify whether GitHub release $tag exists (exit code: $releaseCheckExitCode). Check authentication or network access and try again.`n$releaseCheckText"
	}

	$projectText = Get-Content -LiteralPath $projectPath -Raw
	$versionPattern = '<Version>[^<]+</Version>'
	if ($projectText -notmatch $versionPattern) { throw 'Could not locate <Version> in the project file.' }
	$projectText = [regex]::Replace($projectText, $versionPattern, "<Version>$nextVersion</Version>", 1)
	[IO.File]::WriteAllText($projectPath, $projectText, [Text.UTF8Encoding]::new($false))

	$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) "TJCServiceDesk-publish-$([guid]::NewGuid().ToString('N'))"
	try {
		$null = Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $publishDirectory) -FailureMessage 'dotnet publish failed.'

		$publishedExecutable = Join-Path $publishDirectory 'TJC Service Desk.exe'
		if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
			throw "The published executable was not found: $publishedExecutable"
		}
		$releaseExecutable = Join-Path $publishDirectory 'TJC_Service_Desk.exe'
		Move-Item -LiteralPath $publishedExecutable -Destination $releaseExecutable

		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('add', '--', $projectFile, $scriptFile) -FailureMessage 'Could not stage release source files.'
		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('commit', '-m', "Prepare release $tag") -FailureMessage 'Could not commit the version bump.'
		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('push', 'origin', $branch) -FailureMessage 'Could not push the version bump to GitHub.'

		$null = Invoke-NativeCommand -FilePath 'gh' -Arguments @('release', 'create', $tag, $releaseExecutable, '--repo', $repository, '--title', "TJC Service Desk $nextVersion", '--notes', $ReleaseNotes, '--target', $branch) -FailureMessage "Could not create GitHub release $tag."

		$downloadUrl = "https://github.com/$repository/releases/download/$tag/TJC_Service_Desk.exe"
		$updatedManifest = [ordered]@{
			version = $nextVersion
			downloadUrl = $downloadUrl
			releaseNotes = $ReleaseNotes
		} | ConvertTo-Json
		[IO.File]::WriteAllText($manifestPath, "$updatedManifest`n", [Text.UTF8Encoding]::new($false))

		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('add', '--', $manifestFile) -FailureMessage 'Could not stage version.json.'
		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('commit', '-m', "Publish update manifest for $tag") -FailureMessage 'Could not commit the updated version manifest.'
		$null = Invoke-NativeCommand -FilePath 'git' -Arguments @('push', 'origin', $branch) -FailureMessage 'Could not push version.json to GitHub.'

		Write-Host "Published $tag. Update manifest now points to $downloadUrl"
	}
	finally {
		if (Test-Path -LiteralPath $publishDirectory) {
			Remove-Item -LiteralPath $publishDirectory -Recurse -Force
		}
	}
}
finally {
	Pop-Location
}
