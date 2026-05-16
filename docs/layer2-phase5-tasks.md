# Layer 2 Phase 5 — Recipe Scripting Engine

> Task file for Phase 5. Phases 1–4 of `Horizon.Workflow` are complete and
> bench-verified. This file scopes Phase 5: a recipe **scripting language** —
> the second execution model alongside the step-based `.hwf` workflow.

## Why a second execution model

A `.hwf` workflow is a flat list of steps with limited control flow. A *recipe*
is a **script** — free-form, expressive, with variables, expressions, loops and
conditionals woven together. Process engineers write recipes during product
development to explore and tune; workflows are the locked-down production form.

Per the design doc §13: recreate both. Phase 3 gave the workflow runner;
Phase 5 gives the recipe interpreter.

## Decisions locked from bench feedback

1. **Syntax** — Python/Ruby-flavored for *readability*, but **no significant
   indentation** (the disliked Python trait). Blocks use explicit `{ }`
   delimiters.
2. **Three-part addressing** — `domain.device.method(...)`, e.g.
   `motion.hex-left.move(...)`. The domain is named explicitly (clearer than
   the legacy two-part `hex-left.move`).
3. **Direct Layer 1 access** — the interpreter binds method calls **straight to
   `IHorizonEngine` facet methods**, *not* through the Phase 3 `StepDispatcher`.
   A recipe gets the full engine API and the freedom to script anything the
   hardware can do — it is not limited to the workflow op vocabulary.

## The recipe language

File extension: **`.hrec`** (Layer 2 recipe — distinct from the legacy
`.hrcp`). Stored per-product in `<product>\recipes\`.

```
# comments run from '#' to end of line

set pitch  = 0.05
set count  = 20
set peak   = 0

# three-part call: domain.device.method(named: args)
motion.gantry-main.moveToPosition(position: "Home")
io.IOBottom.setOutputByName(pinName: "Clamp", state: true)
system.wait(ms: 300)
system.log(message: "scan start, pitch = {pitch}")

loop count
    motion.gantry-main.moveRelative(axis: "X", distance: pitch)
    system.wait(ms: 150)
    measurement.Keithley_6482.readCurrent(channel: 1, store: "reading")

    if reading > peak
        set peak = reading
        system.log(message: "new peak {=reading} at iter {=loop.iteration + 1}")
    else
        system.log(message: "  iter {=loop.iteration + 1}: {reading}")
    end
end

system.log(message: "done — peak {=peak}")
io.IOBottom.setOutputByName(pinName: "Clamp", state: false)
```

### Grammar elements

| Element | Form |
|---|---|
| Comment | `# … to end of line` |
| Variable assignment | `set name = expression` |
| Method call | `domain.device.method(arg: expr, arg: expr)` — domainless built-ins: `system.method(...)` |
| Count loop | `loop <expr>` … `end` |
| Infinite loop | `loop` … `end` (cancel/stop to exit) |
| Conditional | `if <cond>` … `end`, with optional `else` |
| Expressions | `+ - * /`, parentheses, number (incl. `1e-9`), string, `true`/`false`, variable refs |
| Comparisons | `> < >= <= == !=` (in `if` conditions) |
| String interpolation | `"{var}"` substitutes a value; `"{=expr}"` evaluates inline |
| Loop variable | `loop.iteration` — 0-based, resets per nesting level |

### Lexing notes

- **No indentation significance** — whitespace and newlines are separators only.
- **Device names contain hyphens** (`gantry-main`). An identifier may include
  `-` when not surrounded by spaces; `a - b` (spaces) is subtraction. (Same
  rule the legacy `RecipeLexer` used.)
- A dotted path `domain.device.method` lexes as `IDENT (DOT IDENT)*`.

## Method binding — direct to the engine

A `RecipeMethodRegistry` maps `domain.method` to a typed handler that calls an
`IHorizonEngine` facet method directly. No reflection — explicit registrations
(the legacy pattern), but bound to facets, not legacy managers.

- The **device** segment becomes the facet method's device argument:
  `motion.gantry-main.moveRelative(axis:"X", distance:5)` →
  `engine.Motion.MoveRelativeAsync("gantry-main", "X", 5, ct)`.
- Domains: `motion`, `io`, `camera`, `laser`, `measurement`, `pneumatic`, plus
  `system` (domainless built-ins: `log`, `wait`).
- The registry exposes the **full facet surface** — every method on each
  `I*Facet`, not the workflow op subset. This is the "freedom" requirement.
- Handlers return `Result` / `Result<T>`; a `store: "name"` argument captures a
  `Result<T>` value into a recipe variable (with unit tag where the facet
  provides one, e.g. measurement → `"A"`).

## Execution model — reuse Phase 3/4 infrastructure

The recipe interpreter is a **tree-walking interpreter**, separate from
`WorkflowRunner`, but it reuses what Phase 3/4 already built:

- **`PauseController`** — breakpoints / pause / step-by-step come for free.
- **`WorkflowStepEvent` + `LogEntry`** — same progress/log surface to Layer 3.
- **`ExpressionEvaluator`** — extend the Phase 3 evaluator with comparison
  operators (it already does `+ - * /`, parens, literals, variables).
- **`IOperatorPrompt`** — `system.askUser(...)` reuses the Phase 4 contract.
- Cancellation via `CancellationToken`, as everywhere else.

Concurrency: a recipe and a workflow must not run hardware at once — a shared
execution guard (the design doc §9 "execution arbitration" note).

## `IProcessService` additions

```csharp
IReadOnlyList<string> ListRecipes();
Result<RecipeProgram> LoadRecipe(string name);   // parsed; carries parse errors
Result SaveRecipe(string name, string source);
Task<Result> RunRecipeAsync(string name, CancellationToken ct = default);
```

`RunRecipeAsync` reuses `StepEvent` / `LogEmitted` / `PausedChanged` and the
`Pause`/`Resume`/`Step` controls — one interactive-execution surface for both
program types.

## Task breakdown

| Task | Deliverable |
|---|---|
| 1 | **Lexer** — `RecipeLexer`, `RecipeToken`, `TokenType`. Tokenize the grammar above; hyphen-in-identifier rule; `#` comments; numbers with scientific notation. |
| 2 | **Parser → AST** — `RecipeParser`, AST nodes (program, set, call, loop, if, expressions). Recursive descent; collect parse errors with line/column. |
| 3 | **Interpreter** — `RecipeInterpreter` tree-walker: `set`, `loop`, `if`, `loop.iteration`, expression eval (extend `ExpressionEvaluator` with comparisons), string interpolation, `RecipeContext`. |
| 4 | **Engine binding** — `RecipeMethodRegistry`: `domain.method` → `IHorizonEngine` facet calls for all domains; `system` built-ins; `store:` capture. |
| 5 | **`IProcessService` + store** — `RecipeStore` (`.hrec` I/O in `recipes\`), `RunRecipeAsync`, reuse `PauseController` / events; console: list / run / pause / step recipes. |

Each task = library code + a `Horizon.Workflow.Console` slice, bench-tested
before the next — the established per-step rhythm.

## Verification (bench, SIPHOG888 loaded)

1. Console → Recipes → write/load a `.hrec`.
2. Run a recipe that moves a stage, toggles IO, loops a measurement, branches
   on an `if` — values logged, interpolation correct.
3. Pause / step / breakpoint a recipe (same controls as a workflow).
4. A parse error reports line/column and does not run.

Success = a recipe script drives Layer 1 hardware directly through
`IProcessService`, with the same interactive-execution controls as a workflow.

## Doc to update

Fold the recipe-language summary into `docs/layer2-process-module-design.md`
§8/§11 once the syntax is bench-confirmed.
