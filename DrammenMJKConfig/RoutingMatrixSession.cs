using System.Text.Json;

namespace DrammenMJKConfig;

internal static class RoutingMatrixSession
{
    internal static readonly string[] LedNames = [
        "B_Rett","B_Avvik","C_Rett","C_Avvik","D_Rett","D_Avvik",
        "E_Rett","E_Avvik","F_Rett","F_Avvik","G_Rett","G_Avvik",
        "H_Rett","H_Avvik","I_Rett","I_Avvik"
    ];
    internal const int LedCount      = 16;
    internal const int MaxConditions = 4;

    internal static void Run(ArduinoDevice arduino)
    {
        Console.WriteLine();
        Console.WriteLine("--- Command 4: LED Routing Matrix ---");
        Console.WriteLine("Controls which switch conditions must be met before an indicator LED lights.");
        Console.WriteLine("Stored in EEPROM; the C# program loads a site-specific JSON file.");

        new Menu(
            [
                ('L', "Load JSON file and upload to Arduino",        () => LoadAndUpload(arduino)),
                ('S', "Download from Arduino and save to JSON file", () => DownloadAndSave(arduino)),
                ('?', "Show JSON file format and example",           () => PrintRoutingFormat()),
            ],
            quitOption: ('Q', "Done")
        ).Run();
    }

    // -------------------------------------------------------------------------

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
        Console.WriteLine();
        Console.WriteLine("The file may use any name; use a different file for each site.");
        Console.WriteLine("Trailing commas and // comments are allowed.");
        Console.WriteLine();
    }

    static string? PickJsonFile()
    {
        string searchRoot = Directory.GetCurrentDirectory();

        string[] files = Directory.GetFiles(searchRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"No JSON files found in {searchRoot}.");
            Console.WriteLine();
            return null;
        }

        Console.WriteLine();
        Console.WriteLine($"JSON files in {searchRoot}:");
        for (int i = 0; i < files.Length; i++)
            Console.WriteLine($"  {i + 1}. {Path.GetFileName(files[i])}");
        Console.WriteLine();
        Console.Write($"Select file (1–{files.Length}), or Esc to cancel: ");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine("Esc"); Console.WriteLine(); return null; }

            if (key.KeyChar >= '1' && key.KeyChar <= '9')
            {
                int idx = key.KeyChar - '1';
                if (idx < files.Length)
                {
                    Console.WriteLine(key.KeyChar);
                    return files[idx];
                }
            }

            string soFar = key.KeyChar.ToString();
            Console.Write(soFar);
            string? rest = Console.ReadLine();
            if (int.TryParse(soFar + rest, out int n) && n >= 1 && n <= files.Length)
                return files[n - 1];

            Console.Write($"Invalid — enter 1–{files.Length} or Esc: ");
        }
    }

    static void LoadAndUpload(ArduinoDevice arduino)
    {
        string? path = PickJsonFile();
        if (path == null) return;

        string? siteName = ReadSiteName(path);
        if (siteName != null) Console.WriteLine($"Site: {siteName}");

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

        Console.Write("Uploading...");
        if (!arduino.RoutingMatrixUploadStart())
        {
            Console.WriteLine(" Arduino did not respond with READY. Aborting.");
            Console.WriteLine();
            return;
        }

        bool ok = true;
        for (int i = 0; i < LedCount; i++)
        {
            string line = BuildMatrixLine(matrix, i);
            if (!arduino.RoutingMatrixSendLine(line))
            {
                Console.WriteLine($"\nError at LED {i} ({LedNames[i]}). Aborting upload.");
                arduino.RoutingMatrixUploadAbort();
                ok = false;
                break;
            }
        }

        if (ok)
        {
            Console.WriteLine(arduino.RoutingMatrixUploadFinish()
                ? " Done. Routing matrix stored successfully."
                : " Upload finished but device did not confirm storage.");
        }
        Console.WriteLine();
    }

    static void DownloadAndSave(ArduinoDevice arduino)
    {
        Console.Write("Save JSON file to path: ");
        string? path = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) { Console.WriteLine("No path given."); Console.WriteLine(); return; }

        byte[] matrix = new byte[LedCount * (1 + MaxConditions * 2)];

        arduino.RoutingMatrixDownloadStart();
        try
        {
            for (int i = 0; i < LedCount; i++)
            {
                string? line = arduino.GetNextDownloadLine(2000);
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
            string? end = arduino.GetNextDownloadLine(1000);
            if (end?.Trim() != "END")
                Console.WriteLine($"Warning: expected END, got '{end}'");
        }
        finally
        {
            arduino.RoutingMatrixDownloadFinish();
        }

        try
        {
            File.WriteAllText(path, MatrixToJson(matrix));
            Console.WriteLine($"Routing matrix saved to {path}");
        }
        catch (Exception ex) { Console.WriteLine($"Failed to save file: {ex.Message}"); }
        Console.WriteLine();
    }

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

    static byte[]? ParseRoutingJson(string path)
    {
        int stride = 1 + MaxConditions * 2;
        byte[] matrix = new byte[LedCount * stride];
        for (int i = 0; i < matrix.Length; i++) matrix[i] = 0xFF;

        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { Console.WriteLine($"Cannot read file: {ex.Message}"); return null; }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
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
                    Console.WriteLine($"  Warning: Unknown LED name '{ledName}' — skipping.");
                    continue;
                }
                if (ledProp.Value.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine($"  Error [{ledName}]: value must be an array of conditions.");
                    errorCount++;
                    continue;
                }

                var condElems = ledProp.Value.EnumerateArray().ToList();
                if (condElems.Count > MaxConditions)
                {
                    Console.WriteLine($"  Warning [{ledName}]: {condElems.Count} conditions exceed max {MaxConditions}; truncating.");
                    condElems = condElems.Take(MaxConditions).ToList();
                }
                if (condElems.Count == 0)
                {
                    matrix[ledIdx * stride] = 0x00;
                    Console.WriteLine($"  Info [{ledName}]: suppressed (always off).");
                    continue;
                }

                int validConditions = 0;
                bool ledHasError = false;

                for (int c = 0; c < condElems.Count; c++)
                {
                    string loc = $"{ledName} condition {c}";
                    if (condElems[c].ValueKind != JsonValueKind.Object)
                    {
                        Console.WriteLine($"  Error [{loc}]: must be an object with 'rett'/'avvik' arrays.");
                        errorCount++; ledHasError = true; continue;
                    }

                    byte rettMask = 0, avvikMask = 0;
                    bool condHasError = false;

                    if (condElems[c].TryGetProperty("rett", out var rettEl))
                    {
                        if (rettEl.ValueKind != JsonValueKind.Array)
                        {
                            Console.WriteLine($"  Error [{loc}]: 'rett' must be an array.");
                            errorCount++; condHasError = true;
                        }
                        else foreach (var el in rettEl.EnumerateArray())
                        {
                            string letter = el.GetString() ?? "";
                            byte bit = ParsePensLetter(letter);
                            if (bit == 0)
                            {
                                Console.WriteLine($"  Error [{loc}]: '{letter}' is not a valid Pens letter (B–I).");
                                errorCount++; condHasError = true; break;
                            }
                            rettMask |= bit;
                        }
                    }

                    if (!condHasError && condElems[c].TryGetProperty("avvik", out var avvikEl))
                    {
                        if (avvikEl.ValueKind != JsonValueKind.Array)
                        {
                            Console.WriteLine($"  Error [{loc}]: 'avvik' must be an array.");
                            errorCount++; condHasError = true;
                        }
                        else foreach (var el in avvikEl.EnumerateArray())
                        {
                            string letter = el.GetString() ?? "";
                            byte bit = ParsePensLetter(letter);
                            if (bit == 0)
                            {
                                Console.WriteLine($"  Error [{loc}]: '{letter}' is not a valid Pens letter (B–I).");
                                errorCount++; condHasError = true; break;
                            }
                            avvikMask |= bit;
                        }
                    }

                    if (condHasError) { ledHasError = true; continue; }

                    byte contradiction = (byte)(rettMask & avvikMask);
                    if (contradiction != 0)
                    {
                        Console.WriteLine($"  Error [{loc}]: {string.Join(",", MaskToPens(contradiction))} in both rett and avvik.");
                        errorCount++; ledHasError = true; continue;
                    }
                    if (rettMask == 0 && avvikMask == 0)
                    {
                        Console.WriteLine($"  Error [{loc}]: empty condition — always true. Add at least one Pens.");
                        errorCount++; ledHasError = true; continue;
                    }

                    int offset = ledIdx * stride + 1 + validConditions * 2;
                    matrix[offset]     = rettMask;
                    matrix[offset + 1] = avvikMask;
                    validConditions++;
                }

                matrix[ledIdx * stride] = ledHasError ? (byte)0xFF : (byte)validConditions;
            }

            if (errorCount > 0)
            {
                Console.WriteLine($"\n{errorCount} error(s) found — file not uploaded.");
                return null;
            }
        }
        return matrix;
    }

    static byte ParsePensLetter(string? letter)
    {
        if (string.IsNullOrEmpty(letter) || letter.Length != 1) return 0;
        int bit = char.ToUpper(letter[0]) - 'B';
        if (bit < 0 || bit > 7) return 0;
        return (byte)(1 << bit);
    }

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
            if (count == 0xFF) continue;
            if (!firstLed) sb.AppendLine(",");
            firstLed = false;
            sb.Append($"    \"{LedNames[i]}\": [");
            if (count == 0x00) { sb.Append("]"); continue; }
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

    static string BuildMatrixLine(byte[] matrix, int ledIdx)
    {
        int stride = 1 + MaxConditions * 2;
        byte count = matrix[ledIdx * stride];
        if (count == 0xFF) return "FF";
        if (count == 0x00) return "00";
        var parts = new List<string> { count.ToString("X2") };
        for (int c = 0; c < count; c++)
        {
            int offset = ledIdx * stride + 1 + c * 2;
            parts.Add(matrix[offset].ToString("X2"));
            parts.Add(matrix[offset + 1].ToString("X2"));
        }
        return string.Join(" ", parts);
    }

    static bool ParseMatrixLine(string line, byte[] matrix, int ledIdx)
    {
        int stride = 1 + MaxConditions * 2;
        var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        if (!byte.TryParse(tokens[0], System.Globalization.NumberStyles.HexNumber, null, out byte count)) return false;
        if (count != 0xFF && count != 0x00 && count > MaxConditions) return false;
        matrix[ledIdx * stride] = count;
        if (count == 0xFF || count == 0x00) return true;
        if (tokens.Length < 1 + count * 2) return false;
        for (int c = 0; c < count; c++)
        {
            int offset = ledIdx * stride + 1 + c * 2;
            if (!byte.TryParse(tokens[1 + c * 2],     System.Globalization.NumberStyles.HexNumber, null, out matrix[offset]))     return false;
            if (!byte.TryParse(tokens[1 + c * 2 + 1], System.Globalization.NumberStyles.HexNumber, null, out matrix[offset + 1])) return false;
        }
        return true;
    }

    static IEnumerable<string> MaskToPens(byte mask)
    {
        for (int bit = 0; bit < 8; bit++)
            if ((mask & (1 << bit)) != 0)
                yield return ((char)('B' + bit)).ToString();
    }
}
