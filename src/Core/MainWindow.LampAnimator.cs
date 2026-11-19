using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Unified lamp animation system for both titlebar and splash screen contexts.
///
/// <para><b>Reversed</b> (<see cref="SetReversedAsync"/>) is the titlebar lamp's Preview mode:
/// it rests dark instead of lit, and every pattern runs from that rest - a flash that would dip
/// it lights it instead, a burst pops away from dark and back, the continuous blink settles dark,
/// and the halo's spin favours the other direction. A super flash stays the brightest thing the
/// lamp does either way, so "something arrived" still reads as arrival. Every path ends in the
/// rest state through <see cref="RestOverlayOpacity"/> and <see cref="RestHaloOpacity"/>, so a
/// new path only has to end there too.</para>
/// </summary>
public class LampAnimator
{
    private readonly LampContext _context;
    private readonly Image _baseImage;
    private readonly Image? _overlayImage;
    private readonly Image? _haloImage;
    private readonly Image? _superImage;

    private readonly string? _onPath;
    private readonly string? _offPath;
    private readonly string? _superPath;
    private readonly string? _haloPath;

    // Default opacity values matching XAML
    private readonly double _defaultHaloOpacity;
    private readonly double _defaultBaseOpacity = 1.0;
    private readonly double _defaultOverlayOpacity = 0.0;
    private readonly double _defaultSuperOpacity = 0.0;

    /// <summary>The halo's opacity while the lamp is dark.</summary>
    private const double DimHaloOpacity = 0.025;

    private bool _reversed;

    /// <summary>Overlay (the off image over the on one) at rest: hidden when lit, shown when reversed.</summary>
    private double RestOverlayOpacity => _reversed ? 1.0 : _defaultOverlayOpacity;
    private double RestHaloOpacity => _reversed ? DimHaloOpacity : _defaultHaloOpacity;

    // The state a dimming flash moves to, which for a reversed lamp is lit.
    private double AwayOverlayOpacity => _reversed ? _defaultOverlayOpacity : 1.0;
    private double AwayHaloOpacity => _reversed ? _defaultHaloOpacity : DimHaloOpacity;

    private CancellationTokenSource? _blinkCts;
    private Task? _animationTask;
    private readonly SemaphoreSlim _animationLock = new(1, 1);
    private bool _isInitialized;
    private bool _isBusy;

    // --- Halo spin physics state ---

    // Angle in degrees; never reset — the wheel just keeps turning wherever it is.
    private double _haloAngle = 0.0;
    // Degrees per second. Positive = clockwise.
    private double _haloVelocity = 0.0;
    private double _superHaloOpacity = 0.0;
    // Background spin loop, shared across all callers while the lamp lives.
    private CancellationTokenSource? _spinCts;
    private Task? _spinTask;
    private readonly SemaphoreSlim _spinLock = new(1, 1);

    // Physics constants
    // Physics constants
    private const double SpinFriction = 0.96;          // velocity multiplied each tick (< 1 = friction)
    private const double SpinTickMs = 8.0;             // Control fps, 16 = 60 fps roughly
    private const double SpinStopThreshold = 0.05;      // deg/s below which we consider it stopped
    private const double SingleFlashImpulse = 240.0;    // deg/s added by a single flash
    private const double BlinkImpulseMin = 60.0;        // minimum impulse per blink cycle
    private const double BlinkImpulseMax = 720.0;       // maximum impulse per blink cycle
    private const double SuperFlashImpulse = 360.0;     // deg/s added by a super flash


    public enum LampContext
    {
        Titlebar,
        Splash
    }

    /// <summary>
    /// Overload that accepts explicit asset paths, bypassing context-based path resolution.
    /// Use this for module-specific lamps that have their own asset sets.
    /// </summary>
    public LampAnimator(
        LampContext context,
        Image baseImage,
        string? onPath = null,
        string? offPath = null,
        string? superPath = null,
        string? haloPath = null,
        Image? overlayImage = null,
        Image? haloImage = null,
        Image? superImage = null)
    {
        _context = context;
        _baseImage = baseImage ?? throw new ArgumentNullException(nameof(baseImage));
        _overlayImage = overlayImage;
        _haloImage = haloImage;
        _superImage = superImage;

        // Halo Opacity in context of splash vs anything else, off is hardcoded to 0.0
        _defaultHaloOpacity = _context == LampContext.Splash ? 0.195 : 0.295;
        _superHaloOpacity = _context == LampContext.Splash ? 0.65 : 0.7;

        // If explicit paths were provided, use them directly.
        // Otherwise fall back to the original context-based resolution.
        if (onPath != null || offPath != null || superPath != null || haloPath != null)
        {
            _onPath = onPath;
            _offPath = offPath;
            _superPath = superPath;
            _haloPath = haloPath;
        }
        else
        {
            var paths = ResolveImagePaths();
            _onPath = paths.on;
            _offPath = paths.off;
            _superPath = paths.super;
            _haloPath = paths.halo;
        }
    }

    /// <summary>
    /// Initializes the animator by preloading images and setting special occasion textures.
    /// Call this immediately after construction to avoid delays on first animation.
    /// Under the animation lock like everything else that initialises: run beside
    /// <see cref="SetReversedAsync"/> without it, its last step - putting every opacity back to the
    /// XAML defaults - can land after the reversed rest was applied and undo it.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        await _animationLock.WaitAsync();
        try
        {
            await EnsureInitialized();
        }
        finally
        {
            _animationLock.Release();
        }
    }

    /// <summary>
    /// Switches between resting lit and resting dark. Waits for a single flash in progress, and
    /// leaves a running continuous blink alone - it settles into whichever rest is current when it
    /// stops. <paramref name="animate"/> false snaps, for <c>SuspendUIAnimations</c>.
    /// </summary>
    public async Task SetReversedAsync(bool reversed, bool animate)
    {
        await _animationLock.WaitAsync();
        try
        {
            await EnsureInitialized();
            if (_reversed == reversed) return;
            _reversed = reversed;
            if (_isBusy || _overlayImage == null) return;

            // Slower than a flash: this is the lamp changing what it is, not reporting an event.
            const double settleMs = 350;
            if (!animate)
            {
                _overlayImage.Opacity = RestOverlayOpacity;
                if (_haloImage != null) _haloImage.Opacity = RestHaloOpacity;
                return;
            }
            await Task.WhenAll(
                AnimateOpacity(_overlayImage, RestOverlayOpacity, settleMs),
                _haloImage != null ? AnimateOpacity(_haloImage, RestHaloOpacity, settleMs) : Task.CompletedTask);
        }
        finally
        {
            _animationLock.Release();
        }
    }

    /// <summary>
    /// Main animation method - handles blinking, single flashes, steady states, and timed animations.
    /// </summary>
    /// <param name="rotate">
    /// When true, the halo layer spins like a loose wheel.
    /// Each flash or blink event delivers an angular impulse that accelerates it;
    /// friction then naturally decelerates it on its own — no forced reset.
    /// </param>
    public async Task Animate(bool enable, bool singleFlash = false, double singleFlashOnChance = 0.75, double? duration = null, bool rotate = false, double rapidFlashChance = 0.0)
    {
        const double initialDelayMs = 900;
        const double minDelayMs = 150;
        const double minRampSec = 1;
        const double maxRampSec = 8;

        double fadeAnimationMs = _context == LampContext.Splash ? 100 : 75;

        if (singleFlash)
        {
            if (!await _animationLock.WaitAsync(0)) // 0ms timeout = don't wait at all
                return;
        }
        else
        {
            await _animationLock.WaitAsync();
        }
        try
        {
            var random = Random.Shared;
            await EnsureInitialized();

            // Single flashes are IGNORED if lamp is busy
            if (singleFlash && _isBusy)
                return;

            // If animation is running, only allow disable command
            if (_animationTask != null && !_animationTask.IsCompleted)
            {
                if (!enable)
                {
                    _blinkCts?.Cancel();
                    try { await _animationTask; }
                    catch (OperationCanceledException) { }

                    _animationTask = null;
                    _blinkCts = null;
                }
                else
                {
                    return; // Already animating
                }
                return;
            }

            // Timed animation (e.g., splash screen)
            if (duration.HasValue)
            {
                _isBusy = true;
                try
                {
                    if (rotate) EnsureSpinLoopRunning();
                    await ExecuteTimedAnimation(duration.Value, random, singleFlashOnChance, fadeAnimationMs, rotate);
                }
                finally
                {
                    _isBusy = false;
                }
                return;
            }

            // Single flash
            if (singleFlash)
            {
                _isBusy = true;
                try
                {
                    if (rotate) EnsureSpinLoopRunning();
                    await ExecuteSingleFlash(random, singleFlashOnChance, fadeAnimationMs, rotate: rotate, rapidFlashChance: rapidFlashChance);
                }
                finally
                {
                    _isBusy = false;
                }
                return;
            }

            // Continuous blinking
            if (enable)
            {
                _blinkCts = new CancellationTokenSource();
                _isBusy = true;
                if (rotate) EnsureSpinLoopRunning();
                _animationTask = BlinkLoop(_blinkCts.Token, fadeAnimationMs, initialDelayMs, minDelayMs, minRampSec, maxRampSec, random, rotate);
            }
            else
            {
                if (!_isBusy)
                    return; // Already stopped

                await SetupRestState(fadeAnimationMs);
            }
        }
        finally
        {
            _animationLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Halo spin physics
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the background spin loop if it isn't already running.
    /// The loop persists until velocity drops to near zero — it is self-terminating.
    /// Calling this again while it is already running is a no-op.
    /// </summary>
    private void EnsureSpinLoopRunning()
    {
        if (_haloImage == null) return;

        _spinLock.Wait();
        try
        {
            if (_spinTask != null && !_spinTask.IsCompleted)
                return; // already spinning

            _spinCts = new CancellationTokenSource();
            _spinTask = RunSpinLoop(_spinCts.Token);
        }
        finally
        {
            _spinLock.Release();
        }
    }

    /// <summary>
    /// Adds an angular impulse to the halo (degrees per second).
    /// Always clockwise (positive). Fires-and-forgets; the loop picks it up.
    /// </summary>
    private void ApplySpinImpulse(double impulseMagnitude)
    {
        if (_haloImage == null) return;

        // Random chance of clockwise (+) or counter-clockwise (-); a reversed lamp favours the other way.
        double sign = Random.Shared.NextDouble() < 0.67 ? 1.0 : -1.0;
        if (_reversed) sign = -sign;

        _haloVelocity += sign * impulseMagnitude;

        EnsureSpinLoopRunning(); // wake the loop if needed
    }

    /// <summary>
    /// Background loop that advances the halo rotation each tick using simple
    /// Euler integration + multiplicative friction. Self-terminates when still.
    /// </summary>
    private async Task RunSpinLoop(CancellationToken ct)
    {
        if (_haloImage == null) return;

        // Ensure the halo has a RenderTransform we can drive.
        // We add it here so XAML doesn't need to declare it.
        EnsureHaloRotateTransform();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                double dt = SpinTickMs / 1000.0; // seconds per tick

                _haloAngle += _haloVelocity * dt;
                _haloVelocity *= SpinFriction;

                // Keep angle in [0, 360) to avoid float drift over long sessions
                _haloAngle = ((_haloAngle % 360) + 360) % 360;

                ApplyHaloRotation(_haloAngle);

                if (Math.Abs(_haloVelocity) < SpinStopThreshold)
                {
                    _haloVelocity = 0.0;
                    break; // wheel has settled — let the task finish
                }

                await Task.Delay((int)SpinTickMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Applies the rotation angle to the halo image's RenderTransform on the UI thread.
    /// </summary>
    private void ApplyHaloRotation(double angleDegrees)
    {
        if (_haloImage?.RenderTransform is RotateTransform rt)
        {
            rt.Angle = angleDegrees;
        }
    }

    /// <summary>
    /// Ensures the halo Image has a RotateTransform centered on itself.
    /// Safe to call multiple times.
    /// </summary>
    private void EnsureHaloRotateTransform()
    {
        if (_haloImage == null) return;
        if (_haloImage.RenderTransform is RotateTransform) return;

        _haloImage.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _haloImage.RenderTransform = new RotateTransform { Angle = _haloAngle };
    }

    // -------------------------------------------------------------------------
    // Existing private helpers (unchanged except spin impulse injections)
    // -------------------------------------------------------------------------

    private (string? on, string? off, string? super, string? halo) ResolveImagePaths()
    {
        string? specialName = Modules.Helpers.GetSpecialOccasionName();

        if (!string.IsNullOrEmpty(specialName))
        {
            string baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "special");
            return (
                Path.Combine(baseDir, $"{specialName}.on.png"),
                Path.Combine(baseDir, $"{specialName}.off.png"),
                Path.Combine(baseDir, $"{specialName}.super.png"),
                Path.Combine(baseDir, $"{specialName}.halo.png")
            );
        }

        if (_context == LampContext.Splash)
        {
            return (
                "ms-appx:///Assets/icons/SplashScreen.Regular.png",
                "ms-appx:///Assets/icons/SplashScreen.Off.png",
                "ms-appx:///Assets/icons/SplashScreen.Super.png",
                "ms-appx:///Assets/icons/SplashScreen.Halo.png"
            );
        }

        return (
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.on.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.off.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.super.small.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vrtx.lamp.halo.png")
        );
    }

    /// <summary>
    /// Ensures all images are preloaded and ready for instant swapping.
    /// XAML defaults are preserved - only overridden for special occasions.
    /// </summary>
    private async Task EnsureInitialized()
    {
        if (_isInitialized)
            return;

        string? specialName = Modules.Helpers.GetSpecialOccasionName();

        // Always preload images for instant access during animations
        var loadTasks = new List<Task>();

        if (!string.IsNullOrEmpty(specialName))
        {
            // Special occasion - preload AND set images
            loadTasks.Add(GetCachedImageAsync(_onPath));
            loadTasks.Add(GetCachedImageAsync(_offPath));
            loadTasks.Add(GetCachedImageAsync(_superPath));
            loadTasks.Add(GetCachedImageAsync(_haloPath));

            await Task.WhenAll(loadTasks);

            // Override XAML with special textures
            await SetImageAsync(_baseImage, _onPath);
            if (_haloImage != null)
                await SetImageAsync(_haloImage, _haloPath);
            if (_overlayImage != null)
                await SetImageAsync(_overlayImage, _offPath);
            if (_superImage != null)
                await SetImageAsync(_superImage, _context == LampContext.Splash ? _offPath : _superPath);
        }
        else if (_context == LampContext.Titlebar)
        {
            // Titlebar on normal days - preload for instant texture swapping
            // (Splash uses ms-appx which loads instantly, no preload needed)
            loadTasks.Add(GetCachedImageAsync(_onPath));
            loadTasks.Add(GetCachedImageAsync(_offPath));
            loadTasks.Add(GetCachedImageAsync(_superPath));
            loadTasks.Add(GetCachedImageAsync(_haloPath));

            await Task.WhenAll(loadTasks);
        }

        // Ensure opacity defaults match XAML
        _baseImage.Opacity = _defaultBaseOpacity;
        if (_overlayImage != null)
            _overlayImage.Opacity = _defaultOverlayOpacity;
        if (_superImage != null)
            _superImage.Opacity = _defaultSuperOpacity;
        if (_haloImage != null)
            _haloImage.Opacity = _defaultHaloOpacity;

        _isInitialized = true;
    }

    private async Task SetupRestState(double fadeMs)
    {
        await SetImageAsync(_baseImage, _onPath);
        _baseImage.Opacity = _defaultBaseOpacity;

        if (_overlayImage != null)
            _overlayImage.Opacity = RestOverlayOpacity;

        if (_superImage != null)
            _superImage.Opacity = _defaultSuperOpacity;

        if (_haloImage != null)
            await AnimateOpacity(_haloImage, RestHaloOpacity, fadeMs);
    }

    private async Task<BitmapImage?> GetCachedImageAsync(string? path)
    {
        return await SharedImageCache.GetOrLoadAsync(path);
    }

    private async Task SetImageAsync(Image? imageControl, string? path)
    {
        if (imageControl == null || string.IsNullOrEmpty(path)) return;

        var image = await GetCachedImageAsync(path);
        if (image != null)
            imageControl.Source = image;
    }

    private async Task AnimateOpacity(FrameworkElement? element, double targetOpacity, double durationMs, CancellationToken ct = default)
    {
        if (element == null) return;

        if (ct.IsCancellationRequested)
        {
            element.Opacity = targetOpacity;
            return;
        }

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);

        var tcs = new TaskCompletionSource<bool>();
        storyboard.Completed += (s, e) => tcs.SetResult(true);
        storyboard.Begin();

        await Task.WhenAny(tcs.Task, Task.Delay(-1, ct));

        if (ct.IsCancellationRequested)
        {
            storyboard.Stop();
            element.Opacity = targetOpacity;
        }
    }

    private async Task ExecuteTimedAnimation(double durationMs, Random random, double superFlashChance, double fadeMs, bool rotate = false)
    {
        const double minFlashDuration = 300;
        const double maxFlashDuration = 700;

        double availableTime = durationMs - 400;
        double flashStart = Math.Max(200, availableTime * 0.3);
        double flashDuration = Math.Clamp(availableTime * 0.4, minFlashDuration, maxFlashDuration);

        await Task.Delay((int)flashStart);
        await ExecuteSingleFlash(random, superFlashChance, fadeMs, (int)flashDuration, rotate: rotate);
        await SetupRestState(fadeMs);
    }

    private async Task ExecuteSingleFlash(Random random, double superFlashChance, double fadeMs, int? customDuration = null, bool rotate = false, double rapidFlashChance = 0.0)
    {
        int flashDuration = customDuration ?? random.Next(300, 800);

        // Roll for rapid flash first — it overrides the normal super/off decision entirely
        if (random.NextDouble() < rapidFlashChance)
        {
            await ExecuteRapidFlash(random, fadeMs, rotate);
            return;
        }

        bool doSuperFlash = random.NextDouble() < superFlashChance;

        // Every flash gives the halo a push. Super flashes hit harder.
        if (rotate)
            ApplySpinImpulse(doSuperFlash ? SuperFlashImpulse : SingleFlashImpulse);

        if (doSuperFlash)
        {
            if (_context == LampContext.Splash && _superImage != null)
            {
                await SetImageAsync(_superImage, _superPath);
                var tasks = new[] {
                    AnimateOpacity(_superImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _superHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                tasks = new[] {
                    AnimateOpacity(_superImage, _defaultSuperOpacity, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
            }
            else if (_context == LampContext.Titlebar)
            {
                await SetImageAsync(_baseImage, _superPath);
                if (_overlayImage != null)
                    _overlayImage.Opacity = 0;

                var tasks = new[] {
                    AnimateOpacity(_baseImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _superHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                await SetImageAsync(_baseImage, _onPath);
                _baseImage.Opacity = _defaultBaseOpacity;
                if (_overlayImage != null)
                    _overlayImage.Opacity = RestOverlayOpacity;
                if (_haloImage != null)
                    await AnimateOpacity(_haloImage, RestHaloOpacity, fadeMs);
            }
        }
        else
        {
            if (_context == LampContext.Splash && _superImage != null)
            {
                await SetImageAsync(_superImage, _offPath);
                var tasks = new[] {
                    AnimateOpacity(_superImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                tasks = new[] {
                    AnimateOpacity(_superImage, _defaultSuperOpacity, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, _defaultHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
            }
            else if (_context == LampContext.Titlebar && _overlayImage != null)
            {
                _baseImage.Opacity = _defaultBaseOpacity;
                _overlayImage.Opacity = RestOverlayOpacity;

                var tasks = new[] {
                    AnimateOpacity(_overlayImage, AwayOverlayOpacity, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, AwayHaloOpacity, fadeMs) : Task.CompletedTask
                };
                await Task.WhenAll(tasks);
                await Task.Delay(flashDuration);

                _overlayImage.Opacity = RestOverlayOpacity;
                _baseImage.Opacity = _defaultBaseOpacity;
                if (_haloImage != null)
                    await AnimateOpacity(_haloImage, RestHaloOpacity, fadeMs);
            }
        }
    }

    /// <summary>
    /// Fires a rapid burst of flashes (flashCount pops). Each pop randomly targets either
    /// SUPER (bright) or OFF (dark), so the burst can mix directions.
    /// Halo opacity is set instantly during pops (fades are too slow for rapid timing)
    /// and only smoothly animated on the final settle back to resting state.
    /// Used both by BlinkLoop's scheduled super-flash events and optionally by single flashes.
    /// </summary>
    private async Task ExecuteRapidFlash(Random random, double fadeMs, bool rotate = false, CancellationToken ct = default)
    {
        if (_overlayImage == null && _context == LampContext.Titlebar) return;

        var flashCount = random.Next(1, 10);
        var flashSpeed = random.Next(16, 64);

        for (int i = 0; i < flashCount; i++)
        {
            if (ct.IsCancellationRequested) break;

            bool flashToSuper = random.NextDouble() < 0.5;

            if (rotate)
                ApplySpinImpulse(SuperFlashImpulse * 0.4);

            if (flashToSuper)
            {
                // Flash to SUPER — halo jumps bright instantly
                if (_context == LampContext.Titlebar)
                {
                    await SetImageAsync(_baseImage, _superPath);
                    if (_overlayImage != null) _overlayImage.Opacity = 0;
                    if (_haloImage != null) _haloImage.Opacity = _superHaloOpacity;
                }
                else if (_context == LampContext.Splash && _superImage != null)
                {
                    await SetImageAsync(_superImage, _superPath);
                    _superImage.Opacity = 1.0;
                    if (_haloImage != null) _haloImage.Opacity = _superHaloOpacity;
                }
            }
            else
            {
                // Flash away from rest - to dark, or to lit when reversed
                if (_context == LampContext.Titlebar && _overlayImage != null)
                {
                    await SetImageAsync(_baseImage, _onPath);
                    _overlayImage.Opacity = AwayOverlayOpacity;
                    if (_haloImage != null) _haloImage.Opacity = AwayHaloOpacity;
                }
                else if (_context == LampContext.Splash && _superImage != null)
                {
                    await SetImageAsync(_superImage, _offPath);
                    _superImage.Opacity = 1.0;
                    if (_haloImage != null) _haloImage.Opacity = 0.025;
                }
            }

            await Task.Delay(75, ct);

            // Return to rest between pops — instant snap, no fade
            if (_context == LampContext.Titlebar)
            {
                await SetImageAsync(_baseImage, _onPath);
                if (_overlayImage != null) _overlayImage.Opacity = RestOverlayOpacity;
                if (_haloImage != null) _haloImage.Opacity = RestHaloOpacity;
            }
            else if (_context == LampContext.Splash && _superImage != null)
            {
                _superImage.Opacity = _defaultSuperOpacity;
                if (_haloImage != null) _haloImage.Opacity = _defaultHaloOpacity;
            }

            if (i < flashCount - 1)
                await Task.Delay(flashSpeed, ct);
        }

        // Smooth settle after the burst is done
        if (_haloImage != null)
            await AnimateOpacity(_haloImage, _context == LampContext.Titlebar ? RestHaloOpacity : _defaultHaloOpacity, fadeMs, ct);
    }

    private async Task BlinkLoop(CancellationToken token, double fadeMs, double initialDelayMs, double minDelayMs,
        double minRampSec, double maxRampSec, Random random, bool rotate = false)
    {
        if (_overlayImage == null)
            return;

        try
        {
            await SetImageAsync(_baseImage, _onPath);
            await SetImageAsync(_overlayImage, _offPath);

            _baseImage.Opacity = _defaultBaseOpacity;
            _overlayImage.Opacity = RestOverlayOpacity;

            bool state = true;
            bool rampingUp = true;
            double currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
            var rampStartTime = DateTime.UtcNow;
            var nextSuperFlash = DateTime.UtcNow.AddSeconds(random.NextDouble() * 5 + 4);

            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                if (now >= nextSuperFlash)
                {
                    bool isRapidFlash = random.NextDouble() < 0.33;

                    if (isRapidFlash)
                    {
                        await ExecuteRapidFlash(random, fadeMs, rotate, token);
                    }
                    else
                    {
                        if (rotate)
                            ApplySpinImpulse(SuperFlashImpulse);

                        var superFlashDuration = random.Next(300, 1500);
                        await SetImageAsync(_baseImage, _superPath);
                        _overlayImage.Opacity = 0;

                        await Task.WhenAll(
                            AnimateOpacity(_baseImage, 1.0, fadeMs, token),
                            _haloImage != null ? AnimateOpacity(_haloImage, _superHaloOpacity, fadeMs, token) : Task.CompletedTask
                        );

                        await Task.Delay(superFlashDuration, token);
                    }

                    if (token.IsCancellationRequested)
                        break;

                    await SetImageAsync(_baseImage, _onPath);
                    await SetImageAsync(_overlayImage, _offPath);

                    await Task.WhenAll(
                        AnimateOpacity(_overlayImage, 1.0, fadeMs, token),
                        _haloImage != null ? AnimateOpacity(_haloImage, 0.025, fadeMs, token) : Task.CompletedTask
                    );

                    state = false;
                    rampingUp = true;
                    currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
                    rampStartTime = DateTime.UtcNow;
                    nextSuperFlash = DateTime.UtcNow.AddSeconds(random.NextDouble() * 5 + 4);
                    continue;
                }

                double phaseTime = (now - rampStartTime).TotalSeconds;
                double progress = Math.Clamp(phaseTime / currentRampDuration, 0, 1);
                double eased = progress < 0.5 ? 2 * progress * progress : 1 - Math.Pow(-2 * progress + 2, 2) / 2;

                double delay = rampingUp
                    ? initialDelayMs - (initialDelayMs - minDelayMs) * eased
                    : minDelayMs + (initialDelayMs - minDelayMs) * eased;

                // Each blink toggle is a small push — faster blink rate = more frequent pushes = feels livelier
                if (rotate)
                {
                    double normalizedSpeed = 1.0 - Math.Clamp((delay - minDelayMs) / (initialDelayMs - minDelayMs), 0, 1);
                    double impulse = BlinkImpulseMin + (BlinkImpulseMax - BlinkImpulseMin) * normalizedSpeed;
                    ApplySpinImpulse(impulse);
                }

                await Task.WhenAll(
                    AnimateOpacity(_overlayImage, state ? 0.0 : 1.0, fadeMs, token),
                    _haloImage != null ? AnimateOpacity(_haloImage, state ? 0.5 : 0.025, fadeMs, token) : Task.CompletedTask
                );

                state = !state;

                if (progress >= 1.0)
                {
                    rampingUp = !rampingUp;
                    rampStartTime = DateTime.UtcNow;
                    currentRampDuration = random.NextDouble() * (maxRampSec - minRampSec) + minRampSec;
                }

                await Task.Delay((int)delay, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try
            {
                await SetImageAsync(_baseImage, _superPath);
                if (_overlayImage != null) _overlayImage.Opacity = 0;

                await Task.WhenAll(
                    AnimateOpacity(_baseImage, 1.0, fadeMs),
                    _haloImage != null ? AnimateOpacity(_haloImage, 0.6, fadeMs) : Task.CompletedTask
                );

                await Task.Delay(token.IsCancellationRequested ? 400 : random.Next(450, 900));
            }
            catch { }

            await SetupRestState(fadeMs);
            _isBusy = false;
        }
    }
}
