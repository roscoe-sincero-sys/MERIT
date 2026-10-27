"""County outlines for the map's context layer (issue #48).

Writes the EXEC calls that load a GeoJSON FeatureCollection into config.subJurisdiction through
config.uspSetSubJurisdictionBoundary. Each feature needs properties countyFips (3 digits) and, optionally, labelLatitude
and labelLongitude (a point inside the outline for the name); its geometry must be a Polygon or MultiPolygon in WGS 84.

A jurisdiction loads its own outlines at install, as the demo does for Maryland:

    python boundaries.py data/mdCountyBoundaries.geojson MD-LEAD > boundaries.sql
    sqlcmd -S <server> -d MERIT -E -b -i boundaries.sql

Simplify the outlines first (a few hundred points per county is plenty); the Census Bureau's cartographic boundary files
and its TIGERweb service (maxAllowableOffset) both serve simplified outlines.
"""
import json
import sys
from pathlib import Path


def sql_literal(text: str) -> str:
    return "N'" + text.replace("'", "''") + "'"


def boundary_sql(path: Path, tenant_code: str) -> str:
    collection = json.loads(path.read_text(encoding="utf-8"))
    out = []
    for feature in collection["features"]:
        props = feature["properties"]
        geometry = json.dumps(feature["geometry"], separators=(",", ":"))
        lat, lon = props.get("labelLatitude"), props.get("labelLongitude")
        out.append(f"EXEC config.uspSetSubJurisdictionBoundary @tenantCode = {sql_literal(tenant_code)}, "
                   f"@countyFips = '{props['countyFips']}', "
                   f"@labelLatitude = {'NULL' if lat is None else lat}, @labelLongitude = {'NULL' if lon is None else lon},\n"
                   f"     @boundary = {sql_literal(geometry)};")
    return "\n".join(out) + "\nGO\n"


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: python boundaries.py <outlines.geojson> <root tenant code>")
    sys.stdout.write(boundary_sql(Path(sys.argv[1]), sys.argv[2]))
