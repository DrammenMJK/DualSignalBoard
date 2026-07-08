using System.Text.Json;

namespace DrammenMJKConfig;

static class ConfigSession
{
    public static void Run(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("=== Config Mode ===");
        arduino.Send('C');
        Thread.Sleep(200); // allow Arduino banner to arrive before our menu
        Console.WriteLine();
        PrintMenu();

        while (true)
        {
            Console.Write("Config> ");
            char cmd = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
            Console.WriteLine(cmd);

            switch (cmd)
            {
                case '1': MotorScan(arduino);        break;
                case '2': SwitchMapping(arduino);    break;
                case '3': LedMapping(arduino);       break;
                case 'M': MomentSwitch(arduino);     break;
                case 'D': DreieskiveSwitch(arduino); break;
                case '4': RoutingMatrix(arduino);    break;
                case 'R': ResetConfig(arduino);      break;
                case 'Q':
                    arduino.Send('Q');
                    Console.WriteLine("Exiting config mode.");
                    Console.WriteLine();
                    return;
                default:
                    PrintMenu();
                    break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Command 1: Motor scan
    // -------------------------------------------------------------------------
    static void MotorScan(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 1: Motor Scan ---");
        Console.WriteLine("Phase A — finds the Dreieskive motor (the one with no feedback switches).");
        Console.WriteLine("Phase B — discovers Pens motors by watching which feedback switches change.");
        Console.WriteLine();
        Console.WriteLine("Keys during scan:");
        Console.WriteLine("  Y — Correct / confirm");
        Console.WriteLine("  N — Skip to next motor");
        Console.WriteLine("  R — Pens is currently at Rett");
        Console.WriteLine("  A — Pens is currently at Avvik");
        Console.WriteLine("  1 — '01' bit pattern is CW  (Dreieskive polarity)");
        Console.WriteLine("  2 — '10' bit pattern is CW  (Dreieskive polarity)");
        Console.WriteLine("  X — Emergency stop all motors");
        Console.WriteLine("  Q — Finished, return to config menu");
        Console.WriteLine();
        Prompt("Press Enter to begin motor scan...");

        arduino.Send('1');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Command 2: Manual switch → Pens mapping
    // -------------------------------------------------------------------------
    static void SwitchMapping(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 2: Manual Switch Mapping ---");
        Console.WriteLine("Move ALL manual panel switches to the Rett position, then press Enter.");
        Prompt();

        Console.WriteLine("For each Pens:");
        Console.WriteLine("  Enter B–I to select a Pens to configure.");
        Console.WriteLine("  Flip its switch to Avvik and back to Rett when Arduino prompts.");
        Console.WriteLine("  Y = correct switch detected   N = retry");
        Console.WriteLine("  Y/N during motor cycle to confirm which Pens moves.");
        Console.WriteLine("  R = Rett / A = Avvik to set polarity.");
        Console.WriteLine("  Q = finished.");
        Console.WriteLine();

        arduino.Send('2');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Command 3: LED mapping
    // -------------------------------------------------------------------------
    static void LedMapping(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 3: LED Mapping ---");
        Console.WriteLine("All Penser should be at their Rett position (run Command 2 first).");
        Console.WriteLine();
        Console.WriteLine("  B–I — select Pens to configure");
        Console.WriteLine("  N   — next LED");
        Console.WriteLine("  P   — previous LED");
        Console.WriteLine("  S   — save this LED for the current position (Rett then Avvik)");
        Console.WriteLine("  Q   — finished");
        Console.WriteLine();

        arduino.Send('3');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Command M: Moment (signal) button
    // -------------------------------------------------------------------------
    static void MomentSwitch(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command M: Moment Button Detection ---");
        Console.WriteLine("When the Arduino prompts, press the physical moment signal button.");
        Console.WriteLine("  Y — detected input is correct");
        Console.WriteLine("  N — retry");
        Console.WriteLine("  Q — done");
        Console.WriteLine();

        arduino.Send('M');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Command D: Dreieskive (turntable) switch
    // -------------------------------------------------------------------------
    static void DreieskiveSwitch(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command D: Dreieskive Switch Configuration ---");
        Console.WriteLine("The Dreieskive switch has three positions: middle, CW, CCW.");
        Console.WriteLine();
        Console.WriteLine("Step 1 — Move the Dreieskive switch to the MIDDLE position, then press Enter.");
        Prompt();

        Console.WriteLine("Step 2 — When Arduino prompts, move the switch to CW (clockwise).");
        Console.WriteLine("Step 3 — Move back to middle, then to CCW (counter-clockwise).");
        Console.WriteLine("  Y — confirm / continue");
        Console.WriteLine("  Q — done");
        Console.WriteLine();

        arduino.Send('D');
        Relay(arduino);
    }

    // -------------------------------------------------------------------------
    // Command 4: LED routing matrix upload / download
    // -------------------------------------------------------------------------
    static void RoutingMatrix(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 4: LED Routing Matrix ---");
        Console.WriteLine("Controls which switch conditions must be met before an indicator LED lights.");
        Console.WriteLine("Stored in EEPROM; the C# program loads a site-specific JSON file.");
        Console.WriteLine();
        Console.WriteLine("  L — Load JSON file and upload routing matrix to Arduino");
        Console.WriteLine("  S — Download routing matrix from Arduino and save to JSON file");
        Console.WriteLine("  ? — Show JSON file format and example");
        Console.WriteLine("  Q — Done");
        Console.WriteLine();

        arduino.Send('4');
        Thread.Sleep(200);

        while (true)
        {
            Console.Write("Routing> ");
            char cmd = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
            Console.WriteLine(cmd);

            switch (cmd)
            {
                case 'L': LoadAndUpload(arduino);    break;
                case 'S': DownloadAndSave(arduino);  break;
                case '?': PrintRoutingFormat();      break;
                case 'Q':
                    arduino.Send('Q');
                    Console.WriteLine("Routing matrix done.");
                    Console.WriteLine();
                    return;
                default:
                    Console.WriteLine("L = load+upload   S = download+save   ? = format help   Q = done");
                    break;
            }
        }
    }

    // 16 LEDs in fixed order: B_Rett, B_Avvik, C_Rett, C_Avvik, ... I_Rett, I_Avvik
    static readonly string[] LedNames = [
        "B_Rett","B_Avvik","C_Rett","C_Avvik","D_Rett","D_Avvik",
        "E_Rett","E_Avvik","F_Rett","F_Avvik","G_Rett","G_Avvik",
        "H_Rett","H_Avvik","I_Rett","I_Avvik"
    ];
    const int LedCount = 16;
    const int MaxConditions = 4;

    static void PrintRoutingFormat()
    {
        Console.WriteLine();
        Console.WriteLine("=== Routing Matrix JSON File Format ===");
        Console.WriteLine();
        Console.WriteLine("The file is a JSON object with an optional 'site' label and a 'routing' map.");
        Console.WriteLine("Each entry in 'routing' names one LED and lists the conditions that light it.");
        Console.WriteLine("An LED not listed here uses simple 1-to-1 mapping (lights when its own Pens");
        Console.WriteLine("is in the matching position).");
        Console.WriteLine();
        Console.WriteLine("Valid LED names:  " + string.Join("  ", LedNames));
        Console.WriteLine("Valid Pens letters in conditions:  B  C  D  E  F  G  H  I");
        Console.WriteLine();
        Console.WriteLine("Each condition has two optional arrays:");
        Console.WriteLine("  'rett'  — Pens letters that must be in Rett  position for this condition");
        Console.WriteLine("  'avvik' — Pens letters that must be in Avvik position for this condition");
        Console.WriteLine("The LED lights if ANY condition is fully satisfied (OR of AND-chains).");
        Console.WriteLine($"Maximum {MaxConditions} conditions per LED.");
        Console.WriteLine();
        Console.WriteLine("--- Example file (save as e.g. Fossli.json, Sentrum.json, ...) ---");
        Console.WriteLine();
        Console.WriteLine("""
{
  "site": "Fossli",
  "routing": {
    "G_Rett": [
      { "rett": ["G","H","F"], "avvik": ["E"] },
      { "rett": ["G","H"],     "avvik": ["F"] }
    ],
    "H_Rett": [
      { "rett": ["G","H","F"], "avvik": ["E"] },
      { "rett": ["H"],         "avvik": ["F"] }
    ],
    "F_Rett": [
      { "rett": ["G","H","F"], "avvik": ["E"] }
    ],
    "F_Avvik": [
      { "rett": ["G","H"], "avvik": ["F"] }
    ],
    // E_Avvik shares the same physical LED as F_Avvik.
    // Suppress it so only F_Avvik's conditions above drive the shared pin.
    "E_Avvik": []
  }
}
""");
        Console.WriteLine("LEDs absent from 'routing' use default 1-to-1 mapping (light when own Pens");
        Console.WriteLine("is in the matching position).");
        Console.WriteLine();
        Console.WriteLine("To suppress an LED entirely (shared physical pin, driven by another LED):");
        Console.WriteLine("  \"E_Avvik\": []");
        Console.WriteLine("This marks E_Avvik as always-off; the shared physical LED is driven only");
        Console.WriteLine("by F_Avvik's conditions.");
        Console.WriteLine();
        Console.WriteLine("The file may use any name; use a different file for each site.");
        Console.WriteLine("Trailing commas and // comments are allowed.");
        Console.WriteLine();
    }

    // Load a site-specific JSON file and upload the routing matrix to Arduino.
    static void LoadAndUpload(ArduinoConnection arduino)
    {
        Console.WriteLine("Enter the path to the JSON routing file for this site.");
        Console.WriteLine("Use a different file name for each site (e.g. Fossli.json, Sentrum.json).");
        Console.WriteLine("Press ? for format help.");
        Console.Write("File path: ");
        string? path = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("No path entered.");
            Console.WriteLine();
            return;
        }
        if (path == "?") { PrintRoutingFormat(); return; }
        if (!File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            Console.WriteLine();
            return;
        }

        string? siteName = ReadSiteName(path);
        if (siteName != null)
            Console.WriteLine($"Site: {siteName}");

        byte[]? matrix = ParseRoutingJson(path);
        if (matrix == null) return;

        int customCount = 0;
        for (int i = 0; i < LedCount; i++)
            if (matrix[i * (1 + MaxConditions * 2)] > 0) customCount++;

        Console.WriteLine($"Parsed OK: {customCount} of {LedCount} LEDs have custom routing conditions.");
        Console.Write("Upload to Arduino? (Y/N): ");
        if (char.ToUpper(Console.ReadKey(intercept: true).KeyChar) != 'Y')
        {
            Console.WriteLine("\nCancelled.");
            Console.WriteLine();
            return;
        }
        Console.WriteLine();

        arduino.StartCapture();
        bool uploadStarted = false;
        try
        {
            arduino.Send('U');
            string? ack = arduino.GetCapturedLine(1500);
            if (ack?.Trim() != "READY")
            {
                Console.WriteLine($"Arduino did not acknowledge (got: '{ack}'). Aborting.");
                return;
            }

            uploadStarted = true;

            for (int i = 0; i < LedCount; i++)
            {
                string line = BuildMatrixLine(matrix, i);
                arduino.SendLine(line);
                ack = arduino.GetCapturedLine(500);
                if (ack?.Trim() != "OK")
                {
                    Console.WriteLine($"Error at LED {i} ({LedNames[i]}): '{ack}'. Aborting upload.");
                    arduino.SendLine("ABORT");
                    return;
                }
            }

            arduino.SendLine("END");
            ack = arduino.GetCapturedLine(1500);
            bool ok = ack?.Contains("STORED", StringComparison.OrdinalIgnoreCase) == true;
            Console.WriteLine(ok ? "Routing matrix stored successfully." : $"Upload result: {ack}");
            uploadStarted = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Upload error: {ex.Message}");
            if (uploadStarted) arduino.SendLine("ABORT");
        }
        finally
        {
            arduino.StopCapture();
            Console.WriteLine();
        }
    }

    // Download the routing matrix from Arduino and save to a JSON file.
    static void DownloadAndSave(ArduinoConnection arduino)
    {
        Console.Write("Save JSON file to path: ");
        string? path = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("No path given.");
            Console.WriteLine();
            return;
        }

        byte[] matrix = new byte[LedCount * (1 + MaxConditions * 2)];

        arduino.StartCapture();
        try
        {
            arduino.Send('D');
            for (int i = 0; i < LedCount; i++)
            {
                string? line = arduino.GetCapturedLine(2000);
                if (line == null)
                {
                    Console.WriteLine($"Timeout waiting for LED {i} ({LedNames[i]}).");
                    return;
                }
                if (!ParseMatrixLine(line, matrix, i))
                {
                    Console.WriteLine($"Bad data for LED {i} ({LedNames[i]}): '{line}'");
                    return;
                }
            }
            string? end = arduino.GetCapturedLine(1000);
            if (end?.Trim() != "END")
                Console.WriteLine($"Warning: expected END, got '{end}'");
        }
        finally
        {
            arduino.StopCapture();
        }

        try
        {
            string json = MatrixToJson(matrix);
            File.WriteAllText(path, json);
            Console.WriteLine($"Routing matrix saved to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save file: {ex.Message}");
        }
        Console.WriteLine();
    }

    // Read the optional "site" label from a JSON file without full parsing.
    static string? ReadSiteName(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("site", out var site) && site.ValueKind == JsonValueKind.String)
                return site.GetString();
        }
        catch { }
        return null;
    }

    // Parse a JSON routing file into a flat byte array:
    // LedCount × (1 count byte + MaxConditions × 2 mask bytes)
    // Returns null and prints all errors if any validation fails.
    static byte[]? ParseRoutingJson(string path)
    {
        int stride = 1 + MaxConditions * 2;
        byte[] matrix = new byte[LedCount * stride];
        // All bytes = 0xFF: count 0xFF = default 1-to-1, condition slots 0xFF = unused.
        // An LED not mentioned in the JSON keeps count=0xFF (default).
        for (int i = 0; i < matrix.Length; i++) matrix[i] = 0xFF;

        // --- File read ---
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { Console.WriteLine($"Cannot read file: {ex.Message}"); return null; }

        // --- JSON syntax ---
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { Console.WriteLine($"JSON syntax error: {ex.Message}"); return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"JSON root must be an object, got {root.ValueKind}.");
                return null;
            }
            if (!root.TryGetProperty("routing", out var routing))
            {
                Console.WriteLine("JSON missing required 'routing' property.");
                return null;
            }
            if (routing.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"'routing' must be a JSON object, got {routing.ValueKind}.");
                return null;
            }

            int errorCount = 0;

            foreach (var ledProp in routing.EnumerateObject())
            {
                string ledName = ledProp.Name;
                int ledIdx = Array.IndexOf(LedNames, ledName);
                if (ledIdx < 0)
                {
                    Console.WriteLine($"  Warning: Unknown LED name '{ledName}' (valid names: {string.Join(", ", LedNames)}) — skipping.");
                    continue;
                }
                if (ledProp.Value.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine($"  Error [{ledName}]: value must be an array of conditions, got {ledProp.Value.ValueKind}.");
                    errorCount++;
                    continue;
                }

                var condElems = ledProp.Value.EnumerateArray().ToList();
                if (condElems.Count > MaxConditions)
                {
                    Console.WriteLine($"  Warning [{ledName}]: {condElems.Count} conditions exceed maximum {MaxConditions}; truncating.");
                    condElems = condElems.Take(MaxConditions).ToList();
                }
                if (condElems.Count == 0)
                {
                    // Explicitly present with [] → suppressed: LED never lights independently.
                    // Used for shared physical LEDs where the other logical LED drives the pin.
                    matrix[ledIdx * stride] = 0x00;
                    Console.WriteLine($"  Info [{ledName}]: empty condition list — LED is suppressed (always off). The shared physical pin will still light via the other LED's conditions.");
                    continue;
                }

                int validConditions = 0;
                bool ledHasError = false;

                for (int c = 0; c < condElems.Count; c++)
                {
                    string loc = $"{ledName} condition {c}";

                    if (condElems[c].ValueKind != JsonValueKind.Object)
                    {
                        Console.WriteLine($"  Error [{loc}]: must be an object with 'rett' and/or 'avvik' arrays, got {condElems[c].ValueKind}.");
                        errorCount++;
                        ledHasError = true;
                        continue;
                    }

                    byte rettMask = 0, avvikMask = 0;
                    bool condHasError = false;

                    if (condElems[c].TryGetProperty("rett", out var rettEl))
                    {
                        if (rettEl.ValueKind != JsonValueKind.Array)
                        {
                            Console.WriteLine($"  Error [{loc}]: 'rett' must be an array of Pens letters, got {rettEl.ValueKind}.");
                            errorCount++;
                            condHasError = true;
                        }
                        else
                        {
                            foreach (var el in rettEl.EnumerateArray())
                            {
                                if (el.ValueKind != JsonValueKind.String)
                                {
                                    Console.WriteLine($"  Error [{loc}]: 'rett' array elements must be strings, got {el.ValueKind}.");
                                    errorCount++;
                                    condHasError = true;
                                    break;
                                }
                                string letter = el.GetString()!;
                                byte bit = ParsePensLetter(letter);
                                if (bit == 0)
                                {
                                    Console.WriteLine($"  Error [{loc}]: '{letter}' is not a valid Pens letter (must be B–I).");
                                    errorCount++;
                                    condHasError = true;
                                    break;
                                }
                                rettMask |= bit;
                            }
                        }
                    }

                    if (!condHasError && condElems[c].TryGetProperty("avvik", out var avvikEl))
                    {
                        if (avvikEl.ValueKind != JsonValueKind.Array)
                        {
                            Console.WriteLine($"  Error [{loc}]: 'avvik' must be an array of Pens letters, got {avvikEl.ValueKind}.");
                            errorCount++;
                            condHasError = true;
                        }
                        else
                        {
                            foreach (var el in avvikEl.EnumerateArray())
                            {
                                if (el.ValueKind != JsonValueKind.String)
                                {
                                    Console.WriteLine($"  Error [{loc}]: 'avvik' array elements must be strings, got {el.ValueKind}.");
                                    errorCount++;
                                    condHasError = true;
                                    break;
                                }
                                string letter = el.GetString()!;
                                byte bit = ParsePensLetter(letter);
                                if (bit == 0)
                                {
                                    Console.WriteLine($"  Error [{loc}]: '{letter}' is not a valid Pens letter (must be B–I).");
                                    errorCount++;
                                    condHasError = true;
                                    break;
                                }
                                avvikMask |= bit;
                            }
                        }
                    }

                    if (condHasError) { ledHasError = true; continue; }

                    // Logic checks
                    byte contradiction = (byte)(rettMask & avvikMask);
                    if (contradiction != 0)
                    {
                        Console.WriteLine($"  Error [{loc}]: Pens {string.Join(", ", MaskToPens(contradiction))} appear in both 'rett' and 'avvik' — condition can never be satisfied.");
                        errorCount++;
                        ledHasError = true;
                        continue;
                    }
                    if (rettMask == 0 && avvikMask == 0)
                    {
                        Console.WriteLine($"  Error [{loc}]: both 'rett' and 'avvik' are empty — this condition is always true, making the LED permanently lit. Add at least one Pens requirement.");
                        errorCount++;
                        ledHasError = true;
                        continue;
                    }

                    int offset = ledIdx * stride + 1 + validConditions * 2;
                    matrix[offset]     = rettMask;
                    matrix[offset + 1] = avvikMask;
                    validConditions++;
                }

                if (ledHasError)
                    matrix[ledIdx * stride] = 0xFF; // reset to default; do not store partial conditions
                else
                    matrix[ledIdx * stride] = (byte)validConditions;
            }

            if (errorCount > 0)
            {
                Console.WriteLine($"\n{errorCount} error(s) found — file not uploaded.");
                return null;
            }
        }

        return matrix;
    }

    // Parse a single Pens letter (B–I) to its bitmask. Returns 0 for invalid input.
    static byte ParsePensLetter(string? letter)
    {
        if (string.IsNullOrEmpty(letter) || letter.Length != 1) return 0;
        int bit = char.ToUpper(letter[0]) - 'B';
        if (bit < 0 || bit > 7) return 0;
        return (byte)(1 << bit);
    }

    // Convert the flat matrix back to a human-readable JSON string.
    static string MatrixToJson(byte[] matrix)
    {
        int stride = 1 + MaxConditions * 2;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"routing\": {");

        bool firstLed = true;
        for (int i = 0; i < LedCount; i++)
        {
            byte count = matrix[i * stride];
            if (count == 0xFF) continue; // default 1-to-1 — not written to JSON

            if (!firstLed) sb.AppendLine(",");
            firstLed = false;
            sb.Append($"    \"{LedNames[i]}\": [");

            if (count == 0x00)
            {
                // Suppressed — emit empty array
                sb.Append("]");
                continue;
            }

            for (int c = 0; c < count; c++)
            {
                int offset = i * stride + 1 + c * 2;
                byte rettMask  = matrix[offset];
                byte avvikMask = matrix[offset + 1];

                if (c > 0) sb.Append(", ");
                sb.Append("{ \"rett\": [");
                sb.Append(string.Join(", ", MaskToPens(rettMask).Select(p => $"\"{p}\"")));
                sb.Append("], \"avvik\": [");
                sb.Append(string.Join(", ", MaskToPens(avvikMask).Select(p => $"\"{p}\"")));
                sb.Append("] }");
            }
            sb.Append("]");
        }

        sb.AppendLine();
        sb.AppendLine("  }");
        sb.Append("}");
        return sb.ToString();
    }

    // Build the wire-format line for one LED: "CC [RR AA ...]"
    // CC = hex count byte: FF=default 1-to-1, 00=suppressed, 01-04=conditions.
    static string BuildMatrixLine(byte[] matrix, int ledIdx)
    {
        int stride = 1 + MaxConditions * 2;
        byte count = matrix[ledIdx * stride];
        if (count == 0xFF) return "FF"; // default 1-to-1
        if (count == 0x00) return "00"; // suppressed

        var parts = new List<string> { count.ToString("X2") };
        for (int c = 0; c < count; c++)
        {
            int offset = ledIdx * stride + 1 + c * 2;
            parts.Add(matrix[offset].ToString("X2"));
            parts.Add(matrix[offset + 1].ToString("X2"));
        }
        return string.Join(" ", parts);
    }

    // Parse a wire-format line "N RR AA [RR AA ...]" into the matrix buffer at ledIdx.
    static bool ParseMatrixLine(string line, byte[] matrix, int ledIdx)
    {
        int stride = 1 + MaxConditions * 2;
        var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        // Count byte is hex: FF=default, 00=suppressed, 01-04=conditions.
        if (!byte.TryParse(tokens[0], System.Globalization.NumberStyles.HexNumber, null, out byte count))
            return false;
        if (count != 0xFF && count != 0x00 && count > MaxConditions)
            return false;

        matrix[ledIdx * stride] = count;
        if (count == 0xFF || count == 0x00) return true; // no condition bytes follow
        if (tokens.Length < 1 + count * 2) return false;

        for (int c = 0; c < count; c++)
        {
            int offset = ledIdx * stride + 1 + c * 2;
            if (!byte.TryParse(tokens[1 + c * 2],     System.Globalization.NumberStyles.HexNumber, null, out matrix[offset]))     return false;
            if (!byte.TryParse(tokens[1 + c * 2 + 1], System.Globalization.NumberStyles.HexNumber, null, out matrix[offset + 1])) return false;
        }
        return true;
    }

    // Enumerate which Pens letters (B–I) have their bit set in a mask.
    static IEnumerable<string> MaskToPens(byte mask)
    {
        for (int bit = 0; bit < 8; bit++)
            if ((mask & (1 << bit)) != 0)
                yield return ((char)('B' + bit)).ToString();
    }

    // -------------------------------------------------------------------------
    // Command R: Reset (clear) all stored config from EEPROM
    // -------------------------------------------------------------------------
    static void ResetConfig(ArduinoConnection arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command R: Reset Configuration ---");
        Console.WriteLine("WARNING: This will erase ALL stored configuration from EEPROM.");
        Console.WriteLine("Motor mappings, switch mappings, LED mappings and polarity will all be lost.");
        Console.WriteLine("The direction state (signal logic) is NOT affected.");
        Console.WriteLine();
        Console.Write("Type Y to confirm reset, any other key to cancel: ");
        char confirm = char.ToUpper(Console.ReadKey(intercept: true).KeyChar);
        Console.WriteLine(confirm);

        if (confirm != 'Y')
        {
            Console.WriteLine("Reset cancelled.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("Sending reset command to Arduino...");
        arduino.Send('R');
        Thread.Sleep(500); // allow Arduino to complete EEPROM erase and print confirmation
        Console.WriteLine("Reset complete. Run Command 1, 2, 3, M and D to reconfigure.");
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static void PrintMenu()
    {
        Console.WriteLine("  1 — Motor scan  (find Dreieskive + all Pens motors)");
        Console.WriteLine("  2 — Manual switch → Pens mapping");
        Console.WriteLine("  3 — LED mapping");
        Console.WriteLine("  4 — LED routing matrix (upload from JSON / download to JSON)");
        Console.WriteLine("  M — Moment (signal) button");
        Console.WriteLine("  D — Dreieskive switch");
        Console.WriteLine("  R — Reset: erase all config from EEPROM");
        Console.WriteLine("  Q — Exit config mode");
        Console.WriteLine();
    }

    // Relay keypresses to Arduino until Q (or Escape). Echoes each sent key.
    static void Relay(ArduinoConnection arduino)
    {
        Console.WriteLine("(Keystrokes forwarded to Arduino.  Q = return to config menu  X = emergency stop)");
        Console.WriteLine();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            char c = char.ToUpper(key.KeyChar);
            Console.WriteLine($"→ {c}");
            arduino.Send(c);

            if (c == 'Q' || key.Key == ConsoleKey.Escape)
                break;
        }
        Console.WriteLine();
    }

    // Waits for Enter; optional prompt text.
    static void Prompt(string message = "Press Enter to continue...")
    {
        Console.Write(message + " ");
        Console.ReadLine();
    }
}
