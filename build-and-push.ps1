[CmdletBinding(SupportsShouldProcess = $true)]
param(
	[string]$ImageName = "holosheep/docker-swarm-webhook",
	[string]$Tag = "latest",
	[string]$Dockerfile = "DockerSwarmWebhook/Dockerfile",
	[string]$Context = ".",
	[string[]]$Platforms = @("linux/amd64", "linux/arm64"),
	[string]$Builder,
	[switch]$SingleArch,
	[switch]$NoLatest,
	[switch]$SkipLogin
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
	param(
		[string]$Message,
		[string]$Target,
		[scriptblock]$Action
	)

	Write-Host "==> $Message" -ForegroundColor Cyan
	if ($PSCmdlet.ShouldProcess($Target, $Message)) {
		& $Action
	}
}

function Test-DockerLogin {
	$configPath = Join-Path $HOME ".docker/config.json"
	if (-not (Test-Path $configPath)) {
		return $false
	}

	try {
		$config = Get-Content $configPath -Raw | ConvertFrom-Json
		return $null -ne $config.auths
	}
	catch {
		return $false
	}
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
	throw "Docker CLI is not installed or not available in PATH."
}

if (-not (Test-Path $Dockerfile)) {
	throw "Dockerfile not found: $Dockerfile"
}

$useBuildx = -not $SingleArch -and $null -ne $Platforms -and $Platforms.Count -gt 0

if ($useBuildx -and -not (Get-Command docker -ErrorAction SilentlyContinue)) {
	throw "Docker CLI is not installed or not available in PATH."
}

$tags = [System.Collections.Generic.List[string]]::new()
$tags.Add("${ImageName}:${Tag}")

if (-not $NoLatest -and $Tag -ne "latest") {
	$tags.Add("${ImageName}:latest")
}

if (-not $SkipLogin -and -not (Test-DockerLogin)) {
	Invoke-Step "Docker Hub login" "Docker Hub" {
		docker login
	}
}


if ($useBuildx) {
	$platformList = $Platforms -join ","
	$buildxArgs = @("buildx", "build", "--platform", $platformList, "-f", $Dockerfile)

	if ($Builder) {
		$buildxArgs += @("--builder", $Builder)
	}

	foreach ($imageTag in $tags) {
		$buildxArgs += @("-t", $imageTag)
	}

	$buildxArgs += @("--push", $Context)

	Invoke-Step "Building and pushing multi-arch image for $($tags -join ', ') on $platformList" ($tags -join ", ") {
		docker @buildxArgs
	}
}
else {
	Invoke-Step "Building image $($tags[0])" $tags[0] {
		docker build -f $Dockerfile -t $tags[0] $Context
	}

	for ($i = 1; $i -lt $tags.Count; $i++) {
		$currentTag = $tags[$i]
		Invoke-Step "Tagging image as $currentTag" $currentTag {
			docker tag $tags[0] $currentTag
		}
	}

	foreach ($imageTag in $tags) {
		Invoke-Step "Pushing $imageTag" $imageTag {
			docker push $imageTag
		}
	}
}

Write-Host "Done. Pushed: $($tags -join ', ')" -ForegroundColor Green
