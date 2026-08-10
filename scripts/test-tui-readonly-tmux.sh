#!/usr/bin/env bash
# Read-only visual smoke harness.  It never submits YES and never invokes worker mode.
set -euo pipefail

socket="vela-tui-test"
out_dir="${TMPDIR:-/tmp}/vela-tui-readonly-$$"
command="${VELA_TUI_COMMAND:-}"
mkdir -p "$out_dir"

if [[ -z "$command" ]]; then
  printf '%s\n' 'Set VELA_TUI_COMMAND to the already-built interactive Vela command.' >&2
  exit 2
fi

cleanup() {
  tmux -L "$socket" has-session -t vela 2>/dev/null && {
    tmux -L "$socket" send-keys -t vela Escape
    tmux -L "$socket" kill-session -t vela
  }
}
trap cleanup EXIT

# State capture is observational only.  Do not add shutdown, terminate, diskpart, or worker calls here.
if command -v wsl.exe >/dev/null 2>&1; then wsl.exe -l -v >"$out_dir/wsl-before.txt" || true; fi
if [[ -n "${VELA_GUARD_LOG:-}" ]]; then cp "$VELA_GUARD_LOG" "$out_dir/guard-before.txt"; fi

for size in 140x40 80x24 60x16; do
  width="${size%x*}"; height="${size#*x}"
  tmux -L "$socket" new-session -d -s vela -x "$width" -y "$height" "$command"
  sleep 1
  # Auto-preflight starts with the shell.  Keep selection on Execute and wait for its
  # read-only result before requesting its confirmation page.
  tmux -L "$socket" send-keys -t vela Down
  sleep 1
  tmux -L "$socket" send-keys -t vela Enter
  tmux -L "$socket" send-keys -t vela 'yes' Enter          # rejected: case mismatch
  tmux -L "$socket" send-keys -t vela 'YES ' Enter         # rejected: trailing space
  tmux -L "$socket" send-keys -t vela Escape               # cancellation only
  tmux -L "$socket" capture-pane -p -t vela >"$out_dir/$size.txt"
  tmux -L "$socket" send-keys -t vela Escape
  tmux -L "$socket" kill-session -t vela || true
done

if command -v wsl.exe >/dev/null 2>&1; then wsl.exe -l -v >"$out_dir/wsl-after.txt" || true; fi
if [[ -n "${VELA_GUARD_LOG:-}" ]]; then
  cp "$VELA_GUARD_LOG" "$out_dir/guard-after.txt"
  cmp "$out_dir/guard-before.txt" "$out_dir/guard-after.txt"
fi
grep -q . "$out_dir/140x40.txt"
grep -q . "$out_dir/80x24.txt"
grep -q . "$out_dir/60x16.txt"
printf '%s\n' "$out_dir"
