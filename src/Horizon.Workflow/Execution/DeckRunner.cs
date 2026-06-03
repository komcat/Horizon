using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// Runs a <see cref="WorkflowDeck"/> — a linear chain of process programs that
/// share one <see cref="WorkflowContext"/>. Before each entry it evaluates the
/// entry's transition condition against that context; a false condition or a
/// failed program stops the deck.
/// </summary>
internal sealed class DeckRunner
{
    private readonly Func<string, WorkflowProgram?> _loadProgram;
    private readonly Func<WorkflowProgram, WorkflowContext, CancellationToken, Task<Result>> _runProgram;
    private readonly Action<DeckProgress> _onProgress;
    private readonly Func<int, string, CancellationToken, Task> _awaitActivation;
    private readonly Action<LogLevel, string> _log;

    public DeckRunner(
        Func<string, WorkflowProgram?> loadProgram,
        Func<WorkflowProgram, WorkflowContext, CancellationToken, Task<Result>> runProgram,
        Action<DeckProgress> onProgress,
        Func<int, string, CancellationToken, Task> awaitActivation,
        Action<LogLevel, string> log)
    {
        _loadProgram = loadProgram;
        _runProgram = runProgram;
        _onProgress = onProgress;
        _awaitActivation = awaitActivation;
        _log = log;
    }

    /// <summary>
    /// Runs the deck. <paramref name="ctx"/> is shared by every program — the
    /// caller supplies it so a DUT run (if any) threads through the whole deck.
    /// </summary>
    public async Task<Result> RunAsync(WorkflowDeck deck, WorkflowContext ctx, CancellationToken ct)
    {
        var entries = deck.Entries;
        bool first = true;   // the first enabled entry runs immediately — no transition gate

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            var label = string.IsNullOrEmpty(entry.Program) ? $"entry {i + 1}" : entry.Program;

            if (!entry.Enabled)
            {
                _log(LogLevel.Info, $"Deck '{deck.Name}': '{label}' disabled — skipped.");
                continue;
            }

            // Transition gate into this entry (skipped for the first program to run).
            if (!first)
            {
                if (!entry.Auto)
                {
                    // Manual transition — pause and wait for the operator to activate.
                    _log(LogLevel.Info, $"Deck '{deck.Name}': waiting for operator to activate '{label}'.");
                    _onProgress(new DeckProgress(i, entries.Count, label, false, false, "awaiting operator activation"));
                    await _awaitActivation(i, label, ct).ConfigureAwait(false);
                }
                else
                {
                    // Auto transition — gate on the condition (empty is always true), then delay.
                    if (!string.IsNullOrWhiteSpace(entry.Condition))
                    {
                        bool pass;
                        try
                        {
                            pass = ConditionEvaluator.Evaluate(entry.Condition, ctx);
                        }
                        catch (FormatException ex)
                        {
                            var msg = $"bad condition '{entry.Condition}' before '{label}' — {ex.Message}";
                            _log(LogLevel.Error, $"Deck '{deck.Name}' stopped — {msg}.");
                            _onProgress(new DeckProgress(i, entries.Count, label, true, false, msg));
                            return Result.Fail(ErrorCode.InvalidParameter, $"Deck stopped — {msg}.");
                        }

                        if (!pass)
                        {
                            var msg = $"condition '{entry.Condition}' is false";
                            _log(LogLevel.Warning, $"Deck '{deck.Name}' stopped before '{label}' — {msg}.");
                            _onProgress(new DeckProgress(i, entries.Count, label, true, false, msg));
                            return Result.Ok($"Deck stopped before '{label}' — {msg}.");
                        }
                    }

                    if (entry.DelaySeconds > 0)
                    {
                        _log(LogLevel.Info, $"Deck '{deck.Name}': waiting {entry.DelaySeconds:0.##}s before '{label}'.");
                        await Task.Delay(TimeSpan.FromSeconds(entry.DelaySeconds), ct).ConfigureAwait(false);
                    }
                }
            }

            var program = _loadProgram(entry.Program);
            if (program == null)
            {
                var msg = $"program '{label}' not found";
                _log(LogLevel.Error, $"Deck '{deck.Name}' stopped — {msg}.");
                _onProgress(new DeckProgress(i, entries.Count, label, true, false, msg));
                return Result.Fail(ErrorCode.FileNotFound, $"Deck stopped — {msg}.");
            }

            _onProgress(new DeckProgress(i, entries.Count, label, false, false, string.Empty));
            var result = await _runProgram(program, ctx, ct).ConfigureAwait(false);
            _onProgress(new DeckProgress(i, entries.Count, label, true, result.IsSuccess, result.Message));

            if (!result.IsSuccess)
            {
                _log(LogLevel.Error, $"Deck '{deck.Name}' stopped — '{label}' failed: {result.Message}");
                return Result.Fail(result.Error, $"Deck stopped at '{label}': {result.Message}");
            }

            first = false;   // a program has now run — later entries gate on their transition
        }

        _log(LogLevel.Info, $"Deck '{deck.Name}' complete — {entries.Count(e => e.Enabled)} program(s) run.");
        return Result.Ok();
    }
}
