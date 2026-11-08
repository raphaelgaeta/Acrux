#!/usr/bin/env bash
# Test deploy of the CLI on this machine. Publishes Acrux.Cli from a commit
# into $ACRUX_CLI_HOME/<commit> (default ~/.local/share/acrux-cli) and points
# "current" at it only after tests/smoke passes; "previous" keeps the version
# before, for rollback. Builds the commit itself (git archive), never the
# uncommitted files on disk.
#   scripts/deploy-cli.sh            deploy HEAD (what .githooks/post-commit runs)
#   scripts/deploy-cli.sh <commit>   deploy that commit
set -euo pipefail

repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
root=${ACRUX_CLI_HOME:-$HOME/.local/share/acrux-cli}
mkdir -p "$root"
log() { echo "$(date '+%F %T') $*"; }

# One deploy at a time. A burst of commits (a rebase) queues here, and HEAD is
# read only after the lock: the first in line deploys the newest commit, the
# rest find it already current.
exec 9>"$root/.lock"
flock 9
# Trim the log in place (same file, not a new one): the hook appends to it.
if [[ -f $root/deploy.log ]] && (( $(wc -l < "$root/deploy.log") > 1000 )); then
    tail -n 500 "$root/deploy.log" > "$root/deploy.log.tmp"
    cat "$root/deploy.log.tmp" > "$root/deploy.log"
    rm "$root/deploy.log.tmp"
fi
rev=$(git -C "$repo" rev-parse --short=12 "${1:-HEAD}^{commit}")
deployed=$(readlink "$root/current" 2>/dev/null || true)
if [[ $rev == "$deployed" ]]; then
    log "$rev is already current"
    exit 0
fi
if ! git -C "$repo" cat-file -e "$rev:tests/smoke/run.sh" 2>/dev/null; then
    log "$rev has no tests/smoke: not deployed"
    exit 1
fi
log "deploying $rev: $(git -C "$repo" log -1 --format=%s "$rev")"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
git -C "$repo" archive "$rev" Directory.Build.props src/Acrux.Core src/Acrux.Cli tests/smoke | tar -x -C "$work"

rm -rf "${root:?}/$rev"
if ! dotnet publish "$work/src/Acrux.Cli/Acrux.Cli.csproj" -c Release -r linux-x64 \
        --self-contained false -o "$root/$rev" -nologo -v q; then
    rm -rf "${root:?}/$rev"
    log "build failed: $rev not deployed, current stays ${deployed:-none}"
    exit 1
fi
git -C "$repo" log -1 --format='%h %cs %s' "$rev" > "$root/$rev/COMMIT"   # read by `acrux version`

if ! "$work/tests/smoke/run.sh" "$root/$rev/Acrux.Cli"; then
    rm -rf "${root:?}/$rev"
    log "smoke test failed: $rev not deployed, current stays ${deployed:-none}"
    exit 1
fi

# rename over the old link: there is no instant without a "current"
[[ -n $deployed ]] && ln -sfn "$deployed" "$root/previous"
ln -sfn "$rev" "$root/current.new"
mv -T "$root/current.new" "$root/current"
log "current is now $rev (previous: ${deployed:-none})"

# Keep current and previous only (~170 MB each, mostly Polars' native library).
# Real directories only: "rm -rf link/" would empty the link's target.
for dir in "$root"/*; do
    [[ -d $dir && ! -L $dir ]] || continue
    name=$(basename "$dir")
    [[ $name == "$rev" || $name == "$deployed" ]] || rm -rf "$dir"
done
