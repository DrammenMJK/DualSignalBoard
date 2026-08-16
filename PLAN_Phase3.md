# Plan: Phase 3

Collects Phase 3 scope as it's decided, same pattern as `PLAN_Phase2.md`.

## Multi-chip SCB support

Motivating context: during live bring-up of FCSBR, it came up that the data
model has no way to represent a board built from more than one MCP23017.

Today `BoardScb` (`BoardConfig.cs`) has a single `VirtualAddress: byte`
field, and `ConfigSession.MotorScan` uses that one `vaddr` for both firing
motor bits (Port B) and polling feedback (Port A). If a board actually needed
a second chip, there's nowhere in the record to put the second address, and
polling would only ever watch the first chip — a switch whose motor bit lives
on chip 1 but feedback on chip 2 couldn't be scanned at all.

Not an active problem yet: checking `hardware.json`'s own `_todo` notes,
FCSBL's full bit count (3 motors + 3 signal LEDs + 1 status LED + 6 feedback
+ 1 track-detect = 14 bits, +2 spares = 16) fits inside one chip's 16 bits.
Both boards defined in `BoardConfig` today are single-chip. Deliberately
holding off on generalizing this until a real 2-chip board shows up, so the
design is driven by actual requirements rather than speculation (see the
[[main-thread]] discussion this came out of).

When it does come up, needs:

- `BoardScb` to carry a *set* of chip vaddrs instead of one (motor-bit and
  feedback-bit definitions per switch would need to say which chip they're
  on, since a single board's switches could be split across chips).
- `MotorScan`'s baseline-read/fire/poll loop to read and poll across all of
  a board's chips together, not just one vaddr — a switch moving could show
  up as a feedback change on either chip depending on wiring.
- Scope stays "one board at a time" either way (per the bring-up
  conversation this came from) — multi-chip only changes how many addresses
  *that one board* touches, not whether multiple boards get scanned together.
