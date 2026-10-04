var outputDir = @"C:\Temp\localization";

System.IO.Directory.CreateDirectory(outputDir);

var settings = UnityEngine.Localization.Settings.LocalizationSettings.Instance;
var locales = settings.GetAvailableLocales().Locales;

var result = new System.Text.StringBuilder();
var totalLocales = locales.Count;
var exportedLocales = 0;
var exportedTables = 0;

result.AppendLine("Available locales: " + totalLocales);

foreach (var locale in locales)
{
    var localeCode = locale.Identifier.Code;
    var localeDir = System.IO.Path.Combine(outputDir, localeCode);

    System.IO.Directory.CreateDirectory(localeDir);

    try
    {
        var handle = settings.GetStringDatabase().GetAllTables(locale);

        handle.WaitForCompletion();

        var tables = handle.Result;

        if (tables == null)
        {
            result.AppendLine("ERROR: " + localeCode + " -> no tables");
            continue;
        }

        var localeTables = 0;

        foreach (var table in tables)
        {
            if (table == null)
                continue;

            try
            {
                var tableName = table.TableCollectionName;

                if (string.IsNullOrEmpty(tableName))
                    tableName = table.name;

                var invalid = System.IO.Path.GetInvalidFileNameChars();

                var safeName = new string(
                    tableName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()
                );

                var path = System.IO.Path.Combine(
                    localeDir,
                    safeName + ".json"
                );

                var entries = new System.Collections.Generic.List<object>();

                foreach (var entry in table.Values)
                {
                    if (entry == null)
                        continue;

                    entries.Add(
                        new
                        {
                            keyId = entry.KeyId,
                            key = entry.Key,
                            value = entry.LocalizedValue
                        }
                    );
                }

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    new
                    {
                        table = tableName,
                        locale = localeCode,
                        entries = entries
                    },
                    Newtonsoft.Json.Formatting.Indented
                );

                System.IO.File.WriteAllText(
                    path,
                    json,
                    System.Text.Encoding.UTF8
                );

                localeTables++;
                exportedTables++;
            }
            catch (System.Exception e)
            {
                result.AppendLine(
                    "ERROR: " + localeCode +
                    " / " + table.name +
                    " -> " + e.Message
                );
            }
        }

        exportedLocales++;

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(outputDir, "progress.txt"),
            "Locales: " + totalLocales + "\n" +
            "Processed: " + exportedLocales + "/" + totalLocales + "\n" +
            "Tables: " + exportedTables + "\n" +
            "Current locale: " + localeCode + "\n" +
            "Tables in locale: " + localeTables
        );

        result.AppendLine(
            localeCode + ": " + localeTables + " tables"
        );
    }
    catch (System.Exception e)
    {
        result.AppendLine(
            "ERROR: " + localeCode + " -> " + e
        );
    }
}

System.IO.File.WriteAllText(
    System.IO.Path.Combine(outputDir, "progress.txt"),
    "DONE\n" +
    "Locales: " + totalLocales + "\n" +
    "Processed: " + exportedLocales + "/" + totalLocales + "\n" +
    "Tables: " + exportedTables + "\n\n" +
    result
);

"DONE\n" +
"Locales: " + exportedLocales + "/" + totalLocales + "\n" +
"Tables: " + exportedTables + "\n" +
"Output: " + outputDir;
