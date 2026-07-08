# LED Routing Matrix — JSON File Format

The routing matrix tells the Arduino which switch conditions must be satisfied before
each indicator LED on the operator panel is allowed to light. This replaces the simple
1-to-1 rule (LED lights = switch is in that position) with route-aware logic.

A separate JSON file is used for each site. The file name is your choice;
a meaningful name like `Fossli.json` or `Sentrum.json` keeps sites distinct.

---

## Why this is needed

In a typical station panel, a switch indicator should only light when the route through
that switch is actually *reachable* from the approach direction. Example: the Rett LED
for switch G should stay dark if switch F is set such that no train coming from block 21
can reach G — even if G itself is in Rett position.

The routing matrix stores, per LED, the complete set of switch states that must be true
for that LED to light.

---

## File structure

```json
{
  "site": "SiteName",
  "routing": {
    "LED_NAME": [
      { "rett": ["X", "Y"], "avvik": ["Z"] },
      { "rett": ["X"],      "avvik": ["Y"] }
    ],
    ...
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `site` | No | Human-readable label shown when the file is loaded. Used only for display. |
| `routing` | **Yes** | Map of LED names to their activation conditions. |

---

## LED names

Each entry in `routing` must use one of these exact names (case-sensitive):

| Pens | Rett LED | Avvik LED |
|------|----------|-----------|
| B | `B_Rett` | `B_Avvik` |
| C | `C_Rett` | `C_Avvik` |
| D | `D_Rett` | `D_Avvik` |
| E | `E_Rett` | `E_Avvik` |
| F | `F_Rett` | `F_Avvik` |
| G | `G_Rett` | `G_Avvik` |
| H | `H_Rett` | `H_Avvik` |
| I | `I_Rett` | `I_Avvik` |

LEDs **not listed** in `routing` use the default behaviour: the LED lights whenever
its own Pens is in the matching position, with no other prerequisites.

---

## Conditions

Each LED entry is an array of **activation conditions**. The LED lights when
**any one** of its conditions is fully satisfied (logical OR of AND-chains).

Each condition object has two optional arrays:

| Key | Meaning |
|-----|---------|
| `rett` | Pens letters that must be in **Rett** position for this condition to pass |
| `avvik` | Pens letters that must be in **Avvik** position for this condition to pass |

Both arrays are optional but **at least one must be non-empty** in each condition.

**Pens letters** are single uppercase letters `B` through `I`.  
Maximum **4 conditions** per LED.

### Suppressing an LED (shared physical pins)

Some switches share one physical indicator LED on the panel. For example, at Fossli,
the Avvik indicator for switch E and the Avvik indicator for switch F are the same
physical LED (both connect to the same track end).

In Command 3 (LED mapping), both `E_Avvik` and `F_Avvik` are assigned the **same
physical MCP23017 output pin**. At runtime the Arduino ORs their results: the LED
lights if either entry's conditions are satisfied.

To prevent one of the logical entries from independently lighting the LED (for example,
if `E_Avvik` should *never* independently contribute — only `F_Avvik` drives the shared
pin), list the LED in `routing` with an **empty array**:

```json
"E_Avvik": []
```

This sets the count byte to `0x00` (suppressed). The Arduino treats it as always-off,
so the shared physical pin is driven solely by `F_Avvik`'s routing conditions.

**Without this entry:** `E_Avvik` would use the default 1-to-1 rule and light the
physical LED whenever E is in Avvik position, regardless of other switch states —
potentially lighting the shared LED when it should be dark.

### Rules the validator enforces

| Check | Error |
|-------|-------|
| A Pens letter appears in both `rett` and `avvik` | The condition could never be true |
| Both `rett` and `avvik` are absent or empty | The condition is always true (LED permanently lit) |
| An unknown LED name | Warning — entry is skipped |
| More than 4 conditions | Warning — extras are truncated |
| Any other structural problem (wrong JSON type, bad letter, etc.) | Error — file is rejected |

All errors in the file are reported before any data is sent to the Arduino.

---

## Complete example — Fossli station

### Track topology

The yard has three blocks (1, 2, 3) accessed from both ends.
Block 41 (main Vallekilen line) and block 42 (secondary) both approach from the left via switch B.

```
LEFT (from blocks 41 / 42 via Vallekilen):

  41 ─┐
      B ─── C(Rett)  ─────────────── block 1 ─────────────── G(Avvik) ─┐
  42 ─┘     C(Avvik) ─ D(Rett)  ─── block 2 ─────────────── F(Avvik) ─┤─ H(Rett) ─ 21
                         D(Avvik) ── block 3 ─── E(Avvik) ──────────────┘
                                          └────── E(Rett) ─── 31 ─── Dreieskive

RIGHT (from block 21):
  H: Rett = enter yard toward G  / Avvik = divert to I → blocks 22/23
  G: Rett = continue toward F    / Avvik = enter block 1
  F: Rett = continue toward E    / Avvik = enter block 2  (shared LED with E_Avvik)
  E: Avvik = enter block 3 (from 21 direction, suppressed)
     Rett  = exit block 3 to block 31/Dreieskive
```

### Block connectivity

| Direction | From | Can reach |
|-----------|------|-----------|
| Left → right | Block 41 | Block 1, Block 2, Block 3 |
| Left → right | Block 42 | Block 1, Block 2, Block 3 |
| Left → right | Block 1 | Block 21 |
| Left → right | Block 2 | Block 21 |
| Left → right | Block 3 | Block 21 **or** Block 31 |
| Right → left | Block 21 | Block 1, Block 2, Block 3, Block 22, Block 23 |
| Right → left | Block 31 | Block 3 |
| Right → left | Block 22 | Block 21 only |
| Right → left | Block 23 | Block 21 only |
| Right → left | Block 1/2/3 | Block 41, Block 42 |

B=Rett connects block 41; B=Avvik connects block 42.  
The complete routing file is also provided as `Fossli.json` in this folder.

### Design principle: one section per LED

Each LED condition covers **one section** — the switch's own position plus its direct upstream
prerequisite. Full routes are read by looking at all lit LEDs together.

- **B**: no upstream → `{rett:[B]}` or `{avvik:[B]}`
- **C**: B connects both 41 and 42 to the same track, so C has no upstream prerequisite → `{rett:[C]}`
- **D**: only reachable via C=Avvik → `{rett:[D], avvik:[C]}`
- **H**: E=Rett cuts off access from block 21 when G and F are also Rett, so H_Rett needs 3 conditions (one per reachable block) to stay dark in that dead-end case. G_Rett and F_Rett follow the same pattern.
- **G**: 2 conditions — one for the route to block 2 (F=Avvik) and one for block 3 (F=Rett, E=Avvik)
- **F**: F_Rett: 1 condition (block 3, E=Avvik required). F_Avvik: 1 condition (block 2)
- **E_Rett**: E is at the opposite end of block 3 from D — independent section → `{rett:[E]}`

### Complete routing matrix

```json
{
  "site": "Fossli",
  "routing": {

    // ── LEFT SIDE: approach from blocks 41 / 42 via Vallekilen ─────

    // B: which left-approach block is connected.
    "B_Rett":  [{ "rett":  ["B"] }],  // block 41 connected
    "B_Avvik": [{ "avvik": ["B"] }],  // block 42 connected

    // C: first split — block 1 or toward D.
    // No B prerequisite: both 41 and 42 reach C through B.
    "C_Rett":  [{ "rett":  ["C"] }],
    "C_Avvik": [{ "avvik": ["C"] }],

    // D: second split — block 2 or block 3.
    // C=Avvik is D's direct upstream prerequisite.
    "D_Rett":  [{ "rett": ["D"], "avvik": ["C"] }],
    "D_Avvik": [{ "avvik": ["C", "D"] }],

    // ── RIGHT SIDE: approach from block 21 ─────────────────────────

    // H_Rett: block 21 connected to the yard AND a valid route exists.
    // E=Rett cuts off the path from block 21 when G and F are also Rett,
    // so H_Rett must verify the downstream switches lead to an actual block.
    "H_Rett": [
      { "rett": ["H"],           "avvik": ["G"] },  // 21 → block 1
      { "rett": ["H", "G"],      "avvik": ["F"] },  // 21 → block 2
      { "rett": ["H", "G", "F"], "avvik": ["E"] }   // 21 → block 3
    ],
    // H_Avvik: block 21 directed toward I → blocks 22 and 23.
    "H_Avvik": [{ "avvik": ["H"] }],

    // I: only reachable via H=Avvik.
    "I_Rett":  [{ "rett": ["I"],  "avvik": ["H"] }],
    "I_Avvik": [{ "avvik": ["H", "I"] }],

    // G: two conditions — one per reachable block (2 and 3). Dead-end excluded.
    "G_Rett": [
      { "rett": ["H", "G", "F"], "avvik": ["E"] },  // 21 → block 3
      { "rett": ["H", "G"],      "avvik": ["F"] }   // 21 → block 2
    ],
    "G_Avvik": [{ "rett": ["H"], "avvik": ["G"] }],  // 21 → block 1

    // F: F_Rett only for block 3 (E=Avvik required). F_Avvik drives shared LED.
    "F_Rett":  [{ "rett": ["H", "G", "F"], "avvik": ["E"] }],  // 21 → block 3
    "F_Avvik": [{ "rett": ["H", "G"],      "avvik": ["F"] }],  // 21 → block 2

    // E_Rett: block 3 connected to block 31 / Dreieskive.
    // E is at the opposite end of block 3 from D — no C/D prerequisite.
    "E_Rett": [{ "rett": ["E"] }],

    // E_Avvik: SUPPRESSED — shares the physical LED with F_Avvik above.
    "E_Avvik": []
  }
}
```

### What this produces at runtime

The operator reads the complete route by observing all lit LEDs together.

**Left side (from 41 or 42):**

| B | C | D | E | LEDs lit | Route |
|---|---|---|---|----------|-------|
| Rett | Rett | — | — | B_Rett, C_Rett | 41 → block 1 |
| Avvik | Rett | — | — | B_Avvik, C_Rett | 42 → block 1 |
| Rett | **Avvik** | Rett | — | B_Rett, C_Avvik, D_Rett | 41 → block 2 |
| Avvik | **Avvik** | Rett | — | B_Avvik, C_Avvik, D_Rett | 42 → block 2 |
| Rett | **Avvik** | **Avvik** | — | B_Rett, C_Avvik, D_Avvik | 41 → block 3 |
| Avvik | **Avvik** | **Avvik** | — | B_Avvik, C_Avvik, D_Avvik | 42 → block 3 |
| any | **Avvik** | **Avvik** | Rett | + E_Rett | → block 3 also connected to block 31 |

**Right side (from block 21):**

| H | G | F | E | LEDs lit | Route |
|---|---|---|---|----------|-------|
| Rett | **Avvik** | — | — | H_Rett, G_Avvik | 21 → block 1 |
| Rett | Rett | **Avvik** | — | H_Rett, G_Rett, F_Avvik | 21 → block 2 |
| Rett | Rett | Rett | **Avvik** | H_Rett, G_Rett, F_Rett | 21 → block 3 (E_Avvik suppressed) |
| **Avvik** | — | — | — | H_Avvik, + I_Rett or I_Avvik | 21 → block 22 or 23 |

---

## Syntax notes

- Trailing commas are allowed: `["G", "H",]`
- Single-line `//` comments are allowed (useful for annotating conditions)
- Key names are case-sensitive; `G_rett` is not the same as `G_Rett`
- Extra properties at any level are silently ignored

---

## Using the file from DrammenMJKConfig

1. Enter **Config mode** (`C` from main menu)
2. Select **4 — LED routing matrix**
3. Press **L** to load a file and upload to Arduino
4. Enter the full path to the JSON file (e.g. `C:\Sites\Fossli.json`)
5. Press **?** at the file path prompt for an in-program format reminder

The program displays the `site` name from the file before parsing, so you can confirm
you have the right file for the current layout before committing to upload.

To switch sites: load the other site's JSON file. The Arduino EEPROM is overwritten
with the new matrix. The previous matrix is gone; keep the JSON files as the
authoritative source.
