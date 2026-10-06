#!/usr/bin/env bash
# Fails if tracked files contain machine- or person-specific data, or files that must not be published.
# Generic patterns only: user-profile paths, e-mail addresses, binaries, local-only folders.
set -u
cd "$(dirname "$0")/.."
status=0

fail() { echo "::error::$1"; status=1; }

files=$(git ls-files)

# 1. Absolute user-profile paths (Windows, macOS, Linux). %USERPROFILE%-style placeholders are fine.
if hits=$(git grep -n -I -E '[A-Za-z]:[\/]+Users[\/]+[^\/%$<{ "]+|/(home|Users)/[a-z0-9._-]+/' -- . ':!scripts/check-hygiene.sh'); then
  fail "user-specific paths found:"; echo "$hits"
fi

# 2. E-mail addresses (noreply/example addresses are allowed).
if hits=$(git grep -n -I -E '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' -- . ':!scripts/check-hygiene.sh' \
          | grep -v -E 'noreply|example\.(com|org)'); then
  fail "e-mail addresses found:"; echo "$hits"
fi

# 3. Binaries and archives (build output, game files, BepInEx) never belong in the repo.
if hits=$(echo "$files" | grep -i -E '\.(dll|exe|pdb|zip|7z|so|dylib|assets|sav)$'); then
  fail "binary files tracked:"; echo "$hits"
fi

# 4. Local-only folders.
if hits=$(echo "$files" | grep -E '^(reverse-engine|\.cache|re|samples|dist)/'); then
  fail "local-only files tracked:"; echo "$hits"
fi

[ $status -eq 0 ] && echo "Hygiene check passed ($(echo "$files" | wc -l) tracked files)."
exit $status
