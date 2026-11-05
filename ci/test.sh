#!/usr/bin/env bash
# Build and test the .NET edition, then enforce the line-coverage gate.
# Same entry point locally and in CI (constitution Principle IV).
#
# Inputs (environment variables):
#   COVERAGE_MIN   Minimum line coverage percent. (default: 90)
set -euo pipefail

cd "$(dirname "$0")/.."
min="${COVERAGE_MIN:-90}"
results="TestResults"
rm -rf "${results}"

# P/Invoke shims (Native/) and the live-network host are reported, not gated.
dotnet test --solution JellySend.slnx --results-directory "${PWD}/${results}" -- \
    --coverlet --coverlet-output-format cobertura \
    --coverlet-include '[JellySend.*]*' \
    --coverlet-exclude '[JellySend.Tests]*' \
    --coverlet-exclude '[JellySend.Discord]JellySend.Discord.Native.*' \
    --coverlet-exclude '[JellySend.Host]*' \
    --coverlet-exclude-by-attribute ExcludeFromCodeCoverage

report="$(find "${results}" -name "coverage*.xml" -print -quit)"
if [[ -z "${report}" ]]; then
    echo "no coverage report produced" >&2
    exit 1
fi

rate="$(grep -m1 -o 'line-rate="[0-9.]*"' "${report}" | grep -o '[0-9.]*')"
pct="$(awk -v r="${rate}" 'BEGIN { printf "%.2f", r * 100 }')"
echo "line coverage: ${pct}% (minimum ${min}%)"
awk -v p="${pct}" -v m="${min}" 'BEGIN { exit (p + 0 >= m + 0) ? 0 : 1 }' || {
    echo "coverage ${pct}% is below ${min}%" >&2
    exit 1
}
