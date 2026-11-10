#!/usr/bin/env bash
# ReFix Uninstall & Restore Tool for Linux (Steam Play / Proton / Wine)
# Restores original Steam and EOS DLLs and reverts save state.
#
# Usage:
#   bash Uninstall_ReFix.sh                    # interactive (GUI or terminal prompt)
#   bash Uninstall_ReFix.sh "<game folder>"    # restore specific game folder
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AUTODEPLOY="$SCRIPT_DIR/AutoDeploy.sh"

if [ ! -f "$AUTODEPLOY" ]; then
    printf '\033[31m[ERROR]\033[0m AutoDeploy.sh not found in %s\n' "$SCRIPT_DIR" >&2
    exit 1
fi

exec bash "$AUTODEPLOY" --uninstall "$@"
