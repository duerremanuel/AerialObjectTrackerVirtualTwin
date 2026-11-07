import { Line } from '@react-three/drei';
import { useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { useMemo, useRef } from 'react';
import type { Group, Mesh } from 'three';
import { aircraftPoseAt, flightPathOf, heightAt, type Aircraft, type Geo, type Terrain } from '../model';
import { select, useEditor } from '../store';

// Aircraft in the viewport: a stand-in shape per kind, flying its path on the browser's clock by the
// server's rule (AircraftMotion.cs), and for the selected one its path with a handle at each waypoint.
// Altitude is metres above sea level; the world's y = 0 is at geo.elevation.

export function AircraftsView({ aircraft, geo, terrain }: { aircraft: Aircraft[]; geo: Geo; terrain: Terrain }) {
  const selection = useEditor((s) => s.selection);
  return (
    <>
      {aircraft.map((a) => (
        <AircraftView key={a.id} aircraft={a} geo={geo} terrain={terrain} selected={selection?.kind === 'aircraft' && selection.id === a.id} />
      ))}
    </>
  );
}

function AircraftView({ aircraft, geo, terrain, selected }: { aircraft: Aircraft; geo: Geo; terrain: Terrain; selected: boolean }) {
  const body = useRef<Group>(null);
  const proxy = useRef<Mesh>(null);
  const camera = useThree((s) => s.camera);
  const size = useThree((s) => s.size);
  const path = useMemo(() => flightPathOf(aircraft), [aircraft]);
  const controls = useThree((s) => s.controls);

  useFrame(() => {
    const group = body.current;
    if (!group) return;
    const pose = aircraftPoseAt(path, Date.now());
    // Gone from the sky (a Restart path between flights).
    group.visible = pose !== null;
    if (!pose) return;
    group.position.set(pose.x, pose.altitude - geo.elevation, pose.z);
    group.rotation.y = (-pose.heading * Math.PI) / 180;

    // Small and far away it would be no target at all: an invisible sphere keeps one about 16 px across.
    const target = proxy.current;
    if (!target) return;
    const fov = 'fov' in camera ? camera.fov : 50;
    const worldPerPixel = (2 * camera.position.distanceTo(group.position) * Math.tan((fov * Math.PI) / 360)) / size.height;
    const radius = Math.max(aircraft.wingspan / 2, aircraft.length / 2, 8 * worldPerPixel);
    target.scale.setScalar(radius);
  });

  const onClick = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    select({ kind: 'aircraft', id: aircraft.id });
  };

  function grab(e: ThreeEvent<PointerEvent>, index: number) {
    if (e.button !== 0 || useEditor.getState().tool.kind !== 'select') return;
    e.stopPropagation();
    if (controls && 'enabled' in controls) controls.enabled = false;
    useEditor.setState({ draggedPoint: index });
    const release = () => {
      window.removeEventListener('pointerup', release);
      if (controls && 'enabled' in controls) controls.enabled = true;
      useEditor.setState({ draggedPoint: null });
    };
    window.addEventListener('pointerup', release);
  }

  const emissive = selected ? '#444433' : '#000000';
  const body1 = aircraft.color ?? (aircraft.kind === 'drone' ? '#34363c' : aircraft.kind === 'helicopter' ? '#d6dae0' : '#ecedf2');
  const trim = aircraft.color2 ?? (aircraft.kind === 'drone' ? '#f08c1e' : aircraft.kind === 'helicopter' ? '#285496' : '#c4342c');
  const l = aircraft.length;
  const w = aircraft.wingspan;

  const waypoints = aircraft.waypoints;
  const curve = path.points.map((p) => [p.x, p.altitude - geo.elevation, p.z] as const);

  return (
    <>
      <group ref={body} onClick={onClick}>
        <mesh ref={proxy} visible={false}>
          <sphereGeometry args={[1, 8, 6]} />
          <meshBasicMaterial />
        </mesh>
        {aircraft.kind === 'airplane' && (
          <>
            <mesh>
              <boxGeometry args={[0.15 * l, 0.15 * l, l]} />
              <meshLambertMaterial color={body1} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0, -0.05 * l]}>
              <boxGeometry args={[w, 0.02 * l, 0.16 * l]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0, 0.43 * l]}>
              <boxGeometry args={[0.36 * w, 0.015 * l, 0.08 * l]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0.1 * l, 0.41 * l]}>
              <boxGeometry args={[0.015 * l, 0.14 * l, 0.1 * l]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
          </>
        )}
        {aircraft.kind === 'helicopter' && (
          <>
            <mesh position={[0, 0, -0.25 * l]}>
              <sphereGeometry args={[0.18 * l, 12, 8]} />
              <meshLambertMaterial color={body1} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0, 0.225 * l]}>
              <boxGeometry args={[0.035 * l, 0.04 * l, 0.55 * l]} />
              <meshLambertMaterial color={body1} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0.15 * l, -0.2 * l]}>
              <boxGeometry args={[w, 0.008 * l, 0.025 * l]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
            <mesh position={[0, 0.15 * l, -0.2 * l]}>
              <boxGeometry args={[0.025 * l, 0.008 * l, w]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
          </>
        )}
        {aircraft.kind === 'drone' && (
          <>
            <mesh>
              <boxGeometry args={[0.3 * w, 0.35 * l, 0.6 * l]} />
              <meshLambertMaterial color={body1} emissive={emissive} />
            </mesh>
            <mesh rotation-y={Math.PI / 4}>
              <boxGeometry args={[w * 0.85, 0.04 * l, 0.06 * w]} />
              <meshLambertMaterial color="#34363c" emissive={emissive} />
            </mesh>
            <mesh rotation-y={-Math.PI / 4}>
              <boxGeometry args={[w * 0.85, 0.04 * l, 0.06 * w]} />
              <meshLambertMaterial color="#34363c" emissive={emissive} />
            </mesh>
            <mesh position={[0, 0, -0.38 * l]}>
              <boxGeometry args={[0.12 * w, 0.2 * l, 0.15 * l]} />
              <meshLambertMaterial color={trim} emissive={emissive} />
            </mesh>
          </>
        )}
      </group>
      {selected && curve.length > 1 && <Line points={curve} color="#66ccff" lineWidth={2} />}
      {selected &&
        waypoints.map((p, index) => {
          const y = p.altitude - geo.elevation;
          const ground = heightAt(terrain, p.x, p.z);
          return (
            <group key={index}>
              {/* The drop to the ground shows where under the waypoint it is. */}
              <Line points={[[p.x, ground, p.z], [p.x, y, p.z]]} color="#66ccff" lineWidth={1} transparent opacity={0.45} raycast={() => null} />
              <mesh position={[p.x, y, p.z]} onPointerDown={(e) => grab(e, index)}>
                <sphereGeometry args={[Math.max(1.2, l * 0.2), 12, 8]} />
                <meshBasicMaterial color={index === 0 ? '#44dd66' : '#66ccff'} />
              </mesh>
            </group>
          );
        })}
    </>
  );
}
