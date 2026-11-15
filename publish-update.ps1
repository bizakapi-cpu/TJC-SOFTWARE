[CmdletBinding()]
param(
	[string]$ReleaseNotes = "TJC Service Desk update"
)

$ErrorActionPreference = 'Stop'
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
$scriptPath = Join-Path $PSScriptRoot $scriptFile

Push-Location $PSScriptRoot
try {
	if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git was not found on PATH.' }
	if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) was not found on PATH.' }
	if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK was not found on PATH.' }

	$repositoryRoot = (& git rev-parse --show-toplevel 2>$null)
	if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($repositoryRoot.Trim()) -ne [IO.Path]::GetFullPath($PSScriptRoot)) {
		throw 'Run this script from a Git working tree at the project root.'
	}

	$remoteUrl = (& git remote get-url origin 2>$null)
	if ($LASTEXITCODE -ne 0 -or $remoteUrl -notmatch 'bizakapi-cpu/TJC-SOFTWARE(?:\.git)?$') {
		throw "Git remote 'origin' must point to $repository."
	}

	$currentBranch = (& git branch --show-current).Trim()
	if ($LASTEXITCODE -ne 0 -or $currentBranch -ne $branch) {
		throw "Check out '$branch' before publishing a release."
	}

	& git diff --cached --quiet
	if ($LASTEXITCODE -ne 0) { throw 'Commit or unstage existing staged changes before publishing; the script will not include them.' }

	$workingTreeChanges = & git status --porcelain --untracked-files=all
	if ($LASTEXITCODE -ne 0) { throw 'Could not inspect working-tree status.' }
	if ($workingTreeChanges) { throw "Commit or revert all project changes before publishing so the release source matches the executable.`n$workingTreeChanges" }

	& gh auth status --hostname github.com
	if ($LASTEXITCODE -ne 0) { throw 'Authenticate with GitHub CLI first by running: gh auth login' }

	[xml]$project = Get-Content -LiteralPath $projectPath -Raw
	$versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1
	if (-not $versionNode -or [string]$versionNode -notmatch '^\d+\.\d+\.\d+$') {
		throw 'The project must contain a valid three-part <Version> value.'
	}

	$projectVersion = [version]$versionNode
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'version.json must contain a valid three-part version.' }
	$manifestVersion = [version]$manifest.version
	if ($manifestVersion -lt $projectVersion) {
		throw "version.json version ($manifestVersion) cannot be lower than project version ($projectVersion)."
	}

	if ($manifestVersion -gt $projectVersion) {
		$nextVersion = $manifestVersion.ToString(3)
	}
	else {
		$nextVersion = '{0}.{1}.{2}' -f $projectVersion.Major, $projectVersion.Minor, ($projectVersion.Build + 1)
	}
	$tag = "v$nextVersion"
	$projectText = Get-Content -LiteralPath $projectPath -Raw
	$versionPattern = '<Version>[^<]+</Version>'
	if ($projectText -notmatch $versionPattern) { throw 'Could not locate <Version> in the project file.' }
	$projectText = [regex]::Replace($projectText, $versionPattern, "<Version>$nextVersion</Version>", 1)
	[IO.File]::WriteAllText($projectPath, $projectText, [Text.UTF8Encoding]::new($false))

	$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) "TJCServiceDesk-publish-$([guid]::NewGuid().ToString('N'))"
	try {
		& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDirectory
		if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

		$publishedExecutable = Join-Path $publishDirectory 'TJC Service Desk.exe'
		if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
			throw "The published executable was not found: $publishedExecutable"
		}
		$releaseExecutable = Join-Path $publishDirectory 'TJC_Service_Desk.exe'
		Move-Item -LiteralPath $publishedExecutable -Destination $releaseExecutable

		& git add -- $projectFile $scriptFile
		if ($LASTEXITCODE -ne 0) { throw 'Could not stage release source files.' }
		& git commit -m "Prepare release $tag"
		if ($LASTEXITCODE -ne 0) { throw 'Could not commit the version bump.' }
		& git push origin $branch
		if ($LASTEXITCODE -ne 0) { throw 'Could not push the version bump to GitHub.' }

		& gh release view $tag --repo $repository *> $null
		if ($LASTEXITCODE -eq 0) { throw "GitHub release $tag already exists." }
		& gh release create $tag $releaseExecutable --repo $repository --title "TJC Service Desk $nextVersion" --notes $ReleaseNotes --target $branch
		if ($LASTEXITCODE -ne 0) { throw "Could not create GitHub release $tag." }

		$downloadUrl = "https://github.com/$repository/releases/download/$tag/TJC_Service_Desk.exe"
		$updatedManifest = [ordered]@{
			version = $nextVersion
			downloadUrl = $downloadUrl
			releaseNotes = $ReleaseNotes
		} | ConvertTo-Json
		[IO.File]::WriteAllText($manifestPath, "$updatedManifest`n", [Text.UTF8Encoding]::new($false))

		& git add -- $manifestFile
		if ($LASTEXITCODE -ne 0) { throw 'Could not stage version.json.' }
		& git commit -m "Publish update manifest for $tag"
		if ($LASTEXITCODE -ne 0) { throw 'Could not commit the updated version manifest.' }
		& git push origin $branch
		if ($LASTEXITCODE -ne 0) { throw 'Could not push version.json to GitHub.' }

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
