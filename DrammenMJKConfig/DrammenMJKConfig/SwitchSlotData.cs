namespace DrammenMJKConfig;

// Raw EEPROM switch-slot fields, read/written as one unit. Shared by
// SwitchTable (display) and SwitchEditSession (fix mislabeling/polarity
// mistakes without a physical re-scan) -- EEPROM stays authoritative, this
// is just a typed view of it, same fields as ArduinoDevice's RegionSlot*.
readonly record struct SwitchSlotData(
    byte MotorVAddr, byte MotorBit, byte Polarity,
    byte FeedbackVAddr, byte FeedbackRett, byte FeedbackAvvik)
{
    public bool IsConfigured => MotorVAddr != ArduinoDevice.Unset;

    public static SwitchSlotData Read(ArduinoDevice arduino, int slot) => new(
        arduino.EepromRead(ArduinoDevice.RegionSlotMotorVAddr    + slot),
        arduino.EepromRead(ArduinoDevice.RegionSlotMotorBit      + slot),
        arduino.EepromRead(ArduinoDevice.RegionSlotPolarity      + slot),
        arduino.EepromRead(ArduinoDevice.RegionSlotFeedbackVAddr + slot),
        arduino.EepromRead(ArduinoDevice.RegionSlotFeedbackRett  + slot),
        arduino.EepromRead(ArduinoDevice.RegionSlotFeedbackAvvik + slot));

    public void Write(ArduinoDevice arduino, int slot)
    {
        arduino.EepromWrite(ArduinoDevice.RegionSlotMotorVAddr    + slot, MotorVAddr);
        arduino.EepromWrite(ArduinoDevice.RegionSlotMotorBit      + slot, MotorBit);
        arduino.EepromWrite(ArduinoDevice.RegionSlotPolarity      + slot, Polarity);
        arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackVAddr + slot, FeedbackVAddr);
        arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackRett  + slot, FeedbackRett);
        arduino.EepromWrite(ArduinoDevice.RegionSlotFeedbackAvvik + slot, FeedbackAvvik);
    }

    // Builds/removes the SystemConfig.json mirror entry for this slot's data,
    // keeping the JSON file consistent with whatever EEPROM (authoritative)
    // now holds after an edit.
    public void ApplyTo(SystemConfigFile file, string label)
    {
        if (!IsConfigured) { file.Switches.Remove(label); return; }
        file.Switches[label] = new SwitchEntry
        {
            MotorVAddr = $"0x{MotorVAddr:X2}",
            MotorBit = MotorBit,
            Polarity = Polarity,
            FeedbackVAddr = $"0x{FeedbackVAddr:X2}",
            FeedbackRettBit = FeedbackRett,
            FeedbackAvvikBit = FeedbackAvvik,
        };
    }
}
