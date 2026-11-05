# Firmware runtime loop -- panel drives the layout without the PC

Branch `Phase2` (commit dabda90). Design in `PLAN_Runtime.md`, user docs in
`DrammenMJKConfig/README.md` "Layout operation (runtime loop)".

## Decisions from the site owner (2026-11-05)

- Motors move ONLY on a panel switch change.
- Event driven, no per-pens state. Everything is polled all the time; each
  switch/pens pair is independent, so one pens can move while another switch
  changes, and a switch flipped back mid-move needs no special case.
- Move sequence: panel change -> motor bit set. Feedback leaves the position
  (owner notation `00`) -> current LED off. Feedback reaches the target ->
  that LED on.
- Owner notation is 1 = contact closed. Raw pin levels (pull-ups) are the
  inverse: owner `10 -> 00 -> 01` = raw `10 -> 11 -> 01`; owner error `11` =
  raw `00` (`read_switch_state()` FAULT).
- Panel LEDs are always active-low; SCB signal LEDs always active-high. Fixed
  per board type, nothing stored.
- At boot, every pens is synced to its panel switch.
- Dreieskive CW and CCW both active = error, motor stops. How errors show on
  the panel is still open (owner decides later).
- Signal lamps, green request and track detection come later. Track
  monitoring will be the one thing watched besides the panel.

## How it is built

- `loop()` = `serviceSerial()` (non-blocking line reader; the old
  `readLine()` waited 5 s per pass) + `rt_service()` (10 ms tick).
- Watch lists built in `rt_start()` from EEPROM: panel input ports (`P`
  table), feedback ports (switch table, port A), LED ports (`Q` table).
  Caps: 16 read ports, 8 LED ports.
- Debounce: 3 equal reads in a row. First read after start counts every bit
  as changed, which is the boot sync.
- Motor and dreieskive writes are read-modify-write on `OLATB`, like
  `cmdSW`. LED ports keep a RAM byte seeded from the chip's OLAT; only
  changed ports are written.
- Error lines start with `!`, so the config tool prints them at once.
- Dreieskive inputs assumed active-low (`shortcut:` comment in
  `rt_panel_event`).
- Size: 19794 bytes flash (61 %), 873 bytes RAM (42 %).

## Config tool

- `RT 0` after connect, `RT 1` on Quit; `ArduinoConnection.ReconnectCommand`
  re-sends `RT 0` after a reconnect (reopening the port reboots the Uno);
  Config -> A pauses again after the restart.
- Status shows runtime running/paused and pens errors by label.
- `RuntimeStatus` parses the `RT` reply (unit tested);
  `Runtime_PausesAndResumes` integration test (`[Explicit]`, Prod mode).

## Not done yet

- Not run against the panel or motor boards.
- Bench without the mux: Venstre (0x40) aliases the panel chip 0x20, so the
  runtime drives motor bits on the panel chip. Remove that slot or fit the
  mux before testing.
- No detection of "never arrived" or "wrong end"; the operator sees it on
  the LEDs. Needs one byte per pens plus a start time if wanted later.
