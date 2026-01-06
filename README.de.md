# Eisenbahn-Signalsteuerung – Designspezifikation

## Zustandsübergänge und Signalverhalten

Dieser Abschnitt beschreibt, wie die Steuerung zwischen Zuständen basierend auf
Eingaben und Ereignissen wechselt.

---

### Richtungszustand

**Richtung** definiert, welches Ende des Blocks Grün zeigen darf.

| Richtung | Bedeutung |
|----------|---------|
| **A → B** | **Signal A** ist das aktive grüne Ende |
| **B → A** | **Signal B** ist das aktive grüne Ende |

---

### Eingabetypen

| Eingabe | Auslösertyp | Aktiver Zustand |
|------|--------------|--------------|
| PBA | **Flankengesteuert** | LOW (Tastendruck) |
| PBB | **Flankengesteuert** | LOW (Tastendruck) |
| SCA | **Pegelbasiert** | LOW (Geschlossen) |
| SCB | **Pegelbasiert** | LOW (Geschlossen) |
| Zug | **Pegelbasiert** | LOW (Zug vorhanden) |

---

### Richtungszustandsübergänge

Richtungsänderungen erfolgen **nur bei Tastendruck-Flanken** und nur wenn **kein Zug vorhanden** ist.

| Aktuelle Richtung | Zug vorhanden | Tastenereignis | Nächste Richtung |
|------------------|---------------|--------------|----------------|
| Beliebig | Nein | **PBA gedrückt** | **A → B** |
| Beliebig | Nein | **PBB gedrückt** | **B → A** |
| Beliebig | Ja | PBA oder PBB gedrückt | Unverändert |
| Beliebig | Beliebig | Kein Tastendruck | Unverändert |

Hinweise:

- PBA und PBB werden nur bei der **Druckflanke** ausgewertet
- Gedrückthalten einer Taste löst **keine** erneute Auslösung aus
- Drücken der Taste, die der aktuellen Richtung entspricht, hat keine Wirkung

---

### Signalausgangslogik (Normalbetrieb)

| Bedingung | Signal A – Rot | Signal A – Grün 1 | Signal A – Grün 2 | Signal B – Rot | Signal B – Grün 1 | Signal B – Grün 2 |
|----------|----------------|-------------------|-------------------|----------------|-------------------|-------------------|
| Zug vorhanden | EIN | AUS | AUS | EIN | AUS | AUS |
| Richtung **A → B**, SCA offen | AUS | EIN | AUS | EIN | AUS | AUS |
| Richtung **A → B**, SCA geschlossen | AUS | EIN | EIN | EIN | AUS | AUS |
| Richtung **B → A**, SCB offen | EIN | AUS | AUS | AUS | EIN | AUS |
| Richtung **B → A**, SCB geschlossen | EIN | AUS | AUS | AUS | EIN | EIN |

---

### Debug-Modus Zustandsübergänge

| Aktueller Debug-Modus | Debug-Taste gedrückt | Nächster Debug-Modus |
|-------------------|----------------------|-----------------|
| Aus | Ja | AlleAn |
| AlleAn | Ja | Auto |
| Auto | Ja | Manuell |
| Manuell (Schritt < letzter) | Ja | Manuell (nächster Schritt) |
| Manuell (letzter Schritt) | Ja | Aus |

---

### Debug-Abbruch

| Ereignis | Wirkung |
|------|--------|
| PBA oder PBB während Debug gedrückt | Debug-Modus wird sofort beendet |
| Debug abgebrochen | Richtung ändert sich **nicht** beim selben Tastendruck |

---

### Einschalt- / Reset-Zustand

| Bedingung beim Einschalten | Resultierender Zustand |
|----------------------|-----------------|
| Debug-Taste nicht gehalten | Richtung aus EEPROM wiederhergestellt |
| Debug-Taste gehalten | Richtung auf **A → B** erzwungen, EEPROM zurückgesetzt |

---

### Zusammenfassende Regeln

- Richtung wird **explizit gesetzt**, nie umgeschaltet
- PBA wählt immer **A → B**
- PBB wählt immer **B → A**
- Taster sind **flankengesteuert - high -> low**
- Schalter und Zugerkennung sind **pegelbasiert**
- Zugpräsenz überschreibt alle andere Logik
- Debug-Modi überschreiben Lampenausgänge, **ändern aber nicht die Richtung**
- EEPROM-Reset stellt immer eine **bekannte, sichere Richtung** wieder her

---

## Steuerung

- Platine: **Arduino Uno-kompatibel (GeekCreit)**
- MCU: **ATmega328P**
- Takt: **16 MHz**
- EEPROM: **1 KB (on-chip)**

---

## Stromversorgung

- Systemversorgung: **+10–12 V**
- Logikversorgung: **Abwärtswandler → 5,0–5,1 V**
- Arduino versorgt über **5V-Pin**
- VIN wird nicht verwendet
- Gemeinsame Masse für Logik und Ausgänge

---

## Eingänge (Alle über Optokoppler)

- Optokoppler: **PC817 / EL817**
- Eingangsspannung: **10–12 V**
- LED-Vorwiderstand: **3,3 kΩ**
- Typischer LED-Strom: **~2–3 mA**
- Arduino-Seite: `INPUT_PULLUP`
- Logikpegel: **LOW = aktiv**

### Eingangsfunktionen und Pins

| Funktion | Arduino Pin | Platinen-Beschriftung |
|--------|-------------|-------------|
| Zugerkennung | D2 | 2 |
| Taster A | D3 | 3 |
| Taster B | D4 | 4 |
| Schalter A geschlossen | D5 | 5 |
| Schalter B geschlossen | D6 | 6 |
| Debug-Taste | A4 | A4 |
| Seriell-Aktivierungs-Jumper | A5 | A5 |

---

## Ausgänge (Diskrete Transistortreiber)

- Topologie: **NPN Low-Side-Schaltung**
- Transistor: **BC547 / BC337 / 2N2222**
- Basiswiderstand: **4,7 kΩ**
- Basis-Pull-down: **100 kΩ (Basis → Emitter)**
- Last: **Signal-LEDs**
- LED-Versorgung: **+10 V**
- LED-Strom: **10 mA**

### LED-Vorwiderstand

- Nennwert: **680 Ω**
- Akzeptabler Bereich: **680–820 Ω**
- Belastbarkeit: **¼ W**

### Ausgangsfunktionen und Pins

| Signal | Arduino Pin | Platinen-Beschriftung |
|------|-------------|-------------|
| Signal A – Rot | D7 | 7 |
| Signal A – Grün 1 | D9 | 9 |
| Signal A – Grün 2 | D8 | 8 |
| Signal B – Rot | D10 | 10 |
| Signal B – Grün 1 | D12 | 12 |
| Signal B – Grün 2 | D11 | 11 |

- Logik: **Arduino HIGH = LED EIN**
- Hardware-Invertierung durch Transistor

---

## Debug-LEDs (5 V Logik)

- Active-high
- Direktansteuerung vom Arduino-Pin
- Widerstand: **1 kΩ**
- LED-Strom: **~3 mA**

### Debug-LED Pins und Bedeutung

| Debug-LED | Arduino Pin | LED EIN bedeutet |
|----------|-------------|--------------|
| Richtung (Q) | A0 | Signal **B** ist das aktive grüne Ende (`g_stateQ = true`) |
| Zug | A1 | Zug im Block erkannt |
| A_Gen | A2 | Signal A darf Grün zeigen |
| B_Gen | A3 | Signal B darf Grün zeigen |

---

## Debug-Tasten-Verhalten

- Taste verbunden mit **A4 → GND**
- `INPUT_PULLUP`
- Active-low

### Debug-Zustandssequenz

1. ALL_ON (=1) **Alle Signal-LEDs EIN**
2. CYCLE (=2) **Automatischer Zyklus** (1 s pro LED):
   A_G1 → A_G2 → A_R → B_G1 → B_G2 → B_R
3. MAN_0 (=3), MAN_1 (=4) ... MAN_5 (=8) **Manueller Schritt** (ein Tastendruck pro LED, gleiche Reihenfolge)
4. OFF (=0) (Bei Zustandseintritt: Alle 3x blinken, 0,5 s dazwischen, dann Debug beenden) **Debug beenden → Normalbetrieb**

- Jeder Druck von **Signaltaste A oder B**:
  - Bricht Debug sofort ab
  - Ändert **nicht** den Signalzustand beim selben Tastendruck

---

## Normale Signallogik

- Zustandsvariable: `g_stateQ`
  - `false` = A ist grünes Ende
  - `true`  = B ist grünes Ende
- Startzustand:
  - Aus EEPROM wiederhergestellt
- Zug vorhanden:
  - Beide Signale rot
- Taster A oder B:
  - Schaltet `g_stateQ` bei **Druckflanke** um
  - Ignoriert wenn Zug vorhanden
- Schalter A oder B geschlossen:
  - Grün 1 für A oder B ist Ein
- Schalter A oder B geöffnet:
  - Beide Grüns für A oder B sind ein.

- Wenn ein Zug vorhanden ist, gehen beide Signale auf Rot. Der Zustand sollte vorher gespeichert werden.
- Wenn kein Zug mehr vorhanden ist, sollte der Zustand wiederhergestellt werden.

---

## EEPROM-Nutzung

- EEPROM-Größe: **1024 Bytes**
- Adresskarte:
  - **Adresse 0**: `g_stateQ`
- Schreibmethode:
  - `EEPROM.update()` (schreibt nur bei Wertänderung)
- Schreibereignisse:
  - Nur wenn `g_stateQ` umschaltet
- EEPROM wird während Debug-Modi **nicht** beschrieben

---

## EEPROM-Reset / Wiederherstellungsverfahren

### Zweck

Steuerung in einen **bekannten, sicheren Zustand** zurücksetzen, falls das Verhalten ungültig wird.

### Reset-Mechanismus

- Reset erfolgt durch **Aus- und Einschalten**
- Kein dedizierter Reset-Jumper erforderlich

### Reset-Schritte

1. Platine mit **JP2** **ausschalten**
2. **Debug-Taste (A4)** **gedrückt halten**
3. **Einschalten**
4. Debug-Taste nach dem Start loslassen

### Reset-Wirkung

- `g_stateQ` wird auf **false** erzwungen
- Signal **A** wird das aktive grüne Ende
- EEPROM-Adresse `0` wird entsprechend aktualisiert

---

## Serielle Debug-Ausgabe

- Baudrate: **115200**
- Status wird einmal pro Sekunde ausgegeben
- Gesteuert durch Jumper auf **A5**
  - Jumper installiert (LOW): Seriell aktiviert
  - Jumper entfernt (HIGH): Seriell deaktiviert

---

## Verkabelungshinweise

- Eingangskabellängen: **2–5 m**
- Optokoppler für alle Eingänge erforderlich
- Optional: **100 nF Kondensator** parallel zur Opto-LED bei störanfälligen Leitungen
- Pins **D0/D1** vermeiden
- Pin **D13** unbenutzt (On-Board-LED verfügbar)

---
