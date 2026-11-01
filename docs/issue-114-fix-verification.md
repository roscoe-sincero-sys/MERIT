# Issue #114 verification

Verified against merged `origin/main` at `d945ac5`. All findings remain valid: nearby threshold text overlaps, circles have no individual exact-value labels or links, endpoint dates omit days, and reference levels are selected for today without a historical caveat.

## Recommendation checks and implementation

- A value of 45 gives a vertical scale near 54; levels 3.5 and 5 are separated by only about five pixels. Staggering text would require leader lines to avoid implying different threshold positions and still has limited vertical space for many levels. The alternative is a wrapping HTML legend below the SVG. Threshold lines retain exact value positions and individual hover titles; full labels include their effective dates and units in the legend.
- Each point now includes an encoded title and accessible link label with exact collection date, operator, value, unit and sample type. Points link to their result details and have a visible keyboard focus indicator. The SVG uses a named group so its child links remain exposed to assistive technology. Axis endpoints include the day.
- Reference notes identify the current reference date and population and explicitly state that the chart does not reconstruct historical classifications. An additional warning appears when a plotted result predates a displayed level's effective date. Current effective dates are visible in the legend; the chart does not imply retrospective reclassification.
- Charts are separated by specimen and population so one population's levels are not applied across another population's results. Each chart has a unique SVG title ID.

## Validation

The .NET build and all 173 unit tests passed. The new SVG regression covers dense levels, exact point labels/links, precise endpoint dates and text encoding. The main integration run passed 73 tests; the known intermittent session-renewal failure passed its isolated rerun. The new historical-chart integration test also passed, verifying reference warnings and links for an older result. All five selected person/trend/timeline browser and accessibility checks passed, including keyboard navigation to result detail. Git whitespace checks passed.

AI-assisted implementation: OpenAI Codex.
