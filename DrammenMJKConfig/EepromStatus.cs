namespace DrammenMJKConfig;

internal static class EepromStatus
{
    internal static void Print(ArduinoDevice arduino)
    {
        Console.WriteLine("=== EEPROM Status ===");

        int motorConfigured = 0, switchConfigured = 0, ledConfigured = 0;

        for (char k = arduino.FirstPens; k <= arduino.LastPens; k++)
        {
            int i = k - arduino.FirstPens;
            byte pair    = arduino.EepromRead(ArduinoDevice.Region1Base  + i);
            byte pol     = arduino.EepromRead(ArduinoDevice.Region1Pol   + i);
            byte fbRett  = arduino.EepromRead(ArduinoDevice.Region2Rett  + i);
            byte fbAvvik = arduino.EepromRead(ArduinoDevice.Region2Avvik + i);
            byte sw      = arduino.EepromRead(ArduinoDevice.Region3Base  + i);
            byte ledR    = arduino.EepromRead(ArduinoDevice.Region4Rett  + i);
            byte ledA    = arduino.EepromRead(ArduinoDevice.Region4Avvik + i);

            bool motorOk  = pair != ArduinoDevice.Unset;
            bool switchOk = sw   != ArduinoDevice.Unset;
            bool ledOk    = ledR != ArduinoDevice.Unset && ledA != ArduinoDevice.Unset;

            if (motorOk)  motorConfigured++;
            if (switchOk) switchConfigured++;
            if (ledOk)    ledConfigured++;

            string polStr   = pol == ArduinoDevice.Unset ? "?" : pol.ToString();
            string motorStr = motorOk
                ? $"pair={pair} pol={polStr}  fb={DecodePin(fbRett)}/{DecodePin(fbAvvik)}"
                : "not set";
            string switchStr = switchOk ? DecodePin(sw) : "not set";
            string ledRStr = ledR == ArduinoDevice.LedNoPin ? "none"
                           : ledR == ArduinoDevice.Unset    ? "?"
                           : ledR.ToString();
            string ledAStr = ledA == ArduinoDevice.LedNoPin ? "none"
                           : ledA == ArduinoDevice.Unset    ? "?"
                           : ledA.ToString();
            string ledStr = ledOk ? $"Rett={ledRStr} Avvik={ledAStr}" : "not set";

            Console.WriteLine($"  Pens {k}:  motor={motorStr}  switch={switchStr}  led={ledStr}");
        }

        byte drPair = arduino.EepromRead(ArduinoDevice.Region5MotorPin);
        byte drPol  = arduino.EepromRead(ArduinoDevice.Region5MotorPol);
        byte drCw   = arduino.EepromRead(ArduinoDevice.Region5SwCw);
        byte drCcw  = arduino.EepromRead(ArduinoDevice.Region5SwCcw);
        byte moment = arduino.EepromRead(ArduinoDevice.Region5Moment);

        string drPolStr = drPol == ArduinoDevice.Unset ? "?" : drPol.ToString();
        string drStr = drPair != ArduinoDevice.Unset
            ? $"pair={drPair} pol={drPolStr}  sw CW={DecodePin(drCw)} CCW={DecodePin(drCcw)}"
            : "not configured";
        string momentStr = moment != ArduinoDevice.Unset ? DecodePin(moment) : "not set";

        Console.WriteLine($"  Dreieskive:    {drStr}");
        Console.WriteLine($"  Moment button: {momentStr}");
        Console.WriteLine();

        int numPens = arduino.NumPens;
        Console.WriteLine($"  Motors:   {motorConfigured}/{numPens}");
        Console.WriteLine($"  Switches: {switchConfigured}/{numPens}");
        Console.WriteLine($"  LEDs:     {ledConfigured}/{numPens}");

        int stride = 1 + RoutingMatrixSession.MaxConditions * 2;
        bool hasRouting = false;
        for (int i = 0; i < RoutingMatrixSession.LedCount && !hasRouting; i++)
            hasRouting = arduino.EepromRead(ArduinoDevice.Region6Base + i * stride) != ArduinoDevice.Unset;
        Console.WriteLine($"  Routing:  {(hasRouting ? "stored" : "not set")}");
    }

    // Decode an encoded hardware pin byte to a human-readable label.
    // Encoding (matches Firmware.ino):
    //   bit7=1 → Arduino direct pin (D0–D13, A0–A5)
    //   bit7=0 → MCP23017: bits[6:5]=chip (0=U7@0x20, 1=U2@0x21, 2=U1@0x22, 3=U5@0x25)
    //                       bit[4]=port (0=A, 1=B), bits[3:0]=bit 0–7
    internal static string DecodePin(byte enc)
    {
        if (enc == ArduinoDevice.Unset)    return "?";
        if (enc == ArduinoDevice.LedNoPin) return "none";

        if ((enc & 0x80) != 0)
        {
            int pin = enc & 0x7F;
            return pin >= 14 ? $"A{pin - 14}" : $"D{pin}";
        }

        int chip = (enc >> 5) & 0x03;
        int port = (enc >> 4) & 0x01;
        int bit  =  enc       & 0x0F;
        int[]    addrs = { 0x20, 0x21, 0x22, 0x25 };
        string[] names = { "U7", "U2", "U1", "U5" };
        char portChar = port == 0 ? 'A' : 'B';
        return $"{names[chip]}(0x{addrs[chip]:X2}):{portChar}{bit}";
    }
}
