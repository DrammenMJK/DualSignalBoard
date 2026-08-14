# Introduction

This is a plan for the extended control system.  THe former had a single controller that controlled only the signal lights.  
The new system will control two (actually three) stillverk (SVB), where we control the switches and see the resuilts using leds. It further controls five different switch control boards (SCB), which controls the switchmotors and receive feedback from them (Tortoise switches).  There will be 5 switch control boards, two stillverk boards, and one combined stillverk and switch control board, and finally a controller board (CB).  These will be distributed through 4 I2C buses, the I2C serial communication will be used for all communication between the boards.

The controller itself (same Arduino as before) will not interface with any hardware  directly, but through an I2C mux which controls the 4 bus'es (0-3).

Later we will also introduce another Arduino connected with a 2x16 display which will be used for monitoring and some small commands.

## Current state

1. There is a first version of the PC DrammenMJKConfig program.  We will continue on this one.
2. THere is a simulator.ino in a Simulator folder.  We can look at this, and possible use it when we dont have hardware around, and need to work with the PC DrammenMJKConfig.
3. There is a Firmaware_FirstAttempt folder with a Firmware.ino file. This is a first attempt for only two SCBs and one SVB covered on a single bus, but it shows the principle.  We will not reuse this, but can have a look if needed.

## Phase 1

This is the testing phase.  The controller will here communicate directly (I2C) with the "Fossli Switch Control Right Side Board" (FCSBR).  It will be at address 0x20.

### Simulator => Firmware

We copy the simulator onto a new folder, called Firmware, and trim off all simulation itself, only keep the communication to the PC, and other layering needed.

The firmware should receive commands from the PC serial link (PSL), and act upon them, and either use them for config and setup, or send them to a switch controller board (SCB) over I2C.

Tasks:

* Add I2C code, prepare for bus if needed, or we adapt that later.
* Implement the protocol for receiving SCB commands from the PSL.

### Switch drivers

The switches are driven by an MCP23017 (and an 74HC14 IC, but that's irrellevant for the communication).
The normal setup is that Port A is reserved for all input, and Port B is for the output.  But in some cases we have to use parts of Port A also for output.

We have wired up the Int A so that we can use that for interrupts to the Arduino.  In this phase we will not implement this.

#### FCSBR content, RIGHT SIDE

FSCBR controls 4 switches and one dreieskive. , and receives feedback from the 4 switches.
It has one red led that indicates that we have proper communication. THis is common for nearly all boards.

The address is 0x20.  It will later be added to Bus 2, and then have a virtual address of 0x40.  In phase 1 we will use 0x20.

This led is controlled by the GPB7 pin (port B, pin 7). The line is pulled high at start, since the pin is an input by default, and the red led will light up.  We will set it to output, and set it low, which will stop the red led, indicating we have managed to communicate with the MCP23017 over the I2C bus.

##### Port B config

Pin 0-3 controls the switch motors.  High and Low indicate the two directions.  We will through config say which is Switch Closed  and which is Switch Open (Avvik).

Pin 4-5 is dedicated to the dreieskive. Dreieskiven has 3 states, stopped (00 or 11), go one way (01), go opposite way (10). The correct direction is determined through config.

##### Port A config

All pins are inputs, There are 4 input values, one per pair, 0-1, 2-3, 4-5, 6-7. Which belongs to which switch is determined through config.  The inputs shall be set to pulled high.

The values are 01 => one side, 10 => the other side, 11 => moving inbetween, 00 => switch is broken/should not happen.

#### FCSBL  Fossli LEFT SIDE

VAddre: 0x21

This board controls 3 switches, and receives feedback from the same 3 switches. In additon it controls the signal lamps for Fossli, we have two of these, so they should be numbered. Each signal has 3 lamps, Green, Red and secondary Green, and use the algorithm we had earlier.  
The letters we use there is now converted to numbers.  Will come back to that.

The input comes from the 3 switches, plus input from track detection, a single bit. We have to configure what high and low means.

##### Port B config Left

Pin 1-4 are motors outputs, but only 1-3 are connected to a real switch motor.
Pin 5-7 are signal leds outputs
Pin 8 is the connectivity warning led output.

##### Port A config Left

Pin 0-5 are switch feedback inputs
Pin 7 is Track Detection

#### Commonality

All SCBs will have similar setup, but the pin assignments will differ.

### DrammenMJKConfig

The DrammenMJKConfig program has currently the following menu:

```csharp
     var menu = new Menu(
         [
             ('1', "Motor scan — find Dreieskive + all Pens motors", () => MotorScan(arduino)),
             ('2', "Manual switch → Pens mapping",                   () => SwitchMapping(arduino)),
             ('3', "LED mapping",                                     () => LedMapping(arduino)),
             ('4', "LED routing matrix (upload/download JSON)",       () => RoutingMatrixSession.Run(arduino)),
             ('M', "Moment button",                                   () => MomentSwitch(arduino)),
             ('D', "Dreieskive switch",                               () => DreieskiveSwitch(arduino)),
             ('R', "Reset: erase all config from EEPROM",            () => ResetConfig(arduino)),
             ('S', "Show EEPROM status",                              () => EepromStatus.Print(arduino)),
         ],
```

This need to be extended, because we need to take into account that we will have multiple SCBs. The program needs to know about these.  For phase 1, we will only need to cover item 1, Motor scan.  We should not try to find dreieskive, as that is preassigned now to a set of pins. So for Phase 1 we only handle finding the motors and connecting them with their feedbacks.

We can also find out closed and open positions, by sending commands to the switch motors and ask the user about positions.  Further, we can assign the correct numbers to these switches which the stillverk can use later.

#### Command mode

So, add a command mode, so we can send direct commands to the switch motors, by giving their number. and if we want them to go closed or open.  That way we can actually operate the switches from the PC.


#### Fossli right side switches

|  Switch numbers |
| -------|
|  1 |
|  3 |
|  5 and 6 |
|  7 |
| 31 (Dreieskive) |

Switches 5 and 6 is special as they are driven together.

## System description

We have 4 buses, 0-3.

Bus 0:  Stillverk

SVB 1:  LedAndSwitchesFossli  address 0x20 and 0x21
SVB 2:  LedAndSwitchesHavna   address 0x22 and 0x23

Virtual addresses the same.

### Buses and boards

Bus 1:  Havna and Vallekilen

Havna SCB   Virtual addresses 0x30 and 0x31
Vallekilen SCB   Virtual addresses 0x32
Vallekilen SVB ,  Virtual address 0x33

Bus 2:   Fossli

Fossli Høyre:  Virtual addresses 0x40
Fossli Venstre:  Virtual address 0x41

Bus 3:  Sidespor

Sidespor:  Virtual address 0x50

Virtual address:    Bus number * 20 + local address

### SVBs and SCBs controls

|SVB| Name | Controls SCBs | Bus |
|---|---|---|
| SVB 1 |  Fossli|  Fossli Høyre SCB, Fossli Venstre SCB | 2 |
| SVB 2 |  Havna |  Havna SCB, Sidespor SCB | 1 and 3 |
| SVB 3 | Vallekilen | Vallekilen SCB  | 1 |

### SVB numbers of switches

For each SVB the switches (motors) have their own number.  An example is the list above for [Fossli] (#fossli-right-side-switches).

These numbers can be used from the config program, and then used in the config setup.  So when we need to change a switch, we will give the either the virtual address and the switch number, or the SVB number (1-3) and the switch number.  The latter would be best, since we can then in the Command mode from the PC use the names of the SVB.

The SVB leds are just numbered 0 to max per SVB, and assigned to the switches during the config. 

## Hardware configuration

The hardware configuration, stored in hardware.json holds information per board on how that board is physically wired.

A board may contain 1 or 2 MCP23017.  The ports may be output or input, and if input may have pull ups.

For a SCB, outputs are :  

1. To motors, one bit in most cases, two bit in a very few cases
2. On board led
3. Signals on track (3 leds), used with dimming up/down.

Inputs are:  

1. Switch feedback
2. Other switches, moment or toggle.
3. Track detection

For a SVB:

Outputs are:

1. Single Leds indicating switch positions
2. May have an output for a motor (combined board, one case of this)
3. Signal indication on SVB

Inputs :

1. Toggle switches on/off
2. Toggle switch on1/off/on2 (Toggle switch with middle position), use two bits
3. Moment button


