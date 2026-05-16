using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Recipe;

/// <summary>Typed accessor over a recipe call's evaluated arguments.</summary>
internal readonly struct RecipeArgs
{
    private readonly IReadOnlyDictionary<string, RecipeValue> _args;

    public RecipeArgs(IReadOnlyDictionary<string, RecipeValue> args) => _args = args;

    public bool Has(string name) => _args.ContainsKey(name);

    public double Number(string name, double fallback = 0.0)
        => _args.TryGetValue(name, out var v) ? v.AsNumber() : fallback;

    public int Int(string name, int fallback = 0) => (int)Number(name, fallback);

    public string Text(string name, string fallback = "")
        => _args.TryGetValue(name, out var v) ? v.Display() : fallback;

    public bool Flag(string name, bool fallback = false)
        => _args.TryGetValue(name, out var v) ? v.IsTruthy : fallback;
}

/// <summary>A bound recipe method — receives the (optional) device and call args.</summary>
internal delegate Task<Result> RecipeMethod(
    string? device, RecipeArgs args, RecipeContext ctx, CancellationToken ct);

/// <summary>
/// Binds recipe <c>domain.device.method</c> calls straight to
/// <see cref="IHorizonEngine"/> facet methods — no dispatcher, no reflection,
/// no workflow-op vocabulary in the way. A recipe reaches the full facet API.
/// Built per run so it captures the current operator-prompt handler.
/// </summary>
internal sealed class RecipeMethodRegistry : IRecipeMethodInvoker
{
    private readonly Dictionary<string, RecipeMethod> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHorizonEngine _engine;
    private readonly Func<string, string, CancellationToken, Task<Result>> _moveToNamed;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;

    public RecipeMethodRegistry(
        IHorizonEngine engine,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        IOperatorPrompt? prompt,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _moveToNamed = moveToNamed;
        _prompt = prompt;
        _log = log;

        RegisterSystem();
        RegisterMotion();
        RegisterIO();
        RegisterCamera();
        RegisterLaser();
        RegisterMeasurement();
        RegisterPneumatic();
    }

    public Task<Result> InvokeAsync(
        CallStatement call, IReadOnlyDictionary<string, RecipeValue> args,
        RecipeContext ctx, CancellationToken ct)
    {
        if (!_methods.TryGetValue($"{call.Domain}.{call.Method}", out var method))
            return Task.FromResult(Result.Fail(
                ErrorCode.NotSupported, $"unknown recipe method '{call.Domain}.{call.Method}'"));

        return method(call.Device, new RecipeArgs(args), ctx, ct);
    }

    private void Register(string domain, string method, RecipeMethod handler)
        => _methods[$"{domain}.{method}"] = handler;

    /// <summary>Wraps a handler that requires a device segment.</summary>
    private static RecipeMethod Hw(
        string domain, Func<string, RecipeArgs, RecipeContext, CancellationToken, Task<Result>> handler)
        => (device, args, ctx, ct) => string.IsNullOrEmpty(device)
            ? Task.FromResult(Result.Fail(ErrorCode.InvalidParameter,
                $"'{domain}' calls need a device — use {domain}.<device>.<method>(...)"))
            : handler(device!, args, ctx, ct);

    // --- system (domainless built-ins) ---

    private void RegisterSystem()
    {
        Register("system", "log", (device, a, ctx, ct) =>
        {
            _log(LogLevel.Info, a.Text("message"));
            return Task.FromResult(Result.Ok());
        });

        Register("system", "wait", async (device, a, ctx, ct) =>
        {
            int ms = a.Int("ms");
            if (ms > 0) await Task.Delay(ms, ct).ConfigureAwait(false);
            return Result.Ok();
        });

        Register("system", "askUser", async (device, a, ctx, ct) =>
        {
            if (_prompt == null)
                return Result.Fail(ErrorCode.NotSupported, "no operator-prompt handler registered");
            bool ok = await _prompt.ConfirmAsync(a.Text("title", "Confirm"), a.Text("message"), ct)
                .ConfigureAwait(false);
            var store = a.Text("store");
            if (!string.IsNullOrWhiteSpace(store)) ctx.Set(store, RecipeValue.Bool(ok));
            return Result.Ok();
        });
    }

    // --- motion ---

    private void RegisterMotion()
    {
        Register("motion", "connect", Hw("motion", (d, a, ctx, ct) => _engine.Motion.ConnectAsync(d, ct)));
        Register("motion", "moveAbsolute", Hw("motion", (d, a, ctx, ct) =>
            _engine.Motion.MoveAbsoluteAsync(d, a.Text("axis"), a.Number("position"), ct)));
        Register("motion", "moveRelative", Hw("motion", (d, a, ctx, ct) =>
            _engine.Motion.MoveRelativeAsync(d, a.Text("axis"), a.Number("distance"), ct)));
        Register("motion", "moveToPosition", Hw("motion", (d, a, ctx, ct) =>
            _moveToNamed(d, a.Text("position"), ct)));
        Register("motion", "setVelocity", Hw("motion", (d, a, ctx, ct) =>
            _engine.Motion.SetDeviceVelocityAsync(d, a.Number("speed"))));
        Register("motion", "setAxisVelocity", Hw("motion", (d, a, ctx, ct) =>
            _engine.Motion.SetVelocityAsync(d, a.Text("axis"), a.Number("velocity"))));
        Register("motion", "stop", Hw("motion", (d, a, ctx, ct) => _engine.Motion.StopAsync(d)));
    }

    // --- digital IO ---

    private void RegisterIO()
    {
        Register("io", "connect", Hw("io", (d, a, ctx, ct) => _engine.IO.ConnectAsync(d, ct)));
        Register("io", "setOutput", Hw("io", (d, a, ctx, ct) =>
            _engine.IO.SetOutputAsync(d, a.Int("pin"), a.Flag("state"), ct)));
        Register("io", "setOutputByName", Hw("io", (d, a, ctx, ct) =>
            _engine.IO.SetOutputByNameAsync(d, a.Text("pinName"), a.Flag("state"), ct)));
    }

    // --- camera ---

    private void RegisterCamera()
    {
        Register("camera", "connect", Hw("camera", (d, a, ctx, ct) => _engine.Camera.ConnectAsync(d, ct)));
        Register("camera", "startGrabbing", Hw("camera", (d, a, ctx, ct) => _engine.Camera.StartGrabbingAsync(d)));
        Register("camera", "stopGrabbing", Hw("camera", (d, a, ctx, ct) => _engine.Camera.StopGrabbingAsync(d)));

        Register("camera", "setExposure", Hw("camera", (d, a, ctx, ct) =>
        {
            if (!_engine.Camera.IsConnected(d))
                return Task.FromResult(Result.Fail(ErrorCode.CameraNotConnected,
                    $"camera '{d}' is not connected"));
            var ex = _engine.Camera.GetState(d).Exposure;
            return _engine.Camera.ApplyExposureAsync(d, new CameraExposure(
                a.Has("exposureUs")   ? a.Number("exposureUs")  : ex.ExposureUs,
                a.Has("gain")         ? a.Number("gain")        : ex.Gain,
                a.Has("autoExposure") ? a.Flag("autoExposure")  : ex.AutoExposure,
                a.Has("autoGain")     ? a.Flag("autoGain")      : ex.AutoGain,
                a.Has("frameRate")    ? a.Number("frameRate")   : ex.FrameRate));
        }));

        Register("camera", "snap", Hw("camera", async (d, a, ctx, ct) =>
        {
            var r = await _engine.Camera.SnapToFileAsync(d).ConfigureAwait(false);
            if (!r.IsSuccess) return Result.Fail(r.Error, r.Message);
            var store = a.Text("store");
            if (!string.IsNullOrWhiteSpace(store)) ctx.Set(store, RecipeValue.Text(r.Value));
            _log(LogLevel.Info, $"{d} snapped → {r.Value}");
            return Result.Ok();
        }));
    }

    // --- laser controller ---

    private void RegisterLaser()
    {
        Register("laser", "connect", Hw("laser", (d, a, ctx, ct) => _engine.Laser.ConnectAsync(d, ct)));
        Register("laser", "setTemperature", Hw("laser", (d, a, ctx, ct) =>
            _engine.Laser.SetTemperatureAsync(d, a.Number("celsius"))));
        Register("laser", "tecOn", Hw("laser", (d, a, ctx, ct) => _engine.Laser.TecOnAsync(d)));
        Register("laser", "tecOff", Hw("laser", (d, a, ctx, ct) => _engine.Laser.TecOffAsync(d)));
        Register("laser", "setCurrent", Hw("laser", (d, a, ctx, ct) =>
            _engine.Laser.SetLaserCurrentAsync(d, a.Number("milliamps"))));
        Register("laser", "laserOn", Hw("laser", (d, a, ctx, ct) => _engine.Laser.LaserOnAsync(d)));
        Register("laser", "laserOff", Hw("laser", (d, a, ctx, ct) => _engine.Laser.LaserOffAsync(d)));
    }

    // --- current measurement ---

    private void RegisterMeasurement()
    {
        Register("measurement", "connect", Hw("measurement", (d, a, ctx, ct) =>
            _engine.Measurement.ConnectAsync(d, ct)));
        Register("measurement", "setVoltage", Hw("measurement", (d, a, ctx, ct) =>
            _engine.Measurement.SetVoltageAsync(d, a.Number("volts"))));
        Register("measurement", "outputOn", Hw("measurement", (d, a, ctx, ct) =>
            _engine.Measurement.SetVoltageEnabledAsync(d, true)));
        Register("measurement", "outputOff", Hw("measurement", (d, a, ctx, ct) =>
            _engine.Measurement.SetVoltageEnabledAsync(d, false)));
        Register("measurement", "reset", Hw("measurement", (d, a, ctx, ct) =>
            _engine.Measurement.ResetAsync(d)));

        Register("measurement", "readCurrent", Hw("measurement", (d, a, ctx, ct) =>
        {
            var state = _engine.Measurement.GetState(d);
            if (!state.Valid)
                return Task.FromResult(Result.Fail(ErrorCode.OperationFailed,
                    $"measurement '{d}' has no readings yet"));
            int channel = a.Int("channel", 1);
            int index = channel - 1;
            if (index < 0 || index >= state.Channels.Count)
                return Task.FromResult(Result.Fail(ErrorCode.InvalidParameter,
                    $"channel {channel} out of range for '{d}'"));

            double value = state.Channels[index];
            var store = a.Text("store");
            if (!string.IsNullOrWhiteSpace(store)) ctx.Set(store, RecipeValue.Number(value));
            _log(LogLevel.Info, $"{d} CH{channel} = {value} {state.Unit}" +
                (string.IsNullOrWhiteSpace(store) ? "" : $" → {store}"));
            return Task.FromResult(Result.Ok());
        }));
    }

    // --- pneumatic slides (the "device" segment is the slide name) ---

    private void RegisterPneumatic()
    {
        Register("pneumatic", "extend", Hw("pneumatic", (d, a, ctx, ct) =>
            _engine.Pneumatic.ExtendAndWaitAsync(d, ct)));
        Register("pneumatic", "retract", Hw("pneumatic", (d, a, ctx, ct) =>
            _engine.Pneumatic.RetractAndWaitAsync(d, ct)));
        Register("pneumatic", "extendNoWait", Hw("pneumatic", (d, a, ctx, ct) =>
            _engine.Pneumatic.ExtendAsync(d, ct)));
        Register("pneumatic", "retractNoWait", Hw("pneumatic", (d, a, ctx, ct) =>
            _engine.Pneumatic.RetractAsync(d, ct)));
    }
}
