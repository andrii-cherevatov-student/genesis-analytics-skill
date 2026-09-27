#!/usr/bin/env sh
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/.build/wiki-interest.dll"
if [ ! -f "$DLL" ] || [ -n "$(find "$ROOT/src" \( -name bin -o -name obj \) -prune -o -type f -newer "$DLL" -print 2>/dev/null | head -n 1)" ]; then
  dotnet build "$ROOT/src/WikipediaInterestSkill/WikipediaInterestSkill.csproj" -c Release -o "$ROOT/.build" --nologo -v q >&2
fi
exec dotnet "$DLL" "$@"
