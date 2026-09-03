using Horizon.Common;
using Horizon.Engine;
using MoonSharp.Interpreter;

namespace Horizon.Workflow.Scripting;

/// <summary>
/// Resolves a named teach-point and moves a device to it. Supplied by a Layer 2
/// host (<c>IProcessService</c>) so <c>machine.motion.moveTo</c> can reach the
/// active product's positions. Null in engine-only contexts (e.g. the console
/// test host), where <c>moveTo</c> reports that named positions are unavailable.
/// </summary>
public delegate Task<Result> NamedPositionMover(string device, string position, CancellationToken ct);

/// <summary>
/// Reads one unified signal by key (e.g. <c>smu.PIC.ch1.current</c>) — the same
/// catalog the <c>signal.read</c> workflow step uses. Supplied by a Layer 2 host
/// (<c>IProcessService</c>) so <c>machine.signal.read</c> reaches built-in engine
/// signals plus any product source (FAAM's SiPhOG). Null in engine-only contexts.
/// </summary>
public delegate Task<Result<double>> SignalReader(string key, CancellationToken ct);

/// <summary>
/// Runs a saved per-product vision profile by name against the latest cached frame
/// from the profile's camera, returning the method-agnostic detection result. Supplied
/// by a Layer 2 host (<c>IProcessService</c>) so <c>machine.vision.runProfile</c> reaches
/// the active product's profiles. Null in engine-only contexts (the console test host),
/// where <c>runProfile</c> reports that product vision profiles are unavailable.
/// </summary>
public delegate Task<Result<VisionDetectResult>> VisionProfileRunner(string profileName);

/// <summary>
/// The part-alignment seam a Layer 2 host hands to scripts: run the alignment (vision points
/// only — no operator), read the part snapshot, and the two camera moves. Null in engine-only
/// contexts, where <c>machine.part.*</c> reports that part alignment is unavailable.
/// </summary>
public sealed record PartAlignmentScriptHost(
    Func<string, CancellationToken, Task<Result<Registration.PartMeasurement>>> Run,
    Func<string, Result<Registration.PartSnapshot>> Snapshot,
    Func<string, int, CancellationToken, Task<Result>> CameraToFiducial,
    Func<string, string, bool, CancellationToken, Task<Result>> CameraToObject,
    Func<string, string, int, CancellationToken, Task<Result>> CameraToObjectPoint);

/// <summary>
/// Builds the global <c>machine</c> Lua table and binds its functions straight to
/// <see cref="IHorizonEngine"/> facet calls — the bridge between a <c>.moon</c>
/// script and the hardware, modelled on <c>RecipeMethodRegistry</c>.
///
/// Each bound function is synchronous from the script's point of view: it blocks
/// on the underlying async facet call. That is safe because the whole script runs
/// on a background worker thread (see <see cref="MoonScriptEngine"/>) — the UI
/// thread is never touched here. Every call checks the run's cancellation token
/// first, and a failing <see cref="Result"/> is raised as a Lua runtime error
/// (so a script may guard it with <c>pcall</c>).
///
/// Phase 1 surface: system helpers + motion, IO, pneumatic. The remaining facets
/// (SMU, PSU, laser, TEC, lighting, measurement, camera, vision) and the unified
/// <c>signal.read</c> are Phase 2 — add them as new sub-tables here.
/// </summary>
internal sealed class MachineApi
{
    private readonly Script _script;
    private readonly IHorizonEngine _engine;
    private readonly NamedPositionMover? _mover;
    private readonly SignalReader? _signalReader;
    private readonly VisionProfileRunner? _visionRunner;
    private readonly IImageDisplaySink? _imageDisplay;
    private readonly IOperatorPrompt? _prompt;
    private readonly PartAlignmentScriptHost? _part;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;

    public MachineApi(
        Script script,
        IHorizonEngine engine,
        NamedPositionMover? mover,
        SignalReader? signalReader,
        VisionProfileRunner? visionRunner,
        IImageDisplaySink? imageDisplay,
        IOperatorPrompt? prompt,
        Action<string> log,
        CancellationToken ct,
        PartAlignmentScriptHost? part = null)
    {
        _script = script;
        _engine = engine;
        _mover = mover;
        _signalReader = signalReader;
        _visionRunner = visionRunner;
        _imageDisplay = imageDisplay;
        _prompt = prompt;
        _part = part;
        _log = log;
        _ct = ct;
    }

    /// <summary>Installs the <c>machine</c> global into the script's environment.</summary>
    public void Install()
    {
        var machine = new Table(_script);
        InstallSystem(machine);
        machine["motion"] = DynValue.NewTable(BuildMotion());
        machine["io"] = DynValue.NewTable(BuildIo());
        machine["pneumatic"] = DynValue.NewTable(BuildPneumatic());
        machine["smu"] = DynValue.NewTable(BuildSmu());
        machine["psu"] = DynValue.NewTable(BuildPsu());
        machine["laser"] = DynValue.NewTable(BuildLaser());
        machine["tec"] = DynValue.NewTable(BuildTec());
        machine["lighting"] = DynValue.NewTable(BuildLighting());
        machine["measurement"] = DynValue.NewTable(BuildMeasurement());
        machine["camera"] = DynValue.NewTable(BuildCamera());
        machine["vision"] = DynValue.NewTable(BuildVision());
        machine["signal"] = DynValue.NewTable(BuildSignal());
        machine["ui"] = DynValue.NewTable(BuildUi());
        machine["part"] = DynValue.NewTable(BuildPart());
        _script.Globals["machine"] = DynValue.NewTable(machine);
    }

    // --- top-level helpers: machine.wait / machine.log / machine.ask ---

    private void InstallSystem(Table machine)
    {
        machine["log"] = Cb(a =>
        {
            _log(ReqStr(a, 0, "machine.log"));
            return DynValue.Nil;
        });

        machine["wait"] = Cb(a =>
        {
            int ms = (int)ReqNum(a, 0, "machine.wait");
            // A token-aware, non-throwing sleep: returns early if cancelled, then we
            // stop the script cleanly (no TaskCanceledException escaping the interpreter).
            if (ms > 0) _ct.WaitHandle.WaitOne(ms);
            StopIfCancelled();
            return DynValue.Nil;
        });

        machine["ask"] = Cb(a =>
        {
            if (_prompt == null)
                throw new ScriptRuntimeException("machine.ask needs an operator-prompt host (none registered)");
            string title = ReqStr(a, 0, "machine.ask");
            string message = a.Count > 1 ? ReqStr(a, 1, "machine.ask") : title;
            bool ok = Block(_prompt.ConfirmAsync(title, message, _ct));
            return DynValue.NewBoolean(ok);
        });
    }

    // --- motion ---

    private Table BuildMotion()
    {
        var m = new Table(_script);

        m["list"] = Cb(_ => Names(_engine.Motion.DeviceNames));
        m["connect"] = Cb(a => Do(_engine.Motion.ConnectAsync(Dev(a, "motion.connect"), _ct),
            $"motion.connect({Dev(a, "motion.connect")})"));
        m["disconnect"] = Cb(a => Do(_engine.Motion.DisconnectAsync(Dev(a, "motion.disconnect")),
            $"motion.disconnect({Dev(a, "motion.disconnect")})"));

        m["moveAbs"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "motion.moveAbs");
            string axis = ReqStr(a, 1, "motion.moveAbs");
            double pos = ReqNum(a, 2, "motion.moveAbs");
            return Do(_engine.Motion.MoveAbsoluteAsync(dev, axis, pos, _ct),
                $"motion.moveAbs({dev},{axis},{pos})");
        });

        m["moveRel"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "motion.moveRel");
            string axis = ReqStr(a, 1, "motion.moveRel");
            double dist = ReqNum(a, 2, "motion.moveRel");
            return Do(_engine.Motion.MoveRelativeAsync(dev, axis, dist, _ct),
                $"motion.moveRel({dev},{axis},{dist})");
        });

        m["moveTo"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "motion.moveTo");
            string position = ReqStr(a, 1, "motion.moveTo");
            if (_mover == null)
                throw new ScriptRuntimeException(
                    "motion.moveTo needs a Layer 2 host — named positions are unavailable in this context");
            return Do(_mover(dev, position, _ct), $"motion.moveTo({dev},{position})");
        });

        m["setVelocity"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "motion.setVelocity");
            double vel = ReqNum(a, 1, "motion.setVelocity");
            return Do(_engine.Motion.SetDeviceVelocityAsync(dev, vel), $"motion.setVelocity({dev},{vel})");
        });

        m["stop"] = Cb(a => Do(_engine.Motion.StopAsync(Dev(a, "motion.stop")),
            $"motion.stop({Dev(a, "motion.stop")})"));

        m["position"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "motion.position");
            var r = _engine.Motion.GetPositionAsync(dev).GetAwaiter().GetResult();
            if (!r.IsSuccess) throw new ScriptRuntimeException($"motion.position failed: {r.Message}");
            return PositionTable(r.Value);
        });

        return m;
    }

    // --- digital IO ---

    private Table BuildIo()
    {
        var io = new Table(_script);

        io["list"] = Cb(_ => Names(_engine.IO.DeviceNames));
        io["connect"] = Cb(a => Do(_engine.IO.ConnectAsync(Dev(a, "io.connect"), _ct),
            $"io.connect({Dev(a, "io.connect")})"));

        io["set"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "io.set");
            int pin = (int)ReqNum(a, 1, "io.set");
            bool state = ReqBool(a, 2, "io.set");
            return Do(_engine.IO.SetOutputAsync(dev, pin, state, _ct), $"io.set({dev},{pin},{state})");
        });

        io["setNamed"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "io.setNamed");
            string pinName = ReqStr(a, 1, "io.setNamed");
            bool state = ReqBool(a, 2, "io.setNamed");
            return Do(_engine.IO.SetOutputByNameAsync(dev, pinName, state, _ct),
                $"io.setNamed({dev},{pinName},{state})");
        });

        return io;
    }

    // --- pneumatic slides ---

    private Table BuildPneumatic()
    {
        var p = new Table(_script);

        p["list"] = Cb(_ => Names(_engine.Pneumatic.SlideNames));
        p["extend"] = Cb(a => Do(_engine.Pneumatic.ExtendAndWaitAsync(Dev(a, "pneumatic.extend"), _ct),
            $"pneumatic.extend({Dev(a, "pneumatic.extend")})"));
        p["retract"] = Cb(a => Do(_engine.Pneumatic.RetractAndWaitAsync(Dev(a, "pneumatic.retract"), _ct),
            $"pneumatic.retract({Dev(a, "pneumatic.retract")})"));
        p["extendNoWait"] = Cb(a => Do(_engine.Pneumatic.ExtendAsync(Dev(a, "pneumatic.extendNoWait"), _ct),
            $"pneumatic.extendNoWait({Dev(a, "pneumatic.extendNoWait")})"));
        p["retractNoWait"] = Cb(a => Do(_engine.Pneumatic.RetractAsync(Dev(a, "pneumatic.retractNoWait"), _ct),
            $"pneumatic.retractNoWait({Dev(a, "pneumatic.retractNoWait")})"));

        return p;
    }

    // --- SMU (source-measure unit) ---

    private Table BuildSmu()
    {
        var s = new Table(_script);

        s["list"] = Cb(_ => Names(_engine.Smu.DeviceNames));
        s["connect"] = Cb(a => Do(_engine.Smu.ConnectAsync(Dev(a, "smu.connect"), _ct),
            $"smu.connect({Dev(a, "smu.connect")})"));
        s["disconnect"] = Cb(a => Do(_engine.Smu.DisconnectAsync(Dev(a, "smu.disconnect")),
            $"smu.disconnect({Dev(a, "smu.disconnect")})"));

        s["sourceVoltage"] = Cb(a => Do(
            _engine.Smu.SetSourceVoltageAsync(ReqStr(a, 0, "smu.sourceVoltage"), ReqNum(a, 1, "smu.sourceVoltage")),
            $"smu.sourceVoltage({ReqStr(a, 0, "smu.sourceVoltage")},{ReqNum(a, 1, "smu.sourceVoltage")})"));
        s["sourceCurrent"] = Cb(a => Do(
            _engine.Smu.SetSourceCurrentAsync(ReqStr(a, 0, "smu.sourceCurrent"), ReqNum(a, 1, "smu.sourceCurrent")),
            $"smu.sourceCurrent({ReqStr(a, 0, "smu.sourceCurrent")},{ReqNum(a, 1, "smu.sourceCurrent")})"));
        s["compliance"] = Cb(a => Do(
            _engine.Smu.SetComplianceAsync(ReqStr(a, 0, "smu.compliance"), ReqNum(a, 1, "smu.compliance")),
            $"smu.compliance({ReqStr(a, 0, "smu.compliance")},{ReqNum(a, 1, "smu.compliance")})"));
        s["outputOn"] = Cb(a => Do(_engine.Smu.OutputOnAsync(Dev(a, "smu.outputOn")),
            $"smu.outputOn({Dev(a, "smu.outputOn")})"));
        s["outputOff"] = Cb(a => Do(_engine.Smu.OutputOffAsync(Dev(a, "smu.outputOff")),
            $"smu.outputOff({Dev(a, "smu.outputOff")})"));

        s["readCurrent"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "smu.readCurrent");
            var r = Val(_engine.Smu.ReadMeasurementAsync(dev, OptInt(a, 1, 1)), $"smu.readCurrent({dev})");
            return DynValue.NewNumber(r.Current);
        });
        s["readVoltage"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "smu.readVoltage");
            var r = Val(_engine.Smu.ReadMeasurementAsync(dev, OptInt(a, 1, 1)), $"smu.readVoltage({dev})");
            return DynValue.NewNumber(r.Voltage);
        });

        return s;
    }

    // --- power supply ---

    private Table BuildPsu()
    {
        var p = new Table(_script);

        p["list"] = Cb(_ => Names(_engine.PowerSupply.DeviceNames));
        p["connect"] = Cb(a => Do(_engine.PowerSupply.ConnectAsync(Dev(a, "psu.connect"), _ct),
            $"psu.connect({Dev(a, "psu.connect")})"));
        p["disconnect"] = Cb(a => Do(_engine.PowerSupply.DisconnectAsync(Dev(a, "psu.disconnect")),
            $"psu.disconnect({Dev(a, "psu.disconnect")})"));

        // Channel is optional: setVoltage(dev, volts) targets CH1;
        // setVoltage(dev, channel, volts) targets a specific channel on a multi-output supply.
        p["setVoltage"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "psu.setVoltage");
            var (ch, volts) = ChannelValue(a, "psu.setVoltage");
            return Do(_engine.PowerSupply.SetVoltageAsync(dev, ch, volts), $"psu.setVoltage({dev},CH{ch},{volts})");
        });
        p["setCurrentLimit"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "psu.setCurrentLimit");
            var (ch, amps) = ChannelValue(a, "psu.setCurrentLimit");
            return Do(_engine.PowerSupply.SetCurrentLimitAsync(dev, ch, amps),
                $"psu.setCurrentLimit({dev},CH{ch},{amps})");
        });
        p["outputOn"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "psu.outputOn");
            int ch = OptInt(a, 1, 1);
            return Do(_engine.PowerSupply.OutputOnAsync(dev, ch), $"psu.outputOn({dev},CH{ch})");
        });
        p["outputOff"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "psu.outputOff");
            int ch = OptInt(a, 1, 1);
            return Do(_engine.PowerSupply.OutputOffAsync(dev, ch), $"psu.outputOff({dev},CH{ch})");
        });

        p["readVoltage"] = Cb(a => DynValue.NewNumber(PsuChannel(a, "psu.readVoltage").Voltage));
        p["readCurrent"] = Cb(a => DynValue.NewNumber(PsuChannel(a, "psu.readCurrent").Current));

        return p;
    }

    // --- laser controller ---

    private Table BuildLaser()
    {
        var l = new Table(_script);

        l["list"] = Cb(_ => Names(_engine.Laser.DeviceNames));
        l["connect"] = Cb(a => Do(_engine.Laser.ConnectAsync(Dev(a, "laser.connect"), _ct),
            $"laser.connect({Dev(a, "laser.connect")})"));
        l["disconnect"] = Cb(a => Do(_engine.Laser.DisconnectAsync(Dev(a, "laser.disconnect")),
            $"laser.disconnect({Dev(a, "laser.disconnect")})"));

        l["setTemperature"] = Cb(a => Do(
            _engine.Laser.SetTemperatureAsync(ReqStr(a, 0, "laser.setTemperature"), ReqNum(a, 1, "laser.setTemperature")),
            $"laser.setTemperature({ReqStr(a, 0, "laser.setTemperature")},{ReqNum(a, 1, "laser.setTemperature")})"));
        l["tecOn"] = Cb(a => Do(_engine.Laser.TecOnAsync(Dev(a, "laser.tecOn")), $"laser.tecOn({Dev(a, "laser.tecOn")})"));
        l["tecOff"] = Cb(a => Do(_engine.Laser.TecOffAsync(Dev(a, "laser.tecOff")), $"laser.tecOff({Dev(a, "laser.tecOff")})"));
        l["setCurrent"] = Cb(a => Do(
            _engine.Laser.SetLaserCurrentAsync(ReqStr(a, 0, "laser.setCurrent"), ReqNum(a, 1, "laser.setCurrent")),
            $"laser.setCurrent({ReqStr(a, 0, "laser.setCurrent")},{ReqNum(a, 1, "laser.setCurrent")}mA)"));
        l["laserOn"] = Cb(a => Do(_engine.Laser.LaserOnAsync(Dev(a, "laser.laserOn")), $"laser.laserOn({Dev(a, "laser.laserOn")})"));
        l["laserOff"] = Cb(a => Do(_engine.Laser.LaserOffAsync(Dev(a, "laser.laserOff")), $"laser.laserOff({Dev(a, "laser.laserOff")})"));

        l["temperature"] = Cb(a => DynValue.NewNumber(_engine.Laser.GetState(ReqStr(a, 0, "laser.temperature")).Temperature));

        return l;
    }

    // --- TEC (standalone) ---

    private Table BuildTec()
    {
        var t = new Table(_script);

        t["list"] = Cb(_ => Names(_engine.Tec.DeviceNames));
        t["connect"] = Cb(a => Do(_engine.Tec.ConnectAsync(Dev(a, "tec.connect"), _ct),
            $"tec.connect({Dev(a, "tec.connect")})"));
        t["disconnect"] = Cb(a => Do(_engine.Tec.DisconnectAsync(Dev(a, "tec.disconnect")),
            $"tec.disconnect({Dev(a, "tec.disconnect")})"));

        t["setTemperature"] = Cb(a => Do(
            _engine.Tec.SetTemperatureAsync(ReqStr(a, 0, "tec.setTemperature"), ReqNum(a, 1, "tec.setTemperature")),
            $"tec.setTemperature({ReqStr(a, 0, "tec.setTemperature")},{ReqNum(a, 1, "tec.setTemperature")})"));
        t["tecOn"] = Cb(a => Do(_engine.Tec.TecOnAsync(Dev(a, "tec.tecOn")), $"tec.tecOn({Dev(a, "tec.tecOn")})"));
        t["tecOff"] = Cb(a => Do(_engine.Tec.TecOffAsync(Dev(a, "tec.tecOff")), $"tec.tecOff({Dev(a, "tec.tecOff")})"));

        t["readTemperature"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "tec.readTemperature");
            _engine.Tec.RefreshAsync().GetAwaiter().GetResult();
            var st = _engine.Tec.GetState(dev);
            if (!st.Valid || double.IsNaN(st.Temperature))
                throw new ScriptRuntimeException($"tec.readTemperature: '{dev}' has no reading yet");
            return DynValue.NewNumber(st.Temperature);
        });

        return t;
    }

    // --- lighting ---

    private Table BuildLighting()
    {
        var lt = new Table(_script);

        lt["list"] = Cb(_ => Names(_engine.Lighting.DeviceNames));
        lt["connect"] = Cb(a => Do(_engine.Lighting.ConnectAsync(Dev(a, "lighting.connect"), _ct),
            $"lighting.connect({Dev(a, "lighting.connect")})"));
        lt["disconnect"] = Cb(a => Do(_engine.Lighting.DisconnectAsync(Dev(a, "lighting.disconnect")),
            $"lighting.disconnect({Dev(a, "lighting.disconnect")})"));

        lt["set"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "lighting.set");
            int ch = (int)ReqNum(a, 1, "lighting.set");
            int intensity = (int)ReqNum(a, 2, "lighting.set");
            return Do(_engine.Lighting.SetChannelIntensityAsync(dev, ch, intensity), $"lighting.set({dev},{ch},{intensity})");
        });
        lt["setNamed"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "lighting.setNamed");
            string name = ReqStr(a, 1, "lighting.setNamed");
            int intensity = (int)ReqNum(a, 2, "lighting.setNamed");
            return Do(_engine.Lighting.SetNamedChannelAsync(dev, name, intensity),
                $"lighting.setNamed({dev},{name},{intensity})");
        });
        lt["setGroup"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "lighting.setGroup");
            string group = ReqStr(a, 1, "lighting.setGroup");
            int intensity = (int)ReqNum(a, 2, "lighting.setGroup");
            return Do(_engine.Lighting.SetGroupAsync(dev, group, intensity),
                $"lighting.setGroup({dev},{group},{intensity})");
        });
        lt["setAll"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "lighting.setAll");
            int intensity = (int)ReqNum(a, 1, "lighting.setAll");
            return Do(_engine.Lighting.SetAllAsync(dev, intensity), $"lighting.setAll({dev},{intensity})");
        });

        return lt;
    }

    // --- current measurement (Keithley 6482, measure-only) ---

    private Table BuildMeasurement()
    {
        var m = new Table(_script);

        m["list"] = Cb(_ => Names(_engine.Measurement.DeviceNames));
        m["connect"] = Cb(a => Do(_engine.Measurement.ConnectAsync(Dev(a, "measurement.connect"), _ct),
            $"measurement.connect({Dev(a, "measurement.connect")})"));
        m["disconnect"] = Cb(a => Do(_engine.Measurement.DisconnectAsync(Dev(a, "measurement.disconnect")),
            $"measurement.disconnect({Dev(a, "measurement.disconnect")})"));

        m["setVoltage"] = Cb(a => Do(
            _engine.Measurement.SetVoltageAsync(ReqStr(a, 0, "measurement.setVoltage"), ReqNum(a, 1, "measurement.setVoltage")),
            $"measurement.setVoltage({ReqStr(a, 0, "measurement.setVoltage")},{ReqNum(a, 1, "measurement.setVoltage")})"));
        m["outputOn"] = Cb(a => Do(_engine.Measurement.SetVoltageEnabledAsync(Dev(a, "measurement.outputOn"), true),
            $"measurement.outputOn({Dev(a, "measurement.outputOn")})"));
        m["outputOff"] = Cb(a => Do(_engine.Measurement.SetVoltageEnabledAsync(Dev(a, "measurement.outputOff"), false),
            $"measurement.outputOff({Dev(a, "measurement.outputOff")})"));
        m["reset"] = Cb(a => Do(_engine.Measurement.ResetAsync(Dev(a, "measurement.reset")),
            $"measurement.reset({Dev(a, "measurement.reset")})"));

        m["readCurrent"] = Cb(a =>
        {
            string dev = ReqStr(a, 0, "measurement.readCurrent");
            int ch = OptInt(a, 1, 1);
            var st = _engine.Measurement.GetState(dev);
            if (!st.Valid)
                throw new ScriptRuntimeException($"measurement.readCurrent: '{dev}' has no readings yet");
            int idx = ch - 1;
            if (idx < 0 || idx >= st.Channels.Count)
                throw new ScriptRuntimeException($"measurement.readCurrent: channel {ch} out of range for '{dev}'");
            return DynValue.NewNumber(st.Channels[idx]);
        });

        return m;
    }

    // --- camera ---

    private Table BuildCamera()
    {
        var c = new Table(_script);

        c["list"] = Cb(_ => Names(_engine.Camera.CameraIds));
        c["connect"] = Cb(a => Do(_engine.Camera.ConnectAsync(Dev(a, "camera.connect"), _ct),
            $"camera.connect({Dev(a, "camera.connect")})"));
        c["disconnect"] = Cb(a => Do(_engine.Camera.DisconnectAsync(Dev(a, "camera.disconnect")),
            $"camera.disconnect({Dev(a, "camera.disconnect")})"));
        c["startGrab"] = Cb(a => Do(_engine.Camera.StartGrabbingAsync(Dev(a, "camera.startGrab")),
            $"camera.startGrab({Dev(a, "camera.startGrab")})"));
        c["stopGrab"] = Cb(a => Do(_engine.Camera.StopGrabbingAsync(Dev(a, "camera.stopGrab")),
            $"camera.stopGrab({Dev(a, "camera.stopGrab")})"));

        c["setExposure"] = Cb(a =>
        {
            string id = ReqStr(a, 0, "camera.setExposure");
            double us = ReqNum(a, 1, "camera.setExposure");
            if (!_engine.Camera.IsConnected(id))
                throw new ScriptRuntimeException($"camera.setExposure: '{id}' is not connected");
            var ex = _engine.Camera.GetState(id).Exposure;
            return Do(_engine.Camera.ApplyExposureAsync(id,
                new CameraExposure(us, ex.Gain, ex.AutoExposure, ex.AutoGain, ex.FrameRate)),
                $"camera.setExposure({id},{us}us)");
        });

        c["snap"] = Cb(a =>
        {
            string id = ReqStr(a, 0, "camera.snap");
            string path = Val(_engine.Camera.SnapToFileAsync(id), $"camera.snap({id})");
            _log($"camera.snap({id}) → {path}");
            return DynValue.NewString(path);
        });

        return c;
    }

    // --- vision (HALCON) ---

    private Table BuildVision()
    {
        var v = new Table(_script);

        v["available"] = Cb(_ => DynValue.NewBoolean(_engine.Vision.HalconAvailable));
        v["hasFrame"] = Cb(a => DynValue.NewBoolean(_engine.Vision.HasFrame(ReqStr(a, 0, "vision.hasFrame"))));

        v["analyze"] = Cb(a =>
        {
            string id = ReqStr(a, 0, "vision.analyze");
            var s = Val(_engine.Vision.AnalyzeFrameAsync(id), $"vision.analyze({id})");
            var t = new Table(_script);
            t["width"] = DynValue.NewNumber(s.Width);
            t["height"] = DynValue.NewNumber(s.Height);
            t["channels"] = DynValue.NewNumber(s.Channels);
            t["minGray"] = DynValue.NewNumber(s.MinGray);
            t["meanGray"] = DynValue.NewNumber(s.MeanGray);
            t["maxGray"] = DynValue.NewNumber(s.MaxGray);
            return DynValue.NewTable(t);
        });

        v["blobs"] = Cb(a =>
        {
            string id = ReqStr(a, 0, "vision.blobs");
            int min = (int)ReqNum(a, 1, "vision.blobs");
            int max = (int)ReqNum(a, 2, "vision.blobs");
            var r = Val(_engine.Vision.CountBlobsAsync(id, min, max), $"vision.blobs({id})");
            return DynValue.NewNumber(r.BlobCount);
        });

        // Runs a saved product vision profile (NCC / circle / line) by name against its
        // camera's latest frame and returns a table: 'found' (bool) plus every result
        // value keyed by name (e.g. x, y, angle, length for a line profile).
        v["runProfile"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "vision.runProfile");
            if (_visionRunner == null)
                throw new ScriptRuntimeException(
                    "vision.runProfile needs a Layer 2 host — product vision profiles are unavailable in this context");
            var r = Block(_visionRunner(name));
            if (!r.IsSuccess) throw new ScriptRuntimeException($"vision.runProfile({name}) failed: {r.Message}");

            var t = new Table(_script);
            t["found"] = DynValue.NewBoolean(r.Value.Found);
            foreach (var val in r.Value.Values)
                t[val.Key] = DynValue.NewNumber(val.Value);
            _log($"vision.runProfile({name}) → found={r.Value.Found}");
            return DynValue.NewTable(t);
        });

        return v;
    }

    // --- operator-facing UI surfaces (Layer 3 sinks) ---

    private Table BuildUi()
    {
        var u = new Table(_script);

        // Pushes a still picture (typically the path camera.snap just returned) into
        // the head's image panel, so the operator sees what the machine is looking at
        // at this point in the process.
        u["showImage"] = Cb(a =>
        {
            string path = ReqStr(a, 0, "ui.showImage");
            string? caption = a.Count > 1 ? ReqStr(a, 1, "ui.showImage") : null;
            if (_imageDisplay == null)
                throw new ScriptRuntimeException(
                    "ui.showImage needs a Layer 3 image panel (none registered)");
            var r = _imageDisplay.Show(path, caption);
            if (!r.IsSuccess) throw new ScriptRuntimeException($"ui.showImage failed: {r.Message}");
            _log($"ui.showImage({path})" + (caption != null ? $" — {caption}" : string.Empty));
            return DynValue.Nil;
        });

        u["clearImage"] = Cb(_ =>
        {
            if (_imageDisplay == null)
                throw new ScriptRuntimeException(
                    "ui.clearImage needs a Layer 3 image panel (none registered)");
            var r = _imageDisplay.Clear();
            if (!r.IsSuccess) throw new ScriptRuntimeException($"ui.clearImage failed: {r.Message}");
            return DynValue.Nil;
        });

        return u;
    }

    // --- unified signal read (Layer 2: engine catalog + product sources) ---

    private Table BuildSignal()
    {
        var s = new Table(_script);

        s["read"] = Cb(a =>
        {
            string key = ReqStr(a, 0, "signal.read");
            if (_signalReader == null)
                throw new ScriptRuntimeException(
                    "signal.read needs a Layer 2 host — unified signals are unavailable in this context");
            var r = Block(_signalReader(key, _ct));
            if (!r.IsSuccess) throw new ScriptRuntimeException($"signal.read({key}) failed: {r.Message}");
            return DynValue.NewNumber(r.Value);
        });

        return s;
    }

    // --- part alignment (fiducials + local objects, taught on Vision Alignment) ---

    private Table BuildPart()
    {
        var p = new Table(_script);

        PartAlignmentScriptHost Host(string fn)
            => _part ?? throw new ScriptRuntimeException($"part.{fn} needs a Layer 2 host — part alignment is unavailable in this context");

        // Runs the alignment (camera -> E1, E2, rebuild the frame, re-measure every object;
        // every point must have a vision profile) and returns the resulting snapshot table.
        p["run"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "part.run");
            var host = Host("run");
            var r = Block(host.Run(name, _ct));
            if (!r.IsSuccess) throw new ScriptRuntimeException($"part.run({name}) failed: {r.Message}");
            int measured = r.Value.Objects.Count(o => o.Measured);
            _log($"part.run({name}) → frame origin ({r.Value.Frame.OriginX:F4}, {r.Value.Frame.OriginY:F4}), X axis {r.Value.Frame.AngleDeg:F3}°, {measured}/{r.Value.Objects.Length} object(s) detected");
            var snap = host.Snapshot(name);
            if (!snap.IsSuccess) throw new ScriptRuntimeException($"part.run({name}): {snap.Message}");
            return DynValue.NewTable(SnapshotTable(snap.Value));
        });

        // The part as it stands now (measured after a run, taught otherwise) — no motion.
        p["snapshot"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "part.snapshot");
            var snap = Host("snapshot").Snapshot(name);
            if (!snap.IsSuccess) throw new ScriptRuntimeException($"part.snapshot({name}) failed: {snap.Message}");
            return DynValue.NewTable(SnapshotTable(snap.Value));
        });

        p["cameraToFiducial"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "part.cameraToFiducial");
            int which = (int)ReqNum(a, 1, "part.cameraToFiducial");
            return Do(Host("cameraToFiducial").CameraToFiducial(name, which, _ct), $"part.cameraToFiducial({name}, E{which})");
        });

        // machine.part.cameraToObject(part, object [, applyOffset = true])
        p["cameraToObject"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "part.cameraToObject");
            string obj = ReqStr(a, 1, "part.cameraToObject");
            bool applyOffset = a.Count > 2 ? ReqBool(a, 2, "part.cameraToObject") : true;
            return Do(Host("cameraToObject").CameraToObject(name, obj, applyOffset, _ct),
                $"part.cameraToObject({name}, {obj}{(applyOffset ? " + offset" : "")})");
        });

        // machine.part.cameraToObjectPoint(part, object, index) — index 1-based (a line: 1 or 2)
        p["cameraToObjectPoint"] = Cb(a =>
        {
            string name = ReqStr(a, 0, "part.cameraToObjectPoint");
            string obj = ReqStr(a, 1, "part.cameraToObjectPoint");
            int index = (int)ReqNum(a, 2, "part.cameraToObjectPoint");
            return Do(Host("cameraToObjectPoint").CameraToObjectPoint(name, obj, index, _ct),
                $"part.cameraToObjectPoint({name}, {obj}, P{index})");
        });

        return p;
    }

    /// <summary>
    /// A snapshot as a Lua table: {name, measured, originX, originY, angleDeg, e1={x,y},
    /// e2={x,y}, objects={ {name, kind, measured, x, y, targetX, targetY, points={{x,y},...}}, ... }}.
    /// x/y = the object's reference (a point; a line's midpoint); targetX/Y = reference + its offset.
    /// </summary>
    private Table SnapshotTable(Registration.PartSnapshot snap)
    {
        var t = new Table(_script);
        t["name"] = DynValue.NewString(snap.Name);
        t["measured"] = DynValue.NewBoolean(snap.FromMeasurement);
        if (snap.Frame is { } f)
        {
            t["originX"] = DynValue.NewNumber(f.OriginX);
            t["originY"] = DynValue.NewNumber(f.OriginY);
            t["angleDeg"] = DynValue.NewNumber(f.AngleDeg);
        }
        if (snap.E1 is { } e1) t["e1"] = DynValue.NewTable(Xy(e1.X, e1.Y));
        if (snap.E2 is { } e2) t["e2"] = DynValue.NewTable(Xy(e2.X, e2.Y));

        var objects = new Table(_script);
        foreach (var o in snap.Objects)
        {
            var ot = new Table(_script);
            ot["name"] = DynValue.NewString(o.Name);
            ot["kind"] = DynValue.NewString(o.IsLine ? "line" : "point");
            ot["measured"] = DynValue.NewBoolean(o.Measured);
            ot["x"] = DynValue.NewNumber(o.Reference.X);
            ot["y"] = DynValue.NewNumber(o.Reference.Y);
            ot["targetX"] = DynValue.NewNumber(o.Target.X);
            ot["targetY"] = DynValue.NewNumber(o.Target.Y);
            var pts = new Table(_script);
            foreach (var pt in o.Points) pts.Append(DynValue.NewTable(Xy(pt.X, pt.Y)));
            ot["points"] = DynValue.NewTable(pts);
            objects.Append(DynValue.NewTable(ot));
        }
        t["objects"] = DynValue.NewTable(objects);
        return t;

        Table Xy(double x, double y)
        {
            var xy = new Table(_script);
            xy["x"] = DynValue.NewNumber(x);
            xy["y"] = DynValue.NewNumber(y);
            return xy;
        }
    }

    // --- shared plumbing ---

    /// <summary>Wraps a callback body with a cancellation check on every invocation.</summary>
    private DynValue Cb(Func<CallbackArguments, DynValue> body)
        => DynValue.NewCallback((_, a) =>
        {
            StopIfCancelled();
            return body(a);
        });

    /// <summary>
    /// Stops the script when the run was cancelled — as a <see cref="ScriptRuntimeException"/>
    /// (MoonSharp's normal callback-error channel) rather than an
    /// <see cref="OperationCanceledException"/> that would escape the interpreter and trip
    /// the debugger's user-unhandled first-chance break. Classified back to a cancel in
    /// <c>MoonScriptEngine</c>.
    /// </summary>
    private void StopIfCancelled()
    {
        if (_ct.IsCancellationRequested) throw Cancelled();
    }

    private static ScriptRuntimeException Cancelled() => new("script cancelled");

    /// <summary>Awaits a hardware task, converting a mid-call cancel into a clean script stop.</summary>
    private static T Block<T>(Task<T> task)
    {
        try { return task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw Cancelled(); }
    }

    /// <summary>Blocks on a hardware call, raising a Lua error on failure; logs success.</summary>
    private DynValue Do(Task<Result> task, string what)
    {
        var r = Block(task);
        if (!r.IsSuccess) throw new ScriptRuntimeException($"{what} failed: {r.Message}");
        _log(what);
        return DynValue.Nil;
    }

    /// <summary>Blocks on a read, raising a Lua error on failure and returning the value.</summary>
    private static T Val<T>(Task<Result<T>> task, string what)
    {
        var r = Block(task);
        if (!r.IsSuccess) throw new ScriptRuntimeException($"{what} failed: {r.Message}");
        return r.Value;
    }

    /// <summary>Refreshes the supply and returns the requested 1-based channel's polled state.</summary>
    private PowerSupplyChannelState PsuChannel(CallbackArguments a, string fn)
    {
        string dev = ReqStr(a, 0, fn);
        int ch = OptInt(a, 1, 1);
        _engine.PowerSupply.RefreshAsync().GetAwaiter().GetResult();
        var st = _engine.PowerSupply.GetState(dev);
        if (!st.Valid)
            throw new ScriptRuntimeException($"{fn}: '{dev}' has no readings yet");
        var channel = st.Channels.FirstOrDefault(x => x.Channel == ch);
        if (channel == null)
            throw new ScriptRuntimeException($"{fn}: channel {ch} not found for '{dev}'");
        return channel;
    }

    private DynValue PositionTable(MotionPosition p)
    {
        var t = new Table(_script);
        t["x"] = DynValue.NewNumber(p.X);
        t["y"] = DynValue.NewNumber(p.Y);
        t["z"] = DynValue.NewNumber(p.Z);
        t["u"] = DynValue.NewNumber(p.U);
        t["v"] = DynValue.NewNumber(p.V);
        t["w"] = DynValue.NewNumber(p.W);
        return DynValue.NewTable(t);
    }

    private DynValue Names(IReadOnlyList<string> names)
    {
        var t = new Table(_script);
        for (int i = 0; i < names.Count; i++)
            t[i + 1] = DynValue.NewString(names[i]);   // Lua arrays are 1-based
        return DynValue.NewTable(t);
    }

    // First positional arg as the device/slide name — the common case.
    private static string Dev(CallbackArguments a, string fn) => ReqStr(a, 0, fn);

    private static string ReqStr(CallbackArguments a, int i, string fn)
        => a.AsType(i, fn, DataType.String, false).String;

    private static double ReqNum(CallbackArguments a, int i, string fn)
        => a.AsType(i, fn, DataType.Number, false).Number;

    private static int OptInt(CallbackArguments a, int i, int fallback)
        => i < a.Count && a[i].Type == DataType.Number ? (int)a[i].Number : fallback;

    /// <summary>
    /// Parses the optional-channel set form: <c>(device, value)</c> → CH1, or
    /// <c>(device, channel, value)</c> → the named channel of a multi-output device.
    /// </summary>
    private static (int channel, double value) ChannelValue(CallbackArguments a, string fn)
        => a.Count >= 3
            ? ((int)ReqNum(a, 1, fn), ReqNum(a, 2, fn))
            : (1, ReqNum(a, 1, fn));

    private static bool ReqBool(CallbackArguments a, int i, string fn)
    {
        if (i >= a.Count)
            throw new ScriptRuntimeException($"{fn}: missing argument {i + 1} (expected a boolean)");
        return a[i].CastToBool();   // Lua truthiness: nil/false are false, everything else true
    }
}
