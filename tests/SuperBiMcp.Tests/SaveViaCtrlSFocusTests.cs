using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for <see cref="DesktopInterop.SaveViaCtrlSCore"/> - the save retry loop with every
/// side effect injected, so the no-blind-keystroke guarantee is proven with no live Desktop.
///
/// Regression for the confirmed finding "SaveViaCtrlS still dispatches a global Ctrl+S with no proof the
/// target window has focus": SendInput is global, so when Windows' foreground lock denies
/// SetForegroundWindow (e.g. an operator RDPs into the VM with their own PBIDesktop focused), the old code
/// sent Ctrl+S anyway and silently saved the WRONG document. The core must withhold the keystroke on every
/// attempt whose focus cannot be proven, and must re-verify the foreground after the settle delay.
///
/// The core is pure over its delegates (no OS calls), so these tests run on any platform.
/// </summary>
public sealed class SaveViaCtrlSFocusTests
{
    private static readonly IntPtr Target = new(0x1234);
    private static readonly IntPtr Stranger = new(0x9999);

    [Fact]
    public void ForegroundDeniedForEveryAttempt_WithholdsTheCtrlS_AndFails()
    {
        // The finding's exact scenario: another window holds the foreground lock, all 10 ForceForeground
        // tries of every attempt fail. HEAD dispatched the global Ctrl+S anyway; now it must not.
        int keys = 0, foregroundTries = 0;

        bool ok = DesktopInterop.SaveViaCtrlSCore(Target, saveRetries: 3,
            forceForeground: _ => { foregroundTries++; return false; },
            foregroundWindow: () => Stranger,
            sendKeys: () => keys++,
            lastWriteUtc: () => DateTime.UnixEpoch,
            sleep: _ => { });

        Assert.False(ok);
        Assert.Equal(0, keys);              // never a blind keystroke
        Assert.Equal(30, foregroundTries);  // the whole existing budget was still spent trying (3 x 10)
    }

    [Fact]
    public void ForegroundLostDuringTheSettleDelay_WithholdsTheCtrlS()
    {
        // ForceForeground succeeded, but another window stole the focus before the settle delay elapsed -
        // the re-check between the delay and the keystroke must catch it.
        int keys = 0;

        bool ok = DesktopInterop.SaveViaCtrlSCore(Target, saveRetries: 2,
            forceForeground: _ => true,
            foregroundWindow: () => Stranger,
            sendKeys: () => keys++,
            lastWriteUtc: () => DateTime.UnixEpoch,
            sleep: _ => { });

        Assert.False(ok);
        Assert.Equal(0, keys);
    }

    [Fact]
    public void AZeroTargetHandle_FailsImmediately_WithoutTouchingTheKeyboard()
    {
        // GetForegroundWindow() can itself return zero (no foreground window during a desktop switch), so a
        // zero target must fail up front rather than accidentally "matching" that state.
        int keys = 0, foregroundTries = 0;

        bool ok = DesktopInterop.SaveViaCtrlSCore(IntPtr.Zero, saveRetries: 3,
            forceForeground: _ => { foregroundTries++; return true; },
            foregroundWindow: () => IntPtr.Zero,
            sendKeys: () => keys++,
            lastWriteUtc: () => DateTime.UnixEpoch,
            sleep: _ => { });

        Assert.False(ok);
        Assert.Equal(0, keys);
        Assert.Equal(0, foregroundTries);
    }

    [Fact]
    public void ProvenFocus_SendsTheCtrlS_AndSucceedsWhenTheFileAdvances()
    {
        // The happy path is unchanged: proven focus, one keystroke, LastWriteTime bump = saved.
        int keys = 0;
        DateTime mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        bool ok = DesktopInterop.SaveViaCtrlSCore(Target, saveRetries: 3,
            forceForeground: _ => true,
            foregroundWindow: () => Target,
            sendKeys: () => { keys++; mtime = mtime.AddSeconds(5); },
            lastWriteUtc: () => mtime,
            sleep: _ => { });

        Assert.True(ok);
        Assert.Equal(1, keys);
    }

    [Fact]
    public void AFailedFocusAttempt_SpendsOneRetry_ThenALaterAttemptCanStillSave()
    {
        // Attempt 1: the focus lock denies all 10 tries (keystroke withheld). Attempt 2: focus proven, the
        // keystroke lands and the file bumps. Failing an attempt must stay INSIDE the retry budget, not
        // abort the whole save.
        int foregroundTries = 0, keys = 0;
        DateTime mtime = DateTime.UnixEpoch;

        bool ok = DesktopInterop.SaveViaCtrlSCore(Target, saveRetries: 2,
            forceForeground: _ => ++foregroundTries > 10,   // all of attempt 1 fails, attempt 2 succeeds
            foregroundWindow: () => Target,
            sendKeys: () => { keys++; mtime = mtime.AddSeconds(5); },
            lastWriteUtc: () => mtime,
            sleep: _ => { });

        Assert.True(ok);
        Assert.Equal(1, keys);
        Assert.Equal(11, foregroundTries);
    }

    [Fact]
    public void ProvenFocusWhoseSaveNeverLands_RetriesTheKeystrokePerAttempt_ThenFails()
    {
        // Pre-existing semantics preserved on the proven-focus path: if the LastWriteTime never advances
        // (Save-As dialog, no unsaved change), each attempt dispatches once and the call returns false.
        int keys = 0;

        bool ok = DesktopInterop.SaveViaCtrlSCore(Target, saveRetries: 3,
            forceForeground: _ => true,
            foregroundWindow: () => Target,
            sendKeys: () => keys++,
            lastWriteUtc: () => DateTime.UnixEpoch,
            sleep: _ => { });

        Assert.False(ok);
        Assert.Equal(3, keys);
    }
}
