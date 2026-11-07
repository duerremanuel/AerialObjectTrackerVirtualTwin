import { aircraftKindNames, toGeo, type Aircraft, type AircraftInterpolation, type AircraftKind, type AircraftPathMode, type Waypoint } from '../model';
import { aircraftPresets, removeSelected, startDrawing, updateAircraft, useEditor } from '../store';
import { BlurTextField, CheckboxField, ColorField, NumberField, SelectField, Section } from './fields';
import { InspectorTitle } from './FindButton';

const kinds = (Object.keys(aircraftKindNames) as AircraftKind[]).map((value) => ({ value, label: aircraftKindNames[value] }));
const interpolations: { value: AircraftInterpolation; label: string }[] = [
  { value: 'spline', label: 'Smooth curve (spline)' },
  { value: 'linear', label: 'Straight legs' },
];
const pathModes: { value: AircraftPathMode; label: string }[] = [
  { value: 'loop', label: 'Loop (last waypoint to first)' },
  { value: 'reverse', label: 'Reverse (fly it back)' },
  { value: 'restart', label: 'Restart (vanish, then again)' },
];

const knots = (metresPerSecond: number) => Math.round(metresPerSecond * 1.943844);

export function AircraftInspector({ aircraft }: { aircraft: Aircraft }) {
  const geo = useEditor((s) => s.world?.geo);
  const change = (patch: Partial<Aircraft>) => updateAircraft({ ...aircraft, ...patch });
  const changeWaypoint = (index: number, patch: Partial<Waypoint>) =>
    change({ waypoints: aircraft.waypoints.map((w, i) => (i === index ? { ...w, ...patch } : w)) });
  const text = (value: string | null | undefined) => value ?? '';

  // Another kind takes that kind's size, pace and emitter category too; the paint goes back to its own.
  function changeKind(kind: AircraftKind) {
    const { agl: _agl, prefix: _prefix, ...preset } = aircraftPresets[kind];
    change({ kind, ...preset, color: null, color2: null });
  }

  return (
    <>
      <InspectorTitle>
        <h2>
          {aircraftKindNames[aircraft.kind]} <span className="muted">{aircraft.id}</span>
        </h2>
      </InspectorTitle>

      <Section title="ADS-B identity">
        <CheckboxField label="Transmitting" checked={aircraft.transmitting} onChange={(transmitting) => change({ transmitting })} />
        <p className="muted">
          Off, the aircraft is still in the camera pictures but absent from the ADS-B feed: a target nobody reports.
        </p>
        <BlurTextField label="Hex (ICAO)" value={aircraft.hex} placeholder="a1b2c3" onChange={(hex) => change({ hex: hex.trim().toLowerCase() })} />
        <BlurTextField label="Callsign" value={aircraft.callsign} onChange={(callsign) => change({ callsign: callsign.trim().toUpperCase() })} />
        <BlurTextField label="Squawk" value={aircraft.squawk} placeholder="1200" onChange={(squawk) => change({ squawk: squawk.trim() })} />
        <BlurTextField label="Category" value={aircraft.category} placeholder="A1" onChange={(category) => change({ category: category.trim().toUpperCase() })} />
        <BlurTextField label="Registration" value={text(aircraft.registration)} onChange={(registration) => change({ registration: registration.trim() || null })} />
        <BlurTextField label="Type code" value={text(aircraft.typeCode)} placeholder="C172" onChange={(typeCode) => change({ typeCode: typeCode.trim().toUpperCase() || null })} />
        {aircraft.hex && (
          <p className="muted">
            Served at <code>/v2/hex/{aircraft.hex}</code>
          </p>
        )}
      </Section>

      <Section title="Flight">
        <SelectField label="Kind" value={aircraft.kind} options={kinds} onChange={changeKind} />
        <NumberField label="Speed, m/s" value={aircraft.speed} min={0} max={400} step={1} onChange={(speed) => change({ speed })} />
        <p className="muted">≈ {knots(aircraft.speed)} kn</p>
        <SelectField label="Between waypoints" value={aircraft.interpolation} options={interpolations} onChange={(interpolation) => change({ interpolation })} />
        <p className="muted">A curve needs at least three waypoints; with fewer the legs are straight.</p>
        <SelectField label="At the end" value={aircraft.path} options={pathModes} onChange={(path) => change({ path })} />
        {aircraft.path === 'restart' && (
          <NumberField label="Gone for, s" value={aircraft.gapSeconds} min={0} max={86400} step={5} onChange={(gapSeconds) => change({ gapSeconds })} />
        )}
        <NumberField label="Time offset, s" value={aircraft.offsetSeconds} step={5} onChange={(offsetSeconds) => change({ offsetSeconds })} />
      </Section>

      <Section title="Body">
        <NumberField label="Length, m" value={aircraft.length} min={0.1} max={80} step={0.1} onChange={(length) => change({ length })} />
        <NumberField label="Wingspan, m" value={aircraft.wingspan} min={0.1} max={90} step={0.1} onChange={(wingspan) => change({ wingspan })} />
        <ColorField label="Paint" value={aircraft.color ?? '#ecedf2'} onChange={(color) => change({ color })} />
        <ColorField label="Wings and tail" value={aircraft.color2 ?? '#c4342c'} onChange={(color2) => change({ color2 })} />
      </Section>

      <Section title="Waypoints">
        <p className="muted">
          Altitude is metres above sea level. Drag a waypoint's ball over the ground to move it sideways; type an altitude to
          climb or descend.
        </p>
        <ol className="points">
          {aircraft.waypoints.map((w, index) => {
            const place = geo ? toGeo(geo, w.x, w.z) : null;
            return (
              <li key={index}>
                <NumberField label={`${index + 1}: X`} value={w.x} step={5} onChange={(x) => changeWaypoint(index, { x })} />
                <NumberField label="Z" value={w.z} step={5} onChange={(z) => changeWaypoint(index, { z })} />
                <NumberField label="Altitude" value={w.altitude} min={-500} max={30000} step={10} onChange={(altitude) => changeWaypoint(index, { altitude })} />
                {place && (
                  <p className="muted">
                    {place.latitude.toFixed(6)}, {place.longitude.toFixed(6)}
                  </p>
                )}
                <button type="button" disabled={aircraft.waypoints.length <= 1} onClick={() => change({ waypoints: aircraft.waypoints.filter((_, i) => i !== index) })}>
                  Remove waypoint {index + 1}
                </button>
              </li>
            );
          })}
        </ol>
        <button type="button" onClick={() => startDrawing(aircraft.kind, aircraft.id)}>
          Add waypoints at the end
        </button>
      </Section>

      <button type="button" className="danger" onClick={removeSelected}>
        Delete aircraft
      </button>
    </>
  );
}
