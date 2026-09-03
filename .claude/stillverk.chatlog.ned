# Stillverk (switch machine) — findings from live bring-up session

## Motor travel timing

- Real switch motor travel takes ~5 seconds end to end (observed on the bench).
- The two feedback contacts (Rett/Avvik) do NOT flip together: one releases
  early as the motor leaves the start position, then the bus sits at a
  single-bit-changed "between" reading (both contacts open, pulled high) for
  most of the travel -- only the second contact closing at the very end
  produces the real, final pair transition. E.g. `10 -> 11 -> 01`, not a
  clean `10 -> 01`.
- Consequence for `MPOLL` (Firmware.ino): it's not enough to wait for a
  *stable* reading -- the "between" plateau is stable too, just not done.
  It must wait for at least 2 bits to differ from baseline (matching the
  fact that a real transition always flips a whole feedback pair) AND be
  stable for ~20ms, before reporting CHANGED.
- Consequence for timeouts: `MotorScan`'s `FeedbackTimeoutMs` and
  `CommandSession`'s drive `TimeoutMs` are both 10000ms -- real margin above
  the ~5s observed travel time, not just above I2C round-trip time.

## Polarity encoding bug (fixed in ConfigSession.cs)

- The stored `polarity` byte must be the literal OLATB bit value that
  represents Rett -- `Firmware.ino`'s `cmdSW` reads it directly:
  `targetLevel = (pos=='R') ? polarity : 1-polarity`.
- Bug: `polarity` was computed as a fixed 0/1 literal from `pos` alone,
  ignoring `fireState` (the actual bit value the switch was driven to during
  the scan). This only worked by coincidence while the old firing code
  always drove to a hardcoded `false` first; it broke once firing was fixed
  to toggle away from whatever the real rest state was (rest state is no
  longer guaranteed to be `false`).
- Fix: `polarity` is now derived as `(pos=='R') == fireState`, i.e. the true
  Rett-bit value empirically observed during the scan, not a hardcoded
  literal.
- Already-scanned switches from before the fix need their stored polarity
  corrected once, by reading live feedback + the live OLATB bit and
  recomputing -- the fix itself only applies to scans done after it landed.

## CommandSession (Operate mode) UI scope

- The "only offer the position not already confirmed" / "Toggle position"
  behavior is a PC-console-only convenience in `CommandSession.cs`. It reads
  live feedback (`ReadSwitch`/`SR`) fresh every time before deciding what to
  show -- nothing cached or assumed.
- Does NOT touch `Firmware.ino` or the `SW` protocol command, which still
  accepts either R or A unconditionally regardless of current state.
- Has no bearing on real stillverk switches' actual physical Rett/Avvik
  positions, and no bearing on any future Phase 2+ autonomous firmware
  runtime (SVB panel switch -> SCB motor, no PC attached) -- that will live
  entirely in firmware and won't go through this C# menu at all.
