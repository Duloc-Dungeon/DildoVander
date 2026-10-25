#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
swiftc "$root/ios/App/App/DiagnosticExportFile.swift" "$root/tests/native_diagnostic_export/main.swift" -o "$work_dir/diagnostic-export-test"
"$work_dir/diagnostic-export-test"
