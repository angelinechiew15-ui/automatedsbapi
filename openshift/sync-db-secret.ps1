#requires -Version 5.1
<#
.SYNOPSIS
  Pushes the Oracle connection strings into OpenShift and wires them into the
  AutomatedSb.Api backend deployment, then verifies the API responds.

.DESCRIPTION
  The connection strings are read from the project's local .NET user-secrets
  store (the source of truth set up locally), so no passwords are hard-coded in
  this script or committed to git. The script is idempotent: re-running it just
  updates the Secret and re-applies the env wiring.

  Steps performed:
    1. Verify you are logged in to OpenShift (oc whoami).
    2. Read ConnectionStrings:Oracle / ConnectionStrings:OracleREALIS from user-secrets.
    3. Create or update an OpenShift Secret (default: automatedsbapi-db).
    4. Inject the Secret keys as env vars into the backend (Deployment or DeploymentConfig).
    5. Wait for rollout, then curl /api/sbol from inside the frontend pod.

.EXAMPLE
  ./sync-db-secret.ps1 -Project my-namespace

.EXAMPLE
  ./sync-db-secret.ps1 -BackendName automatedsbapi-git -FrontendName automatedsb-git
#>
[CmdletBinding()]
param(
    # OpenShift project/namespace. If omitted, the current `oc project` is used.
    [string]$Project,

    # Name of the backend Deployment/DeploymentConfig and its Service.
    [string]$BackendName = 'automatedsbapi-git',

    # Name of the frontend Deployment/DeploymentConfig (used only for verification).
    [string]$FrontendName = 'automatedsb-git',

    # Name of the OpenShift Secret to create/update.
    [string]$SecretName = 'automatedsbapi-db',

    # Backend service port that server.mjs proxies to.
    [int]$BackendPort = 8080,

    # Path to the API csproj that owns the user-secrets store.
    [string]$Csproj = (Join-Path $PSScriptRoot '..\AutomatedSb.Api\AutomatedSb.Api.csproj')
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Fail($msg)       { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# --- 1. Preconditions ------------------------------------------------------
Write-Step 'Checking prerequisites (oc, dotnet, login state)'
foreach ($tool in 'oc', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Fail "'$tool' is not on PATH. Install it and retry."
    }
}

$who = (& oc whoami) 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Not logged in to OpenShift. Run 'oc login <server> --token=...' first."
}
Write-Ok "Logged in as: $who"

if ($Project) {
    & oc project $Project | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Could not switch to project '$Project'." }
}
$currentProject = (& oc project -q) 2>&1
Write-Ok "Using project: $currentProject"

$csprojFull = (Resolve-Path $Csproj -ErrorAction SilentlyContinue)
if (-not $csprojFull) { Fail "Cannot find csproj at '$Csproj'." }
Write-Ok "User-secrets source: $csprojFull"

# --- 2. Read connection strings from user-secrets --------------------------
Write-Step 'Reading connection strings from local user-secrets'
$secretsRaw = (& dotnet user-secrets list --project "$csprojFull") 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Failed to read user-secrets. Output:`n$secretsRaw"
}

$secrets = @{}
foreach ($line in $secretsRaw) {
    $idx = $line.IndexOf(' = ')
    if ($idx -gt 0) {
        $key = $line.Substring(0, $idx).Trim()
        $val = $line.Substring($idx + 3)
        $secrets[$key] = $val
    }
}

$oracle = $secrets['ConnectionStrings:Oracle']
$realis = $secrets['ConnectionStrings:OracleREALIS']
if ([string]::IsNullOrWhiteSpace($oracle)) { Fail "ConnectionStrings:Oracle not found in user-secrets." }
if ([string]::IsNullOrWhiteSpace($realis)) { Fail "ConnectionStrings:OracleREALIS not found in user-secrets." }
Write-Ok 'Found ConnectionStrings:Oracle and ConnectionStrings:OracleREALIS (values hidden).'

# --- 3. Create/update the OpenShift Secret (idempotent) --------------------
# Keys use the .NET env-var convention (double underscore = config nesting).
Write-Step "Applying Secret '$SecretName'"
$secretYaml = & oc create secret generic $SecretName `
    --from-literal=ConnectionStrings__Oracle="$oracle" `
    --from-literal=ConnectionStrings__OracleREALIS="$realis" `
    --dry-run=client -o yaml
if ($LASTEXITCODE -ne 0) { Fail "Failed to render Secret yaml." }
$secretYaml | & oc apply -f -
if ($LASTEXITCODE -ne 0) { Fail "Failed to apply Secret '$SecretName'." }
Write-Ok "Secret '$SecretName' applied."

# --- 4. Detect workload type and inject env vars ---------------------------
Write-Step "Wiring Secret into backend '$BackendName'"
$kind = $null
& oc get deployment $BackendName -o name 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $kind = 'deployment' }
else {
    & oc get dc $BackendName -o name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $kind = 'dc' }
}
if (-not $kind) { Fail "No Deployment or DeploymentConfig named '$BackendName' found in '$currentProject'." }
Write-Ok "Backend workload type: $kind"

& oc set env "$kind/$BackendName" --from="secret/$SecretName"
if ($LASTEXITCODE -ne 0) { Fail "Failed to set env from secret on $kind/$BackendName." }
Write-Ok 'Environment variables injected.'

# Always restart: updating an existing Secret does NOT restart running pods,
# and re-applying the same env wiring is a no-op (no rollout). A restart forces
# the pods to re-read the latest Secret values.
& oc rollout restart "$kind/$BackendName" | Out-Null
Write-Ok 'Triggered rollout restart to pick up latest Secret values.'

# --- 5. Wait for rollout and verify ----------------------------------------
Write-Step 'Waiting for backend rollout to complete'
& oc rollout status "$kind/$BackendName" --timeout=180s
if ($LASTEXITCODE -ne 0) { Fail "Rollout did not complete. Check 'oc logs $kind/$BackendName'." }
Write-Ok 'Backend rollout complete.'

Write-Step "Verifying API via in-cluster call from frontend '$FrontendName'"
$verifyUrl = "http://$BackendName`:$BackendPort/api/sbol"
$frontKind = 'deployment'
& oc get deployment $FrontendName -o name 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & oc get dc $FrontendName -o name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $frontKind = 'dc' } else { $frontKind = $null }
}

if ($frontKind) {
    $resp = (& oc rsh "$frontKind/$FrontendName" curl -s -o /dev/null -w "%{http_code}" $verifyUrl) 2>&1
    if ($resp -match '200') {
        Write-Ok "SUCCESS: $verifyUrl returned HTTP 200 from inside the frontend pod."
    } else {
        Write-Host "    WARNING: $verifyUrl returned '$resp'. Check 'oc logs $kind/$BackendName --tail=50'." -ForegroundColor Yellow
    }
} else {
    Write-Host "    Skipped in-cluster verification (frontend '$FrontendName' not found)." -ForegroundColor Yellow
}

Write-Host "`nDone. The backend now reads DB credentials from Secret '$SecretName'." -ForegroundColor Green
