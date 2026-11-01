# Issue #113 verification

Verified against merged `origin/main` at `c139d81`. All four findings remain valid: map classifications differ only in color, cluster map points have no pick handler, map/timeline SVG buttons omit Space activation, and above-point tooltip transforms can clip content at the map edges.

The recommended changes are implemented:

- Confirmed, suspect and not-a-case markers use circle, triangle and square shapes, with matching SVG symbols in the legend. A dark outline preserves the shape boundary independently of fill color; the existing flag ring remains separate.
- Cluster map picks open the selected case's detail page. The case table's ordinary links remain available.
- Map points and timeline point/interval buttons activate with Enter or Space. Keyboard activation prevents the browser's default scrolling, and held-key repeat events do not trigger repeated activations.
- Tooltips are measured after rendering, placed above the point when possible or below it otherwise, and clamped within the visible map area. Width and height are constrained to the plot. Positioning reacts to the selected point, zoom/pan and map resizing.

Validation: Angular production build and .NET web build passed. All 15 selected map, cluster, timeline and accessibility browser checks passed. New coverage verifies distinct/matching shapes, tooltip bounds, cluster case navigation with Space, and Space activation of both timeline points and intervals without scrolling. Existing zoom/pan, county context, compact cluster map, filter history and unlike-analyte timeline checks also passed. Git whitespace checks passed. Backend/database code is unchanged.

AI-assisted implementation: OpenAI Codex.
