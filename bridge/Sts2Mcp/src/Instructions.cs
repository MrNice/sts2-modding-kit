namespace Sts2Mcp;

/// <summary>Sent to the agent when it connects: how to use these tools well.</summary>
public static class Instructions
{
    public const string Text = """
        sts2-mcp drives local test copies of Slay the Spire 2 (offline profiles, the Sts2Bridge dev mod) to test mods.

        Pick a way of working from what the person asked:
        - check (default): they want to know whether something works. launch(mode "check") runs at instant speed; you do
          every step and report results, numbers, log errors and the screenshots that matter.
        - stage: they want to see something (an animation, an effect, a layout). Drive at instant speed up to the step
          before it, then end the plan with a handoff step, which switches to normal speed, shows the instruction in
          their game window and waits for them. Say in chat what you're handing over before you run that plan.
        - watch: they want to follow along. launch(mode "watch") or speed("normal"), and act in small plans.

        Be fast: one plan per decision, not one tool call per click. Plan steps wait for buttons, cards and turns on
        their own, so don't look between steps you can predict; end plans with an expect for what should be true.
        Typical session: launch -> start_run -> goto("shop" / "monster" / "fight <ID>") -> plan -> look -> stop.
        Use look (compact) rather than state; console for setup (gold, card, relic, godmode, win; IDs in
        SCREAMING_SNAKE_CASE, "help <cmd>" for arguments); tail_log for errors. In co-op, console commands are
        synchronized (run once); presses and card plays act as the player whose copy they run in (player "client").
        Always stop at the end, which closes the copies and cleans the mods folder.
        """;
}
