# Returned-error review — September 18, 2026

Reviewed current CLI, Dataset validation, session parsing, simulation parsing,
and dry-run failure messages against the development rules in AGENTS.md.
Stable codes and structured paths remain the machine-readable interface; message
wording may improve without changing generator semantics or schema versions.

## Changes

- Malformed JSON reports safe, one-based line and byte positions when available,
  with syntax/nesting guidance. Positions count bytes, not displayed characters.
  Raw exception messages and input-derived parser paths remain suppressed.
- Invalid Unicode explains how to repair unpaired surrogate escapes.
- Random-integer fields report their exact accepted bounds. Duration fields also
  identify milliseconds, including nested hold-duration ranges.
- Boolean timeline duration errors report remaining cumulative capacity in
  milliseconds. This bound reflects valid earlier steps processed by the parser.
- Invalid numeric bounds no longer produce a spurious range-ordering error from
  fallback zeros.
- Schedule feasibility, total-hold limits, and sequence/staircase overflow errors
  explain which durations or steps to adjust.
- Missing sequence/gate patterns list supported kinds; missing generators direct
  callers to add the matching generator or fix its earlier validation errors.
- File-size and encoding failures give the byte limit or a concrete save action.

Other current messages already identify expected types, allowed literals, field
requirements, or corrections through their message and structured path. This is
an offline error review, not coverage of future network/persistence failures.
Cascading validation errors can still follow an earlier malformed field; correct
earlier errors first and rerun. At most 100 diagnostics and one omission notice
are retained. No rejected values, unknown property names, or raw documents are
returned in diagnostics.

Nine new regression cases cover explicit bounds and units, nested locations,
remaining timeline capacity, safe JSON positions, Unicode repair guidance, and
sensitive-input non-disclosure. Existing CLI tests cover failure exit codes,
partial-output suppression, and bounded responses.
