#!/usr/bin/env bash
set -euo pipefail

project_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"

if command -v godot-mono >/dev/null 2>&1; then
  exec godot-mono --path "$project_dir"
fi

if command -v godot >/dev/null 2>&1; then
  exec godot --path "$project_dir"
fi

if command -v godot4 >/dev/null 2>&1; then
  exec godot4 --path "$project_dir"
fi

if command -v flatpak >/dev/null 2>&1 && flatpak info org.godotengine.Godot >/dev/null 2>&1; then
  exec flatpak run org.godotengine.Godot --path "$project_dir"
fi

printf 'OpenCanvas 3D needs Godot 4 with C# support, usually the godot-mono command.\n' >&2
printf 'Open this project manually in Godot: %s\n' "$project_dir" >&2
exit 1
