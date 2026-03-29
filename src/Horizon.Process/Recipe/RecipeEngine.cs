using System.Globalization;
using System.Text;
using Horizon.Process.Recipe.Ast;

namespace Horizon.Process.Recipe;

/// <summary>
/// Tree-walking interpreter for .hrcp recipe scripts.
/// Walks the AST and calls hardware managers directly via typed lambdas in RecipeMethodRegistry.
/// Supports cancellation, timeout, breakpoints, step-by-step debugging, and UI callbacks.
/// </summary>
public sealed class RecipeEngine
{
    private readonly RecipeMethodRegistry _methods = new();
    private ManualResetEventSlim? _stepGate;
    private int _executionLock;

    public RecipeMethodRegistry Methods => _methods;

    // --- Execution lock (shared concept with ProcessEngine) ---

    public string? RunningOwner { get; private set; }
    public bool IsRunning => _executionLock != 0;

    /// <summary>
    /// Optional gate function. If set and returns true, TryAcquire is blocked.
    /// Used by safety watchdog to prevent new executions while safety flag is raised.
    /// </summary>
    public Func<bool>? SafetyGate { get; set; }

    public bool TryAcquire(string owner)
    {
        if (SafetyGate?.Invoke() == true)
            return false;
        if (Interlocked.CompareExchange(ref _executionLock, 1, 0) != 0)
            return false;
        RunningOwner = owner;
        return true;
    }

    public void Release()
    {
        RunningOwner = null;
        Interlocked.Exchange(ref _executionLock, 0);
    }

    // --- Debugging ---

    public bool StepByStepMode { get; set; }
    public HashSet<int> Breakpoints { get; } = new();

    public void ContinueStep() => _stepGate?.Set();

    // --- Callbacks for UI ---

    /// <summary>(line, device, method)</summary>
    public Action<int, string, string>? OnStepStarted;

    /// <summary>(line, success, description)</summary>
    public Action<int, bool, string>? OnStepCompleted;

    /// <summary>(line, currentIteration, totalCount)</summary>
    public Action<int, int, int>? OnLoopIteration;

    /// <summary>Log messages from system.log</summary>
    public Action<string>? OnLog;

    /// <summary>Variable changed (for live watch)</summary>
    public Action? OnContextChanged;

    // --- Default timeout ---

    public int DefaultTimeoutMs { get; set; } = 60_000;

    // --- Parse ---

    public static (ProgramNode? Program, IReadOnlyList<RecipeParseError> Errors) Parse(string source)
    {
        var lexer = new RecipeLexer(source);
        var tokens = lexer.Tokenize();
        var parser = new RecipeParser(tokens);
        var program = parser.Parse();
        return (parser.Errors.Count == 0 ? program : null, parser.Errors);
    }

    // --- Execute ---

    public async Task<bool> ExecuteAsync(ProgramNode program, RecipeContext context, CancellationToken ct)
    {
        context.CancellationToken = ct;
        context.StartTime = DateTimeOffset.UtcNow;
        _stepGate = new ManualResetEventSlim(false);

        try
        {
            return await Task.Run(() => ExecuteStatements(program.Statements, context, ct), ct);
        }
        finally
        {
            context.EndTime = DateTimeOffset.UtcNow;
            _stepGate?.Dispose();
            _stepGate = null;
        }
    }

    private bool ExecuteStatements(List<RecipeNode> statements, RecipeContext context, CancellationToken ct)
    {
        foreach (var node in statements)
        {
            ct.ThrowIfCancellationRequested();

            // Breakpoint / step-by-step gate
            if (node is not CommentNode)
            {
                if (StepByStepMode || Breakpoints.Contains(node.Line))
                {
                    _stepGate!.Reset();
                    WaitHandle.WaitAny(new[] { _stepGate!.WaitHandle, ct.WaitHandle });
                    ct.ThrowIfCancellationRequested();
                }
            }

            bool success = node switch
            {
                CommentNode => true,
                SetVariableNode setVar => ExecuteSetVariable(setVar, context),
                MethodCallNode call => ExecuteMethodCall(call, context, ct),
                LoopNode loop => ExecuteLoop(loop, context, ct),
                IfNode ifNode => ExecuteIf(ifNode, context, ct),
                _ => throw new InvalidOperationException($"Unknown AST node type: {node.GetType().Name}")
            };

            if (!success)
                return false;
        }

        return true;
    }

    private bool ExecuteSetVariable(SetVariableNode node, RecipeContext context)
    {
        try
        {
            var value = EvaluateExpression(node.Value, context);
            context.SetObject(node.Name, value);

            // Propagate unit from source variables in the expression
            var unit = InferUnitFromExpression(node.Value, context);
            if (unit != null)
                context.Set($"{node.Name}.unit", unit);

            OnContextChanged?.Invoke();
            var formatted = value is double d && unit != null
                ? FormatWithSIPrefix(d, unit)
                : FormatValue(value);
            OnStepCompleted?.Invoke(node.Line, true, $"set {node.Name} = {formatted}");
            return true;
        }
        catch (Exception ex)
        {
            OnStepCompleted?.Invoke(node.Line, false, $"set {node.Name} — {ex.Message}");
            return false;
        }
    }

    /// <summary>Walk an expression AST to find if any referenced variable has a .unit tag.</summary>
    private static string? InferUnitFromExpression(RecipeExpression expr, RecipeContext context)
    {
        return expr switch
        {
            VariableRef v => context.GetString($"{v.Name}.unit") is { Length: > 0 } u ? u : null,
            BinaryExpr b => InferUnitFromExpression(b.Left, context) ?? InferUnitFromExpression(b.Right, context),
            UnaryExpr u => InferUnitFromExpression(u.Operand, context),
            _ => null
        };
    }

    private bool ExecuteMethodCall(MethodCallNode node, RecipeContext context, CancellationToken ct)
    {
        OnStepStarted?.Invoke(node.Line, node.Device, node.Method);

        if (!_methods.TryGetMethod(node.Device, node.Method, out var handler) || handler == null)
        {
            var desc = $"{node.Device}.{node.Method} — unknown method";
            OnStepCompleted?.Invoke(node.Line, false, desc);
            return false;
        }

        try
        {
            // Resolve arguments
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in node.Args)
            {
                args[arg.Name] = EvaluateExpression(arg.Value, context);
            }

            // Build description with resolved args
            var argDesc = string.Join(", ", args.Select(kv => $"{kv.Key}: {FormatValue(kv.Value)}"));
            var callDesc = $"{node.Device}.{node.Method}({argDesc})";

            // Determine timeout
            int timeoutMs = DefaultTimeoutMs;
            if (args.TryGetValue("timeout", out var timeoutVal))
                timeoutMs = Convert.ToInt32(timeoutVal);

            // Execute with timeout
            var methodTask = handler(args, context, ct);
            var completed = Task.WhenAny(methodTask, Task.Delay(timeoutMs, ct)).Result;

            bool success;
            string resultDesc;
            if (completed == methodTask)
            {
                success = methodTask.Result;
                // Check if the method stored a result in context (via "store" arg)
                var storeKey = args.GetValueOrDefault("store")?.ToString();
                if (success && storeKey != null && context.HasValue(storeKey))
                {
                    var stored = context.GetRaw(storeKey);
                    var unit = context.GetString($"{storeKey}.unit");
                    var formatted = stored is double d && !string.IsNullOrEmpty(unit)
                        ? FormatWithSIPrefix(d, unit)
                        : FormatValue(stored);
                    resultDesc = $"{callDesc} -> {storeKey} = {formatted}";
                }
                else
                {
                    resultDesc = success ? $"{callDesc} — OK" : $"{callDesc} — FAILED";
                }
            }
            else
            {
                success = false;
                resultDesc = $"{callDesc} — TIMEOUT ({timeoutMs}ms)";
            }

            OnContextChanged?.Invoke();
            OnStepCompleted?.Invoke(node.Line, success, resultDesc);
            return success;
        }
        catch (OperationCanceledException)
        {
            OnStepCompleted?.Invoke(node.Line, false, $"{node.Device}.{node.Method} — cancelled");
            throw;
        }
        catch (Exception ex)
        {
            OnStepCompleted?.Invoke(node.Line, false, $"{node.Device}.{node.Method} — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Replace {varName} with context values and {=expr} with evaluated expressions.
    /// Example: "Power = {power} mA, double = {=power * 2}"
    /// </summary>
    private static string InterpolateString(string message, RecipeContext context)
    {
        if (!message.Contains('{')) return message;

        var sb = new StringBuilder(message.Length);
        int i = 0;

        while (i < message.Length)
        {
            if (message[i] == '{')
            {
                int close = message.IndexOf('}', i + 1);
                if (close < 0)
                {
                    sb.Append(message[i..]);
                    break;
                }

                var key = message[(i + 1)..close];

                if (key.StartsWith('='))
                {
                    // {=expression} — evaluate arithmetic
                    try
                    {
                        var exprText = key[1..];
                        var result = ExpressionEvaluator.Evaluate(exprText,
                            ToProcessContext(context));
                        sb.Append(FormatValue(result));
                    }
                    catch
                    {
                        sb.Append("{ERR}");
                    }
                }
                else
                {
                    // {varName} — lookup context variable with unit
                    var raw = context.GetRaw(key);
                    if (raw != null)
                    {
                        var unit = context.GetString($"{key}.unit");
                        if (raw is double d && !string.IsNullOrEmpty(unit))
                            sb.Append(FormatWithSIPrefix(d, unit));
                        else
                            sb.Append(FormatValue(raw));
                    }
                    else
                    {
                        sb.Append($"{{{key}}}");
                    }
                }

                i = close + 1;
            }
            else
            {
                sb.Append(message[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Build a ProcessContext from RecipeContext for ExpressionEvaluator.</summary>
    private static Horizon.Process.ProcessContext ToProcessContext(RecipeContext recipe)
    {
        var pc = new Horizon.Process.ProcessContext();
        foreach (var (key, value, _) in recipe.GetSnapshot())
        {
            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out var f))
                pc.SetValue(key, f);
            else
                pc.SetValue(key, value);
        }
        return pc;
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            double d => d.ToString("G6", CultureInfo.InvariantCulture),
            float f => f.ToString("G6", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>Format a number with SI prefix: 1.23e-9 → "1.23 n", 0.005 → "5 m"</summary>
    public static string FormatWithSIPrefix(double value, string unit = "")
    {
        if (value == 0) return $"0 {unit}".TrimEnd();
        if (double.IsNaN(value)) return "NaN";
        if (double.IsInfinity(value)) return value > 0 ? "+Inf" : "-Inf";

        double abs = Math.Abs(value);
        string prefix;
        double scaled;

        if (abs >= 1e12)      { scaled = value / 1e12; prefix = "T"; }
        else if (abs >= 1e9)  { scaled = value / 1e9;  prefix = "G"; }
        else if (abs >= 1e6)  { scaled = value / 1e6;  prefix = "M"; }
        else if (abs >= 1e3)  { scaled = value / 1e3;  prefix = "k"; }
        else if (abs >= 1)    { scaled = value;         prefix = "";  }
        else if (abs >= 1e-3) { scaled = value * 1e3;   prefix = "m"; }
        else if (abs >= 1e-6) { scaled = value * 1e6;   prefix = "u"; }
        else if (abs >= 1e-9) { scaled = value * 1e9;   prefix = "n"; }
        else if (abs >= 1e-12){ scaled = value * 1e12;  prefix = "p"; }
        else                  { scaled = value * 1e15;  prefix = "f"; }

        return $"{scaled.ToString("F4", CultureInfo.InvariantCulture)} {prefix}{unit}".TrimEnd();
    }

    private bool ExecuteLoop(LoopNode node, RecipeContext context, CancellationToken ct)
    {
        int count = 0;
        bool infinite = true;

        if (node.Count != null)
        {
            var countVal = EvaluateExpression(node.Count, context);
            count = Convert.ToInt32(countVal);
            infinite = count <= 0;
        }

        int iteration = 0;
        while (infinite || iteration < count)
        {
            ct.ThrowIfCancellationRequested();

            context.Set("loop.iteration", (double)iteration);
            OnContextChanged?.Invoke();
            OnLoopIteration?.Invoke(node.Line, iteration + 1, infinite ? 0 : count);

            if (!ExecuteStatements(node.Body, context, ct))
                return false;

            iteration++;
        }

        return true;
    }

    private bool ExecuteIf(IfNode node, RecipeContext context, CancellationToken ct)
    {
        bool conditionMet = EvaluateCondition(node.Condition, context);

        if (conditionMet)
            return ExecuteStatements(node.ThenBranch, context, ct);

        if (node.ElseBranch != null)
            return ExecuteStatements(node.ElseBranch, context, ct);

        return true;
    }

    // --- Expression evaluation ---

    private object EvaluateExpression(RecipeExpression expr, RecipeContext context)
    {
        return expr switch
        {
            NumberLiteral num => num.Value,
            StringLiteral str => str.Value,
            BooleanLiteral b => b.Value,
            VariableRef varRef => ResolveVariable(varRef, context),
            UnaryExpr unary => EvaluateUnary(unary, context),
            BinaryExpr binary => EvaluateBinary(binary, context),
            ComparisonExpr comp => EvaluateComparison(comp, context),
            _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}")
        };
    }

    private object ResolveVariable(VariableRef varRef, RecipeContext context)
    {
        var raw = context.GetRaw(varRef.Name);
        if (raw != null) return raw;

        throw new InvalidOperationException($"Undefined variable '{varRef.Name}' at line {varRef.Line}");
    }

    private object EvaluateUnary(UnaryExpr expr, RecipeContext context)
    {
        var operand = EvaluateExpression(expr.Operand, context);
        return expr.Op switch
        {
            UnaryOp.Negate => -ToDouble(operand),
            _ => throw new InvalidOperationException($"Unknown unary operator: {expr.Op}")
        };
    }

    private object EvaluateBinary(BinaryExpr expr, RecipeContext context)
    {
        var left = ToDouble(EvaluateExpression(expr.Left, context));
        var right = ToDouble(EvaluateExpression(expr.Right, context));

        return expr.Op switch
        {
            BinaryOp.Add => left + right,
            BinaryOp.Subtract => left - right,
            BinaryOp.Multiply => left * right,
            BinaryOp.Divide => right == 0.0 ? double.PositiveInfinity : left / right,
            _ => throw new InvalidOperationException($"Unknown binary operator: {expr.Op}")
        };
    }

    private bool EvaluateCondition(RecipeExpression expr, RecipeContext context)
    {
        if (expr is ComparisonExpr comp)
            return EvaluateComparison(comp, context) is true;

        // Truthy evaluation for non-comparison expressions
        var value = EvaluateExpression(expr, context);
        return value switch
        {
            bool b => b,
            double d => d != 0.0,
            string s => !string.IsNullOrEmpty(s),
            _ => value != null
        };
    }

    private object EvaluateComparison(ComparisonExpr comp, RecipeContext context)
    {
        var left = ToDouble(EvaluateExpression(comp.Left, context));
        var right = ToDouble(EvaluateExpression(comp.Right, context));

        return comp.Op switch
        {
            CompareOp.GreaterThan => left > right,
            CompareOp.LessThan => left < right,
            CompareOp.GreaterOrEqual => left >= right,
            CompareOp.LessOrEqual => left <= right,
            CompareOp.Equal => Math.Abs(left - right) < 0.0001,
            CompareOp.NotEqual => Math.Abs(left - right) >= 0.0001,
            _ => throw new InvalidOperationException($"Unknown comparison operator: {comp.Op}")
        };
    }

    private static double ToDouble(object value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            bool b => b ? 1.0 : 0.0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0,
            _ => 0.0
        };
    }

    // --- Built-in system methods registration ---

    public void RegisterBuiltins()
    {
        _methods.Register("system", "wait", async (args, ctx, ct) =>
        {
            var ms = Convert.ToInt32(args.GetValueOrDefault("ms", 1000.0));
            await Task.Delay(ms, ct);
            return true;
        });

        _methods.Register("system", "log", (args, ctx, ct) =>
        {
            var message = args.GetValueOrDefault("message", "")?.ToString() ?? "";
            message = InterpolateString(message, ctx);
            OnLog?.Invoke(message);
            return Task.FromResult(true);
        });

        _methods.RegisterDeviceType("system", "system");

        // --- Parameter hints ---
        var P = (string name, string type, bool req, string? desc, string? def) =>
            new ParamHint { Name = name, Type = type, Required = req, Description = desc, Default = def };

        // System
        _methods.RegisterParams("system", "wait",
            P("ms", "number", true, "Delay in milliseconds", "1000"));
        _methods.RegisterParams("system", "log",
            P("message", "string", true, "Supports {var} and {=expr} interpolation", null));

        // Motion
        _methods.RegisterParams("motion", "movetoposition",
            P("position", "string", true, "Named position", null));
        _methods.RegisterParams("motion", "movetoabs",
            P("axis", "string", true, "Axis name (X,Y,Z,U,V,W)", null),
            P("position", "number", true, "Absolute position", null));
        _methods.RegisterParams("motion", "moverelative",
            P("axis", "string", true, "Axis name (X,Y,Z,U,V,W)", null),
            P("distance", "number", true, "Relative distance", null));
        _methods.RegisterParams("motion", "moverelativemulti",
            P("x", "number", false, "X distance", null),
            P("y", "number", false, "Y distance", null),
            P("z", "number", false, "Z distance", null),
            P("u", "number", false, "U distance", null),
            P("v", "number", false, "V distance", null),
            P("w", "number", false, "W distance", null));
        _methods.RegisterParams("motion", "movetoabs6",
            P("x", "number", false, "X position", null),
            P("y", "number", false, "Y position", null),
            P("z", "number", false, "Z position", null),
            P("u", "number", false, "U position", null),
            P("v", "number", false, "V position", null),
            P("w", "number", false, "W position", null));
        _methods.RegisterParams("motion", "getposition");
        _methods.RegisterParams("motion", "stopall");
        _methods.RegisterParams("motion", "ismoving",
            P("store", "string", false, "Variable to store result", "isMoving"));
        _methods.RegisterParams("motion", "setvelocity",
            P("speed", "number", true, "Velocity value", null));
        _methods.RegisterParams("motion", "waitforposition",
            P("axis", "string", true, "Axis name", null),
            P("timeoutMs", "number", false, "Timeout in ms", "30000"),
            P("pollMs", "number", false, "Poll interval in ms", "50"));

        // IO
        _methods.RegisterParams("io", "setoutput",
            P("pin", "number", true, "Pin number", null),
            P("state", "bool", true, "true/false", null));
        _methods.RegisterParams("io", "setoutputbyname",
            P("pinName", "string", true, "Pin name", null),
            P("state", "bool", true, "true/false", null));
        _methods.RegisterParams("io", "readinput",
            P("pin", "number", true, "Pin number", null),
            P("store", "string", false, "Variable to store result", null));

        // Pneumatic
        _methods.RegisterParams("pneumatic", "extend");
        _methods.RegisterParams("pneumatic", "retract");
        _methods.RegisterParams("pneumatic", "extendandwait");
        _methods.RegisterParams("pneumatic", "retractandwait");

        // Current Measurement
        _methods.RegisterParams("currentmeasurement", "measure",
            P("channel", "number", true, "Channel number (1 or 2)", null),
            P("store", "string", false, "Variable to store reading", null));
        _methods.RegisterParams("currentmeasurement", "readall");
        _methods.RegisterParams("currentmeasurement", "reset");
        _methods.RegisterParams("currentmeasurement", "connect");
        _methods.RegisterParams("currentmeasurement", "disconnect");
        _methods.RegisterParams("currentmeasurement", "setvoltage",
            P("volts", "number", true, "Bias voltage", "0"));
        _methods.RegisterParams("currentmeasurement", "outputon");
        _methods.RegisterParams("currentmeasurement", "outputoff");
        _methods.RegisterParams("currentmeasurement", "startpolling",
            P("intervalMs", "number", false, "Poll interval in ms", "500"));
        _methods.RegisterParams("currentmeasurement", "stoppolling");
        _methods.RegisterParams("currentmeasurement", "startfastpolling");
        _methods.RegisterParams("currentmeasurement", "stopfastpolling");
        _methods.RegisterParams("currentmeasurement", "initialize",
            P("volts", "number", false, "Bias voltage", "1"));

        // Laser Controller
        _methods.RegisterParams("lasercontroller", "settemperature",
            P("celsius", "number", true, "Temperature setpoint", "25.0"));
        _methods.RegisterParams("lasercontroller", "readtemperature",
            P("store", "string", false, "Variable to store reading", null));
        _methods.RegisterParams("lasercontroller", "tecon");
        _methods.RegisterParams("lasercontroller", "tecoff");
        _methods.RegisterParams("lasercontroller", "setlasercurrent",
            P("milliamps", "number", true, "Current in mA", null));
        _methods.RegisterParams("lasercontroller", "readlasercurrent",
            P("store", "string", false, "Variable to store reading", null));
        _methods.RegisterParams("lasercontroller", "laseron");
        _methods.RegisterParams("lasercontroller", "laseroff");
        _methods.RegisterParams("lasercontroller", "connect");
        _methods.RegisterParams("lasercontroller", "disconnect");
        _methods.RegisterParams("lasercontroller", "initialize",
            P("celsius", "number", false, "TEC temperature setpoint", "25"));

        // Camera
        _methods.RegisterParams("camera", "capture",
            P("filename", "string", false, "Output file path", null));
        _methods.RegisterParams("camera", "startgrabbing");
        _methods.RegisterParams("camera", "stopgrabbing");
        _methods.RegisterParams("camera", "setexposure",
            P("exposure", "number", true, "Exposure time in us", "10000"));
    }
}
