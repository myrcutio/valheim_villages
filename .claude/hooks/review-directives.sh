#!/usr/bin/env bash
# Stop-hook: review uncommitted C# changes against the project's architecture
# directives (.claude/hooks/directives.md) using a headless `claude -p` pass.
#
# Behaviour:
#   - clean / no changes  -> exit 0 (silent)
#   - violations found     -> exit 2, which under asyncRewake wakes the agent
#                             with the findings so they get fixed before the
#                             turn truly ends.
# A per-(session,diff) hash guards against re-reviewing an unchanged diff, so a
# turn that the agent could not (or did not) fix does not loop forever.
set -uo pipefail

# Recursion guard: the headless `claude -p` below disables hooks, but if that
# ever fails this prevents the review from invoking itself forever.
if [ "${VV_DIRECTIVE_REVIEW:-}" = "1" ]; then
  exit 0
fi

REPO="${CLAUDE_PROJECT_DIR:-$(pwd)}"
RUBRIC="$REPO/.claude/hooks/directives.md"

payload="$(cat)"
session_id="$(printf '%s' "$payload" | jq -r '.session_id // "nosession"' 2>/dev/null || echo nosession)"

cd "$REPO" 2>/dev/null || exit 0

# --- collect uncommitted C# changes under src/ (staged + unstaged + untracked) ---
review_input="$(git diff HEAD -- src 2>/dev/null || true)"
untracked="$(git ls-files --others --exclude-standard -- src 2>/dev/null | grep -E '\.cs$' || true)"
if [ -n "$untracked" ]; then
  while IFS= read -r f; do
    [ -z "$f" ] && continue
    review_input="${review_input}"$'\n\n=== NEW FILE: '"$f"$' ===\n'"$(cat "$f" 2>/dev/null)"
  done <<< "$untracked"
fi

# nothing to review
if [ -z "$(printf '%s' "$review_input" | tr -d '[:space:]')" ]; then
  exit 0
fi

# --- loop guard: skip if we already reviewed this exact diff this session ---
# The hash is recorded only after a verdict completes (see end), so a killed or
# empty review retries next time instead of being silently skipped forever.
diff_hash="$(printf '%s' "$review_input" | sha1sum | cut -d' ' -f1)"
state_file="${TMPDIR:-/tmp}/claude-directive-review-${session_id}"
if [ -f "$state_file" ] && [ "$(cat "$state_file" 2>/dev/null)" = "$diff_hash" ]; then
  exit 0
fi

# --- scale rigor to change size: extended thinking only for larger diffs ---
# Changed lines = added+removed in tracked files, plus all lines of new files.
added_removed="$(git diff HEAD --numstat -- src 2>/dev/null | awk '{a+=$1; r+=$2} END {print a+r+0}')"
untracked_lines=0
if [ -n "$untracked" ]; then
  while IFS= read -r f; do
    [ -z "$f" ] && continue
    n="$(wc -l < "$f" 2>/dev/null || echo 0)"
    untracked_lines=$((untracked_lines + n))
  done <<< "$untracked"
fi
changed_lines=$((added_removed + untracked_lines))
if [ "$changed_lines" -lt 100 ]; then
  review_settings='{"disableAllHooks":true,"alwaysThinkingEnabled":false}'
else
  review_settings='{"disableAllHooks":true}'
fi

CLAUDE_BIN="$(command -v claude || true)"
if [ -z "$CLAUDE_BIN" ]; then
  printf '{"systemMessage":"directive-review hook skipped: claude CLI not on PATH"}\n'
  exit 0
fi

rubric_text="$(cat "$RUBRIC" 2>/dev/null)"
if [ -z "$rubric_text" ]; then
  printf '{"systemMessage":"directive-review hook skipped: rubric missing at .claude/hooks/directives.md"}\n'
  exit 0
fi

read -r -d '' instructions <<EOF || true
You are a strict code reviewer for the Valheim Villages BepInEx mod (C#).
Review ONLY the uncommitted diff below against the project directives. Flag a
violation ONLY when the diff clearly breaks a directive; never speculate about
unchanged code, and prefer false negatives over false positives.

DIRECTIVES:
${rubric_text}

OUTPUT FORMAT (strict, no exceptions):
- If the diff fully complies, output exactly the single word: COMPLIANT
- Otherwise output a markdown bullet list and nothing else. Each bullet:
  \`path/to/File.cs:line\` — <directive # and name> — <what's wrong and the fix>.
- No preamble, no summary, no closing remarks.

UNCOMMITTED DIFF:
EOF

# Headless review. Run from a clean temp dir so the nested session does NOT load
# this project's large CLAUDE.md / auto-memory (which makes startup hang). The
# prompt is self-contained (the diff is embedded), so cwd is irrelevant. Disable
# MCP servers (slow/hanging startup) and ALL hooks (stops this Stop hook from
# re-firing inside the nested session).
review_dir="$(mktemp -d 2>/dev/null || echo /tmp)"
result="$(printf '%s\n%s' "$instructions" "$review_input" | ( cd "$review_dir" && VV_DIRECTIVE_REVIEW=1 "$CLAUDE_BIN" -p \
  --strict-mcp-config --mcp-config '{"mcpServers":{}}' \
  --settings "$review_settings" \
  --model sonnet ) 2>/dev/null || true)"
[ "$review_dir" != "/tmp" ] && rmdir "$review_dir" 2>/dev/null || true
trimmed="$(printf '%s' "$result" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"

if [ -z "$trimmed" ]; then
  printf '{"systemMessage":"directive-review hook: review produced no output; skipped"}\n'
  exit 0
fi

# A verdict completed — record this diff so an unchanged tree is not re-reviewed.
printf '%s' "$diff_hash" > "$state_file"

case "$trimmed" in
  COMPLIANT*) exit 0 ;;
esac

printf 'The uncommitted changes violate project directives (.claude/hooks/directives.md). Fix these before finishing:\n\n%s\n' "$trimmed"
exit 2
