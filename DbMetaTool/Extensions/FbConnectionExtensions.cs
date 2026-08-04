using System.Text;
using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Isql;
using FirebirdSql.Data.Services;

namespace DbMetaTool.Extensions;

public static class FbConnectionExtensions
{
    private const string Domains =
        """
        SELECT
            RDB$FIELD_NAME,
            RDB$FIELD_TYPE,
            RDB$FIELD_LENGTH,
            RDB$NULL_FLAG,
            RDB$FIELD_SUB_TYPE,
            RDB$FIELD_PRECISION,
            RDB$FIELD_SCALE,
            RDB$DEFAULT_SOURCE,
            RDB$VALIDATION_SOURCE
        FROM RDB$FIELDS
        WHERE RDB$SYSTEM_FLAG = 0 AND RDB$FIELD_NAME NOT STARTING WITH 'RDB$'
        """;

    private const string TablesWithColumns =
        """
        SELECT
            r.RDB$RELATION_NAME,
            rf.RDB$FIELD_NAME,
            rf.RDB$FIELD_SOURCE,
            f.RDB$FIELD_TYPE,
            f.RDB$FIELD_LENGTH,
            f.RDB$FIELD_SUB_TYPE,
            f.RDB$FIELD_PRECISION,
            f.RDB$FIELD_SCALE,
            f.RDB$SYSTEM_FLAG
        FROM RDB$RELATIONS r
        JOIN RDB$RELATION_FIELDS rf
            ON rf.RDB$RELATION_NAME = r.RDB$RELATION_NAME
        JOIN RDB$FIELDS f
            ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
        WHERE r.RDB$SYSTEM_FLAG = 0
        AND r.RDB$VIEW_BLR IS NULL
        ORDER BY r.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION
        """;

    private const string Procedures =
        """
        SELECT
            p.RDB$PROCEDURE_NAME,
            p.RDB$PROCEDURE_SOURCE,
            pp.RDB$PARAMETER_NAME,
            pp.RDB$PARAMETER_TYPE,
            f.RDB$FIELD_TYPE,
            f.RDB$FIELD_LENGTH,
            f.RDB$FIELD_SUB_TYPE,
            f.RDB$FIELD_PRECISION,
            f.RDB$FIELD_SCALE
        FROM RDB$PROCEDURES p
        LEFT JOIN RDB$PROCEDURE_PARAMETERS pp
            ON pp.RDB$PROCEDURE_NAME = p.RDB$PROCEDURE_NAME
        LEFT JOIN RDB$FIELDS f
            ON f.RDB$FIELD_NAME = pp.RDB$FIELD_SOURCE
        WHERE p.RDB$SYSTEM_FLAG = 0
        ORDER BY p.RDB$PROCEDURE_NAME, pp.RDB$PARAMETER_TYPE, pp.RDB$PARAMETER_NUMBER
        """;

    #region Read

    public static string ExportDomains(this FbConnection connection)
    {
        var sql = new StringBuilder();

        using var command = new FbCommand(Domains, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var name = reader.GetString(0).Trim();
            var fieldType = reader.GetInt32(1);
            var length = reader.GetInt32(2);
            var subType = reader.GetInt32OrDefault(4);
            var precision = reader.GetInt32OrDefault(5);
            var scale = reader.GetInt32OrDefault(6);

            var type = GetFirebirdType(fieldType, length, subType, precision, scale);
            sql.Append($"CREATE DOMAIN {name} AS {type}");

            // DEFAULT
            if (!reader.IsDBNull(7))
            {
                var defaultValue = reader.GetString(7).Trim();
                sql.Append($" {defaultValue}");
            }

            // NOT NULL
            if (!reader.IsDBNull(3) && reader.GetInt32(3) == 1)
            {
                sql.Append(" NOT NULL");
            }

            // CHECK
            if (!reader.IsDBNull(8))
            {
                var checkConstraint = reader.GetString(8).Trim();
                sql.Append($" {checkConstraint}");
            }

            sql.AppendLine(";");
            sql.AppendLine();
        }

        return sql.ToString();
    }

    public static string ExportTables(this FbConnection connection)
    {
        var sql = new StringBuilder();

        using var command = new FbCommand(TablesWithColumns, connection);
        using var reader = command.ExecuteReader();

        string? currentTable = null;
        var columns = new List<string>();

        while (reader.Read())
        {
            var tableName = reader.GetString(0).Trim();

            if (currentTable is not null && tableName != currentTable)
            {
                AppendTableScript(sql, currentTable, columns);
                columns.Clear();
            }

            currentTable = tableName;
            var columnName = reader.GetString(1).Trim();
            var domainOrSource = reader.GetString(2).Trim();

            var fieldType = reader.GetInt32(3);
            var length = reader.GetInt32(4);
            var subType = reader.GetInt32OrDefault(5);
            var precision = reader.GetInt32OrDefault(6);
            var scale = reader.GetInt32OrDefault(7);

            var isUserDefinedDomain = reader.GetInt32(8) == 0 && !domainOrSource.StartsWith("RDB$");

            // Jeśli kolumna używa domeny stworzonej przez użytkownika (np. D_KWOTA), używamy jej nazwy.
            // W przeciwnym razie jest to typ wpisany bezpośrednio ("z palca"), więc zamieniamy go na typ SQL (np. NUMERIC(15, 2)).
            var typeOrDomain =
                isUserDefinedDomain ? domainOrSource : GetFirebirdType(fieldType, length, subType, precision, scale);

            columns.Add($"    {columnName} {typeOrDomain}");
        }

        if (currentTable is null)
            return sql.ToString();

        AppendTableScript(sql, currentTable, columns);

        return sql.ToString();
    }


    public static string ExportProcedures(this FbConnection connection)
    {
        var sql = new StringBuilder();
        var procedures = new Dictionary<string, (string? Source, List<string> Inputs, List<string> Outputs)>();

        using (var command = new FbCommand(Procedures, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0).Trim();
                var source = reader.IsDBNull(1) ? null : reader.GetString(1);

                if (!procedures.TryGetValue(name, out var entry))
                {
                    entry = (source, [], []);
                    procedures[name] = entry;
                }

                if (reader.IsDBNull(2)) continue;

                var paramName = reader.GetString(2).Trim();
                var paramType = reader.GetInt32(3);
                var fieldType = reader.GetInt32(4);
                var length = reader.GetInt32OrDefault(5);
                var subType = reader.GetInt32OrDefault(6);
                var precision = reader.GetInt32OrDefault(7);
                var scale = reader.GetInt32OrDefault(8);

                var declaration = $"{paramName} {GetFirebirdType(fieldType, length, subType, precision, scale)}";

                if (paramType == 0)
                    entry.Inputs.Add(declaration);
                else
                    entry.Outputs.Add(declaration);
            }
        }

        if (procedures.Count == 0)
            return string.Empty;

        sql.AppendLine("SET TERM ^ ;");
        sql.AppendLine();

        foreach (var name in procedures.Keys)
        {
            var (source, inputs, outputs) = procedures[name];

            sql.Append($"CREATE OR ALTER PROCEDURE {name} ");

            if (inputs.Count > 0)
                sql.Append($"({string.Join(", ", inputs)}) ");

            if (outputs.Count > 0)
                sql.Append($"RETURNS ({string.Join(", ", outputs)}) ");

            sql.AppendLine();
            sql.AppendLine("AS");

            var cleanSource = source?.TrimEnd(';', ' ', '\r', '\n');
            sql.AppendLine($"{cleanSource}^");
            sql.AppendLine();
        }

        sql.AppendLine("SET TERM ; ^");

        return sql.ToString();
    }

    private static void AppendTableScript(StringBuilder sql, string tableName, List<string> columns)
    {
        sql.AppendLine($"CREATE TABLE {tableName}");
        sql.AppendLine("(");
        sql.AppendLine(string.Join(",\n", columns));
        sql.AppendLine(");");
        sql.AppendLine();
    }

    /// <summary>
    /// Pobiera wartość <see cref="int"/> z podanej kolumny lub zwraca wartość domyślną, jeśli wartość w bazie to <c>NULL</c>.
    /// </summary>
    private static int GetInt32OrDefault(this FbDataReader reader, int index, int defaultValue = 0)
        => reader.IsDBNull(index) ? defaultValue : reader.GetInt32(index);

    /// <summary>
    /// Mapuje wewnętrzny identyfikator typu danych Firebird (<c>RDB$FIELD_TYPE</c>) na jego odpowiednik SQL.
    /// Pełna specyfikacja typów: <see href="https://www.firebirdsql.org/file/documentation/chunk/en/refdocs/fblangref30/fblangref-appx04-fields.html">RDB$FIELDS Reference</see>.
    /// </summary>
    private static string GetFirebirdType(int fieldType, int length, int subType = 0, int precision = 0, int scale = 0)
        => fieldType switch
        {
            7 or 8 or 16 when subType is 1 or 2 => $"{(subType == 1 ? "NUMERIC" : "DECIMAL")}({precision},{-scale})",
            7 => "SMALLINT",
            8 => "INTEGER",
            16 => "BIGINT",
            10 => "FLOAT",
            27 => "DOUBLE PRECISION",
            12 => "DATE",
            13 => "TIME",
            35 => "TIMESTAMP",
            14 => $"CHAR({length})",
            37 => $"VARCHAR({length})",
            261 => "BLOB",
            _ => throw new NotSupportedException($"Niewspierany typ Firebird {fieldType}")
        };

    #endregion

    #region Update

    private const string ExistingDomains =
        "SELECT RDB$FIELD_NAME FROM RDB$FIELDS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$FIELD_NAME NOT STARTING WITH 'RDB$'";

    private const string ExistingTables =
        "SELECT RDB$RELATION_NAME FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL";

    public static string Backup(this FbConnection connection, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(backupDirectory, $"database_{timestamp}.fbk");

        var backup = new FbBackup
        {
            ConnectionString = connection.ConnectionString,
            BackupFiles = { new FbBackupFile(backupPath, int.MaxValue) },
            Verbose = false
        };
        backup.Execute();
        return backupPath;
    }

    public static void Restore(this FbConnection connection, string backupPath)
    {
        FbConnection.ClearAllPools();

        var connBuilder = new FbConnectionStringBuilder(connection.ConnectionString);

        var restore = new FbRestore
        {
            ConnectionString = connection.ConnectionString,
            BackupFiles = { new FbBackupFile(backupPath, int.MaxValue) },
            Verbose = false
        };
        restore.Execute();
    }

    public static void UpdateDomains(this FbConnection connection, string filePath)
    {
        var existing = LoadNames(connection, ExistingDomains);
        ExecuteMissing(connection, filePath, existing, "CREATE DOMAIN");
    }

    public static void UpdateTables(this FbConnection connection, string filePath)
    {
        var existing = LoadNames(connection, ExistingTables);
        ExecuteMissing(connection, filePath, existing, "CREATE TABLE");
    }

    public static void UpdateProcedures(this FbConnection connection, string filePath)
    {
        var script = new FbScript(File.ReadAllText(filePath));
        script.Parse();
        var batch = new FbBatchExecution(connection);
        batch.AppendSqlStatements(script);
        batch.Execute();
    }

    private const string ExistingColumns =
        """
        SELECT rf.RDB$RELATION_NAME, rf.RDB$FIELD_NAME
        FROM RDB$RELATION_FIELDS rf
        JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
        WHERE r.RDB$SYSTEM_FLAG = 0 AND r.RDB$VIEW_BLR IS NULL
        """;

    public static void UpdateColumns(this FbConnection connection, string filePath)
    {
        var existingColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new FbCommand(ExistingColumns, connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var table = reader.GetString(0).Trim();
                var column = reader.GetString(1).Trim();
                if (!existingColumns.TryGetValue(table, out var cols))
                {
                    cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    existingColumns[table] = cols;
                }

                cols.Add(column);
            }
        }

        var script = new FbScript(File.ReadAllText(filePath));
        script.Parse();

        foreach (var statement in script.Results)
        {
            var parts = statement.Text.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue; 
            // Sprawdzamy, czy pierwsze dwa słowa komendy to dokładnie "CREATE TABLE".
            if (!string.Join(" ", parts[..2]).Equals("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) continue;

            var tableName = parts[2].Trim();
            if (!existingColumns.TryGetValue(tableName, out var existingCols)) continue;

            var text = statement.Text;
            var start = text.IndexOf('(');
            var end = text.LastIndexOf(')');
            if (start == -1 || end == -1) continue;

            foreach (var colDef in SplitColumns(text[(start + 1)..end]))
            {
                var colName = colDef.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
                if (existingCols.Contains(colName)) continue;

                using var cmd = new FbCommand($"ALTER TABLE {tableName} ADD {colDef}", connection);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static List<string> SplitColumns(string columnSection)
    {
        var result = new List<string>();
        var depth = 0;
        var current = new StringBuilder();

        foreach (var c in columnSection)
        {
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
            }

            if (c == ',' && depth == 0)
            {
                var col = current.ToString().Trim();
                if (col.Length > 0) result.Add(col);
                current.Clear();
            }
            else
                current.Append(c);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0) result.Add(last);
        return result;
    }

    private static HashSet<string> LoadNames(FbConnection connection, string query)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = new FbCommand(query, connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0).Trim());
        return names;
    }

    private static void ExecuteMissing(FbConnection connection, string filePath, HashSet<string> existing,
        string keyword)
    {
        var script = new FbScript(File.ReadAllText(filePath));
        script.Parse();

        foreach (var statement in script.Results)
        {
            var parts = statement.Text.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var stmtKeyword = string.Join(" ", parts[..2]);
            if (!stmtKeyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            var name = parts[2].Trim();
            if (existing.Contains(name)) continue;

            using var cmd = new FbCommand(statement.Text, connection);
            cmd.ExecuteNonQuery();
        }
    }

    #endregion
}