#!/usr/bin/env bash
# Test deploy of the Windows app on this machine, following the CLI's
# (scripts/deploy-cli.sh). Publishes Acrux.WinForms for win-x64 the way a
# release does (self-contained, single file) into $ACRUX_WINDOWS_HOME/<commit>
# (default ~/.local/share/acrux-windows), with "current" and "previous" like
# the CLI's, then hands "current" to this clone's delivery command, if any.
#   scripts/deploy-windows.sh            the commit the CLI's "current" holds:
#                                        the newest one that passed tests/smoke
#   scripts/deploy-windows.sh <commit>   that commit
# Per clone (git config, never committed):
#   acrux.deployWindows true      the post-commit hook runs this after the CLI
#   acrux.windowsDeliver <cmd>    run by sh with the published folder as $1,
#                                 e.g. a copy into a Windows VM. Exit 0 means
#                                 delivered; 3, target offline (skipped); any
#                                 other, failed. Both are retried on the next
#                                 deploy, and neither touches "current".
set -euo pipefail

repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
root=${ACRUX_WINDOWS_HOME:-$HOME/.local/share/acrux-windows}
cli=${ACRUX_CLI_HOME:-$HOME/.local/share/acrux-cli}
mkdir -p "$root"
log() { echo "$(date '+%F %T') windows: $*"; }

exec 9>"$root/.lock"
flock 9
if [[ -n ${1:-} ]]; then
    rev=$(git -C "$repo" rev-parse --short=12 "$1^{commit}")
elif ! rev=$(readlink "$cli/current"); then
    log "no CLI deployed yet ($cli/current): no tested commit to build"
    exit 1
fi
deployed=$(readlink "$root/current" 2>/dev/null || true)

# What the app is built from. A commit that changes none of it (docs, the CLI,
# tests) keeps the build in place: no 300 MB rebuild and nothing new to deliver.
sources() {
    git -C "$repo" rev-parse "$1:Directory.Build.props" "$1:src/Acrux.Core" \
        "$1:src/Acrux.WinForms" 2>/dev/null || echo "unknown $1"
}

build() {
    log "building $rev: $(git -C "$repo" log -1 --format=%s "$rev")"
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    git -C "$repo" archive "$rev" Directory.Build.props src/Acrux.Core src/Acrux.WinForms | tar -x -C "$work"

    rm -rf "${root:?}/$rev"
    # 9>&-: dotnet's build servers outlive the build and would hold the lock
    # (see scripts/deploy-cli.sh)
    if ! dotnet publish "$work/src/Acrux.WinForms/Acrux.WinForms.csproj" -c Release -r win-x64 \
            --self-contained true -p:PublishSingleFile=true -o "$root/$rev" -nologo -v q 9>&-; then
        rm -rf "${root:?}/$rev"
        log "build failed: $rev not deployed, current stays ${deployed:-none}"
        exit 1
    fi
    # .txt: opens with a double click on the Windows side
    git -C "$repo" log -1 --format='%h %cs %s' "$rev" > "$root/$rev/COMMIT.txt"

    [[ -n $deployed ]] && ln -sfn "$deployed" "$root/previous"
    ln -sfn "$rev" "$root/current.new"
    mv -T "$root/current.new" "$root/current"
    log "current is now $rev (previous: ${deployed:-none})"

    # Keep current and previous only (~300 MB each). Real directories only:
    # "rm -rf link/" would empty the link's target.
    for dir in "$root"/*; do
        [[ -d $dir && ! -L $dir ]] || continue
        name=$(basename "$dir")
        [[ $name == "$rev" || $name == "$deployed" ]] || rm -rf "$dir"
    done
}

if [[ $rev == "$deployed" ]]; then
    log "$rev is already current"
elif [[ -n $deployed && $(sources "$rev") == "$(sources "$deployed")" ]]; then
    log "$rev builds the same app as $deployed: kept"
else
    build
fi

deliver=$(git -C "$repo" config --get acrux.windowsDeliver || true)
current=$(readlink "$root/current" 2>/dev/null || true)
[[ -n $deliver && -n $current ]] || exit 0
[[ $current == "$(cat "$root/delivered" 2>/dev/null)" ]] && exit 0

log "delivering $current"
status=0
sh -c "$deliver" deliver "$root/$current" 9>&- || status=$?
case $status in
    0) echo "$current" > "$root/delivered"
       log "delivered $current" ;;
    3) log "delivery target offline: $current not delivered, retried on the next deploy" ;;
    *) log "delivery failed (exit $status): $current not delivered, retried on the next deploy"
       exit 1 ;;
esac
