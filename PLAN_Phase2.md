# Plan: Phase 2 — Visibility & Making It Work

Phase 1 (`PLAN_Phase1.md`) delivers the minimum to configure and drive one SCB
(FCSBR) by hand: hardware/system config upload, Motor Scan, Command mode.
It has no operator-facing way to ask "is this actually working right now?" —
you can only infer state indirectly via `HWD`/`SCD` downloads into JSON files.

Phase 2's theme, per direction from the site owner: **visibility**. Before
adding more boards, buses, or the autonomous runtime loop, the tool needs to
be able to answer, at a glance, "what's on the bus, and does EEPROM agree
with it."

This document collects Phase 2 scope as it's decided. It starts with the
Status/diagnostics feature; more sections get added as Phase 2 is planned out.

## Status / diagnostics command

Motivating ask (verbatim): *"I need to be able to see the status of the
firmware. Is the EEPROM loaded correctly, summary of what information it
has,"* refined to: *"This is a status command. It should let me see a) EEPROM
b) status of the mux or I2C bus, how many devices it finds and so on. I need
visibility."*

Two things exist today that this replaces/extends:

- The Phase-1-deleted `EepromStatus.cs` did this for the **old** Pens-letter/
  Region1–6 layout — a per-Pens breakdown of motor/switch/LED config. Not
  reusable directly (whole layout changed) but the shape (per-item
  configured/not-configured, decoded pin labels) is worth keeping.
- `HWD`/`SCD` already let the PC pull the board hardware table and switch/
  dreieskive/LED/fade config out of EEPROM — but only as a side effect of
  writing to `hardware.json`/`SystemConfig.json`. There's no "just show me,
  don't touch the files" path today.

### a) EEPROM summary

A read-only session (no file writes) that:

- Downloads the board hardware table (`HWD`) and prints one line per
  configured chip: vaddr, IODIR A/B, GPPU A/B.
- Downloads switch/dreieskive/LED/fade config (`SCD`) and prints, per switch
  label from `BoardConfig.AllSwitchSlots()`: configured vs. not, motor
  vaddr/bit/polarity, feedback Rett/Avvik bits. Same for dreieskive and
  status LED (configured vs. not). Fade config always prints (has firmware
  defaults even when EEPROM is blank — reads as `FFFF FF FFFF` when never
  written, which the summary should call out as "not set" rather than a
  literal fade time of 65535 ms).
- Existing `HWD`/`SCD` firmware commands are sufficient — no protocol change
  needed for this half.

### b) I2C bus / mux visibility

Not available today at all: nothing lets the operator ask "what does the
Arduino actually see on the bus right now," independent of what EEPROM
*claims* is there. Needs a new firmware command — proposed `ISCAN`:

- Probes I2C addresses (`Wire.beginTransmission`/`endTransmission`, no data)
  across the relevant range and reports which ACK.
- Phase 1 has no mux (only bus 0 wired) — scan is just the physical bus.
  Once a mux lands (`PlanExtended.md`'s 4-bus design), this needs to become
  bus-aware: select each bus via the mux, scan, tag results by bus number,
  and report mux presence/health itself (does the mux ACK on its own control
  address?) as a separate line.
- Response shape (line-based, matches existing bulk-transfer commands):
  one line per found device, then `END`. C# side counts and lists them.

### c) Cross-check

The useful diagnostic isn't either half alone, it's comparing them: for every
vaddr the EEPROM config references (board hardware table, switch motor/
feedback vaddrs, dreieskive, status LED), is it also present in the I2C scan?
Flag:

- Configured in EEPROM but not found on the bus (wiring fault, dead chip,
  bus not powered).
- Found on the bus but not in the board hardware table (chip present but
  never configured via `hardware.json` upload — IODIR is at power-on default,
  i.e. all-input, not necessarily safe).

## Open items for the rest of Phase 2

Not yet planned — placeholders so this doc has somewhere to grow:

- Mux integration itself (bus select, `i2c_select_bus()` currently hardcoded
  to bus 0 only).
- Second SCB / FCSBL, second bus's boards.
- The autonomous runtime loop (SVB panel switch → SCB motor, no PC attached)
  described as Phase 2+ in `PLAN_Phase1.md`'s device-state-machine section.
- Fading loop (shadow-register design already worked out in `PLAN_Phase1.md`,
  not yet implemented in `Firmware.ino`).
