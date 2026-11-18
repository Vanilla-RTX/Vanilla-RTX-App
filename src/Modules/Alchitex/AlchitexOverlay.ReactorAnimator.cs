using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules.Alchitex;

/// <summary>
/// Which way a queue wash crosses the grid. It is always the direction the pack's own tile
/// is travelling at that moment, which is the only thing that decides it: in, back out, or
/// down and away for one that failed.
/// </summary>
public enum ReactorWashDirection
{
    /// <summary>A pack being taken in, following its tile toward the reactor.</summary>
    LeftToRight,

    /// <summary>A finished or returned pack, following its tile back down the row.</summary>
    RightToLeft,

    /// <summary>A failed pack dropping out of the reactor's underside.</summary>
    TopToBottom,
}

/// <summary>
/// Drives the Generate button's three layers so the reactor reads as a live instrument
/// rather than a picture of one - the same idea as the main window's lamp
/// (Core/MainWindow.LampAnimator.cs), tied here to what generation is actually doing:
///
///   1. Background - a 3x3 grid of tiles built here, not an image. Each tile holds an index
///      into whichever of two palettes is currently active (see _activePalette) - five
///      blues normally, a parallel five reds while the alert palette is up - and the whole
///      point of this class is deciding when, how, and against which palette those change.
///   2. Logo - the static mark. Never animated; it's the still point everything else moves
///      around.
///   3. Bloom - the same mark with its glow, animated by opacity alone.
///
/// Vocabulary, by state:
///   - Rest: the tiles' default diagonal arrangement (brightest top-right, darkest
///     bottom-left, straight off the reference art) and the bloom settled at a fixed middle
///     opacity (RestBloomOpacity) - deliberately not randomized. The bloom now has three
///     tiers with real contrast between them (press near-zero, rest in the middle,
///     flower-dance hover near max), and a random rest could land close enough to the hover
///     tier that unhovering stopped reading as a drop at all.
///   - Flower dance / press-and-hold: one mechanic, two readings. Idle and hovering, the
///     four corners and the four edge-midpoints swap places within their own ring,
///     independently and on their own schedule, around a centre that keeps changing on its
///     own, corners dark and edges bright; the bloom rises to near max. Pressed, the same
///     mechanic runs inverted - corners bright, edges dark - while the bloom collapses to
///     near zero instead, as though the charge is being drawn out from the middle. Neither
///     means anything about the pipeline, unlike everything else here - they exist purely so
///     hovering and pressing have an answer at all while idle.
///   - Generating: one behavior per phase (see Pulse). Underneath all of them the bloom
///     breathes on a loop, which is what separates "running" from "idle" at a glance.
///   - Waiting: the reactor is powered but blocked on something outside itself - a
///     confirmation dialog, a pack being uninstalled, a folder sweep. A single bright cell
///     orbits the eight perimeter tiles with a fading tail behind it, which is the one
///     stance here that reads as "this is not finished, and it is not your turn yet".
///   - Abort hint / error flash: the two places the alert palette (AlertPalette) takes over
///     from the blues. Hovering the button mid-run is just the swap, held for as long as the
///     pointer stays - "click me and this stops" - with nothing painted over it, so whatever
///     the run is actually doing keeps animating underneath, just red instead of blue. A
///     pack aborting on error, or being handed back mid-run, gets a timed version instead:
///     every tile flashes to it at once and eases back on its own.
///
/// How often a phase reports decides what kind of behavior it can have, and getting this
/// wrong is invisible in code and obvious on screen:
///   - Reports continuously (GeneratingTextures) -> a per-pulse behavior. It is also the
///     only phase long enough to show off a one-shot flourish.
///   - Reports once, then works for a long time in silence (Staging, copying thousands of
///     files; ScanningTextures, the orchestrator's own single pass over the whole pack) ->
///     a timer. BeginPhaseLoop exists for exactly this: a per-pulse behavior has a single
///     pulse to work with and leaves the reactor looking switched off for the length of the
///     phase. Staging repeats the ripple (its own field rerolled each time); Scanning
///     repeats the travelling gradient GeneratingTextures otherwise only shows off in a
///     short random burst.
///
/// Running behaviors are built from two ingredients: tiles firing at random, and a
/// travelling gradient (StepGradientWave) - the resting arrangement's own diagonal ramp
/// rolled across the grid. Phases that mean a direction are pure gradient and keep theirs;
/// GeneratingTextures mixes both (StepBusyWork).
///
/// On top of those sit the one-shot flourishes - PlayQueueWash, PlayRipple, PlayImplosion,
/// PlayCompletionWash. They aren't driven by a pulse or a timer; something happened and the
/// reactor answers once, across staggered tiles over several hundred milliseconds. Because
/// of that they take the grid for their duration (ClaimGrid) - otherwise the next pulse
/// repaints all nine tiles and the flourish never finishes.
///
/// BeginAbortHint and PlayErrorFlash are neither pulse-driven nor flourishes, and they touch
/// no tile directly - they only flip _activePalette, for as long as the pointer hovers or a
/// fixed interval respectively, and let whatever is already painting the grid (a phase, a
/// flourish, either one) carry on completely undisturbed underneath. Both used to claim the
/// grid and paint their own shape over it; that was reverted because it meant the one thing
/// actually happening at the time - the run's own progress, or the eject animation for the
/// very pack that just failed - was exactly what got hidden.
///
/// Everything routes through AnimateTile/SetBloom, which honor
/// EnvironmentVariables.Persistent.SuspendUIAnimations: with it on, every transition is applied
/// instantly and the looping behaviors (bloom breathing, the flower dance's swapping) never
/// start. The reactor still tracks state, it just stops moving.
///
/// Cost control matters here, and it has two halves that are easy to confuse:
///
///   * The frame rate is MinPulseIntervalMs. PulseGeneratingTextures is called once per
///     texture - thousands of calls a second on a real pack - and the throttle is what
///     bounds that to a fixed cost. Nine brushes' worth of colour animation is cheap; nine
///     thousand is not. This is the knob for a slow machine.
///   * How fast it *feels* is the transition durations, which are a separate thing
///     entirely. They are all shorter than the pulse interval on purpose: a transition
///     still easing when the next one starts on top of it is what made the reactor look
///     sluggish while changing stances rapidly.
///   * Moving *between* stances is the third thing, and the one those short durations get
///     wrong: a new stance's first step covers a whole change of pattern at the speed meant
///     for one step within it. EnterStance opens a short blend window on every change mid-run
///     that stretches transitions to arrive over it instead (StanceBlendMs).
///
/// A tile runs exactly one storyboard at a time, and starting a new sequence stops the one
/// before it - see PlayTileSequence for why that is load-bearing rather than tidy, and what
/// it means for anything that wants two things happening on one tile at once.
/// </summary>
public sealed class ReactorAnimator
{
    // The RTX Reactor logo's five blues, brightest first. Every tile is always one of
    // these - the reactor never shows a color that isn't part of the mark.
    public static readonly Color[] Palette =
    {
        ColorHelper.FromArgb(255, 0, 72, 138), // brightest
        ColorHelper.FromArgb(255, 0, 59, 114),
        ColorHelper.FromArgb(255, 0, 53, 102),
        ColorHelper.FromArgb(255, 0, 41, 78),
        ColorHelper.FromArgb(255, 0, 35, 66), // darkest
    };

    /// <summary>
    /// A parallel five-step ramp in red, index-for-index the same shape as Palette. The
    /// abort cross used to be three hand-picked reds with no relationship to anything else
    /// in the class; this is that same ramp generalized into a real second palette, so any
    /// index-based pattern - the cross, a future stance, whatever StepBusyWork happens to be
    /// rolling at the moment - can be told to read against red instead of blue without a
    /// second copy of the logic that paints it. See _activePalette.
    /// </summary>
    public static readonly Color[] AlertPalette =
    {
        ColorHelper.FromArgb(255, 255, 0, 0), // brightest
        ColorHelper.FromArgb(255, 192, 0, 0),
        ColorHelper.FromArgb(255, 128, 0, 0),
        ColorHelper.FromArgb(255, 96, 0, 0),
        ColorHelper.FromArgb(255, 64, 0, 0), // darkest
    };

    /// <summary>
    /// Which of the two palettes every colour resolution in this class currently reads
    /// against. Swapping this is the entire "go red" mechanism: whatever is already running
    /// (StepBusyWork, a gradient sweep, a ripple, any of it) keeps painting the exact same
    /// indices it always would, and this one field decides whether that comes out blue or
    /// red. See BeginAbortHint and PlayErrorFlash - neither one paints anything of its own
    /// any more; they just flip this and let the animation already in flight carry the rest.
    /// </summary>
    private Color[] _activePalette = Palette;

    // Resting arrangement, as palette indices - a diagonal gradient with the brightest
    // cell top-right and the darkest bottom-left, matching the reference art.
    private static readonly int[,] RestLayout =
    {
        { 2, 1, 0 },
        { 3, 2, 1 },
        { 4, 3, 2 },
    };

    // The eight perimeter tiles in clockwise order, starting top-left. The waiting stance
    // walks a bright head around this ring; the centre tile is deliberately not part of it,
    // so the ring reads as a ring rather than as nine tiles taking turns.
    private static readonly (int Row, int Col)[] OrbitRing =
    {
        (0, 0), (0, 1), (0, 2), (1, 2), (2, 2), (2, 1), (2, 0), (1, 0),
    };

    private const int GridSize = 3;

    // Nothing reaches the compositor more often than this, however hard the pipeline
    // hammers Pulse. ~14 repaints a second of a nine-tile grid, which is the whole reason
    // a run of thousands of textures costs the same as a run of ten.
    //
    // This is the frame rate, and it is deliberately NOT what makes the reactor feel quick.
    // Raising it costs real work on an iGPU for very little; the transition durations below
    // are the lever, and they are all shorter than the interval so a change lands inside its
    // own slot instead of still easing when the next one starts on top of it.
    private const double MinPulseIntervalMs = 70;

    // How fast the waiting stance's head moves around the ring. Slow enough to read as one
    // travelling cell rather than a flicker, fast enough to look impatient.
    private const double OrbitStepMs = 90;

    // A travelling gradient's cycle length: the palette walked out and back again, so the
    // brightest and darkest ends each come round once per cycle with no seam between them.
    private static readonly int WavePeriod = (Palette.Length - 1) * 2;

    // Just under the pulse throttle: consecutive steps still overlap enough for the band to
    // slide rather than step, without a step being half-finished when the next one lands.
    private const double WaveStepMs = 75;

    // What a tile costs to settle when nothing is driving it - coming back to rest.
    private const double SettleMs = 190;

    // Fixed rather than rolled - the bloom now has three deliberate tiers (press near-zero,
    // rest here, flower-dance hover near max) and a random rest risked landing close enough
    // to the hover tier that unhovering didn't read as a drop. Middle has to mean middle,
    // every time, or the other two tiers lose their contrast.
    private const double RestBloomOpacity = 0.55;

    private readonly Grid _tileGrid;
    private readonly Image? _bloom;

    private readonly SolidColorBrush[,] _brushes = new SolidColorBrush[GridSize, GridSize];

    // One storyboard per tile, built once and reused forever, and this matters far more
    // than it looks. The obvious version news up a Storyboard per colour change and
    // Begin()s it, and nothing ever stops the one before: Pulse repaints nine tiles up to
    // fourteen times a second for the length of a run, the press-hold, orbit and abort
    // timers add their own, and the one-shot flourishes fire several per tile at once. Each
    // of those is a dependent animation ticking on the UI thread, and they accumulated on
    // every brush - which is what made the button lock the window up under a fast pack or
    // an impatient click. Nine live storyboards is now the hard ceiling, whatever the
    // pipeline does.
    private readonly Storyboard?[,] _tileStoryboards = new Storyboard?[GridSize, GridSize];

    private readonly Random _random = new();

    private Storyboard? _bloomLoop;
    private DispatcherTimer? _loopTimer;
    private DispatcherTimer? _abortHintEndTimer;
    private DispatcherTimer? _flowerTimer;
    private DateTime _lastPulseUtc = DateTime.MinValue;
    private bool _isGenerating;
    private bool _isAbortHintActive;
    private bool _isWaiting;
    private bool _isFlowerDancing;
    private bool _isInitialized;

    private static bool AnimationsSuspended => EnvironmentVariables.Persistent.SuspendUIAnimations;

    // ── Stance blending ──────────────────────────────────────────────────────

    /// <summary>What is painting the grid right now, as far as blending cares. A gradient
    /// wave's axis and direction count as part of its identity (see EnterStance's variant),
    /// because a wave picking a new axis is as much a change of pattern as a new phase.</summary>
    private enum Stance { None, Rest, Busy, Wave, Erasure, Trail, Orbit, Flower, Flourish }

    private Stance _stance = Stance.None;
    private int _stanceVariant;
    private DateTime _stanceBlendUntilUtc = DateTime.MinValue;

    /// <summary>
    /// How long a change of stance mid-run takes to arrive. Every running behaviour paints at
    /// its own step duration - 55 to 75ms, tuned for continuous motion inside one stance - and
    /// that same duration is what its very first step gets, coming from an unrelated pattern.
    /// All nine tiles jumping that far that fast is what read as a cut between stances. While
    /// this window is open every transition is stretched to at least what is left of it, so the
    /// old pattern slides into the new one and the stretch shrinks back to the stance's own
    /// pace as the window closes.
    /// </summary>
    private const double StanceBlendMs = 260;

    /// <summary>
    /// Called by every behaviour before it paints. A change opens the blend window, but only
    /// during a run: idle stances (rest, hover, press) answer the pointer and must stay quick,
    /// and the flourishes carry their own rise from the tile's current colour, so they record
    /// the change without being slowed by it - see SetTileColor, the only place the window is read.
    /// </summary>
    private void EnterStance(Stance stance, int variant = 0)
    {
        if (stance == _stance && variant == _stanceVariant) return;

        _stance = stance;
        _stanceVariant = variant;

        if (_isGenerating)
            _stanceBlendUntilUtc = DateTime.UtcNow.AddMilliseconds(StanceBlendMs);
    }

    private double StanceBlendRemainingMs
        => Math.Max(0, (_stanceBlendUntilUtc - DateTime.UtcNow).TotalMilliseconds);

    public ReactorAnimator(Grid tileGrid, Image? bloomLayer)
    {
        _tileGrid = tileGrid ?? throw new ArgumentNullException(nameof(tileGrid));
        _bloom = bloomLayer;
    }

    /// <summary>Builds the 3x3 tiles and puts the reactor in its resting state. Safe to
    /// call more than once; only the first call does anything.</summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        _tileGrid.RowDefinitions.Clear();
        _tileGrid.ColumnDefinitions.Clear();
        _tileGrid.Children.Clear();

        for (var i = 0; i < GridSize; i++)
        {
            _tileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var brush = new SolidColorBrush(Palette[RestLayout[row, col]]);
                var tile = new Border { Background = brush };

                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, col);

                _brushes[row, col] = brush;
                _tileGrid.Children.Add(tile);
            }
        }

        _isInitialized = true;
        EnterRest();
    }

    // ── States ───────────────────────────────────────────────────────────────

    /// <summary>Back to the default arrangement, with the bloom settling at its fixed middle
    /// tier - see RestBloomOpacity.</summary>
    public void EnterRest()
    {
        if (!_isInitialized) return;

        StopOrbit();
        StopBloomLoop();
        StopFlowerDance();
        StopErrorFlashRevertTimer();
        ReleaseGrid();
        _isGenerating = false;

        // A batch can end while an error flash is still mid-flight (its last pack failing
        // right at the finish), and rest must never come out red regardless. Falling back to
        // blue is its own instant cut rather than folded into the settle below, for the same
        // reason BeginAbortHint's switch is instant - easing across this wide a hue gap reads
        // as mud, not a transition.
        var wasAlert = _activePalette != Palette;
        _activePalette = Palette;

        EnterStance(Stance.Rest);

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, RestLayout[row, col], wasAlert ? 0 : SettleMs);

        SetBloom(RestBloomOpacity, SettleMs);
    }

    /// <summary>A run has started: the bloom begins breathing and keeps at it until
    /// EndGeneration, underneath whatever the per-phase pulses are doing.</summary>
    public void BeginGeneration()
    {
        if (!_isInitialized) return;

        // Defensive rather than load-bearing given the button's own wiring: a press always
        // precedes a click and already stops the dance, but keyboard activation doesn't
        // necessarily go through PointerPressed first.
        StopFlowerDance();

        _isGenerating = true;
        StartBloomLoop();
    }

    public void EndGeneration()
    {
        if (!_isInitialized) return;

        _isGenerating = false;

        // The pointer may still be sitting on the button when the run ends, and a wait can
        // still be up if the run ended out from under one. Drop both explicitly, or their
        // flags would keep swallowing every later pulse.
        EndAbortHintImmediate();
        StopOrbit();
        EnterRest();
    }

    // ── Waiting stance ───────────────────────────────────────────────────────

    /// <summary>
    /// The reactor is powered and blocked on something it doesn't control: a confirmation
    /// dialog the user hasn't answered, a pack being uninstalled, a folder sweep. A single
    /// bright cell orbits the eight perimeter tiles with a fading tail behind it - the one
    /// unmistakably "still going, nothing to show yet" shape a 3x3 grid can make.
    ///
    /// Unlike the abort stance, this claims the whole grid, so Pulse is ignored while it's
    /// up - a wait genuinely has nothing to show, where hovering during a run still has a
    /// real animation underneath worth showing. It keeps the bloom alive regardless: during
    /// a run that's the loop already breathing, and outside one it starts it, because a wait
    /// outside a run is still the machine doing something.
    ///
    /// Safe to call twice; the second call is a no-op rather than a restart, so nesting
    /// waits (a dialog inside a batch, say) can't reset the orbit halfway round.
    /// </summary>
    public void BeginWaiting()
    {
        if (!_isInitialized || _isWaiting) return;

        _isWaiting = true;
        _loopPhase = null; // the wait stance takes the loop timer over from any phase using it
        StopFlowerDance(); // a dialog can open while the pointer is resting on or pressing the button
        ReleaseGrid();

        _orbitHead = 0;
        PaintOrbit(100);

        // Outside a run there's no loop yet; inside one it's already going and this is a
        // no-op that just re-arms it after a suspended-animations toggle.
        StartBloomLoop();

        if (AnimationsSuspended) return;

        StopLoopTimer();
        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OrbitStepMs) };
        _loopTimer.Tick += (s, e) =>
        {
            _orbitHead = (_orbitHead + 1) % OrbitRing.Length;
            PaintOrbit(OrbitStepMs * 1.25);
        };
        _loopTimer.Start();
    }

    /// <summary>Whatever the reactor was waiting on has happened. A run in progress goes
    /// back to being repainted by its pulses; otherwise the reactor settles.</summary>
    public void EndWaiting()
    {
        if (!_isInitialized || !_isWaiting) return;

        StopOrbit();

        if (_isGenerating)
        {
            // The next pulse repaints the grid properly; this just gets the orbit off it in
            // the meantime, the same way EndAbortHintImmediate does.
            EnterStance(Stance.Rest);
            for (var row = 0; row < GridSize; row++)
                for (var col = 0; col < GridSize; col++)
                    AnimateTile(row, col, RestLayout[row, col], 110);

            return;
        }

        EnterRest();
    }

    // Which ring tile the bright head is currently on, as an index into OrbitRing.
    private int _orbitHead;

    /// <summary>
    /// Draws the orbit as it stands: the head at its brightest, the three tiles behind it
    /// stepping down through the palette, the rest of the ring dark. The centre holds its
    /// resting value so there's something for the ring to be read against.
    /// </summary>
    private void PaintOrbit(double durationMs)
    {
        EnterStance(Stance.Orbit);

        for (var i = 0; i < OrbitRing.Length; i++)
        {
            var (row, col) = OrbitRing[i];

            // How far behind the head this tile sits, walking backwards around the ring.
            var trail = (_orbitHead - i + OrbitRing.Length) % OrbitRing.Length;

            AnimateTile(row, col, Math.Min(trail, Palette.Length - 1), durationMs);
        }

        AnimateTile(1, 1, RestLayout[1, 1], durationMs);
    }

    private void StopOrbit()
    {
        StopLoopTimer();
        _isWaiting = false;
        _loopPhase = null;
    }

    // ── The background loop as a phase's own behaviour ───────────────────────

    /// <summary>
    /// Which phase currently owns the background-loop timer, if any. The external wait
    /// stance and a long phase share one timer but not one meaning: a wait owns the whole
    /// grid and swallows pulses, while this is simply what one phase looks like and has to
    /// step aside the moment the next phase reports.
    /// </summary>
    private Core.AlchitexPhase? _loopPhase;

    /// <summary>
    /// Runs `tick` on a fixed interval for as long as `phase` is the one reporting - for a
    /// phase that reports once and then works for a long time without saying anything else.
    ///
    /// Staging and ScanningTextures are the cases that need it, and it is worth being
    /// explicit about why, because it is the mirror image of the ripple problem (§ the class
    /// comment on flourishes): copying a heavy pack is thousands of files between two
    /// progress reports, and the orchestrator's own pass over the whole pack is one more,
    /// so there is exactly one pulse for the length of either. A per-pulse behaviour has
    /// nothing to work with there - the reactor sat on one twitched tile and looked switched
    /// off. A phase that reports continuously wants a pulse behaviour; a phase that reports
    /// once and then disappears wants a timer.
    ///
    /// Each phase gets a different `tick`: Staging repeats the ripple (PlayRipple, at its own
    /// duration so consecutive drops chain rather than overlap), ScanningTextures repeats the
    /// travelling gradient (StepGradientWave with no axis, the same call StepBusyWork makes
    /// during a short random burst - just running for the whole phase instead). Whether `tick`
    /// claims the grid is up to it, not this method - PlayRipple does, StepGradientWave
    /// doesn't, and both are fine to defer to a flourish that's still playing (below).
    /// </summary>
    private void BeginPhaseLoop(Core.AlchitexPhase phase, Action tick, double intervalMs)
    {
        if (!_isInitialized || _isWaiting) return;  // the real wait stance outranks this
        if (_loopPhase == phase) return;             // already running for this phase

        StopLoopTimer();
        _loopPhase = phase;

        // Pipeline work is always blue - only abort-hover and an error flash ever go red.
        // A previous pack's error flash can still be mid-flight when the next one's Staging
        // or ScanningTextures starts (its own revert timer hasn't ticked yet), and Scanning's
        // tick is index-based (StepGradientWave), so without this it would briefly read
        // against the wrong palette. An instant reset rather than trusting the flash's own
        // timer to land first.
        _activePalette = Palette;

        tick();

        if (AnimationsSuspended) return;

        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        _loopTimer.Tick += (s, e) =>
        {
            // Defer to a flourish that is still playing rather than cutting it short - the
            // intake wash lands right about here, since a pack's run starts the moment its
            // tile has finished flying in.
            if (IsGridClaimed) return;

            tick();
        };
        _loopTimer.Start();
    }

    private void StopPhaseLoop()
    {
        if (_loopPhase == null) return;

        _loopPhase = null;
        StopLoopTimer();
    }

    private void StopLoopTimer()
    {
        if (_loopTimer == null) return;

        _loopTimer.Stop();
        _loopTimer = null;
    }

    // ── Abort stance ─────────────────────────────────────────────────────────

    /// <summary>
    /// The reactor's "click me and this stops" face, shown while the pointer is over the
    /// button during a run: the whole grid switches to the alert palette, full stop. No
    /// shape, no timer, no repaint of its own - the switch is the entire effect. Whatever the
    /// pipeline is already animating keeps animating exactly as it would (Pulse is no longer
    /// gated on this - see its own remarks), and every colour in this class already resolves
    /// through _activePalette, so it simply reads red instead of blue from here on.
    ///
    /// This replaced a fixed red cross that painted over and froze whatever was running
    /// underneath. The interruption was the point once, but it meant hovering hid the one
    /// thing that would actually tell you whether the run itself was still healthy - the X
    /// looked the same whether the pack behind it was breezing through or stuck. A plain
    /// palette swap says "you can stop this" without hiding "and here's what stopping it
    /// would cost you".
    ///
    /// The bloom is left completely alone. It belongs to the run, and the run is still
    /// going until the user actually commits.
    /// </summary>
    public void BeginAbortHint()
    {
        if (!_isInitialized) return;

        // A pending teardown means the pointer only "left" for a moment - see EndAbortHint.
        StopAbortHintEndTimer();

        if (_isAbortHintActive) return;
        _isAbortHintActive = true;

        _activePalette = AlertPalette;
    }

    /// <summary>
    /// Pointer left. Deliberately debounced rather than immediate: a tooltip opening over
    /// the button counts as leaving it, and the pointer re-enters a frame later, so acting
    /// straight away made the grid flicker red-blue-red for no reason. A re-entry inside the
    /// grace period cancels the teardown entirely.
    /// </summary>
    public void EndAbortHint()
    {
        if (!_isInitialized || !_isAbortHintActive) return;
        if (_abortHintEndTimer != null) return; // already winding down

        if (AnimationsSuspended)
        {
            EndAbortHintImmediate();
            return;
        }

        _abortHintEndTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _abortHintEndTimer.Tick += (s, e) => EndAbortHintImmediate();
        _abortHintEndTimer.Start();
    }

    /// <summary>Drops the abort stance now, no grace period - for the click actually
    /// landing, the run ending, or the module closing. Just the palette back to blue - see
    /// BeginAbortHint for why there's nothing else to undo.</summary>
    public void EndAbortHintImmediate()
    {
        if (!_isInitialized) return;

        var wasActive = _isAbortHintActive;
        StopAbortHint();

        if (!wasActive) return;

        _activePalette = Palette;
    }

    private void StopAbortHint()
    {
        StopAbortHintEndTimer();
        _isAbortHintActive = false;
    }

    private void StopAbortHintEndTimer()
    {
        if (_abortHintEndTimer == null) return;

        _abortHintEndTimer.Stop();
        _abortHintEndTimer = null;
    }

    // ── Error flash ──────────────────────────────────────────────────────────

    // How long an error keeps the grid on the alert palette - long enough to cover a queue
    // wash or an eject animation playing at the same moment, comfortably short of feeling
    // like a stuck state.
    private const double ErrorFlashDurationMs = 510;

    // Reverts _activePalette once the window above has elapsed. A dedicated timer rather
    // than hooking a tile storyboard's Completed event: this no longer paints anything of
    // its own to hook (see PlayErrorFlash), and even if it did, a storyboard interrupted
    // early never raises Completed - a palette stuck on red because of that would be exactly
    // the kind of orphaned state this class otherwise works hard to avoid. Restarting the
    // timer on every call, rather than letting an earlier one fire mid-flash, is what makes
    // back-to-back errors extend the red window instead of cutting each other off early.
    private DispatcherTimer? _errorFlashRevertTimer;

    /// <summary>
    /// A pack aborting on error, or being handed back mid-run: the grid reads red for a
    /// while, exactly the same mechanism as BeginAbortHint and for the same reason - no
    /// painting of its own, just _activePalette flipped to AlertPalette. Whatever is actually
    /// on screen at the moment (most often the queue wash or eject animation already playing
    /// for the very same pack) keeps animating completely undisturbed and simply reads red
    /// instead of blue while this is up.
    ///
    /// This used to paint its own flat flash across the grid, and that was the wrong idea:
    /// a tile runs exactly one storyboard, so painting a flash meant *replacing* whatever
    /// animation was already there rather than recolouring it - the eject wave underneath
    /// simply never got to run, because this cancelled it. Decoupling colour from animation
    /// only holds if colour changes never touch the animation, which is the one thing the
    /// old version did.
    /// </summary>
    public void PlayErrorFlash()
    {
        if (!_isInitialized || _isAbortHintActive) return;

        _activePalette = AlertPalette;

        StopErrorFlashRevertTimer();
        _errorFlashRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ErrorFlashDurationMs) };
        _errorFlashRevertTimer.Tick += (s, e) =>
        {
            StopErrorFlashRevertTimer();
            _activePalette = Palette;
        };
        _errorFlashRevertTimer.Start();
    }

    private void StopErrorFlashRevertTimer()
    {
        if (_errorFlashRevertTimer == null) return;

        _errorFlashRevertTimer.Stop();
        _errorFlashRevertTimer = null;
    }

    // ── Flower dance and press stances ───────────────────────────────────────
    //
    // The four corners and the four edge-midpoints, read as two independent rings around a
    // held centre - the "petals" the name comes from. One ring carries the two darkest
    // blues, the other the two brightest, so the shape reads as an entirely different
    // silhouette to the abort cross or the waiting orbit at a glance - no straight edges, no
    // travelling band.
    //
    // Hovering and pressing share this one mechanic rather than each getting its own -
    // corners dark/edges bright while hovering, corners bright/edges dark while pressed (see
    // _flowerInverted) - so press reads as unmistakably the same shape doing the opposite
    // thing, rather than a second silhouette for the eye to learn. Press used to fire a
    // repeating ripple instead, and that read as a smaller version of Staging's own
    // continuous ripple rather than something that belonged to the button itself; reusing
    // the flower here fixed that by construction, since the two no longer share an effect.

    private static readonly (int Row, int Col)[] FlowerCorners =
    {
        (0, 0), (0, 2), (2, 2), (2, 0),
    };

    private static readonly (int Row, int Col)[] FlowerEdges =
    {
        (0, 1), (1, 2), (2, 1), (1, 0),
    };

    // Palette[3]/[4] are the two darkest blues, Palette[0]/[1] the two brightest - the same
    // contrast the resting diagonal itself spans, just regrouped into two rings instead of
    // one ramp. Which ring draws which is decided per call by _flowerInverted, not baked
    // into these two arrays any more.
    private static readonly int[] FlowerDarkShades = { 3, 4 };
    private static readonly int[] FlowerBrightShades = { 0, 1 };

    private const double FlowerStepMs = 130;
    private const double FlowerSwapDurationMs = 110;
    private const double FlowerCenterDurationMs = 90;
    private const double FlowerBloomMs = 260;
    private const double FlowerBloomMin = 0.90;
    private const double FlowerBloomMax = 1.00;

    // Index into whichever shade array applies to that ring, one per ring slot. Null
    // whenever the dance isn't running - StepFlower and PaintFlower both bail on that rather
    // than assume a Begin method always ran first.
    private int[]? _flowerCornerAssignment;
    private int[]? _flowerEdgeAssignment;

    // True for the pressed variant: corners draw the bright shades and edges the dark ones,
    // the opposite of hovering. Set once per StartFlowerDance call and read only by
    // PaintFlower, so nothing else needs to know which variant is running.
    private bool _flowerInverted;

    /// <summary>
    /// Pointer resting on the reactor with nothing running: the two rings start swapping
    /// their tiles' colours amongst themselves, independently and on their own schedule, and
    /// the bloom rises - the only stance in this class that answers "the pointer is here"
    /// rather than "here's what the pipeline is doing".
    /// </summary>
    public void BeginFlowerDance() => StartFlowerDance(
        inverted: false,
        FlowerBloomMin + _random.NextDouble() * (FlowerBloomMax - FlowerBloomMin));

    /// <summary>Pointer left while just hovering. Settles the grid and the bloom back to
    /// rest, the same as every other stance that isn't mid-run.</summary>
    public void EndFlowerDance()
    {
        if (!_isInitialized || !_isFlowerDancing || _flowerInverted) return;

        EnterRest();
    }

    /// <summary>
    /// Pointer down on the reactor while idle: the same flower mechanic, inverted - corners
    /// draw bright, edges draw dark - while the bloom collapses instead of rising, as though
    /// the charge is being drawn out of it. Continuous for as long as the button is held,
    /// the same as hovering, rather than a flourish repeating on a timer.
    /// </summary>
    public void BeginPressHold()
    {
        // Pressing overrides the hover dance outright - two continuous dances racing the
        // same tiles on their own schedules would fight every tick, and "weaker" has to win
        // over "stronger" the instant the finger comes down.
        StopFlowerDance();
        StartFlowerDance(inverted: true, _random.NextDouble() * 0.10);
    }

    /// <summary>Pointer released or capture lost while pressed. Hands back to whatever the
    /// reactor should be doing - a run in progress keeps its behavior, otherwise it
    /// settles.</summary>
    public void EndPressHold()
    {
        if (!_isInitialized || !_isFlowerDancing || !_flowerInverted) return;

        StopFlowerDance();

        if (_isGenerating)
        {
            StartBloomLoop();
            return;
        }

        EnterRest();
    }

    /// <summary>
    /// Shared starter for both variants above. The caller is expected to check IsGenerating
    /// first - BeginAbortHint owns hover and press alike during a run - but this bails on
    /// its own too, so a race between the two can't leave the dance running underneath the
    /// cross. Safe to call while the same variant is already running; the second call is a
    /// no-op rather than a restart.
    /// </summary>
    private void StartFlowerDance(bool inverted, double bloomOpacity)
    {
        if (!_isInitialized || _isGenerating || _isFlowerDancing) return;

        _isFlowerDancing = true;
        _flowerInverted = inverted;
        ReleaseGrid();
        EnterStance(Stance.Flower);

        // Checkerboard start - opposite ring slots matching - so the very first frame already
        // reads as a flower instead of needing a few ticks to separate out of a solid colour.
        _flowerCornerAssignment = new[] { 0, 1, 0, 1 };
        _flowerEdgeAssignment = new[] { 0, 1, 0, 1 };

        PaintFlower(FlowerSwapDurationMs);
        AnimateTile(1, 1, _random.Next(Palette.Length), FlowerCenterDurationMs);
        SetBloom(bloomOpacity, FlowerBloomMs);

        if (AnimationsSuspended) return;

        StopFlowerTimer();
        _flowerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlowerStepMs) };
        _flowerTimer.Tick += (s, e) => StepFlower();
        _flowerTimer.Start();
    }

    private void StepFlower()
    {
        if (_flowerCornerAssignment == null || _flowerEdgeAssignment == null) return;

        // Each ring swaps one randomly-chosen adjacent pair most ticks, staggered
        // independently - the same "on its own schedule" trick the abort cross uses on its
        // three reds, so the two rings never read as locked to the same beat.
        if (_random.NextDouble() < 0.7) SwapAdjacent(_flowerCornerAssignment);
        if (_random.NextDouble() < 0.7) SwapAdjacent(_flowerEdgeAssignment);

        PaintFlower(FlowerSwapDurationMs);

        // The centre answers to neither ring's schedule - a fresh, independent roll every
        // single tick, which is what makes it read as "rapidly" against rings that only
        // swap most ticks.
        AnimateTile(1, 1, _random.Next(Palette.Length), FlowerCenterDurationMs);
    }

    private void SwapAdjacent(int[] ring)
    {
        var i = _random.Next(ring.Length);
        var j = (i + 1) % ring.Length;
        (ring[i], ring[j]) = (ring[j], ring[i]);
    }

    private void PaintFlower(double durationMs)
    {
        if (_flowerCornerAssignment == null || _flowerEdgeAssignment == null) return;

        var cornerShades = _flowerInverted ? FlowerBrightShades : FlowerDarkShades;
        var edgeShades = _flowerInverted ? FlowerDarkShades : FlowerBrightShades;

        for (var i = 0; i < FlowerCorners.Length; i++)
        {
            var (row, col) = FlowerCorners[i];
            AnimateTile(row, col, cornerShades[_flowerCornerAssignment[i]], durationMs);
        }

        for (var i = 0; i < FlowerEdges.Length; i++)
        {
            var (row, col) = FlowerEdges[i];
            AnimateTile(row, col, edgeShades[_flowerEdgeAssignment[i]], durationMs);
        }
    }

    /// <summary>Tears down whichever variant is running - its timer and its state - without
    /// touching the grid or the bloom. Callers that need those settled go through EnterRest
    /// or the appropriate End method instead.</summary>
    private void StopFlowerDance()
    {
        StopFlowerTimer();
        _isFlowerDancing = false;
        _flowerInverted = false;
        _flowerCornerAssignment = null;
        _flowerEdgeAssignment = null;
    }

    private void StopFlowerTimer()
    {
        if (_flowerTimer == null) return;

        _flowerTimer.Stop();
        _flowerTimer = null;
    }

    // ── Per-phase behavior ───────────────────────────────────────────────────

    /// <summary>
    /// The reactor's reaction to one step of the pipeline. Called from the overlay's
    /// progress handler, which gets the phase straight from AlchitexPipeline rather than
    /// from parsing status strings - if a phase is added there, it gets a behavior here.
    /// </summary>
    public void Pulse(Core.AlchitexPhase phase)
    {
        if (!_isInitialized) return;

        // A phase-driven background loop belongs to exactly one phase, so anything else
        // reporting ends it. Ahead of every gate below deliberately: a pulse that gets
        // swallowed still means that phase is over, and leaving the loop running under the
        // next one would outlast the gate that swallowed it. Safe above the wait check
        // because the wait stance takes _loopPhase to null when it claims the timer, so this
        // can never stop a wait's own orbit.
        if (_loopPhase.HasValue && _loopPhase.Value != phase) StopPhaseLoop();

        // The waiting orbit owns the whole grid while it's up - see BeginWaiting. It isn't a
        // flourish; it means the user is being told something more important than progress.
        // The abort stance used to own the grid the same way; it no longer does (see
        // BeginAbortHint) - pulses keep landing normally while hovering, which is what lets
        // the alert palette show whatever the pipeline is actually doing instead of hiding it.
        if (_isWaiting) return;

        // A pack finishing is the one thing the reactor must never fail to say, and a
        // phase that starts its own background loop (Staging, ScanningTextures) is the same
        // problem in a different shape: each reports exactly once and then falls silent for
        // as long as the phase itself takes (see BeginPhaseLoop), so dropping that one call
        // here doesn't just skip a repaint the way it would for GeneratingTextures - it
        // leaves the reactor showing nothing for the whole phase, because nothing will ever
        // ask again. All three are exempt from both gates below. Safe to let through
        // unconditionally: BeginPhaseLoop no-ops if its phase is already running (so this
        // can't double-start a timer), and interrupting a flourish that's still playing is
        // exactly what PlayTileSequence is built to do cleanly - see StopTile.
        if (phase != Core.AlchitexPhase.Done &&
            phase != Core.AlchitexPhase.Staging &&
            phase != Core.AlchitexPhase.ScanningTextures)
        {
            // Any one-shot flourish owns the grid for its duration, or the next pulse
            // repaints all nine tiles out from under it - see ClaimGrid.
            if (IsGridClaimed) return;

            // Throttle everything else. GeneratingTextures alone can arrive thousands of
            // times a second; the rest are rare but there's no reason to special-case them.
            var now = DateTime.UtcNow;
            if ((now - _lastPulseUtc).TotalMilliseconds < MinPulseIntervalMs) return;
            _lastPulseUtc = now;
        }

        switch (phase)
        {
            case Core.AlchitexPhase.GeneratingTextures:
                StepBusyWork();
                break;

            case Core.AlchitexPhase.StrippingPbr:
                StepErasure();
                break;

            case Core.AlchitexPhase.RemovingPack:
                PlayImplosion();
                break;

            // Copying the pack. Reports once and then goes quiet for as long as the copy
            // takes, so this is a timer rather than a per-pulse behaviour - see
            // BeginPhaseLoop. A repeating ripple, each one rolling its own field for
            // variety, is the shape that means "still going, something is happening to the
            // whole pack at once", which is exactly what staging is.
            case Core.AlchitexPhase.Staging:
                BeginPhaseLoop(phase, () => PlayRipple(brighten: _random.NextDouble() < 0.5), RippleDurationMs);
                break;

            // The orchestrator taking the measure of the whole pack in one pass. Also
            // reports once and then goes quiet for as long as the pass takes, so this gets
            // a timer too - the same travelling gradient StepBusyWork only shows off in a
            // short random burst during generation, just running for the whole phase
            // instead of a random handful of steps.
            case Core.AlchitexPhase.ScanningTextures:
                BeginPhaseLoop(phase, () => StepGradientWave(), WaveStepMs);
                break;

            // Water and glass sweep sideways, the way a pass over a surface does.
            case Core.AlchitexPhase.WaterAndGlass:
                StepGradientWave(WaveAxis.Horizontal, forward: true);
                break;

            // Fog rises.
            case Core.AlchitexPhase.Fog:
                StepGradientWave(WaveAxis.Vertical, forward: false);
                break;

            // The last two passes each walk a bright cell one step along a random path, so
            // the end of a run reads as something tracing its way out rather than another
            // sweep - these are bookkeeping over a finished pack, not work across it.
            //
            // The ripple was tried here and does not belong: post-processing is over in a
            // blink against a texture pass measured in seconds, so a half-second flourish
            // hung on it was never actually seen. It lives in StepBusyWork instead, which
            // is the only phase that runs long enough to show anything off. Anything else
            // wanting a flourish should ask the same question first - is this phase on
            // screen long enough to finish one?
            case Core.AlchitexPhase.Finalizing:
            case Core.AlchitexPhase.Bookkeeping:
                StepTrail();
                break;

            // A pack just landed. One bright wash across the grid, corner to corner.
            case Core.AlchitexPhase.Done:
                PlayCompletionWash();
                break;
        }
    }

    // ── One-shot flourishes ──────────────────────────────────────────────────
    //
    // These are the only behaviors that aren't driven by a pulse or a timer: something
    // happened, and the reactor reacts once. They all span several hundred milliseconds
    // across staggered tiles, which means they need the grid to themselves - a pulse
    // landing mid-flourish repaints all nine tiles and it never finishes. Hence the claim.

    /// <summary>
    /// How long a one-shot owns the grid. A timestamp rather than a flag, deliberately: a
    /// flag that failed to clear (an exception, a run ending mid-flourish, a module closing)
    /// would wedge the reactor into never repainting again, and this is cosmetic code
    /// sitting on top of a pipeline. The worst a stale timestamp can do is expire.
    /// </summary>
    private DateTime _gridClaimedUntilUtc = DateTime.MinValue;

    private bool IsGridClaimed => DateTime.UtcNow < _gridClaimedUntilUtc;

    private void ClaimGrid(double durationMs)
        => _gridClaimedUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);

    private void ReleaseGrid() => _gridClaimedUntilUtc = DateTime.MinValue;

    // A wash's leading edge crossing one line of the grid - a column or a row, depending
    // which way it runs - and how long that line stays lit behind it.
    private const double WashLineDelayMs = 80;
    private const double WashRiseMs = 100;
    private const double WashFallMs = 220;

    /// <summary>
    /// A pack being taken in, handed back, or thrown out: a bright band washing across the
    /// grid in the same direction the pack's own tile travels, so the two read as one event
    /// rather than two things happening at once. Right on the way in, left on the way back
    /// out, down for a failure leaving through the reactor's underside.
    ///
    /// Three lines is not much to say "something landed in water" with, so the band is built
    /// from the active palette rather than a single bright frame: each line rises to its
    /// brightest shade, falls back through the middle of the ramp, and settles at its
    /// resting value, one delay behind the line before it. What sells it is that the
    /// trailing lines are still falling while the leading one has already settled.
    ///
    /// Both axes are three cells long, so the timing constants and the claim are the same
    /// whichever way it runs - the direction only decides which index a cell reads its
    /// place in the queue from. That is the whole reason this is one method: a separate
    /// vertical copy would be the same sequence with one subscript changed, and the two
    /// would drift the first time the band was retuned.
    /// </summary>
    public void PlayQueueWash(ReactorWashDirection direction)
    {
        if (!_isInitialized) return;
        EnterStance(Stance.Flourish);

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var rank = direction switch
                {
                    ReactorWashDirection.LeftToRight => col,
                    ReactorWashDirection.RightToLeft => GridSize - 1 - col,
                    ReactorWashDirection.TopToBottom => row,
                    _ => col,
                };

                var lead = rank * WashLineDelayMs;
                var rest = _activePalette[RestLayout[row, col]];

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (_activePalette[0], lead + WashRiseMs),
                    (_activePalette[2], lead + WashRiseMs + WashFallMs * 0.45),
                    (rest, lead + WashRiseMs + WashFallMs));
            }
        }

        ClaimGrid((GridSize - 1) * WashLineDelayMs + WashRiseMs + WashFallMs);
    }

    private const double RippleRingDelayMs = 75;
    private const double RippleRiseMs = 110;
    private const double RippleFallMs = 230;

    // How long one ripple takes corner to corner, start to settled - shared with
    // BeginPhaseLoop, which uses it as the repeat interval to keep Staging's ripples chained
    // back-to-back rather than cutting each other off mid-sequence.
    private const double RippleDurationMs = (GridSize - 1) * RippleRingDelayMs + RippleRiseMs + RippleFallMs;

    /// <summary>
    /// Which uniform field a ripple plays against, rerolled every call so a run of them (a
    /// held press, a Staging phase) doesn't look identical throughout. All three guarantee a
    /// full-range swing on every cell - the old design settled every ring back to the
    /// diagonal resting layout, so whichever corner already sat close to the peak barely
    /// moved and the wave only ever "manifested" toward the opposite corner.
    /// </summary>
    private enum RippleField
    {
        /// <summary>Darkest field; every ring rises to the brightest blue.</summary>
        Dark,
        /// <summary>Brightest field; every ring falls to the darkest blue.</summary>
        Light,
        /// <summary>Middle field; rings alternate rising and falling, so it reads as a
        /// pulse rather than a paler version of the other two.</summary>
        Middle,
    }

    // True middle of the five-step ramp - the natural field for a ripple that wants room to
    // swing both brighter and darker from the same base.
    private const int MiddlePaletteIndex = 2;

    /// <summary>
    /// A field's settle colour, one shade apart between the centre/corner rings and the edge
    /// ring rather than a single flat value everywhere. Deliberate: with every ring settling
    /// to the exact same colour, a repeating ripple (Staging) or a held press spends part of
    /// every cycle with all nine tiles pinned to one flat card - a real, visible stall, not
    /// just a momentary blend, once ring 2 finishes catching up to rings 0 and 1 and the
    /// whole grid sits there until the next ripple's rise begins. A one-shade wobble between
    /// rings keeps the grid always reading as tiles, at essentially no cost to the contrast
    /// against the peak the redesign above exists for.
    /// </summary>
    private static int RippleBackdropIndex(RippleField field, int ring) => field switch
    {
        RippleField.Dark => ring == 1 ? Palette.Length - 2 : Palette.Length - 1,
        RippleField.Light => ring == 1 ? 1 : 0,
        // The Middle field already alternates its peak brighter/darker by ring parity: the
        // settle colour follows the same parity, just pulled back toward the centre, so the
        // "quiet" moments carry the same pattern as the "active" ones instead of collapsing
        // to a single shade between them.
        _ => ring % 2 == 0 ? MiddlePaletteIndex - 1 : MiddlePaletteIndex + 1,
    };

    // How often a ripple rolls the Middle field regardless of what the caller asked for -
    // often enough to notice, rare enough that "brighten"/"darken" still mean something most
    // of the time.
    private const double RippleMiddleFieldChance = 0.3;

    /// <summary>
    /// A drop landing in the middle: the centre moves first, then the four edge tiles, then
    /// the corners, each ring one delay behind the last.
    ///
    /// Rings are Manhattan distance from the centre, not Chebyshev, and that is the whole
    /// reason this reads as a ripple - Chebyshev gives a 3x3 grid only two rings, which is
    /// a blink. Manhattan gives three, which is a wave. (PlayImplosion uses Chebyshev on
    /// purpose, because a collapse inward wants to arrive all at once.)
    ///
    /// Plays against a RippleField rather than the diagonal resting layout, so the wave has
    /// room to swing its full range on every cell rather than dying quietly into whichever
    /// corner already sat close to the peak. `brighten` still means "arrived" vs "consumed"
    /// most of the time (Dark field with a bright peak, or Light field with a dark peak) -
    /// the Middle field is rolled in regardless of it now and then, alternating brighter and
    /// darker ring to ring, purely for variety. The field's settle colour also varies one
    /// shade by ring (RippleBackdropIndex) rather than being perfectly flat - see its own
    /// remarks for why a flat settle colour is a real visible stall on a repeating ripple,
    /// not just a blend.
    /// </summary>
    public void PlayRipple(bool brighten)
    {
        if (!_isInitialized) return;
        EnterStance(Stance.Flourish);

        var field = _random.NextDouble() < RippleMiddleFieldChance
            ? RippleField.Middle
            : brighten ? RippleField.Dark : RippleField.Light;

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var ring = Math.Abs(row - 1) + Math.Abs(col - 1); // 0, 1 or 2
                var lead = ring * RippleRingDelayMs;

                var peak = field switch
                {
                    RippleField.Dark => _activePalette[0],
                    RippleField.Light => _activePalette[Palette.Length - 1],
                    _ => ring % 2 == 0 ? _activePalette[0] : _activePalette[Palette.Length - 1],
                };
                var backdrop = _activePalette[RippleBackdropIndex(field, ring)];

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (peak, lead + RippleRiseMs),
                    (backdrop, lead + RippleRiseMs + RippleFallMs));
            }
        }

        ClaimGrid(RippleDurationMs);
    }

    // ── Behaviors ────────────────────────────────────────────────────────────

    /// <summary>The four axes a gradient can travel along. Ranks are scaled so every axis
    /// spans the whole palette across the grid, whether it crosses three tiles or five.</summary>
    private enum WaveAxis
    {
        /// <summary>Left to right.</summary>
        Horizontal,
        /// <summary>Top to bottom.</summary>
        Vertical,
        /// <summary>Top-left to bottom-right.</summary>
        Diagonal,
        /// <summary>Top-right to bottom-left - the axis the resting arrangement itself sits
        /// on, so a wave along it is literally the logo's own gradient set moving.</summary>
        AntiDiagonal,
    }

    private WaveAxis _waveAxis = WaveAxis.AntiDiagonal;
    private bool _waveForward = true;
    private int _wavePhase;

    // Pulses left in the current sweep. Zero means back to firing at random.
    private int _waveBurstRemaining;

    // In pulses rather than seconds, since the pulse rate is the pipeline's own throughput.
    // Works out to a sweep every couple of seconds, lasting about one.
    private const double WaveBurstChance = 0.04;
    private const int MinWaveBurstSteps = 8;
    private const int MaxWaveBurstSteps = 15;

    // Rarer than a sweep, but not by much - this is the reactor's only home for the ripple
    // now, and at a pulse every 70ms a chance this size works out to roughly one every few
    // seconds of texture work. Seldom enough to still read as an event, often enough that a
    // run actually shows it off.
    private const double RippleChance = 0.02;

    /// <summary>
    /// Textures being written: mostly every tile firing at once, with a sweep cutting
    /// through every couple of seconds and, now and then, a drop rippling out of the centre.
    ///
    /// The mix is deliberate. Pure noise reads as static and stops meaning anything after a
    /// second of watching, and a pure gradient is far too orderly for a phase that means
    /// "flat out, in no particular order".
    /// </summary>
    private void StepBusyWork()
    {
        if (_waveBurstRemaining > 0)
        {
            _waveBurstRemaining--;
            StepGradientWave();
            return;
        }

        EnterStance(Stance.Busy);

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, _random.Next(Palette.Length), 55);

        // Checked before the sweep so the two can't fire on the same pulse - the ripple
        // claims the grid and the sweep's first step would be thrown away.
        if (_random.NextDouble() < RippleChance)
        {
            PlayRipple(brighten: _random.NextDouble() < 0.5);
            return;
        }

        if (_random.NextDouble() >= WaveBurstChance) return;

        // Phase 0 so the sweep leaves the resting arrangement rather than cutting in
        // halfway through a ramp.
        RerollWave();
        _wavePhase = 0;
        _waveBurstRemaining = _random.Next(MinWaveBurstSteps, MaxWaveBurstSteps + 1);
    }

    /// <summary>
    /// Advances a gradient one step along its axis. Every tile is repainted every step, but
    /// from a single ramp rather than independently, so what moves is the arrangement -
    /// which is why this reads as the mark itself flowing instead of nine tiles flickering.
    ///
    /// Driven by Pulse, not by a timer: the wave's speed is the pipeline's actual
    /// throughput, so a pack full of tiny textures visibly races and a slow one crawls.
    ///
    /// A caller that names an axis owns it for as long as its phase lasts (see Pulse's phase
    /// map). Called with no axis, the wave re-rolls its own direction at the end of each
    /// cycle - that's the "keeps finding new ways to move" behavior, and it belongs only to
    /// the phase that runs long enough to need it.
    /// </summary>
    private void StepGradientWave(WaveAxis? axis = null, bool? forward = null)
    {
        if (axis.HasValue) _waveAxis = axis.Value;
        if (forward.HasValue) _waveForward = forward.Value;

        _wavePhase++;

        if (_wavePhase >= WavePeriod)
        {
            _wavePhase = 0;

            // Only a wave that wasn't given a direction gets to pick a new one.
            if (!axis.HasValue) RerollWave();
        }

        EnterStance(Stance.Wave, (int)_waveAxis * 2 + (_waveForward ? 1 : 0));

        var offset = _waveForward ? _wavePhase : -_wavePhase;

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                AnimateTile(row, col, RampIndex(WaveRank(_waveAxis, row, col) + offset), WaveStepMs);
    }

    private void RerollWave()
    {
        _waveAxis = (WaveAxis)_random.Next(4);
        _waveForward = _random.NextDouble() < 0.5;
    }

    /// <summary>Where a tile sits along an axis, on the palette's own 0..4 scale. The
    /// anti-diagonal case is exactly RestLayout, which is the whole trick: at phase 0 an
    /// anti-diagonal wave IS the resting arrangement.</summary>
    private static int WaveRank(WaveAxis axis, int row, int col) => axis switch
    {
        WaveAxis.Horizontal => col * 2,
        WaveAxis.Vertical => row * 2,
        WaveAxis.Diagonal => row + col,
        _ => row + (GridSize - 1 - col),
    };

    /// <summary>Walks the palette out and back again rather than wrapping it, so the cycle
    /// has no seam where the darkest tile snaps back to the brightest.</summary>
    private static int RampIndex(int position)
    {
        var wrapped = ((position % WavePeriod) + WavePeriod) % WavePeriod;

        return wrapped <= Palette.Length - 1 ? wrapped : WavePeriod - wrapped;
    }

    // Where the erasure head currently is, as a flat 0..8 index in row-major order.
    private int _erasureHead = -1;

    /// <summary>
    /// Stripping a pack's existing PBR: a wipe head travels the grid in reading order,
    /// snapping the tile under it to black-dark instantly while the tiles behind it bleed
    /// back up toward their resting values. What it should look like is content being
    /// cleared off a surface, one cell at a time, with the cleared trail slowly recovering
    /// - not a random flicker, because this phase isn't random work, it's a sweep.
    /// </summary>
    private void StepErasure()
    {
        EnterStance(Stance.Erasure);

        _erasureHead = (_erasureHead + 1) % (GridSize * GridSize);

        var head = _erasureHead;
        var headRow = head / GridSize;
        var headCol = head % GridSize;

        // The head itself: hard cut to the darkest value, no easing in - erasure is not
        // a fade, it's a removal.
        AnimateTile(headRow, headCol, Palette.Length - 1, 0);

        // Two cells behind it, recovering at different rates so the trail has depth.
        var behind1 = (head - 1 + GridSize * GridSize) % (GridSize * GridSize);
        var behind2 = (head - 2 + GridSize * GridSize) % (GridSize * GridSize);

        AnimateTile(behind1 / GridSize, behind1 % GridSize, Palette.Length - 2, 130);
        AnimateTile(behind2 / GridSize, behind2 % GridSize, RestLayout[behind2 / GridSize, behind2 % GridSize], 250);
    }

    /// <summary>
    /// Uninstalling the original pack: everything flashes bright for an instant, then
    /// collapses to black from the outside in - the corners first, the middle last, each
    /// ring delayed behind the one outside it. An implosion, because that's what deleting
    /// the thing you started from feels like it should look like.
    /// </summary>
    private void PlayImplosion()
    {
        const double flashMs = 80;
        const double holdMs = 100;
        const double ringDelayMs = 180;
        const double collapseMs = 220;

        EnterStance(Stance.Flourish);

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                // Chebyshev distance from the centre: 1 for the ring, 0 for the middle.
                // Chebyshev rather than the ripple's Manhattan on purpose - a collapse
                // inward wants the whole outer ring to go together, not corner by corner.
                var ring = Math.Max(Math.Abs(row - 1), Math.Abs(col - 1));
                var collapseAt = flashMs + holdMs + (1 - ring) * ringDelayMs;

                // Flash, hold, then collapse - outer ring first, centre last. One sequence
                // rather than two overlapping animations: the second would now cancel the
                // first, since a tile only ever runs one storyboard (see PlayTileSequence).
                PlayTileSequence(row, col,
                    (_activePalette[0], flashMs),
                    (_activePalette[0], collapseAt),
                    (_activePalette[Palette.Length - 1], collapseAt + collapseMs));
            }
        }

        ClaimGrid(flashMs + holdMs + ringDelayMs + collapseMs);
    }

    /// <summary>
    /// A pack finished: one bright wash sweeping corner to corner, each tile lit on a
    /// delay proportional to how far along the diagonal it sits, then released back to
    /// rest behind the wash.
    /// </summary>
    private void PlayCompletionWash()
    {
        const double diagonalDelayMs = 55;
        const double riseMs = 100;
        const double fallMs = 260;

        EnterStance(Stance.Flourish);

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                var lead = (row + col) * diagonalDelayMs; // 0..4 steps, corner to corner

                PlayTileSequence(row, col,
                    (_brushes[row, col].Color, Math.Max(lead, 1)),
                    (_activePalette[0], lead + riseMs),
                    (_activePalette[RestLayout[row, col]], lead + riseMs + fallMs));
            }
        }

        ClaimGrid((GridSize - 1) * 2 * diagonalDelayMs + riseMs + fallMs);
    }

    // Where the bright cell currently sits, so consecutive trail steps move rather than
    // teleport. -1 means "no trail yet, start anywhere".
    private int _trailRow = -1;
    private int _trailCol = -1;

    /// <summary>
    /// Moves a single bright cell one step to a neighbouring tile, dimming the one it
    /// leaves back toward that tile's resting value. Repeated calls draw a wandering
    /// bright trail across the grid.
    /// </summary>
    private void StepTrail()
    {
        EnterStance(Stance.Trail);

        if (_trailRow < 0)
        {
            _trailRow = _random.Next(GridSize);
            _trailCol = _random.Next(GridSize);
        }
        else
        {
            AnimateTile(_trailRow, _trailCol, RestLayout[_trailRow, _trailCol], 160);

            // A step in one axis, staying on the grid.
            if (_random.NextDouble() < 0.5)
                _trailRow = Math.Clamp(_trailRow + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
            else
                _trailCol = Math.Clamp(_trailCol + (_random.NextDouble() < 0.5 ? -1 : 1), 0, GridSize - 1);
        }

        AnimateTile(_trailRow, _trailCol, 0, 100);
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a palette index against whichever palette is currently active (see
    /// _activePalette) rather than always Palette - this indirection is the whole
    /// "decoupled from colour" idea: every caller still just says "index 0" or "RestLayout
    /// at this cell", and it is this one method that decides whether that means blue or red.
    /// </summary>
    private void AnimateTile(int row, int col, int paletteIndex, double durationMs)
        => SetTileColor(row, col, _activePalette[Math.Clamp(paletteIndex, 0, _activePalette.Length - 1)], durationMs);

    /// <summary>
    /// The one place a tile's color ever changes. Takes a Color rather than a palette index -
    /// AnimateTile is the index-based front door for everything except the handful of
    /// flourishes (PlayRipple, PlayQueueWash, PlayImplosion, PlayCompletionWash) that need a
    /// Color they've already resolved themselves.
    /// </summary>
    private void SetTileColor(int row, int col, Color target, double durationMs)
    {
        if (AnimationsSuspended || durationMs <= 0)
        {
            StopTile(row, col);
            _brushes[row, col].Color = target;
            return;
        }

        // A deliberate cut (duration 0, the erasure head) stays a cut; anything else eases at
        // least over what remains of a stance change - see StanceBlendMs.
        PlayTileSequence(row, col, (target, Math.Max(durationMs, StanceBlendRemainingMs)));
    }

    // Staggering is expressed as key frames on one sequence rather than as a delay on a
    // transition - a tile runs exactly one storyboard, so two overlapping calls would cancel
    // rather than compose. PlayImplosion is the worked example.

    /// <summary>
    /// Runs one tile through a series of colours at absolute times, on that tile's own
    /// reused storyboard. Every colour change in the class ends up here, including the
    /// single-step case - so a flourish that needs three stages costs exactly what a plain
    /// transition costs, and a later call always replaces an earlier one rather than
    /// stacking on top of it.
    ///
    /// A brush's Color is a dependent animation, so this runs on the UI thread rather than
    /// the compositor. Fine at this scale (nine brushes, throttled); the honest alternative
    /// of five stacked opacity layers per tile buys nothing here.
    /// </summary>
    private void PlayTileSequence(int row, int col, params (Color Color, double AtMs)[] frames)
    {
        if (frames.Length == 0) return;

        var brush = _brushes[row, col];

        if (AnimationsSuspended)
        {
            StopTile(row, col);
            brush.Color = frames[^1].Color;
            return;
        }

        // Retire the tile's previous storyboard before starting another. This is the whole
        // fix: a fresh Storyboard per call is fine and always was - what wasn't fine is
        // never stopping the last one, so every brush accumulated live animations for the
        // length of a run. Building a new one rather than re-arming a kept one is
        // deliberate: re-arming means mutating a Storyboard's key frames after it has been
        // begun, which is not something WinUI promises to allow, and this is cosmetic code
        // that must never be the reason a run falls over.
        StopTile(row, col);

        var animation = new ColorAnimationUsingKeyFrames { EnableDependentAnimation = true };

        foreach (var (color, atMs) in frames)
        {
            animation.KeyFrames.Add(new EasingColorKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(Math.Max(atMs, 1)),
                Value = color,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        _tileStoryboards[row, col] = storyboard;
        storyboard.Begin();
    }

    /// <summary>Takes a tile off its storyboard without letting go of the colour it is
    /// currently showing - see PlayTileSequence for why that isn't automatic.</summary>
    private void StopTile(int row, int col)
    {
        var storyboard = _tileStoryboards[row, col];
        if (storyboard == null) return;

        // Stop() reverts the brush to the value it held before that storyboard started, so
        // whatever it is showing right now has to be written back as its base first -
        // otherwise every restart snaps to where the previous sequence began, which on a
        // grid repainted fourteen times a second is a permanent flicker.
        var current = _brushes[row, col].Color;
        storyboard.Stop();
        _brushes[row, col].Color = current;

        _tileStoryboards[row, col] = null;
    }

    // The bloom's one-shot storyboard, reused for the same reason the tiles' are - press
    // and release the button a few times and the naive version leaves one running per
    // press. Separate from _bloomLoop, which is the long-lived breathing.
    private Storyboard? _bloomShot;

    private void SetBloom(double opacity, double durationMs)
    {
        if (_bloom == null) return;

        if (AnimationsSuspended || durationMs <= 0)
        {
            _bloomShot?.Stop();
            _bloom.Opacity = opacity;
            return;
        }

        if (_bloomShot != null)
        {
            var current = _bloom.Opacity;
            _bloomShot.Stop();
            _bloom.Opacity = current;
        }

        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        Storyboard.SetTarget(animation, _bloom);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _bloomShot = new Storyboard();
        _bloomShot.Children.Add(animation);
        _bloomShot.Begin();
    }

    // The breath, as (millisecond, opacity) pairs. Uneven on purpose: two of the three
    // peaks go all the way to 1.0 and the troughs never fully collapse, so the reactor reads
    // as something with a lot of light in it being modulated - not something being switched
    // on and off. An even sine would loop invisibly; this one you notice.
    private static readonly (double AtMs, double Opacity)[] BloomBreath =
    {
        (0, 0.35),
        (520, 1.00),
        (1040, 0.50),
        (1520, 0.88),
        (2000, 0.40),
        (2420, 1.00),
        (2900, 0.35),
    };

    /// <summary>The bloom breathing under everything else for as long as a run - or a wait -
    /// lasts. Calling it while it's already running restarts it, which is why every caller
    /// checks that it isn't.</summary>
    private void StartBloomLoop()
    {
        if (_bloom == null || AnimationsSuspended) return;
        if (_bloomLoop != null) return;

        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };

        foreach (var (atMs, opacity) in BloomBreath)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(atMs),
                Value = opacity,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        Storyboard.SetTarget(animation, _bloom);
        Storyboard.SetTargetProperty(animation, "Opacity");

        _bloomLoop = new Storyboard();
        _bloomLoop.Children.Add(animation);
        _bloomLoop.Begin();
    }

    private void StopBloomLoop()
    {
        if (_bloomLoop == null) return;

        _bloomLoop.Stop();
        _bloomLoop = null;
    }

    /// <summary>Stops every loop this animator owns. Call on close so a timer or a
    /// forever-storyboard can't outlive the overlay it was animating.</summary>
    public void Shutdown()
    {
        StopOrbit();
        StopAbortHint();
        StopFlowerDance();
        StopBloomLoop();
        StopErrorFlashRevertTimer();

        _bloomShot?.Stop();

        for (var row = 0; row < GridSize; row++)
            for (var col = 0; col < GridSize; col++)
                _tileStoryboards[row, col]?.Stop();
    }
}
