using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Isql;
using DbMetaTool.Extensions;

namespace DbMetaTool
{
    public static class Program
    {
        // Przykładowe wywołania:
        // DbMetaTool build-db --db-dir "C:\db\fb5" --scripts-dir "C:\scripts"
        // DbMetaTool export-scripts --connection-string "..." --output-dir "C:\out"
        // DbMetaTool update-db --connection-string "..." --scripts-dir "C:\scripts"
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                // Console.WriteLine("Użycie:");
                // Console.WriteLine("  build-db --db-dir <ścieżka> --scripts-dir <ścieżka>");
                // Console.WriteLine("  export-scripts --connection-string <connStr> --output-dir <ścieżka>");
                // Console.WriteLine("  update-db --connection-string <connStr> --scripts-dir <ścieżka>");
                // return 1;
                var connStr = new FbConnectionStringBuilder
                {
                    Database = @"C:\db\fb5v2\database.fdb",
                    UserID = "SYSDBA",
                    Password = "password",
                    ServerType = FbServerType.Default,
                    Port = 3050,
                    DataSource = "localhost",
                    Charset = "UTF8"
                }.ToString();

                // BuildDatabase(@"C:\db\fb5", @"C:\out");
                UpdateDatabase(connStr, @"C:\out");
                return 1;
            }

            try
            {
                var command = args[0].ToLowerInvariant();

                switch (command)
                {
                    case "build-db":
                    {
                        string dbDir = GetArgValue(args, "--db-dir");
                        string scriptsDir = GetArgValue(args, "--scripts-dir");

                        BuildDatabase(dbDir, scriptsDir);
                        Console.WriteLine("Baza danych została zbudowana pomyślnie.");
                        return 0;
                    }

                    case "export-scripts":
                    {
                        string connStr = GetArgValue(args, "--connection-string");
                        string outputDir = GetArgValue(args, "--output-dir");

                        ExportScripts(connStr, outputDir);
                        Console.WriteLine("Skrypty zostały wyeksportowane pomyślnie.");
                        return 0;
                    }

                    case "update-db":
                    {
                        string connStr = GetArgValue(args, "--connection-string");
                        string scriptsDir = GetArgValue(args, "--scripts-dir");

                        UpdateDatabase(connStr, scriptsDir);
                        Console.WriteLine("Baza danych została zaktualizowana pomyślnie.");
                        return 0;
                    }

                    default:
                        Console.WriteLine($"Nieznane polecenie: {command}");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Błąd: " + ex.Message);
                return -1;
            }
        }

        private static string GetArgValue(string[] args, string name)
        {
            int idx = Array.IndexOf(args, name);
            if (idx == -1 || idx + 1 >= args.Length)
                throw new ArgumentException($"Brak wymaganego parametru {name}");
            return args[idx + 1];
        }

        /// <summary>
        /// Buduje nową bazę danych Firebird 5.0 na podstawie skryptów.
        /// </summary>
        public static void BuildDatabase(string databaseDirectory, string scriptsDirectory)
        {
            if (!Directory.Exists(scriptsDirectory))
                throw new DirectoryNotFoundException($"Katalog ze skryptami nie istnieje: {scriptsDirectory}");


            var allFiles = Directory.GetFiles(scriptsDirectory, "*.sql");

            var domainsFile = allFiles.FirstOrDefault(f => f.Contains("domains", StringComparison.OrdinalIgnoreCase));
            var tablesFile = allFiles.FirstOrDefault(f => f.Contains("tables", StringComparison.OrdinalIgnoreCase));
            var proceduresFile =
                allFiles.FirstOrDefault(f => f.Contains("procedures", StringComparison.OrdinalIgnoreCase));

            if (domainsFile == null || tablesFile == null || proceduresFile == null)
                throw new InvalidOperationException("Katalog ze skryptami musi zawierać pliki: domains, tables oraz procedures.");

            var databasePath = Path.Combine(databaseDirectory, "database.fdb");

            if (File.Exists(databasePath))
                throw new InvalidOperationException($"Baza danych już istnieje pod ścieżką: {databasePath}");

            var targetDirectory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            var connStr = new FbConnectionStringBuilder
            {
                Database = databasePath,
                UserID = "SYSDBA",
                Password = "password",
                ServerType = FbServerType.Default,
                Port = 3050,
                DataSource = "localhost",
                Charset = "UTF8"
            }.ToString();

            // 3. Tworzenie bazy i wykonanie skryptów (Wszystko albo nic)
            FbConnection.CreateDatabase(connStr);

            try
            {
                using var connection = new FbConnection(connStr);
                connection.Open();

                foreach (var file in new[] { domainsFile, tablesFile, proceduresFile })
                {
                    Console.Write($"Wykonywanie: {Path.GetFileName(file)}... ");
                    var script = new FbScript(File.ReadAllText(file));
                    script.Parse();
                    var batchExecution = new FbBatchExecution(connection);
                    batchExecution.AppendSqlStatements(script);
                    batchExecution.Execute();

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("OK");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"BŁĄD: {ex.Message}");
                Console.ResetColor();

                // Usunięcie niekompletnej bazy po awarii
                FbConnection.ClearAllPools();
                File.Delete(databasePath);

                throw;
            }
        }

        /// <summary>
        /// Generuje skrypty metadanych z istniejącej bazy danych Firebird 5.0.
        /// </summary>
        public static void ExportScripts(string connectionString, string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);

            using var connection = new FbConnection(connectionString);
            connection.Open();

            var exportDomainsScript = connection.ExportDomains();
            File.WriteAllText(Path.Combine(outputDirectory, "domains.sql"), exportDomainsScript);

            var exportTablesScript = connection.ExportTables();
            File.WriteAllText(Path.Combine(outputDirectory, "tables.sql"), exportTablesScript);

            var exportProceduresScript = connection.ExportProcedures();
            File.WriteAllText(Path.Combine(outputDirectory, "procedures.sql"), exportProceduresScript);
            connection.Close();
            Console.WriteLine("Eksport skryptów zakończony pomyślnie.");
        }

        /// <summary>
        /// Aktualizuje istniejącą bazę danych Firebird 5.0 na podstawie skryptów.
        /// </summary>
        public static void UpdateDatabase(string connectionString, string scriptsDirectory)
        {
            var allFiles = Directory.GetFiles(scriptsDirectory, "*.sql");
            var domainsFile = allFiles.FirstOrDefault(f => f.Contains("domains", StringComparison.OrdinalIgnoreCase));
            var tablesFile = allFiles.FirstOrDefault(f => f.Contains("tables", StringComparison.OrdinalIgnoreCase));
            var proceduresFile = allFiles.FirstOrDefault(f => f.Contains("procedures", StringComparison.OrdinalIgnoreCase));

            using var connection = new FbConnection(connectionString);
            connection.Open();

            if (domainsFile  is not null)
                connection.UpdateDomains(domainsFile);

            if (tablesFile is not null)
                connection.UpdateTables(tablesFile);

            if (proceduresFile is not null)
                connection.UpdateProcedures(proceduresFile);
            connection.Close();
            Console.WriteLine("Aktualizacja bazy danych zakończona pomyślnie.");
        }
    }
}