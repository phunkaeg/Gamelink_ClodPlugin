# IL-2 Cliffs of Dover Telemetry Experiments

This project explores telemetry extraction from IL-2 Sturmovik: Cliffs of Dover Blitz / Desert Wings Tobruk for use with:

- motion platforms
- YawVR GameLink
- vibration / transducer effects
- wind effects
- telemetry visualization tools
- future bridge applications for other motion/haptics software

## Project Goal

The original goal was to determine whether modern Team Fusion builds of Cliffs of Dover still exposed any usable telemetry for motion platform use.

For a long time, I assumed that telemetry support had effectively disappeared after the Team Fusion source-code handover. Old forum discussions suggested that earlier versions of Cliffs had a DeviceLink-style interface, but it was unclear how much of that still existed or functioned in current builds.

This project set out to answer a few practical questions:

1. Does Cliffs of Dover still expose any live telemetry externally?
2. If so, is it enough to drive a motion platform?
3. Can that telemetry be integrated into YawVR GameLink?
4. Can we identify additional useful channels for vibration, wind, and other effects?
5. Can the old CLODDeviceLink interface be revived or better understood?

## What Was Discovered

### CLODDeviceLink still exists

The biggest breakthrough was confirming that the old memory-mapped telemetry object:

- CLODDeviceLink

still exists and is live in current builds of Cliffs of Dover.

A small probe application was written to test whether the MMF still existed, and it immediately began returning changing values while in flight.

### Confirmed orientation telemetry

The first major confirmed working slots were:

- 840 = yaw / heading-like value
- 841 = pitch
- 842 = roll

These values are live and usable, and they are already enough to drive a motion platform in a basic orientation-following mode.

Observed behavior:

- roll wraps correctly through +180 / -180
- pitch appears to be nose-down positive / nose-up negative
- yaw behaves more like heading than rudder input or yaw rate

### The old schema only partially survived

Based on the old DeviceLink formula:

slot = ((int)ParameterType * 10) + SubType

it appeared that the old layout should have included:

- Z_Coordinates
- Z_Orientation
- Z_Overload
- Z_AltitudeAGL
- Z_AltitudeMSL
- Z_VelocityIAS
- Z_VelocityTAS

While Z_Orientation still lined up correctly, much of the surrounding old schema did not.

That led to the conclusion that:

- some of the old layout survived
- some of it moved
- some of it may have been lost or changed between versions
- some values may depend on instrument availability, subtype selection, or old implementation details

## Slot Scanning and Reverse Discovery

A scanner was built to search the live MMF for changing values during different maneuvers and engine states.

A number of promising candidate slots were found, including:

- 1769 = heading-like 0..360
- 1609 = speed-like
- 1480 = engine RPM-like
- 1619 = altitude-like
- 1629 = variometer / vertical-speed-like
- 1639 = slip-like
- 1749 = turn-like
- 1650 / 1651 / 1652 = compass-related
- 1761 / 1762 = attitude-indicator / instrument-like

Later, an old Cliffs of Dover Patch 3.0 DeviceLink Raw Data reader application was found, along with archived forum posts. That turned out to be extremely useful because it showed that many of these old instrument-style channels were in fact still producing live data.

## Archived Forum / DeviceLink Research

Old forum posts and code samples helped reconstruct the original intent of the system.

Important findings:

- Team Fusion had previously exposed a memory-mapped readonly DeviceLink-style interface
- the interface used the same parameter-style structure as Cliffs mission scripting
- many I_* values appear to be instrument-derived rather than pure physics values
- some values may require subtype -1 rather than subtype 0
- some values may change units depending on aircraft type or instrument implementation

This explains why some values behaved unexpectedly and why the old schema did not always work as originally assumed.

## YawVR GameLink Plugin

A custom IL2CloDPlugin for YawVR GameLink was built as part of this project.

### Current plugin capabilities

The plugin currently:

- reads CLODDeviceLink directly
- exposes confirmed orientation channels
- exposes several newly identified instrument / legacy telemetry channels
- provides soft-limited pose channels for motion use
- includes pause detection based on frozen telemetry
- includes smooth settle / hold while paused
- includes smooth resume blending
- returns smoothly to home on timeout

### Working motion profile

A simple and working GameLink profile has already been tested with the YawVR emulator using:

- Pitch_Soft -> PITCH
- Roll_Soft -> ROLL
- Yaw_Signed_840 -> YAW

This works well enough to prove the end-to-end telemetry path from Cliffs of Dover to GameLink.

## Telemetry Visualization Tools

A simple viewer application was also built to confirm and visualize the live orientation telemetry outside of GameLink.

This helped verify:

- live connection to CLODDeviceLink
- valid changing yaw/pitch/roll data
- consistent behavior during flight

## Current Priorities

The next main goals are:

- improve the plugin with better force-based motion cues
- use newly recovered telemetry more effectively
- incorporate Z_Overload / acceleration if re-exposed by source-code updates
- use RPM / IAS / slip / turn / variometer data for richer effects
- explore vibration, wind, and transducer outputs
- potentially build telemetry bridges for other software such as Sim Racing Studio

## Long-Term Vision

The long-term goal is not just to copy aircraft orientation, but to move toward force-based motion cueing:

- motion based on what the pilot would actually feel
- not just what the aircraft looks like in world space

That means this project is moving toward:

- better derived channels
- acceleration / overload use
- dynamic cueing
- transducer support
- wind support
- cleaner telemetry abstraction for future bridges and tools

## Status

This project has already proven that:

- Cliffs of Dover still exposes live telemetry
- the old CLODDeviceLink path is still usable
- orientation telemetry is confirmed working
- a working YawVR GameLink plugin can be built on top of it
- additional useful telemetry channels are still present and can be identified

In other words: Cliffs of Dover telemetry is very much alive — it just needed rediscovering.
