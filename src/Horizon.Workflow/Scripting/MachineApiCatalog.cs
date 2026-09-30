namespace Horizon.Workflow.Scripting;

/// <summary>One <c>machine</c> API function — for editor help / autocomplete.</summary>
/// <param name="Table">Sub-table (e.g. <c>motion</c>), or empty for the top-level (<c>machine.log</c>).</param>
/// <param name="Name">Function name (e.g. <c>moveRel</c>).</param>
/// <param name="Signature">Full call form shown to the operator.</param>
/// <param name="Summary">One-line description.</param>
public sealed record MachineFn(string Table, string Name, string Signature, string Summary)
{
    /// <summary>Group label for the help panel — the sub-table name, or "core".</summary>
    public string Category => Table.Length == 0 ? "core" : Table;

    /// <summary>Dotted path: <c>machine.log</c> or <c>machine.motion.moveRel</c>.</summary>
    public string FullName => Table.Length == 0 ? $"machine.{Name}" : $"machine.{Table}.{Name}";
}

/// <summary>
/// Authored description of the <c>machine</c> Lua API surface — the single source of
/// truth both Layer 3 heads use for the script editor's command help and
/// autocomplete. Mirror of <see cref="MachineApi"/> (kept in sync by hand, the same
/// way <c>OpCatalog</c> tracks <c>StepDispatcher</c>). When you add a function to
/// <see cref="MachineApi"/>, add its row here.
/// </summary>
public static class MachineApiCatalog
{
    /// <summary>Every <c>machine.*</c> function, in display order.</summary>
    public static readonly IReadOnlyList<MachineFn> All = new MachineFn[]
    {
        // core
        new("", "log", "machine.log(message)", "Write a line to the output / log."),
        new("", "wait", "machine.wait(ms)", "Sleep for ms milliseconds (cancellable)."),
        new("", "ask", "machine.ask(title [, message]) -> bool", "Blocking operator yes/no; returns true for yes."),

        // motion
        new("motion", "list", "machine.motion.list() -> {names}", "Configured motion device names."),
        new("motion", "connect", "machine.motion.connect(device)", "Open the controller connection."),
        new("motion", "disconnect", "machine.motion.disconnect(device)", "Close the controller connection."),
        new("motion", "moveAbs", "machine.motion.moveAbs(device, axis, position)", "Move one axis to an absolute coordinate."),
        new("motion", "moveRel", "machine.motion.moveRel(device, axis, distance)", "Move one axis by a relative distance."),
        new("motion", "moveTo", "machine.motion.moveTo(device, position)", "Move to a named teach point (active product)."),
        new("motion", "setVelocity", "machine.motion.setVelocity(device, velocity)", "Set device velocity (mm/s, controller-aware)."),
        new("motion", "stop", "machine.motion.stop(device)", "Stop all motion on the device immediately."),
        new("motion", "position", "machine.motion.position(device) -> {x,y,z,u,v,w}", "Read the live position as a table."),

        // io
        new("io", "list", "machine.io.list() -> {names}", "Configured IO device names."),
        new("io", "connect", "machine.io.connect(device)", "Open the IO device connection."),
        new("io", "set", "machine.io.set(device, pin, state)", "Set an output by pin index (0-based)."),
        new("io", "setNamed", "machine.io.setNamed(device, pinName, state)", "Set an output by configured name."),

        // pneumatic
        new("pneumatic", "list", "machine.pneumatic.list() -> {names}", "Configured pneumatic slide names."),
        new("pneumatic", "extend", "machine.pneumatic.extend(slide)", "Extend and wait for the sensor."),
        new("pneumatic", "retract", "machine.pneumatic.retract(slide)", "Retract and wait for the sensor."),
        new("pneumatic", "extendNoWait", "machine.pneumatic.extendNoWait(slide)", "Extend without waiting for the sensor."),
        new("pneumatic", "retractNoWait", "machine.pneumatic.retractNoWait(slide)", "Retract without waiting for the sensor."),

        // smu
        new("smu", "list", "machine.smu.list() -> {names}", "Configured SMU device names."),
        new("smu", "connect", "machine.smu.connect(device)", "Open the SMU connection."),
        new("smu", "disconnect", "machine.smu.disconnect(device)", "Close the SMU connection."),
        new("smu", "sourceVoltage", "machine.smu.sourceVoltage(device, volts)", "Switch to V-source mode and set the level."),
        new("smu", "sourceCurrent", "machine.smu.sourceCurrent(device, amps)", "Switch to I-source mode and set the level."),
        new("smu", "compliance", "machine.smu.compliance(device, value)", "Set the compliance limit."),
        new("smu", "outputOn", "machine.smu.outputOn(device)", "Enable the SMU output."),
        new("smu", "outputOff", "machine.smu.outputOff(device)", "Disable the SMU output."),
        new("smu", "readCurrent", "machine.smu.readCurrent(device [, channel]) -> A", "One fresh current reading (channel default 1)."),
        new("smu", "readVoltage", "machine.smu.readVoltage(device [, channel]) -> V", "One fresh voltage reading (channel default 1)."),

        // psu
        new("psu", "list", "machine.psu.list() -> {names}", "Configured power-supply names."),
        new("psu", "connect", "machine.psu.connect(device)", "Open the supply connection."),
        new("psu", "disconnect", "machine.psu.disconnect(device)", "Close the supply connection."),
        new("psu", "setVoltage", "machine.psu.setVoltage(device [, channel], volts)", "Set a channel voltage (channel default 1)."),
        new("psu", "setCurrentLimit", "machine.psu.setCurrentLimit(device [, channel], amps)", "Set a channel current limit (channel default 1)."),
        new("psu", "outputOn", "machine.psu.outputOn(device [, channel])", "Enable a channel output (channel default 1)."),
        new("psu", "outputOff", "machine.psu.outputOff(device [, channel])", "Disable a channel output (channel default 1)."),
        new("psu", "readVoltage", "machine.psu.readVoltage(device [, channel]) -> V", "Read a channel voltage (channel default 1)."),
        new("psu", "readCurrent", "machine.psu.readCurrent(device [, channel]) -> A", "Read a channel current (channel default 1)."),

        // laser
        new("laser", "list", "machine.laser.list() -> {names}", "Configured laser-controller names."),
        new("laser", "connect", "machine.laser.connect(device)", "Open the laser-controller connection."),
        new("laser", "disconnect", "machine.laser.disconnect(device)", "Close the laser-controller connection."),
        new("laser", "setTemperature", "machine.laser.setTemperature(device, celsius)", "Set the TEC setpoint."),
        new("laser", "tecOn", "machine.laser.tecOn(device)", "Enable the laser TEC."),
        new("laser", "tecOff", "machine.laser.tecOff(device)", "Disable the laser TEC."),
        new("laser", "setCurrent", "machine.laser.setCurrent(device, milliamps)", "Set the laser diode current."),
        new("laser", "laserOn", "machine.laser.laserOn(device)", "Enable laser emission."),
        new("laser", "laserOff", "machine.laser.laserOff(device)", "Disable laser emission."),
        new("laser", "temperature", "machine.laser.temperature(device) -> C", "Read the last polled temperature."),

        // tec
        new("tec", "list", "machine.tec.list() -> {names}", "Configured TEC device names."),
        new("tec", "connect", "machine.tec.connect(device)", "Open the TEC connection."),
        new("tec", "disconnect", "machine.tec.disconnect(device)", "Close the TEC connection."),
        new("tec", "setTemperature", "machine.tec.setTemperature(device, celsius)", "Set the TEC setpoint."),
        new("tec", "tecOn", "machine.tec.tecOn(device)", "Enable the TEC."),
        new("tec", "tecOff", "machine.tec.tecOff(device)", "Disable the TEC."),
        new("tec", "readTemperature", "machine.tec.readTemperature(device) -> C", "Refresh and read the temperature."),

        // lighting
        new("lighting", "list", "machine.lighting.list() -> {names}", "Configured lighting-controller names."),
        new("lighting", "connect", "machine.lighting.connect(device)", "Open the lighting connection."),
        new("lighting", "disconnect", "machine.lighting.disconnect(device)", "Close the lighting connection."),
        new("lighting", "set", "machine.lighting.set(device, channel, intensity)", "Set a channel intensity by number."),
        new("lighting", "setNamed", "machine.lighting.setNamed(device, name, intensity)", "Set channels matching a name."),
        new("lighting", "setGroup", "machine.lighting.setGroup(device, group, intensity)", "Set every channel in a group."),
        new("lighting", "setAll", "machine.lighting.setAll(device, intensity)", "Set every channel."),
        new("lighting", "lights", "machine.lighting.lights(device) -> {names}", "Light names: channel groups + ungrouped channels."),
        new("lighting", "setLightPercent", "machine.lighting.setLightPercent(device, light, percent)",
            "Set a light to 0..100 % (0 = off)."),
        new("lighting", "setLightColor", "machine.lighting.setLightColor(device, light, color, percent)",
            "Colour call-out: 'red'/'green'/'blue' (others off) or 'white' (all channels), 0..100 %."),

        // measurement
        new("measurement", "list", "machine.measurement.list() -> {names}", "Configured measurement device names."),
        new("measurement", "connect", "machine.measurement.connect(device)", "Open the measurement connection."),
        new("measurement", "disconnect", "machine.measurement.disconnect(device)", "Close the measurement connection."),
        new("measurement", "setVoltage", "machine.measurement.setVoltage(device, volts)", "Set the source voltage."),
        new("measurement", "outputOn", "machine.measurement.outputOn(device)", "Enable the voltage output."),
        new("measurement", "outputOff", "machine.measurement.outputOff(device)", "Disable the voltage output."),
        new("measurement", "reset", "machine.measurement.reset(device)", "Reset the instrument (*RST)."),
        new("measurement", "readCurrent", "machine.measurement.readCurrent(device [, channel]) -> A", "Read a channel from the poll cache (channel default 1)."),

        // camera
        new("camera", "list", "machine.camera.list() -> {ids}", "Configured camera ids."),
        new("camera", "connect", "machine.camera.connect(camera)", "Open the camera connection."),
        new("camera", "disconnect", "machine.camera.disconnect(camera)", "Close the camera connection."),
        new("camera", "startGrab", "machine.camera.startGrab(camera)", "Start grabbing frames (lease)."),
        new("camera", "stopGrab", "machine.camera.stopGrab(camera)", "Stop grabbing frames (release lease)."),
        new("camera", "setExposure", "machine.camera.setExposure(camera, microseconds)", "Set the exposure time."),
        new("camera", "snap", "machine.camera.snap(camera) -> path", "Capture one frame to a file; returns the path."),

        // ui (Layer 3 surfaces)
        new("ui", "showImage", "machine.ui.showImage(path [, caption])", "Show a saved picture (e.g. camera.snap's path) in the head's image panel."),
        new("ui", "clearImage", "machine.ui.clearImage()", "Clear the head's image panel back to empty."),

        // vision
        new("vision", "available", "machine.vision.available() -> bool", "True if HALCON is loaded and licensed."),
        new("vision", "hasFrame", "machine.vision.hasFrame(camera) -> bool", "True if a frame is available for the camera."),
        new("vision", "analyze", "machine.vision.analyze(camera) -> {width,height,channels,minGray,meanGray,maxGray}", "Image statistics of the latest frame."),
        new("vision", "blobs", "machine.vision.blobs(camera, min, max) -> count", "Threshold + connected-component blob count."),
        new("vision", "runProfile", "machine.vision.runProfile(name) -> {found, x, y, angle, length, ...}", "Runs a saved product vision profile against its camera's latest frame; returns found + all result values keyed by name."),

        // signal
        new("signal", "read", "machine.signal.read(key) -> number", "Read a unified signal (e.g. smu.PIC.ch1.current)."),

        // part alignment (fiducials + local objects taught on Vision Alignment)
        new("part", "run", "machine.part.run(part) -> snapshot", "Camera to E1 / E2, detect, rebuild the frame, re-measure every object (vision profiles only); returns the snapshot {originX, originY, angleDeg, e1, e2, objects={ {name, kind, measured, x, y, targetX, targetY, points} }}."),
        new("part", "snapshot", "machine.part.snapshot(part) -> snapshot", "The part as it stands now (measured after a run, taught otherwise); no motion."),
        new("part", "cameraToFiducial", "machine.part.cameraToFiducial(part, 1|2)", "Move the camera over E1 or E2."),
        new("part", "cameraToObject", "machine.part.cameraToObject(part, object [, applyOffset])", "Move the camera over a local object (+ its XY offset unless applyOffset is false)."),
        new("part", "cameraToObjectPoint", "machine.part.cameraToObjectPoint(part, object, index)", "Move the camera over one point of a local object (1-based; a line has P1 and P2)."),
    };
}
