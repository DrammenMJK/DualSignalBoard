# Jernbane-signalkontroller – Designspesifikasjon

## Tilstandsoverganger og signalatferd

Denne seksjonen beskriver hvordan kontrolleren skifter mellom tilstander basert på
innganger og hendelser.

---

### Retningsstatus

**Retning** definerer hvilken ende av blokken som får vise grønt.

| Retning | Betydning |
|----------|---------|
| **A → B** | **Signal A** er den aktive grønne enden |
| **B → A** | **Signal B** er den aktive grønne enden |

---

### Inngangstyper

| Inngang | Utløsertype | Aktiv tilstand |
|------|--------------|--------------|
| PBA | **Flanketriggert** | LOW (trykk) |
| PBB | **Flanketriggert** | LOW (trykk) |
| SCA | **Nivåbasert** | LOW (Lukket) |
| SCB | **Nivåbasert** | LOW (Lukket) |
| Tog | **Nivåbasert** | LOW (Tog til stede) |

---

### Retningsstatus-overganger

Retningsendringer skjer **kun ved trykknappflanker** og kun når **ingen tog er til stede**.

| Nåværende retning | Tog til stede | Knapphendelse | Neste retning |
|------------------|---------------|--------------|----------------|
| Hvilken som helst | Nei | **PBA trykket** | **A → B** |
| Hvilken som helst | Nei | **PBB trykket** | **B → A** |
| Hvilken som helst | Ja | PBA eller PBB trykket | Uendret |
| Hvilken som helst | Hvilken som helst | Ingen knappetrykk | Uendret |

Merknader:

- PBA og PBB evalueres kun ved **trykkflanken**
- Å holde en knapp inne utløser **ikke** på nytt
- Å trykke knappen som tilsvarer gjeldende retning har ingen effekt

---

### Signalutgangslogikk (Normal drift)

| Betingelse | Signal A – Rød | Signal A – Grønn 1 | Signal A – Grønn 2 | Signal B – Rød | Signal B – Grønn 1 | Signal B – Grønn 2 |
|----------|----------------|-------------------|-------------------|----------------|-------------------|-------------------|
| Tog til stede | PÅ | AV | AV | PÅ | AV | AV |
| Retning **A → B**, SCA åpen | AV | PÅ | AV | PÅ | AV | AV |
| Retning **A → B**, SCA lukket | AV | PÅ | PÅ | PÅ | AV | AV |
| Retning **B → A**, SCB åpen | PÅ | AV | AV | AV | PÅ | AV |
| Retning **B → A**, SCB lukket | PÅ | AV | AV | AV | PÅ | PÅ |

---

### Debug-modus tilstandsoverganger

| Nåværende debug-modus | Debug-knapp trykket | Neste debug-modus |
|-------------------|----------------------|-----------------|
| Av | Ja | AlleOn |
| AlleOn | Ja | Auto |
| Auto | Ja | Manuell |
| Manuell (steg < siste) | Ja | Manuell (neste steg) |
| Manuell (siste steg) | Ja | Av |

---

### Debug-avbrudd

| Hendelse | Effekt |
|------|--------|
| PBA eller PBB trykket under debug | Debug-modus avsluttes umiddelbart |
| Debug avbrutt | Retning endres **ikke** ved samme trykk |

---

### Oppstart- / Reset-tilstand

| Betingelse ved oppstart | Resulterende tilstand |
|----------------------|-----------------|
| Debug-knapp ikke holdt | Retning gjenopprettet fra EEPROM |
| Debug-knapp holdt | Retning tvunget til **A → B**, EEPROM tilbakestilt |

---

### Oppsummerende regler

- Retning blir **eksplisitt satt**, aldri vekslet
- PBA velger alltid **A → B**
- PBB velger alltid **B → A**
- Trykknapper er **flanketriggert - high -> low**
- Brytere og togdeteksjon er **nivåbasert**
- Tog-tilstedeværelse overstyrer all annen logikk
- Debug-modi overstyrer lampeutganger men **endrer ikke retning**
- EEPROM-reset gjenoppretter alltid en **kjent, sikker retning**

---

## Kontroller

- Kort: **Arduino Uno-kompatibel (GeekCreit)**
- MCU: **ATmega328P**
- Klokke: **16 MHz**
- EEPROM: **1 KB (on-chip)**

---

## Strømforsyning

- Systemforsyning: **+10–12 V**
- Logikkforsyning: **Buck-regulator → 5,0–5,1 V**
- Arduino strømforsynt via **5V-pinne**
- VIN brukes ikke
- Felles jord for logikk og utganger

---

## Innganger (Alle via optokoblere)

- Optokobler: **PC817 / EL817**
- Inngangsspenning: **10–12 V**
- LED-seriemotstand: **3,3 kΩ**
- Typisk LED-strøm: **~2–3 mA**
- Arduino-side: `INPUT_PULLUP`
- Logikknivå: **LOW = aktiv**

### Inngangsfunksjoner og pinner

| Funksjon | Arduino Pin | Kort-merking |
|--------|-------------|-------------|
| Togdeteksjon | D2 | 2 |
| Trykknapp A | D3 | 3 |
| Trykknapp B | D4 | 4 |
| Bryter A lukket | D5 | 5 |
| Bryter B lukket | D6 | 6 |
| Debug-knapp | A4 | A4 |
| Seriell-aktiver-jumper | A5 | A5 |

---

## Utganger (Diskrete transistordrivere)

- Topologi: **NPN low-side-svitsjing**
- Transistor: **BC547 / BC337 / 2N2222**
- Basemotstand: **4,7 kΩ**
- Base pull-down: **100 kΩ (base → emitter)**
- Last: **Signal-LEDer**
- LED-forsyning: **+10 V**
- LED-strøm: **10 mA**

### LED-seriemotstand

- Nominell verdi: **680 Ω**
- Akseptabelt område: **680–820 Ω**
- Effektklasse: **¼ W**

### Utgangsfunksjoner og pinner

| Signal | Arduino Pin | Kort-merking |
|------|-------------|-------------|
| Signal A – Rød | D7 | 7 |
| Signal A – Grønn 1 | D9 | 9 |
| Signal A – Grønn 2 | D8 | 8 |
| Signal B – Rød | D10 | 10 |
| Signal B – Grønn 1 | D12 | 12 |
| Signal B – Grønn 2 | D11 | 11 |

- Logikk: **Arduino HIGH = LED PÅ**
- Hardware-inversjon håndtert av transistor

---

## Debug-LEDer (5 V logikk)

- Active-high
- Direktedrift fra Arduino-pinne
- Motstand: **1 kΩ**
- LED-strøm: **~3 mA**

### Debug-LED pinner og betydning

| Debug-LED | Arduino Pin | LED PÅ betyr |
|----------|-------------|--------------|
| Retning (Q) | A0 | Signal **B** er den aktive grønne enden (`g_stateQ = true`) |
| Tog | A1 | Tog detektert i blokken |
| A_Gen | A2 | Signal A tillatt å vise grønt |
| B_Gen | A3 | Signal B tillatt å vise grønt |

---

## Debug-knapp atferd

- Knapp koblet til **A4 → GND**
- `INPUT_PULLUP`
- Active-low

### Debug-tilstandssekvens

1. ALL_ON (=1) **Alle signal-LEDer PÅ**
2. CYCLE (=2) **Automatisk syklus** (1 s per LED):
   A_G1 → A_G2 → A_R → B_G1 → B_G2 → B_R
3. MAN_0 (=3), MAN_1 (=4) ... MAN_5 (=8) **Manuelt steg** (ett trykk per LED, samme rekkefølge)
4. OFF (=0) (Ved tilstandsinngang: Blink alle 3 ganger, 0,5 sek mellom, deretter avslutt debug) **Avslutt debug → normal drift**

- Ethvert trykk på **signalknapp A eller B**:
  - Avbryter debug umiddelbart
  - Endrer **ikke** signaltilstand ved samme trykk

---

## Normal signallogikk

- Tilstandsvariabel: `g_stateQ`
  - `false` = A er grønn ende
  - `true`  = B er grønn ende
- Oppstartstilstand:
  - Gjenopprettet fra EEPROM
- Tog til stede:
  - Begge signaler røde
- Trykknapp A eller B:
  - Endrer `g_stateQ` ved **trykkflanke**
  - Ignorert hvis tog til stede
- Bryter A eller B lukket:
  - Grønn 1 for A eller B er På
- Bryter A eller B åpen:
  - Begge Grønne for A eller B er på.

- Når et tog er til stede, går begge signaler til Rød. Men tilstanden skal lagres før dette.
- Når tog ikke lenger er til stede, skal tilstanden gjenopprettes.

---

## EEPROM-bruk

- EEPROM-størrelse: **1024 bytes**
- Adressekart:
  - **Adresse 0**: `g_stateQ`
- Skrivemetode:
  - `EEPROM.update()` (skriver kun ved verdiendring)
- Skrivehendelser:
  - Kun når `g_stateQ` endres
- EEPROM skrives **ikke** under debug-modi

---

## EEPROM-reset / Gjenopprettingsprosedyre

### Formål

Gjenopprette kontrolleren til en **kjent, sikker tilstand** hvis atferden blir ugyldig.

### Reset-mekanisme

- Reset utføres ved **strømsykling**
- Ingen dedikert reset-jumper nødvendig

### Reset-steg

1. Slå **AV** kortet med **JP2**
2. **Trykk og hold** **Debug-knappen (A4)**
3. Slå **PÅ**
4. Slipp Debug-knappen etter oppstart

### Reset-effekt

- `g_stateQ` tvinges til **false**
- Signal **A** blir den aktive grønne enden
- EEPROM-adresse `0` oppdateres tilsvarende

---

## Seriell debug-utgang

- Baudrate: **115200**
- Status skrives ut én gang per sekund
- Kontrollert av jumper på **A5**
  - Jumper installert (LOW): Seriell aktivert
  - Jumper fjernet (HIGH): Seriell deaktivert

---

## Kablingsmerknader

- Inngangskabellengder: **2–5 m**
- Optokoblere påkrevd på alle innganger
- Valgfritt: **100 nF kondensator** over opto-LED for støyende linjer
- Unngå å bruke pinner **D0/D1**
- Pinne **D13** ubrukt (innebygd LED tilgjengelig)

---
