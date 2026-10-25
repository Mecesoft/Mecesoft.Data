#!/usr/bin/env bash
#
# Validates the four Mecesoft.Data release packages before they are published.
#
# The checks are intentionally fail-closed: every expectation is derived from the
# repository (Directory.Build.props / Directory.Packages.props) and compared with
# the actual package contents. Any mismatch aborts the release.
#
# Usage:
#   validate-release-packages.sh \
#     --packages-dir artifacts/packages \
#     --expected-version 1.0.0 \
#     --expected-commit <40-char-git-sha> \
#     --source-repository Mecesoft/Mecesoft.Data

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: validate-release-packages.sh --packages-dir <dir> --expected-version <version> \
       --expected-commit <sha> --source-repository <owner/repo>
USAGE
}

die() {
  echo "error: $*" >&2
  exit 1
}

ok() {
  echo "ok: $*"
}

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"

packages_dir=""
expected_version=""
expected_commit=""
source_repository=""

while (( $# > 0 )); do
  case "$1" in
    --packages-dir)
      packages_dir="${2:-}"
      shift 2
      ;;
    --expected-version)
      expected_version="${2:-}"
      shift 2
      ;;
    --expected-commit)
      expected_commit="${2:-}"
      shift 2
      ;;
    --source-repository)
      source_repository="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      die "unknown argument: $1"
      ;;
  esac
done

[[ -n "$packages_dir" ]] || die "--packages-dir is required"
[[ -n "$expected_version" ]] || die "--expected-version is required"
[[ -n "$expected_commit" ]] || die "--expected-commit is required"
[[ -n "$source_repository" ]] || die "--source-repository is required"

[[ "$expected_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] \
  || die "invalid version: $expected_version"
[[ "$expected_commit" =~ ^[0-9a-f]{40}$ ]] \
  || die "invalid commit SHA: $expected_commit"
[[ "$source_repository" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] \
  || die "invalid source repository: $source_repository"

[[ -d "$packages_dir" ]] || die "package directory not found: $packages_dir"
packages_dir="$(cd -- "$packages_dir" && pwd)"

build_props="${repo_root}/Directory.Build.props"
package_props="${repo_root}/Directory.Packages.props"
[[ -f "$build_props" ]] || die "missing ${build_props}"
[[ -f "$package_props" ]] || die "missing ${package_props}"

props_version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$build_props" | head -n1)"
[[ -n "$props_version" ]] || die "could not read <Version> from Directory.Build.props"
[[ "$props_version" == "$expected_version" ]] \
  || die "requested version ${expected_version} does not match Directory.Build.props version ${props_version}"

central_version() {
  local package_id="$1"
  local line version

  line="$(grep -F "<PackageVersion Include=\"${package_id}\" Version=\"" "$package_props" | head -n1 || true)"
  [[ -n "$line" ]] || die "central version not found for ${package_id}"

  version="$(sed -n 's:.*Version="\([^"]*\)".*:\1:p' <<<"$line")"
  [[ -n "$version" ]] || die "could not parse central version for ${package_id}"

  printf '%s' "$version"
}

expected_dependencies() {
  local package_id="$1"

  case "$package_id" in
    Mecesoft.Data.Abstractions)
      :
      ;;
    Mecesoft.Data.EntityFrameworkCore)
      printf '%s\n' \
        "Mecesoft.Data.Abstractions ${expected_version}" \
        "Microsoft.EntityFrameworkCore.Relational $(central_version Microsoft.EntityFrameworkCore.Relational)"
      ;;
    Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy)
      printf '%s\n' \
        "Mecesoft.Data.EntityFrameworkCore ${expected_version}" \
        "Microsoft.EntityFrameworkCore.SqlServer $(central_version Microsoft.EntityFrameworkCore.SqlServer)" \
        "Microsoft.Extensions.DependencyInjection.Abstractions $(central_version Microsoft.Extensions.DependencyInjection.Abstractions)" \
        "System.Security.Cryptography.Pkcs $(central_version System.Security.Cryptography.Pkcs)"
      ;;
    Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy)
      printf '%s\n' \
        "Mecesoft.Data.EntityFrameworkCore ${expected_version}" \
        "Npgsql.EntityFrameworkCore.PostgreSQL $(central_version Npgsql.EntityFrameworkCore.PostgreSQL)"
      ;;
    *)
      die "no dependency expectation defined for ${package_id}"
      ;;
  esac
}

package_ids=(
  "Mecesoft.Data.Abstractions"
  "Mecesoft.Data.EntityFrameworkCore"
  "Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy"
  "Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy"
)

nupkg_count="$(find "$packages_dir" -maxdepth 1 -name '*.nupkg' -type f | wc -l | tr -d ' ')"
snupkg_count="$(find "$packages_dir" -maxdepth 1 -name '*.snupkg' -type f | wc -l | tr -d ' ')"
[[ "$nupkg_count" -eq 4 ]] || die "expected 4 .nupkg files, found ${nupkg_count}"
[[ "$snupkg_count" -eq 4 ]] || die "expected 4 .snupkg files, found ${snupkg_count}"

pkcs_version="$(central_version System.Security.Cryptography.Pkcs)"
minimum_pkcs_version="10.0.13"
lowest_pkcs="$(printf '%s\n' "$minimum_pkcs_version" "$pkcs_version" | sort -V | head -n1)"
[[ "$lowest_pkcs" == "$minimum_pkcs_version" ]] \
  || die "System.Security.Cryptography.Pkcs ${pkcs_version} is lower than the required ${minimum_pkcs_version}"

for package_id in "${package_ids[@]}"; do
  nupkg="${packages_dir}/${package_id}.${expected_version}.nupkg"
  snupkg="${packages_dir}/${package_id}.${expected_version}.snupkg"

  [[ -f "$nupkg" ]] || die "missing package: ${nupkg}"
  [[ -f "$snupkg" ]] || die "missing symbol package: ${snupkg}"

  nuspec="$(unzip -p "$nupkg" '*.nuspec')"
  [[ -n "$nuspec" ]] || die "${nupkg}: nuspec is missing"

  grep -q "<id>${package_id}</id>" <<<"$nuspec" \
    || die "${nupkg}: package id ${package_id} is missing"
  grep -q "<version>${expected_version}</version>" <<<"$nuspec" \
    || die "${nupkg}: version ${expected_version} is missing"
  grep -q "url=\"https://github.com/${source_repository}\"" <<<"$nuspec" \
    || die "${nupkg}: repository URL for ${source_repository} is missing"
  grep -q "commit=\"${expected_commit}\"" <<<"$nuspec" \
    || die "${nupkg}: repository commit ${expected_commit} is missing"

  if grep -qi 'Microsoft.SourceLink' <<<"$nuspec"; then
    die "${nupkg}: SourceLink must remain a build-time-only dependency"
  fi

  actual_deps="$(
    grep -o '<dependency id="[^"]*" version="[^"]*"' <<<"$nuspec" \
      | sed -E 's/^<dependency id="([^"]*)" version="([^"]*)"$/\1 \2/' \
      | sort \
      || true
  )"
  expected_deps="$(expected_dependencies "$package_id" | sort)"

  if [[ "$actual_deps" != "$expected_deps" ]]; then
    echo "error: dependency mismatch for ${package_id}" >&2
    diff <(printf '%s\n' "$expected_deps") <(printf '%s\n' "$actual_deps") >&2 || true
    exit 1
  fi

  listing="$(unzip -l "$nupkg")"
  for required in 'README\.md' 'LICENSE' 'lib/net10\.0/[^/]+\.dll' 'lib/net10\.0/[^/]+\.xml'; do
    grep -Eq "$required" <<<"$listing" \
      || die "${nupkg}: missing required entry matching ${required}"
  done

  symbol_listing="$(unzip -l "$snupkg")"
  grep -Eq 'lib/net10\.0/[^/]+\.pdb' <<<"$symbol_listing" \
    || die "${snupkg}: PDB entry is missing"

  pdb="$(mktemp)"
  trap 'rm -f "${pdb:-}"' EXIT
  unzip -p "$snupkg" 'lib/net10.0/*.pdb' > "$pdb"
  grep -aFq "raw.githubusercontent.com/${source_repository}/${expected_commit}" "$pdb" \
    || die "${snupkg}: SourceLink URL for commit ${expected_commit} is missing from the PDB"
  rm -f "$pdb"
  trap - EXIT

  ok "${package_id} ${expected_version}"
done

echo "release packages validated: version=${expected_version} commit=${expected_commit}"
