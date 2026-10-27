import { inject, Injectable, signal } from '@angular/core';
import { ApiClient } from '../../api/api-client';

/** A county outline in longitude and latitude, and where its name goes. */
export interface MapArea {
  id: string;
  name: string;
  /** Closed rings of [longitude, latitude]; a county with islands has several. */
  rings: [number, number][][];
  label: { latitude: number; longitude: number } | null;
}

/**
 * The jurisdiction's county outlines, the context layer under every point map (issue #48). Loaded once from MERIT
 * itself, so no tile service sees where cases are; a jurisdiction without outlines gets the plain grid.
 */
@Injectable({ providedIn: 'root' })
export class MapAreas {
  private readonly api = inject(ApiClient);
  private requested = false;
  readonly areas = signal<MapArea[]>([]);

  load(): void {
    if (this.requested) return;
    this.requested = true;
    this.api.boundaries().subscribe({
      next: b => this.areas.set(b.map(x => ({
        id: x.countyFips,
        name: x.name,
        rings: (x.geometry.type === 'Polygon' ? [x.geometry.coordinates] : x.geometry.coordinates)
          .flatMap(polygon => polygon.map(ring => ring.map(([lon, lat]) => [lon, lat] as [number, number]))),
        label: x.labelLatitude !== null && x.labelLongitude !== null ? { latitude: x.labelLatitude, longitude: x.labelLongitude } : null,
      }))),
      error: () => { this.requested = false; },
    });
  }
}
