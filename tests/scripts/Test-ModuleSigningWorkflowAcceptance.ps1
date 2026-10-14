[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workflowPath = Join-Path $repoRoot '.github\workflows\publish-module.yml'
if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Falta el flujo de publicación: $workflowPath"
}
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredFragments = @(
    'path: ${{ runner.temp }}/module-signing-${{ github.run_id }}-${{ github.run_attempt }}/incoming',
    'RUN_ROOT="${RUNNER_TEMP}/module-signing-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"',
    'cmp --silent "$RUN_ROOT/trusted-publisher.der" "$RUN_ROOT/signer-public.der"',
    '${{ runner.temp }}/module-signing-${{ github.run_id }}-${{ github.run_attempt }}/signed/*.zip',
    '${{ runner.temp }}/module-signing-${{ github.run_id }}-${{ github.run_attempt }}/signed/publisher-public.pem',
    '${{ runner.temp }}/module-signing-${{ github.run_id }}-${{ github.run_attempt }}/signed/*.sha256',
    '- name: Limpiar archivos temporales',
    'if: always()',
    'run: rm -rf -- "${RUNNER_TEMP}/module-signing-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"'
)
foreach ($fragment in $requiredFragments) {
    if (-not $workflow.Contains($fragment)) {
        throw "RED: el flujo debe incluir la protección de publicación '$fragment'."
    }
}
$requiredPatterns = @(
    'openssl pkey -pubin -in "\$RUN_ROOT/incoming/trusted-publisher\.pem"\s*\\\s*-outform DER -out "\$RUN_ROOT/trusted-publisher\.der"',
    'openssl pkey -in "\$POS_MODULE_SIGNING_KEY" -pubout\s*\\\s*-outform DER -out "\$RUN_ROOT/signer-public\.der"'
)
foreach ($pattern in $requiredPatterns) {
    if ($workflow -notmatch $pattern) {
        throw "RED: no se encontró la conversión PEM a DER esperada: $pattern"
    }
}
if ($workflow.Contains("cmp --silent <(tr -d '\r'")) {
    throw 'RED: el firmador compara texto PEM byte a byte en vez de comparar la clave pública normalizada.'
}

Write-Output 'OK: el flujo compara la clave pública en DER, aísla cada intento y limpia los archivos temporales.'

